using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using ModernShell.Native;

namespace ModernShell.Core;

/// <summary>前面化制限 (ForegroundLockTimeout) を回避して確実にウィンドウを前に出す。</summary>
internal static class Foregrounder
{
    public static void Force(IntPtr h)
    {
        if (Win32.SetForegroundWindow(h)) return;
        var fg = Win32.GetForegroundWindow();
        uint fgThread = Win32.GetWindowThreadProcessId(fg, out _);
        uint me = Win32.GetCurrentThreadId();
        if (fgThread != 0 && fgThread != me)
        {
            Win32.AttachThreadInput(me, fgThread, true);
            Win32.BringWindowToTop(h);
            Win32.SetForegroundWindow(h);
            Win32.AttachThreadInput(me, fgThread, false);
        }
        if (Win32.GetForegroundWindow() != h)
        {
            // 最終手段: ALT を一度押下して「直前に入力があった」状態にする
            Input.Tap(Win32.VK_MENU);
            Win32.SetForegroundWindow(h);
        }
    }
}

/// <summary>既知 AUMID → exe 名（ピン留めと実行中ウィンドウの突き合わせ用）。</summary>
internal static class KnownApps
{
    private static readonly Dictionary<string, string> AumidToExe = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Windows.Explorer"] = "explorer.exe",
        ["MSEdge"] = "msedge.exe",
        ["Chrome"] = "chrome.exe",
        ["Microsoft.Office.WINWORD.EXE.15"] = "winword.exe",
        ["Microsoft.Office.EXCEL.EXE.15"] = "excel.exe",
        ["Microsoft.Office.POWERPNT.EXE.15"] = "powerpnt.exe",
        ["Microsoft.Office.OUTLOOK.EXE.15"] = "outlook.exe",
    };

    /// <summary>ピン留めアプリと実行中ウィンドウ（AUMID / exe フルパス）が同じアプリか。</summary>
    public static bool Matches(AppEntry app, string? aumid, string? exePath)
    {
        if (aumid != null && string.Equals(aumid, app.Id, StringComparison.OrdinalIgnoreCase)) return true;
        if (exePath == null) return false;
        if (app.ExePath != null) return string.Equals(app.ExePath, exePath, StringComparison.OrdinalIgnoreCase);
        return AumidToExe.TryGetValue(app.Id, out var exe)
               && string.Equals(Path.GetFileName(exePath), exe, StringComparison.OrdinalIgnoreCase);
    }
}
