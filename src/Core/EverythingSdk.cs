using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ModernShell.Core;

/// <summary>絞り込みの種類（Everything の検索マクロに置き換える）。</summary>
public enum EverythingFilter { All, Folder, Document, Image, Video, Audio, Archive, Executable }

public enum EverythingSort { NameAsc = 1, NameDesc = 2, SizeAsc = 5, SizeDesc = 6, DateAsc = 13, DateDesc = 14 }

public sealed record EverythingHit(string Name, string Dir, bool IsFolder, long Size, DateTime Modified)
{
    public string FullPath => Dir.Length == 0 ? Name : Path.Combine(Dir, Name);
    public string Extension => IsFolder ? "" : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
}

public sealed record EverythingResult(List<EverythingHit> Hits, uint Total, long Ms, string? Error);

/// <summary>
/// Everything SDK（Everything64.dll）で、起動中の Everything に検索を頼む。
/// SDK は 1 つの検索の状態を全体で持つので、呼び出しは 1 つずつ（lock）で行う。
/// </summary>
public static class EverythingSdk
{
    private const string Dll = "Everything64.dll";
    private const uint ReqName = 0x1, ReqPath = 0x2, ReqSize = 0x10, ReqDateModified = 0x40;
    private const uint ErrIpc = 2;

    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern uint Everything_SetSearchW(string s);
    [DllImport(Dll)] private static extern void Everything_SetMatchCase(bool b);
    [DllImport(Dll)] private static extern void Everything_SetMatchPath(bool b);
    [DllImport(Dll)] private static extern void Everything_SetRegex(bool b);
    [DllImport(Dll)] private static extern void Everything_SetMax(uint n);
    [DllImport(Dll)] private static extern void Everything_SetSort(uint n);
    [DllImport(Dll)] private static extern void Everything_SetRequestFlags(uint n);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern bool Everything_QueryW(bool wait);
    [DllImport(Dll)] private static extern uint Everything_GetLastError();
    [DllImport(Dll)] private static extern uint Everything_GetNumResults();
    [DllImport(Dll)] private static extern uint Everything_GetTotResults();
    [DllImport(Dll)] private static extern bool Everything_IsFolderResult(uint i);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern IntPtr Everything_GetResultFileNameW(uint i);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern IntPtr Everything_GetResultPathW(uint i);
    [DllImport(Dll)] private static extern bool Everything_GetResultSize(uint i, out long size);
    [DllImport(Dll)] private static extern bool Everything_GetResultDateModified(uint i, out long fileTime);
    [DllImport(Dll)] private static extern void Everything_Reset();

    private static readonly object Gate = new();

    /// <summary>SDK の DLL が使えるか（64 ビットで、DLL が同梱されている）。</summary>
    public static bool Available => Environment.Is64BitProcess && File.Exists(Path.Combine(AppContext.BaseDirectory, Dll));

    private static string Prefix(EverythingFilter f) => f switch
    {
        EverythingFilter.Folder => "folder: ",
        EverythingFilter.Document => "doc: ",
        EverythingFilter.Image => "pic: ",
        EverythingFilter.Video => "video: ",
        EverythingFilter.Audio => "audio: ",
        EverythingFilter.Archive => "zip: ",
        EverythingFilter.Executable => "exe: ",
        _ => "",
    };

    /// <summary>検索する（待ちがあるので、UI スレッドでは呼ばない）。</summary>
    public static EverythingResult Query(string text, EverythingFilter filter, bool matchCase, bool regex, bool matchPath, EverythingSort sort, uint max = 200)
    {
        var sw = Stopwatch.StartNew();
        if (!Available) return new(new(), 0, 0, "sdk");
        try
        {
            // 起動していなければ、裏で起動して待つ
            if (!Everything.EnsureRunning()) return new(new(), 0, 0, "notfound");
            lock (Gate)
            {
                for (int attempt = 0; ; attempt++)
                {
                    Everything_Reset();
                    Everything_SetSearchW(Prefix(filter) + text);
                    Everything_SetMatchCase(matchCase);
                    Everything_SetMatchPath(matchPath);
                    Everything_SetRegex(regex);
                    Everything_SetMax(max);
                    Everything_SetSort((uint)sort);
                    Everything_SetRequestFlags(ReqName | ReqPath | ReqSize | ReqDateModified);
                    if (Everything_QueryW(true)) break;
                    // 起動した直後は、まだ受け付けないことがある（IPC エラー）。少し待って、やり直す
                    if (Everything_GetLastError() == ErrIpc && attempt < 20) { System.Threading.Thread.Sleep(250); continue; }
                    return new(new(), 0, sw.ElapsedMilliseconds, "ipc");
                }
                uint n = Everything_GetNumResults();
                var hits = new List<EverythingHit>((int)n);
                for (uint i = 0; i < n; i++)
                {
                    string name = Marshal.PtrToStringUni(Everything_GetResultFileNameW(i)) ?? "";
                    string dir = Marshal.PtrToStringUni(Everything_GetResultPathW(i)) ?? "";
                    bool folder = Everything_IsFolderResult(i);
                    Everything_GetResultSize(i, out long size);
                    Everything_GetResultDateModified(i, out long ft);
                    DateTime modified = ft > 0 ? DateTime.FromFileTime(ft) : DateTime.MinValue;
                    hits.Add(new EverythingHit(name, dir, folder, folder ? -1 : size, modified));
                }
                return new(hits, Everything_GetTotResults(), sw.ElapsedMilliseconds, null);
            }
        }
        catch (Exception ex)
        {
            Log.Error("everything sdk", ex);
            return new(new(), 0, sw.ElapsedMilliseconds, "error");
        }
    }
}
