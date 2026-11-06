using System;
using System.Collections.Generic;
using System.IO;
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
                result.Add(new DesktopInfo(Guid.Empty, "デスクトップ 1", globalWallpaper, true));
                return result;
            }
            for (int i = 0; i + 16 <= ids.Length; i += 16)
            {
                var id = new Guid(ids.AsSpan(i, 16));
                using var dk = key!.OpenSubKey($@"Desktops\{{{id}}}");
                var name = dk?.GetValue("Name") as string;
                var wall = dk?.GetValue("Wallpaper") as string;
                result.Add(new DesktopInfo(id, string.IsNullOrWhiteSpace(name) ? $"デスクトップ {i / 16 + 1}" : name!,
                    !string.IsNullOrEmpty(wall) && File.Exists(wall) ? wall : globalWallpaper,
                    id == currentId || (currentId == Guid.Empty && i == 0)));
            }
        }
        catch (Exception ex) { Log.Error("virtual desktops", ex); }
        if (result.Count == 0) result.Add(new DesktopInfo(Guid.Empty, "デスクトップ 1", GlobalWallpaper(), true));
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

    private const ushort VK_CONTROL = 0x11, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_D = 0x44;

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

    /// <summary>新しいデスクトップを作って切り替える（Ctrl+Win+D）。</summary>
    public static void Create() =>
        Input.Send((Native.Win32.VK_LWIN, false), (VK_CONTROL, false), (VK_D, false), (VK_D, true), (VK_CONTROL, true), (Native.Win32.VK_LWIN, true));
}
