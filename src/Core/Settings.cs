using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModernShell.Core;

public enum ThemeMode { System, Light, Dark }
public enum BackdropKind { Mica, Acrylic, Solid }
public enum TaskbarAlignment { Center, Left }
public enum TaskbarEdge { Bottom, Top }

public sealed class Settings
{
    // --- スタートメニュー ---
    /// <summary>Win キー / スタートボタンを横取りして独自スタートメニューを表示する。</summary>
    public bool ReplaceStartMenu { get; set; } = true;
    /// <summary>純正タスクバー使用時、純正スタートボタンのクリックも横取りする。</summary>
    public bool InterceptNativeStartButton { get; set; } = true;
    public int PinnedColumns { get; set; } = 6;
    public bool ShowAllAppsFirst { get; set; } = false;
    public List<string> PinnedStart { get; set; } = new();

    // --- タスクバー ---
    /// <summary>純正タスクバーを隠し、独自タスクバー（AppBar）を表示する。</summary>
    public bool UseCustomTaskbar { get; set; } = false;
    public TaskbarAlignment TaskbarAlignment { get; set; } = TaskbarAlignment.Center;
    public TaskbarEdge TaskbarEdge { get; set; } = TaskbarEdge.Bottom;
    public int TaskbarHeight { get; set; } = 48;
    public bool GroupTaskbarButtons { get; set; } = true;
    public bool ShowTaskbarLabels { get; set; } = false;
    public bool ShowSeconds { get; set; } = false;
    public List<string> PinnedTaskbar { get; set; } = new();

    // --- 外観 ---
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public BackdropKind Backdrop { get; set; } = BackdropKind.Acrylic;
    /// <summary>空ならシステムのアクセントカラー。#RRGGBB 形式。</summary>
    public string AccentColor { get; set; } = "";
    public double CornerRadius { get; set; } = 8;

    // --- 一般 ---
    public bool RunAtStartup { get; set; } = false;

    // ---------------------------------------------------------------
    [JsonIgnore]
    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModernShell");
    [JsonIgnore]
    public static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public event Action? Changed;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception ex) { Log.Error("settings load", ex); }

        var s = new Settings();
        s.PinnedStart.AddRange(Defaults.PinnedStart);
        s.PinnedTaskbar.AddRange(Defaults.PinnedTaskbar);
        return s;
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Error("settings save", ex); }
        Changed?.Invoke();
    }

    private static class Defaults
    {
        // shell:AppsFolder の解析名 (AppUserModelID)。存在しなければ無視される。
        public static readonly string[] PinnedStart =
        {
            "Microsoft.Windows.Explorer",
            "MSEdge",
            "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
            "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
            "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
        };
        public static readonly string[] PinnedTaskbar =
        {
            "Microsoft.Windows.Explorer",
            "MSEdge",
        };
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    private static string PathName => System.IO.Path.Combine(Settings.Directory, "modernshell.log");

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Error(string where, Exception ex) => Write("ERROR", $"{where}: {ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Settings.Directory);
                var fi = new FileInfo(PathName);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
            }
        }
        catch { /* ログ失敗は握りつぶす */ }
    }
}
