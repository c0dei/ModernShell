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
        ["Microsoft.Windows.RemoteDesktop"] = "mstsc.exe",
    };

    private static readonly Dictionary<string, string?> ShortcutExe = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ショートカットのリンク先 exe（読み取り結果を覚えておく）。</summary>
    private static string? TargetExe(string lnk)
    {
        lock (ShortcutExe)
        {
            if (ShortcutExe.TryGetValue(lnk, out var cached)) return cached;
            var t = Shortcut.Read(lnk)?.Target;
            t = t != null && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Environment.ExpandEnvironmentVariables(t) : null;
            ShortcutExe[lnk] = t;
            return t;
        }
    }

    /// <summary>ピン留めアプリと実行中ウィンドウ（AUMID / exe フルパス）が同じアプリか。</summary>
    public static bool Matches(AppEntry app, string? aumid, string? exePath)
    {
        // 1) AppUserModelID が一致（ストアアプリ、Chrome、VS Code など AUMID を持つアプリ）
        if (aumid != null && (string.Equals(aumid, app.Id, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(aumid, app.Aumid, StringComparison.OrdinalIgnoreCase))) return true;
        if (exePath == null) return false;
        var exeName = Path.GetFileName(exePath);
        // 一覧の項目が AUMID だけで実行ファイルを持たないとき（リモート デスクトップ接続など）は、ショートカットのリンク先を使う
        var appExe = app.ExePath ?? (app.ShortcutPath != null ? TargetExe(app.ShortcutPath) : null);
        if (appExe != null)
        {
            // 2) 実行ファイルが同じ
            if (string.Equals(appExe, exePath, StringComparison.OrdinalIgnoreCase)) return true;
            // 3) 同じ名前の exe（バージョン番号付きフォルダに入るアプリなど。ランチャー類の汎用名は除く）
            var pinnedName = Path.GetFileName(appExe);
            if (string.Equals(pinnedName, exeName, StringComparison.OrdinalIgnoreCase) && !GenericExe.Contains(exeName)) return true;
        }
        // 4) ランチャー経由（例: Update.exe --processStart Discord.exe）: 引数に実行中の exe 名が含まれる
        if (app.LaunchArgs != null && app.LaunchArgs.Contains(exeName, StringComparison.OrdinalIgnoreCase)) return true;
        // 5) パッケージ版アプリ: 実行ファイルが WindowsApps\<パッケージ名>_<版>_<CPU>__<発行元ID>\ にある
        if (app.IsStoreApp && PackageMatches(app.Id, exePath)) return true;
        // 6) 既知の組み合わせ
        return AumidToExe.TryGetValue(app.Id, out var exe) && string.Equals(exeName, exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Microsoft.WindowsTerminal_8wekyb3d8bbwe!App" と "...\WindowsApps\Microsoft.WindowsTerminal_1.21_x64__8wekyb3d8bbwe\WindowsTerminal.exe" を照合。</summary>
    private static bool PackageMatches(string aumid, string exePath)
    {
        int bang = aumid.IndexOf('!');
        var family = bang > 0 ? aumid.Substring(0, bang) : aumid;
        int us = family.LastIndexOf('_');
        if (us <= 0) return false;
        string name = family.Substring(0, us), publisher = family.Substring(us + 1);
        var dir = Path.GetFileName(Path.GetDirectoryName(exePath) ?? "") ?? "";
        // パッケージのルート直下とは限らないので、パスの各階層を見る
        foreach (var part in exePath.Split('\\'))
            if (part.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase) && part.EndsWith("__" + publisher, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static readonly HashSet<string> GenericExe = new(StringComparer.OrdinalIgnoreCase)
    {
        "update.exe", "launcher.exe", "setup.exe", "javaw.exe", "java.exe", "python.exe", "pythonw.exe", "electron.exe",
        "rundll32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "mshta.exe", "dllhost.exe",
    };
}

/// <summary>
/// プロセスの AppUserModelID（パッケージ版アプリのみ取得できる）。
/// パッケージ版アプリはウィンドウに AUMID を付けないことが多いため、ピン留めとの照合に使う。
/// </summary>
internal static class ProcessAumid
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint length, StringBuilder? id);

    private static readonly Dictionary<IntPtr, string?> Cache = new();

    public static string? Get(IntPtr hwnd)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(hwnd, out var cached)) return cached;
            string? result = null;
            try
            {
                Win32.GetWindowThreadProcessId(hwnd, out var pid);
                var h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        uint len = 256;
                        var sb = new StringBuilder((int)len);
                        if (GetApplicationUserModelId(h, ref len, sb) == 0 && sb.Length > 0) result = sb.ToString();
                    }
                    finally { Win32.CloseHandle(h); }
                }
            }
            catch { }
            if (Cache.Count > 2000) Cache.Clear();
            Cache[hwnd] = result;
            return result;
        }
    }
}
