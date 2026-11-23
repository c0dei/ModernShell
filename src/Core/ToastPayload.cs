using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace ModernShell.Core;

/// <summary>通知（トースト）に付いている、クリックしたときの動作。</summary>
internal sealed record ToastLaunch(string Launch, string ActivationType)
{
    public bool IsProtocol => ActivationType.Equals("protocol", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 通知の起動パラメータ（<c>launch</c> と <c>activationType</c>）を、Windows の通知データベース
/// （%LocalAppData%\Microsoft\Windows\Notifications\wpndatabase.db）から読む。通知一覧の API ではこれが取れないため。
/// 非公開の内部データなので、読めなければ null を返すだけで、他には影響させない。読むだけで、書き換えない。
/// </summary>
internal static class ToastPayload
{
    private const string Lib = "winsqlite3.dll";
    private const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_OPEN_READONLY = 1;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_int64(IntPtr stmt, int idx, long v);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr stmt);

    /// <summary>通知 ID から、起動パラメータを読む（読めない・無いときは null）。</summary>
    public static ToastLaunch? Read(uint id)
    {
        string? tmp = null;
        try
        {
            string src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications\wpndatabase.db");
            if (!File.Exists(src)) return null;
            // 使用中（WAL モード）なので、-wal ごと一時フォルダーにコピーして、そのコピーを読む
            tmp = Path.Combine(Path.GetTempPath(), "ModernShell-wpn-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            string dst = Path.Combine(tmp, "wpndatabase.db");
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                if (!File.Exists(src + ext)) continue;
                using var from = new FileStream(src + ext, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var to = File.Create(dst + ext);
                from.CopyTo(to);
            }

            byte[]? payload = null;
            if (sqlite3_open_v2(dst, out var db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK) { sqlite3_close(db); return null; }
            try
            {
                if (sqlite3_prepare_v2(db, "SELECT Payload FROM Notification WHERE Id = ?1 AND Type = 'toast' LIMIT 1", -1, out var st, IntPtr.Zero) != SQLITE_OK) return null;
                try
                {
                    sqlite3_bind_int64(st, 1, id);
                    if (sqlite3_step(st) == SQLITE_ROW)
                    {
                        int n = sqlite3_column_bytes(st, 0);
                        var p = sqlite3_column_blob(st, 0);
                        if (n > 0 && p != IntPtr.Zero) { payload = new byte[n]; Marshal.Copy(p, payload, 0, n); }
                    }
                }
                finally { sqlite3_finalize(st); }
            }
            finally { sqlite3_close(db); }

            return payload == null ? null : Parse(Encoding.UTF8.GetString(payload).TrimStart('﻿'));
        }
        catch (Exception ex) { Log.Info("toast payload: " + ex.Message); return null; }
        finally { try { if (tmp != null) Directory.Delete(tmp, true); } catch { } }
    }

    private static ToastLaunch? Parse(string xml)
    {
        var root = XDocument.Parse(xml).Root;
        if (root == null) return null;
        string launch = (string?)root.Attribute("launch") ?? "";
        if (launch.Length == 0) return null;
        return new ToastLaunch(launch, (string?)root.Attribute("activationType") ?? "foreground");
    }
}
