using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>
/// Windows 標準の右クリックメニュー（エクスプローラーと同じもの）を読み取り、ModernShell のメニュー項目に変換する。
/// 表示は自前の Fluent メニュー、実行は Windows に任せる（IContextMenu::InvokeCommand）ので、
/// 「開く」「管理者として実行」「スタートにピン留めする」や、他アプリが追加した項目（7-Zip など）も Windows と同じ動きになる。
/// ※ 「タスクバーにピン留めする」は Windows が外部アプリからの実行を塞いでいるため出ない／動かないことがある。
/// </summary>
public static class ShellContextMenu
{
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, StringBuilder name, uint cch);
    }

    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, StringBuilder name, uint cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        public string? lpParametersW;
        public string? lpDirectoryW;
        public string? lpTitleW;
        public POINT ptInvoke;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MENUITEMINFO
    {
        public int cbSize;
        public uint fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMenuItemInfo(IntPtr h, uint item, bool byPosition, ref MENUITEMINFO mii);

    private const uint MIIM_STATE = 0x1, MIIM_ID = 0x2, MIIM_SUBMENU = 0x4, MIIM_STRING = 0x40, MIIM_BITMAP = 0x80, MIIM_FTYPE = 0x100;
    private const uint MFT_SEPARATOR = 0x800, MFS_DISABLED = 0x3, MFS_DEFAULT = 0x1000;
    private const uint CMF_NORMAL = 0, CMF_EXTENDEDVERBS = 0x100;
    private const uint FirstId = 1, LastId = 0x7FFF;
    private const uint WM_INITMENUPOPUP = 0x0117;

    private static readonly Guid BHID_SFUIObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");

    /// <summary>直近に作ったメニュー。次のメニューを作るときに後始末する（メニューの項目から実行するまで生かしておく必要がある）。</summary>
    private static (IntPtr hmenu, object cm)? _current;

    /// <summary>
    /// パス（ファイル / .lnk / "shell:AppsFolder\AUMID"）の標準メニューを項目一覧として返す。
    /// shift を true にすると Shift+右クリック相当の拡張項目（「パスのコピー」など）も含める。
    /// </summary>
    public static List<MenuItemSpec> For(string parsingName, IntPtr owner, bool shift = false)
    {
        Release();
        var result = new List<MenuItemSpec>();
        IShellItem? item = null;
        try
        {
            var iidItem = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iidItem, out item) != 0 || item == null) return result;
            var bhid = BHID_SFUIObject;
            var iidCm = typeof(IContextMenu).GUID;
            if (item.BindToHandler(IntPtr.Zero, ref bhid, ref iidCm, out var pcm) != 0 || pcm == IntPtr.Zero) return result;
            var cm = Marshal.GetObjectForIUnknown(pcm);
            Marshal.Release(pcm);

            var hmenu = CreatePopupMenu();
            ((IContextMenu)cm).QueryContextMenu(hmenu, 0, FirstId, LastId, CMF_NORMAL | (shift ? CMF_EXTENDEDVERBS : 0));
            _current = (hmenu, cm);
            Read(hmenu, cm, owner, result);
        }
        catch (Exception ex) { Log.Error("shell context menu", ex); }
        finally { if (item != null) Marshal.ReleaseComObject(item); }
        return result;
    }

    private static void Read(IntPtr hmenu, object cm, IntPtr owner, List<MenuItemSpec> into)
    {
        int count = GetMenuItemCount(hmenu);
        bool lastWasSeparator = true;
        for (uint i = 0; i < count; i++)
        {
            var buf = Marshal.AllocHGlobal(1024);
            try
            {
                var mii = new MENUITEMINFO
                {
                    cbSize = Marshal.SizeOf<MENUITEMINFO>(),
                    fMask = MIIM_FTYPE | MIIM_STATE | MIIM_ID | MIIM_SUBMENU | MIIM_STRING | MIIM_BITMAP,
                    dwTypeData = buf, cch = 511,
                };
                if (!GetMenuItemInfo(hmenu, i, true, ref mii)) continue;

                if ((mii.fType & MFT_SEPARATOR) != 0)
                {
                    if (!lastWasSeparator) into.Add(MenuItemSpec.Separator);
                    lastWasSeparator = true;
                    continue;
                }
                var text = CleanText(Marshal.PtrToStringUni(buf) ?? "");
                if (text.Length == 0) continue;

                var icon = MenuBitmap(mii.hbmpItem);
                bool enabled = (mii.fState & MFS_DISABLED) == 0;
                bool isDefault = (mii.fState & MFS_DEFAULT) != 0;

                if (mii.hSubMenu != IntPtr.Zero)
                {
                    // 「送る」などは開く直前に中身が作られるので、WM_INITMENUPOPUP を送ってから読む
                    var sub = mii.hSubMenu;
                    var index = i;
                    into.Add(new MenuItemSpec("", text, () => { }) { Icon = icon, Enabled = enabled, Children = () =>
                    {
                        try { if (cm is IContextMenu2 cm2) cm2.HandleMenuMsg(WM_INITMENUPOPUP, sub, (IntPtr)index); } catch { }
                        var list = new List<MenuItemSpec>();
                        Read(sub, cm, owner, list);
                        return list;
                    } });
                }
                else
                {
                    uint id = mii.wID;
                    into.Add(new MenuItemSpec("", text, () => Invoke(cm, id - FirstId, owner)) { Icon = icon, Enabled = enabled, IsDefault = isDefault });
                }
                lastWasSeparator = false;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        if (into.Count > 0 && into[^1].IsSeparator) into.RemoveAt(into.Count - 1);
    }

    /// <summary>"開く(&amp;O)" → "開く(O)"、"&amp;Open" → "Open"。タブ以降（ショートカットキー表示）は落とす。</summary>
    private static string CleanText(string s)
    {
        int tab = s.IndexOf('\t');
        if (tab >= 0) s = s.Substring(0, tab);
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '&')
            {
                if (i + 1 < s.Length && s[i + 1] == '&') { sb.Append('&'); i++; }
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString().Trim();
    }

    private static ImageSource? MenuBitmap(IntPtr hbmp)
    {
        // 1–11 は HBMMENU_* の特殊値（オーナー描画など）なので画像としては使えない
        if ((long)hbmp <= 11) return null;
        try { return IconHelper.FromHBitmap(hbmp); } catch { return null; }
    }

    private static void Invoke(object cm, uint offset, IntPtr owner)
    {
        try
        {
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = 0x00004000 /*CMIC_MASK_UNICODE*/,
                hwnd = owner,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                nShow = 1 /*SW_SHOWNORMAL*/,
            };
            ((IContextMenu)cm).InvokeCommand(ref info);
        }
        catch (Exception ex) { Log.Error("shell menu invoke", ex); }
        finally { Release(); }
    }

    private static void Release()
    {
        if (_current is not { } c) return;
        _current = null;
        try { DestroyMenu(c.hmenu); } catch { }
        try { Marshal.ReleaseComObject(c.cm); } catch { }
    }
}
