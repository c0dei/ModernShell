using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ModernShell.Core;

/// <summary>ショートカット (.lnk) の中身。</summary>
public sealed record ShortcutInfo(string? Target, string? Arguments, string? Aumid)
{
    /// <summary>ショートカットに指定されたアイコン（ファイルと番号）。未指定なら null（リンク先のアイコンを使う）。</summary>
    public string? IconFile { get; init; }
    public int IconIndex { get; init; }
    /// <summary>.lnk ファイル自体のパス。</summary>
    public string? Path { get; init; }
}

/// <summary>
/// .lnk を読む（リンク先・引数・AppUserModelID）。
/// AUMID は、アプリが自分のウィンドウに付ける ID と同じものがショートカットにも書かれていることが多く、
/// 「ピン留めしたアプリ」と「実行中のウィンドウ」を同じアプリとして結び付ける一番確実な手がかりになる。
/// </summary>
public static class Shortcut
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

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

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY k);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

    public static ShortcutInfo? Read(string lnk)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var sl = (IShellLinkW)link;

            var path = new StringBuilder(1024);
            sl.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(2048);
            try { sl.GetArguments(args, args.Capacity); } catch { }
            var iconPath = new StringBuilder(1024);
            int iconIndex = 0;
            try { sl.GetIconLocation(iconPath, iconPath.Capacity, out iconIndex); } catch { }
            var iconFile = iconPath.Length > 0 ? Environment.ExpandEnvironmentVariables(iconPath.ToString()) : null;

            string? aumid = null;
            if (link is IPropertyStore store)
            {
                var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                if (store.GetValue(ref key, out var pv) == 0)
                {
                    if (pv.vt == 31 /*VT_LPWSTR*/) aumid = Marshal.PtrToStringUni(pv.p);
                    PropVariantClear(ref pv);
                }
            }

            var target = Environment.ExpandEnvironmentVariables(path.ToString());
            return new ShortcutInfo(target.Length > 0 ? target : null, args.Length > 0 ? args.ToString() : null,
                string.IsNullOrEmpty(aumid) ? null : aumid) { Path = lnk, IconFile = iconFile, IconIndex = iconIndex };
        }
        catch { return null; }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }
}
