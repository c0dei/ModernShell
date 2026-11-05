using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModernShell.Core;

public enum ThemeMode { System, Light, Dark }
public enum BackdropKind { Mica, Acrylic, Solid }
public enum TaskbarAlignment { Center, Left }
public enum TaskbarEdge { Bottom, Top }
/// <summary>スタート・検索・タスクビューの位置。WithApps: アプリと一緒（中央 or 左） / LeftEdge: 常に左端（3 分割）</summary>
public enum StartButtonPosition { WithApps, LeftEdge }
public enum BackdropEngine { Auto, SystemBackdrop, AccentBlur }
/// <summary>タスクバーのピン留めの出どころ。Windows: Windows のタスクバーと同じ内容・並び順（読み取り） / ModernShell: 独自に管理</summary>
public enum TaskbarPinSource { Windows, ModernShell }
/// <summary>Band: 画面端に密着した帯 / Floating: 画面端から浮いた島（アイランド、Mac のドック風）</summary>
public enum TaskbarStyle { Band, Floating }

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
    public StartButtonPosition StartButtonPosition { get; set; } = StartButtonPosition.WithApps;
    public bool ShowSearchButton { get; set; } = true;
    public bool ShowTaskViewButton { get; set; } = true;
    /// <summary>タスクバーに常に表示するトレイアイコン（ManagedShell の NotifyIcon.Identifier）。それ以外は「^」の中。</summary>
    public List<string> PinnedTrayIcons { get; set; } = new();
    public TaskbarStyle TaskbarStyle { get; set; } = TaskbarStyle.Band;
    /// <summary>アイランド型のとき画面端からの余白 (DIP)。</summary>
    public int IslandMargin { get; set; } = 8;
    /// <summary>アイランド型のときホバーでアイコンを拡大する。</summary>
    public bool MagnifyOnHover { get; set; } = true;
    public bool GroupTaskbarButtons { get; set; } = true;
    public bool ShowTaskbarLabels { get; set; } = false;
    public bool ShowSeconds { get; set; } = false;
    public List<string> PinnedTaskbar { get; set; } = new();
    public TaskbarPinSource TaskbarPinSource { get; set; } = TaskbarPinSource.Windows;
    /// <summary>タスクバーのアプリアイコンの大きさ (px)。</summary>
    public int TaskbarIconSize { get; set; } = 24;

    // --- 外観 ---
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public BackdropKind Backdrop { get; set; } = BackdropKind.Acrylic;
    /// <summary>空ならシステムのアクセントカラー。#RRGGBB 形式。</summary>
    public string AccentColor { get; set; } = "";
    public double CornerRadius { get; set; } = 8;
    /// <summary>背景効果の描画方式。Auto: Win11 22H2 以降は公式 API、それ以外は Acrylic ぼかし。</summary>
    public BackdropEngine BackdropEngine { get; set; } = BackdropEngine.Auto;

    // --- ウィジェット（スタートメニュー右側） ---
    public bool ShowWidgets { get; set; } = true;
    public bool WidgetClock { get; set; } = true;
    public bool WidgetWeather { get; set; } = true;
    public bool WidgetSystem { get; set; } = true;
    public bool WidgetFolders { get; set; } = true;
    public bool WidgetRecent { get; set; } = true;
    public bool WidgetNotes { get; set; } = false;
    /// <summary>ウィジェットの並び順（clock / weather / system / folders / recent / notes）。</summary>
    public List<string> WidgetOrder { get; set; } = new(WidgetIds);

    public static readonly string[] WidgetIds = { "clock", "weather", "system", "folders", "recent", "notes" };

    /// <summary>保存された並び順を正規化（未知の ID を除き、足りない ID を末尾に補う）。</summary>
    public List<string> OrderedWidgets()
    {
        var list = WidgetOrder.Where(id => WidgetIds.Contains(id)).Distinct().ToList();
        list.AddRange(WidgetIds.Where(id => !list.Contains(id)));
        return list;
    }

    public bool IsWidgetEnabled(string id) => id switch
    {
        "clock" => WidgetClock, "weather" => WidgetWeather, "system" => WidgetSystem,
        "folders" => WidgetFolders, "recent" => WidgetRecent, "notes" => WidgetNotes, _ => false,
    };

    public void SetWidgetEnabled(string id, bool on)
    {
        switch (id)
        {
            case "clock": WidgetClock = on; break;
            case "weather": WidgetWeather = on; break;
            case "system": WidgetSystem = on; break;
            case "folders": WidgetFolders = on; break;
            case "recent": WidgetRecent = on; break;
            case "notes": WidgetNotes = on; break;
        }
    }

    /// <summary>ウィジェット <paramref name="id"/> を <paramref name="index"/> 番目へ移動。</summary>
    public void MoveWidget(string id, int index)
    {
        var list = OrderedWidgets();
        int from = list.IndexOf(id);
        if (from < 0) return;
        list.RemoveAt(from);
        if (from < index) index--;
        list.Insert(Math.Clamp(index, 0, list.Count), id);
        WidgetOrder = list;
    }
    /// <summary>天気の地点（都市名。Open-Meteo のジオコーディングで検索）。</summary>
    public string WeatherCity { get; set; } = "Tokyo";

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

    /// <summary>保存して Changed を発火（UI が作り直される）。</summary>
    public void Save()
    {
        SaveQuiet();
        Changed?.Invoke();
    }

    /// <summary>保存のみ（UI を作り直さない。トレイのピン留めなど細かい変更用）。</summary>
    public void SaveQuiet()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }

    public static IReadOnlyList<string> DefaultPinnedStart => Defaults.PinnedStart;

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
