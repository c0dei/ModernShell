using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace ModernShell.Core;

/// <summary>ファイル検索ソフト「Everything」（voidtools）を見つけて、ウィンドウを開く。</summary>
public static class Everything
{
    private static string? _exe;
    private static bool _searched;

    /// <summary>Everything の実行ファイル（入っていなければ null）。見つけた場所は覚えておく。</summary>
    public static string? FindExe()
    {
        if (_exe != null && File.Exists(_exe)) return _exe;
        if (_searched && _exe == null && (DateTime.UtcNow - _lastSearch).TotalSeconds < 30) return null;
        _searched = true;
        _lastSearch = DateTime.UtcNow;
        _exe = Locate();
        return _exe;
    }

    private static DateTime _lastSearch = DateTime.MinValue;

    public static bool IsInstalled => FindExe() != null;

    private static string? Locate()
    {
        // 登録されている場所（App Paths）→ よくあるインストール先 → 起動中のプロセス
        // 管理者として動いているときは、ユーザーが書き換えられる場所（HKCU・LocalAppData・動いているプロセスの場所）の exe は信用しない
        // （そこに置かれた偽物を、管理者の権限で見つけて起動してしまわないように）
        bool trustUserLocations = !Launcher.IsElevated;
        foreach (var hive in trustUserLocations ? new[] { Registry.LocalMachine, Registry.CurrentUser } : new[] { Registry.LocalMachine })
        {
            foreach (var name in new[] { "Everything.exe", "Everything64.exe" })
            {
                try
                {
                    using var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                    if (k?.GetValue(null) as string is { Length: > 0 } p && File.Exists(p = p.Trim('"'))) return p;
                }
                catch { }
            }
        }
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        if (trustUserLocations)
            roots = roots.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")).ToArray();
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var dir in new[] { "Everything", "Everything 1.5a", "Everything (Alpha)" })
            {
                var p = Path.Combine(root, dir, "Everything.exe");
                if (File.Exists(p)) return p;
            }
        }
        if (!trustUserLocations) return null;
        try
        {
            foreach (var name in new[] { "Everything", "Everything64", "Everything1.5a" })
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    using (proc)
                        if (proc.MainModule?.FileName is { } f && File.Exists(f)) return f;
                }
        }
        catch { }
        return null;
    }

    /// <summary>Everything のウィンドウを開く（起動していればそのウィンドウを前面に、していなければ起動する）。入っていなければ false。</summary>
    public static bool Open()
    {
        var exe = FindExe();
        if (exe == null) return false;
        try
        {
            Launcher.Open(exe, null, Path.GetDirectoryName(exe)); // 普通の権限で起動する（管理者のまま起動しない）
            return true;
        }
        catch (Exception ex) { Log.Error("everything", ex); return false; }
    }

    /// <summary>Everything が起動していなければ、画面に出さずに（トレイで）起動する。入っていなければ false。</summary>
    public static bool EnsureRunning()
    {
        var exe = FindExe();
        if (exe == null) return false;
        try
        {
            foreach (var name in new[] { "Everything", "Everything64", "Everything1.5a" })
            {
                var procs = Process.GetProcessesByName(name);
                bool running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (running) return true;
            }
            Launcher.Open(exe, "-startup", Path.GetDirectoryName(exe));
            return true;
        }
        catch (Exception ex) { Log.Error("everything start", ex); return false; }
    }

    /// <summary>Everything のウィンドウを、この言葉で検索した状態で開く。入っていなければ false。</summary>
    public static bool OpenWithQuery(string query)
    {
        var exe = FindExe();
        if (exe == null) return false;
        try
        {
            Launcher.Open(exe, "-search \"" + query.Replace("\"", "\\\"") + "\"", Path.GetDirectoryName(exe));
            return true;
        }
        catch (Exception ex) { Log.Error("everything", ex); return false; }
    }

    /// <summary>前面にあるのが Everything のウィンドウなら、ウィンドウを閉じて true（クリックで開閉するトグル用）。</summary>
    public static bool CloseIfForeground()
    {
        try
        {
            var h = ModernShell.Native.Win32.GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            var sb = new System.Text.StringBuilder(64);
            ModernShell.Native.Win32.GetClassName(h, sb, sb.Capacity);
            if (!sb.ToString().StartsWith("EVERYTHING", StringComparison.OrdinalIgnoreCase)) return false;
            ModernShell.Native.Win32.PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE（常駐の設定ならトレイへ、そうでなければ終了。次は Open() で起動し直す）
            return true;
        }
        catch { return false; }
    }
}
