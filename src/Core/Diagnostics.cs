using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ModernShell.Core;

/// <summary>
/// 起動の安定化（セーフ モード）と、診断情報の書き出し。
/// 起動して間もなく（<see cref="StableAfterSeconds"/> 秒以内に）落ちる状態が続いたら、次の起動は自動でセーフ モードにする。
/// セーフ モード: 純正タスクバーはそのまま・ウィジェットとアニメーションはオフ・設定は保存しない・デバッグ ログはオン。
/// </summary>
public static class Diagnostics
{
    public const int StableAfterSeconds = 20;
    private const int CrashesBeforeSafeMode = 2;

    /// <summary>今回の起動がセーフ モードか。</summary>
    public static bool SafeMode { get; private set; }
    /// <summary>セーフ モードになった理由（表示用）。</summary>
    public static string SafeReason { get; private set; } = "";

    private static string StatePath => Path.Combine(Settings.Directory, "startup.state");

    private static int ReadCount()
    {
        try { return int.TryParse(File.ReadAllText(StatePath).Trim(), out var n) ? n : 0; }
        catch { return 0; }
    }

    private static void WriteCount(int n)
    {
        try { Directory.CreateDirectory(Settings.Directory); File.WriteAllText(StatePath, n.ToString()); }
        catch { }
    }

    /// <summary>
    /// 起動の最初に呼ぶ。続けて落ちていた（または --safe が指定された）ならセーフ モードにする。
    /// forceNormal（--normal）なら、記録を消して通常起動する。
    /// </summary>
    public static void BeginStartup(bool forceSafe, bool forceNormal)
    {
        int crashes = forceNormal ? 0 : ReadCount();
        if (forceSafe) { SafeMode = true; SafeReason = "--safe"; }
        else if (crashes >= CrashesBeforeSafeMode) { SafeMode = true; SafeReason = $"{crashes} consecutive early exits"; }
        WriteCount(crashes + 1);
    }

    /// <summary>起動が安定した（落ちずに一定時間たった）、または正常に終了したとき。次回は通常起動にする。</summary>
    public static void MarkStable() => WriteCount(0);

    /// <summary>セーフ モードの設定の上書き（メモリ上だけ。保存はしない）。</summary>
    public static void ApplySafeOverrides(Settings s)
    {
        Settings.SafeMode = true;
        s.UseCustomTaskbar = false;
        s.ShowWidgets = false;
        s.AnimateStartMenu = s.AnimatePreviews = s.AnimateTrayOverflow = s.AnimateMenus = false;
        s.DebugLog = true;
    }

    // ---------------------------------------------------------------

    /// <summary>診断情報（環境・設定の要点）をテキストにする。</summary>
    public static string BuildReport(Settings s)
    {
        var sb = new StringBuilder();
        void Line(string k, object? v) => sb.AppendLine($"{k}: {v}");
        Line("ModernShell", typeof(Diagnostics).Assembly.GetName().Version?.ToString(3));
        Line("time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        Line("OS", Environment.OSVersion.VersionString);
        Line(".NET", Environment.Version);
        Line("64bit", Environment.Is64BitProcess);
        Line("elevated", Launcher.IsElevated);
        Line("safe mode", SafeMode ? "yes (" + SafeReason + ")" : "no");
        Line("startup state (consecutive early exits)", ReadCount());
        try { using var p = Process.GetCurrentProcess(); Line("uptime", DateTime.Now - p.StartTime); Line("working set MB", p.WorkingSet64 / 1048576); }
        catch { }
        Line("render tier", System.Windows.Media.RenderCapability.Tier >> 16);
        Line("render mode (GPU setting)", s.UseGpuRendering ? "GPU" : "software");
        Line("no GPU detected", Anim.NoGpu);
        Line("light mode", Anim.LightMode);
        Line("language", s.Language);
        try
        {
            int i = 0;
            foreach (var m in System.Windows.Forms.Screen.AllScreens)
                Line($"monitor {i++}", $"{m.Bounds.Width}x{m.Bounds.Height} at {m.Bounds.X},{m.Bounds.Y} primary={m.Primary} work={m.WorkingArea.Width}x{m.WorkingArea.Height}");
        }
        catch { }
        Line("taskbar", $"custom={s.UseCustomTaskbar} edge={s.TaskbarEdge} style={s.TaskbarStyle} height={s.TaskbarHeight} align={s.TaskbarAlignment}");
        Line("start menu", $"replace={s.ReplaceStartMenu} position={s.StartMenuPosition}");
        Line("debug log", s.DebugLog);
        sb.AppendLine();
        sb.AppendLine("Note: settings.json may contain app names, folder paths and the weather location.");
        return sb.ToString();
    }

    /// <summary>診断情報を zip にまとめて書き出す（診断情報・設定・ログの末尾）。</summary>
    public static void Export(Settings s, string zipPath)
    {
        s.SaveQuiet();
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void AddText(string name, string text)
        {
            var e = zip.CreateEntry(name);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        AddText("diagnostics.txt", BuildReport(s));
        try { if (File.Exists(Settings.FilePath)) AddText("settings.json", File.ReadAllText(Settings.FilePath)); } catch { }
        try
        {
            var log = Path.Combine(Settings.Directory, "modernshell.log");
            if (File.Exists(log))
            {
                using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long keep = Math.Min(fs.Length, 1_000_000);
                fs.Seek(-keep, SeekOrigin.End);
                using var r = new StreamReader(fs, Encoding.UTF8);
                AddText("modernshell.log", r.ReadToEnd());
            }
            else AddText("modernshell.log", "(no log: the debug log is off. Turn on \"Write a debug log\" in settings, reproduce the problem, and export again.)");
        }
        catch (Exception ex) { AddText("modernshell.log", "(could not read the log: " + ex.Message + ")"); }
    }
}
