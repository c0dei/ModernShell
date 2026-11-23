using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ModernShell.Core;

/// <summary>仮想デスクトップ 1 つ分。</summary>
public sealed record DesktopInfo(Guid Id, string Name, string? Wallpaper, bool IsCurrent);

/// <summary>
/// 仮想デスクトップの一覧（読み取り）と切り替え。
/// Windows は仮想デスクトップを操作する公開 API を提供していない（内部 COM はビルドごとに変わる）ので、
/// 一覧はレジストリから読み、切り替えは Windows のショートカットキー（Ctrl+Win+←/→、Ctrl+Win+D）で行う。
/// </summary>
public static class VirtualDesktops
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    public static List<DesktopInfo> List()
    {
        var result = new List<DesktopInfo>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            var ids = key?.GetValue("VirtualDesktopIDs") as byte[];
            var current = key?.GetValue("CurrentVirtualDesktop") as byte[];
            var currentId = current is { Length: >= 16 } ? new Guid(current.AsSpan(0, 16)) : Guid.Empty;
            string? globalWallpaper = GlobalWallpaper();
            if (ids == null || ids.Length < 16)
            {
                // デスクトップを増やしたことがなければ一覧は無い（デスクトップは 1 つだけ）
                result.Add(new DesktopInfo(Guid.Empty, L.T("デスクトップ 1"), globalWallpaper, true));
                return result;
            }
            for (int i = 0; i + 16 <= ids.Length; i += 16)
            {
                var id = new Guid(ids.AsSpan(i, 16));
                using var dk = key!.OpenSubKey($@"Desktops\{{{id}}}");
                var name = dk?.GetValue("Name") as string;
                var wall = dk?.GetValue("Wallpaper") as string;
                result.Add(new DesktopInfo(id, string.IsNullOrWhiteSpace(name) ? L.F("デスクトップ {0}", i / 16 + 1) : name!,
                    !string.IsNullOrEmpty(wall) && File.Exists(wall) ? wall : globalWallpaper,
                    id == currentId || (currentId == Guid.Empty && i == 0)));
            }
        }
        catch (Exception ex) { Log.Error("virtual desktops", ex); }
        if (result.Count == 0) result.Add(new DesktopInfo(Guid.Empty, L.T("デスクトップ 1"), GlobalWallpaper(), true));
        return result;
    }

    private static string? GlobalWallpaper()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (k?.GetValue("WallPaper") is string w && File.Exists(w)) return w;
        }
        catch { }
        // 元の画像が消されていても、Windows が変換して保存している壁紙が残っている
        var transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : null;
    }

    private const ushort VK_CONTROL = 0x11, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_D = 0x44, VK_F4 = 0x73;

    /// <summary>index 番目のデスクトップへ切り替える（今の位置から左右キーを必要な回数送る）。</summary>
    public static void SwitchTo(int index)
    {
        var list = List();
        int current = list.FindIndex(d => d.IsCurrent);
        if (current < 0 || index == current || index < 0 || index >= list.Count) return;
        ushort arrow = index > current ? VK_RIGHT : VK_LEFT;
        var keys = new List<(ushort, bool)> { (Native.Win32.VK_LWIN, false), (VK_CONTROL, false) };
        for (int i = 0; i < Math.Abs(index - current); i++) { keys.Add((arrow, false)); keys.Add((arrow, true)); }
        keys.Add((VK_CONTROL, true));
        keys.Add((Native.Win32.VK_LWIN, true));
        Input.Send(keys.ToArray());
    }

    /// <summary>今のデスクトップを閉じる（Ctrl+Win+F4）。開いていたウィンドウは隣のデスクトップへ移る（Windows と同じ）。</summary>
    private static void CloseCurrent() =>
        Input.Send((Native.Win32.VK_LWIN, false), (VK_CONTROL, false), (VK_F4, false), (VK_F4, true), (VK_CONTROL, true), (Native.Win32.VK_LWIN, true));

    // ---- 内部 COM（切り替えずに削除する）。Windows 11 24H2 以降（26100.2605〜）の定義。ビルドで変わるので、合わなければ呼ばずに従来の方法へ戻る ----
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderCom
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr obj);
    }

    /// <summary>ポインタとして渡すだけなので、メソッドは宣言しない。</summary>
    [ComImport, Guid("3F07F4BE-B107-441A-AF0F-39D82529072C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopCom { }

    [ComImport, Guid("53F5CA0B-158F-4124-900C-057158060B27"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManagerInternalCom
    {
        [PreserveSig] int GetCount(out uint count);                                          // 0
        [PreserveSig] int MoveViewToDesktop(IntPtr view, IVirtualDesktopCom desktop);        // 1
        [PreserveSig] int CanViewMoveDesktops(IntPtr view, out int can);                     // 2
        [PreserveSig] int GetCurrentDesktop(out IVirtualDesktopCom desktop);                 // 3
        [PreserveSig] int GetDesktops(out IntPtr array);                                     // 4
        [PreserveSig] int GetAdjacentDesktop(IVirtualDesktopCom from, uint direction, out IVirtualDesktopCom desktop); // 5
        [PreserveSig] int SwitchDesktop(IVirtualDesktopCom desktop);                         // 6
        [PreserveSig] int SwitchDesktopAndMoveForegroundView(IVirtualDesktopCom desktop);    // 7
        [PreserveSig] int CreateDesktop(out IVirtualDesktopCom desktop);                     // 8
        [PreserveSig] int MoveDesktop(IVirtualDesktopCom desktop, uint index);               // 9
        [PreserveSig] int RemoveDesktop(IVirtualDesktopCom destroy, IVirtualDesktopCom fallback); // 10
        [PreserveSig] int FindDesktop(ref Guid id, out IVirtualDesktopCom desktop);          // 11
    }

    private static bool _directFailed; // 一度合わないと分かったら、このセッションでは試さない

    /// <summary>
    /// index 番目のデスクトップを、切り替えずにその場で削除する（内部 COM の RemoveDesktop）。
    /// 開いていたウィンドウは隣（左、いちばん左なら右）へ移る。呼び出し前後で数と ID を確かめ、合わなければ false（従来の方法へ戻る）。
    /// </summary>
    private static async System.Threading.Tasks.Task<bool> TryRemoveDirect(List<DesktopInfo> list, int index)
    {
        if (_directFailed || list.Any(d => d.Id == Guid.Empty)) return false;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239"));
            if (type == null || Activator.CreateInstance(type) is not IServiceProviderCom shell) return false;
            var service = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
            var iid = typeof(IVirtualDesktopManagerInternalCom).GUID;
            if (shell.QueryService(ref service, ref iid, out var ptr) != 0 || ptr == IntPtr.Zero) { _directFailed = true; return false; }
            IVirtualDesktopManagerInternalCom mgr;
            try { mgr = (IVirtualDesktopManagerInternalCom)Marshal.GetObjectForIUnknown(ptr); }
            finally { Marshal.Release(ptr); }

            // 定義が合っているかの確認: 数が一覧と同じで、ID から削除する分と移動先が引ける
            if (mgr.GetCount(out uint count) != 0 || count != list.Count) { _directFailed = true; return false; }
            var removeId = list[index].Id;
            var fallbackId = list[index > 0 ? index - 1 : 1].Id;
            if (mgr.FindDesktop(ref removeId, out var destroy) != 0 || destroy == null) { _directFailed = true; return false; }
            if (mgr.FindDesktop(ref fallbackId, out var fallback) != 0 || fallback == null) { _directFailed = true; return false; }

            if (mgr.RemoveDesktop(destroy, fallback) != 0) { _directFailed = true; return false; }
            for (int i = 0; i < 20; i++)
            {
                await System.Threading.Tasks.Task.Delay(50);
                if (List().Count < list.Count) return true;
            }
            _directFailed = true; // 呼べたが何も起きなかった: 定義が合っていない
            return false;
        }
        catch (Exception ex)
        {
            Log.Info("direct desktop remove unavailable: " + ex.Message);
            _directFailed = true;
            return false;
        }
    }

    /// <summary>
    /// index 番目のデスクトップを削除する。まず切り替えずに削除する（内部 COM）。
    /// 使えないときは、Windows のキー操作が「今のデスクトップを閉じる」しかできないので、別のデスクトップなら一度そこへ切り替えて閉じ、元のデスクトップへ戻る。
    /// </summary>
    public static async System.Threading.Tasks.Task Remove(int index)
    {
        var list = List();
        if (list.Count <= 1 || index < 0 || index >= list.Count) return;
        if (await TryRemoveDirect(list, index)) return;
        int current = list.FindIndex(d => d.IsCurrent);
        if (index == current) { CloseCurrent(); return; }
        SwitchTo(index);
        await WaitCurrent(index);
        CloseCurrent();
        // 閉じたあとは隣（左、いちばん左なら右）へ移る。元のデスクトップの番号は、消したものより後ろなら 1 つ詰まる
        int back = current > index ? current - 1 : current;
        await System.Threading.Tasks.Task.Delay(150);
        for (int i = 0; i < 20 && List().Count == list.Count; i++) await System.Threading.Tasks.Task.Delay(50);
        SwitchTo(back);
    }

    /// <summary>切り替えが終わる（レジストリの「今のデスクトップ」が変わる）まで少し待つ。</summary>
    private static async System.Threading.Tasks.Task WaitCurrent(int index)
    {
        for (int i = 0; i < 20; i++)
        {
            await System.Threading.Tasks.Task.Delay(50);
            if (List().FindIndex(d => d.IsCurrent) == index) break;
        }
        await System.Threading.Tasks.Task.Delay(100);
    }

    /// <summary>新しいデスクトップを作って切り替える（Ctrl+Win+D）。</summary>
    public static void Create() =>
        Input.Send((Native.Win32.VK_LWIN, false), (VK_CONTROL, false), (VK_D, false), (VK_D, true), (VK_CONTROL, true), (Native.Win32.VK_LWIN, true));
}
