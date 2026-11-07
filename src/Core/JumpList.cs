using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ModernShell.Core;

/// <summary>ジャンプリストの 1 項目。Path を開けば実行できる（タスクは一時 .lnk、最近使ったものは実ファイル）。</summary>
public sealed record JumpItem(string Title, string Path, bool IsSeparator = false)
{
    /// <summary>ショートカットに書かれたアイコンの場所（DLL / EXE / ICO と番号）。</summary>
    public string? IconFile { get; init; }
    public int IconIndex { get; init; }
    /// <summary>ショートカットのリンク先（アイコンの最後の手段に使う）。</summary>
    public string? Target { get; init; }
}

/// <summary>ジャンプリストの区切り（「タスク」「最近使ったもの」やアプリ独自のカテゴリ）。</summary>
public sealed record JumpCategory(string Title, List<JumpItem> Items);

/// <summary>
/// タスクバーの右クリックに出る「ジャンプリスト」を読む（読み取りのみ）。
///  - タスク / アプリ独自のカテゴリ: %AppData%\Microsoft\Windows\Recent\CustomDestinations\{AppID の CRC64}.customDestinations-ms
///    （非公開形式。中身は .lnk の連続なので、構造をたどって 1 件ずつ取り出す）
///  - 最近使ったもの: 公開 API の IApplicationDocumentLists
/// AppID は、アプリが明示した AppUserModelID か、なければ exe の「既知フォルダ相対パス」（例: {1AC14E77-…}\notepad.exe）。
/// ファイル名は AppID を大文字の UTF-16 にして CRC64 (多項式 0x92C64265D32139A4, 初期値 全ビット1) を取った 16 進。
/// </summary>
public static class JumpList
{
    private static string CustomDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent\CustomDestinations");
    private static string TempDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"ModernShell\jumplist");

    public static List<JumpCategory> Read(string? aumid, string? exePath, int recentCount = 8)
    {
        var result = new List<JumpCategory>();
        foreach (var appId in AppIds(aumid, exePath))
        {
            try
            {
                var recent = Recent(appId, recentCount);
                var custom = Custom(appId);
                if (recent.Count == 0 && custom.Count == 0) continue;
                if (recent.Count > 0) result.Add(new JumpCategory(L.T("最近使ったもの"), recent));
                result.AddRange(custom);
                break;
            }
            catch (Exception ex) { Log.Error("jumplist", ex); }
        }
        return result;
    }

    // ------------------------------------------------------------------
    // AppID

    private static IEnumerable<string> AppIds(string? aumid, string? exePath)
    {
        if (!string.IsNullOrEmpty(aumid)) yield return aumid;
        if (!string.IsNullOrEmpty(exePath))
        {
            if (KnownFolderRelative(exePath) is { } rel) yield return rel;
            yield return exePath;
        }
    }

    private static readonly Guid[] KnownFolders =
    {
        new("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7"), // System
        new("D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27"), // SystemX86
        new("F38BF404-1D43-42F2-9305-67DE0B28FC23"), // Windows
        new("6D809377-6AF0-444B-8957-A3773F02200E"), // ProgramFilesX64
        new("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E"), // ProgramFilesX86
        new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"), // LocalAppData
        new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"), // RoamingAppData
        new("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97"), // ProgramData
        new("5CD7AEE2-2219-4A67-B85D-6C9CE15660CB"), // UserProgramFiles
        new("5E6C858F-0E22-4760-9AFE-EA3317B67173"), // Profile
    };

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    /// <summary>exe のパスを、いちばん深く一致する既知フォルダからの相対パスにする（Windows が AppID を作るときと同じ形）。</summary>
    private static string? KnownFolderRelative(string exe)
    {
        string? best = null; int bestLen = 0;
        foreach (var id in KnownFolders)
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) continue;
            var root = Marshal.PtrToStringUni(p);
            Marshal.FreeCoTaskMem(p);
            if (string.IsNullOrEmpty(root)) continue;
            var withSep = root.TrimEnd('\\') + "\\";
            if (exe.StartsWith(withSep, StringComparison.OrdinalIgnoreCase) && withSep.Length > bestLen)
            {
                best = "{" + id.ToString().ToUpperInvariant() + "}\\" + exe.Substring(withSep.Length);
                bestLen = withSep.Length;
            }
        }
        return best;
    }

    public static string Hash(string appId)
    {
        const ulong poly = 0x92C64265D32139A4;
        ulong crc = ulong.MaxValue;
        foreach (var b in Encoding.Unicode.GetBytes(appId.ToUpperInvariant()))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? poly : 0);
        }
        return crc.ToString("x16");
    }

    // ------------------------------------------------------------------
    // タスク / アプリ独自のカテゴリ（customDestinations-ms）

    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");
    private const uint Footer = 0xBABFFBAB;

    private static List<JumpCategory> Custom(string appId)
    {
        var file = System.IO.Path.Combine(CustomDir, Hash(appId) + ".customDestinations-ms");
        var cats = new List<JumpCategory>();
        if (!File.Exists(file)) return cats;
        var data = File.ReadAllBytes(file);
        int pos = 0;
        uint U32() { var v = BitConverter.ToUInt32(data, pos); pos += 4; return v; }

        U32(); // version
        uint count = U32();
        U32(); // reserved
        for (int c = 0; c < count && pos < data.Length - 4; c++)
        {
            uint type = U32();
            string? title = null;
            uint items = 0;
            switch (type)
            {
                case 0: // アプリ独自のカテゴリ
                    int len = BitConverter.ToUInt16(data, pos); pos += 2;
                    title = ResolveIndirect(Encoding.Unicode.GetString(data, pos, len * 2)); pos += len * 2;
                    items = U32();
                    break;
                case 1: // 既定カテゴリ（よく使うもの / 最近使ったもの）は API 側で取得するので中身なし
                    U32();
                    break;
                case 2: // タスク
                    title = L.T("タスク");
                    items = U32();
                    break;
                default:
                    return cats; // 未知の形式
            }

            var list = new List<JumpItem>();
            for (int i = 0; i < items; i++)
            {
                if (pos + 16 > data.Length) return cats;
                var clsid = new Guid(data.AsSpan(pos, 16)); pos += 16;
                if (clsid != ShellLinkClsid) return cats; // .lnk 以外は長さが分からないので打ち切る
                int lnkLen = LnkLength(data, pos);
                if (lnkLen <= 0 || pos + lnkLen > data.Length) return cats;
                var lnk = data.AsSpan(pos, lnkLen).ToArray();
                pos += lnkLen;
                if (ToItem(lnk) is { } item) list.Add(item);
            }
            if (pos + 4 <= data.Length && BitConverter.ToUInt32(data, pos) == Footer) pos += 4;
            if (title != null && list.Any(x => !x.IsSeparator)) cats.Add(new JumpCategory(title, list));
        }
        return cats;
    }

    /// <summary>[MS-SHLLINK] の構造をたどって .lnk 1 個分の長さを求める。</summary>
    private static int LnkLength(byte[] d, int start)
    {
        int p = start;
        if (BitConverter.ToUInt32(d, p) != 0x4C) return -1;
        uint flags = BitConverter.ToUInt32(d, p + 0x14);
        p += 0x4C;
        if ((flags & 0x01) != 0) p += 2 + BitConverter.ToUInt16(d, p);           // LinkTargetIDList
        if ((flags & 0x02) != 0) p += (int)BitConverter.ToUInt32(d, p);          // LinkInfo
        bool unicode = (flags & 0x80) != 0;
        foreach (uint f in new uint[] { 0x04, 0x08, 0x10, 0x20, 0x40 })          // StringData
            if ((flags & f) != 0) p += 2 + BitConverter.ToUInt16(d, p) * (unicode ? 2 : 1);
        while (p + 4 <= d.Length)                                                 // ExtraData
        {
            uint size = BitConverter.ToUInt32(d, p);
            if (size < 4) { p += 4; break; }
            p += (int)size;
        }
        return p - start;
    }

    // ---- .lnk をメモリから読み込んでタイトルを取り出す ----

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr pfd, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
    }

    [ComImport, Guid("00000109-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistStream
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load(IntPtr stream); // IStream*（SHCreateMemStream の戻り値をそのまま渡す）
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)] private struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY k);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [DllImport("shlwapi.dll")] private static extern IntPtr SHCreateMemStream(byte[] data, uint size);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHLoadIndirectString(string src, StringBuilder buf, int cch, IntPtr reserved);

    private static readonly Guid TitleFmt = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
    private static readonly Guid AppUserModelFmt = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    private static JumpItem? ToItem(byte[] lnk)
    {
        object? link = null;
        IntPtr stream = IntPtr.Zero;
        try
        {
            link = new ShellLink();
            stream = SHCreateMemStream(lnk, (uint)lnk.Length);
            if (stream == IntPtr.Zero) return null;
            ((IPersistStream)link).Load(stream);

            string? title = null;
            bool separator = false;
            if (link is IPropertyStore ps)
            {
                var sep = new PROPERTYKEY { fmtid = AppUserModelFmt, pid = 6 };
                if (ps.GetValue(ref sep, out var sv) == 0)
                {
                    separator = sv.vt == 11 /*VT_BOOL*/ && (short)(long)sv.p != 0;
                    PropVariantClear(ref sv);
                }
                var key = new PROPERTYKEY { fmtid = TitleFmt, pid = 2 };
                if (ps.GetValue(ref key, out var pv) == 0)
                {
                    if (pv.vt == 31 /*VT_LPWSTR*/) title = Marshal.PtrToStringUni(pv.p);
                    PropVariantClear(ref pv);
                }
            }
            if (separator) return new JumpItem("", "", IsSeparator: true);
            if (string.IsNullOrEmpty(title)) return null;

            // 実行は「一時 .lnk を開く」ことで、引数・作業フォルダ・AUMID などをアプリが登録したとおりに再現する
            Directory.CreateDirectory(TempDir);
            var path = System.IO.Path.Combine(TempDir, Convert.ToHexString(SHA1.HashData(lnk)) + ".lnk");
            if (!File.Exists(path)) File.WriteAllBytes(path, lnk);
            // アイコンの場所とリンク先（シェルのアイコン取得で取れなかったときに使う）
            string? iconFile = null, target = null;
            int iconIndex = 0;
            try
            {
                var sl = (IShellLinkW)link;
                var ib = new StringBuilder(1024);
                sl.GetIconLocation(ib, ib.Capacity, out iconIndex);
                iconFile = ib.Length > 0 ? Environment.ExpandEnvironmentVariables(ib.ToString()) : null;
                var tb = new StringBuilder(1024);
                sl.GetPath(tb, tb.Capacity, IntPtr.Zero, 0);
                target = tb.Length > 0 ? Environment.ExpandEnvironmentVariables(tb.ToString()) : null;
            }
            catch { }
            return new JumpItem(ResolveIndirect(title), path) { IconFile = iconFile, IconIndex = iconIndex, Target = target };
        }
        catch { return null; }
        finally
        {
            if (stream != IntPtr.Zero) Marshal.Release(stream);
            if (link != null) Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>"@{パッケージ}?ms-resource://…" や "@dll,-123" 形式のリソース参照を実際の文字列にする。</summary>
    private static string ResolveIndirect(string s)
    {
        if (!s.StartsWith("@")) return s;
        var sb = new StringBuilder(512);
        return SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : s;
    }

    // ------------------------------------------------------------------
    // 最近使ったもの（公開 API）

    [ComImport, Guid("86bec222-30f2-47e0-9f25-60d11cd75c28")] private class ApplicationDocumentLists { }

    [ComImport, Guid("3c594f9f-9f30-47a1-979a-c9e83d3d0a06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationDocumentLists
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int GetList(int listType, uint itemsDesired, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? ppv);
    }

    [ComImport, Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdn, out IntPtr name);
    }

    private static List<JumpItem> Recent(string appId, int count)
    {
        var items = new List<JumpItem>();
        object? lists = null;
        try
        {
            lists = new ApplicationDocumentLists();
            var adl = (IApplicationDocumentLists)lists;
            adl.SetAppID(appId);
            var iid = typeof(IObjectArray).GUID;
            if (adl.GetList(0 /*ADLT_RECENT*/, (uint)count, ref iid, out var arrObj) != 0 || arrObj is not IObjectArray arr) return items;
            arr.GetCount(out var n);
            var itemIid = typeof(IShellItem).GUID;
            for (uint i = 0; i < n; i++)
            {
                try
                {
                    arr.GetAt(i, ref itemIid, out var o);
                    var si = (IShellItem)o;
                    si.GetDisplayName(0 /*SIGDN_NORMALDISPLAY*/, out var pName);
                    si.GetDisplayName(0x80058000 /*SIGDN_FILESYSPATH*/, out var pPath);
                    var name = Marshal.PtrToStringUni(pName); Marshal.FreeCoTaskMem(pName);
                    var path = Marshal.PtrToStringUni(pPath); Marshal.FreeCoTaskMem(pPath);
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path)) items.Add(new JumpItem(name, path));
                    Marshal.ReleaseComObject(o);
                }
                catch { /* ファイル以外の項目などは飛ばす */ }
            }
            Marshal.ReleaseComObject(arr);
        }
        catch { }
        finally { if (lists != null) Marshal.ReleaseComObject(lists); }
        return items;
    }
}
