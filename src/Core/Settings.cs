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
/// <summary>タスクバーの下地。Acrylic: Windows 11 のすりガラス（明るさの層とノイズ入り） / Blur: ぼかしのみ / Clear: 完全に透明</summary>
public enum TaskbarMaterial { Acrylic, Blur, Clear, Solid }
/// <summary>スタートメニューを開く位置。Auto: スタートボタンの位置に合わせる</summary>
public enum StartMenuPosition { Auto, Left, Center }
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
    /// <summary>スタートメニュー（ModernShell / Windows 標準どちらも）を開く位置。</summary>
    public StartMenuPosition StartMenuPosition { get; set; } = StartMenuPosition.Auto;
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
    /// <summary>帯型のとき画面の端（左右と下/上）から離す距離 (DIP)。0 より大きいと角丸になる。</summary>
    public int BandMargin { get; set; } = 0;
    /// <summary>Windows が角丸を描かない環境（仮想マシン、リモートデスクトップ等）でも、ウィンドウを角丸に切り抜く。</summary>
    public bool ForceRoundCorners { get; set; } = false;
    /// <summary>アイランド型のときホバーでアイコンを拡大する。</summary>
    public bool MagnifyOnHover { get; set; } = true;
    public bool GroupTaskbarButtons { get; set; } = true;
    public bool ShowTaskbarLabels { get; set; } = false;
    public bool ShowSeconds { get; set; } = false;
    /// <summary>タスクバーのボタンにカーソルを置くとウィンドウのプレビューを表示する。</summary>
    public bool ShowThumbnailPreview { get; set; } = true;
    /// <summary>タスクビューボタンにカーソルを置くと仮想デスクトップの一覧を表示する。</summary>
    public bool ShowDesktopsOnTaskView { get; set; } = true;
    /// <summary>プレビューを表示するまでの待ち時間 (ミリ秒)。</summary>
    public int PreviewDelayMs { get; set; } = 400;
    /// <summary>プレビューの大きさ（Small / Medium / Large）。</summary>
    public string PreviewSize { get; set; } = "Medium";
    public List<string> PinnedTaskbar { get; set; } = new();
    public TaskbarPinSource TaskbarPinSource { get; set; } = TaskbarPinSource.Windows;
    /// <summary>「Windows と共有」のとき、Windows のピン留めの後ろに ModernShell 側で追加したピン留め。</summary>
    public List<string> PinnedTaskbarExtra { get; set; } = new();
    /// <summary>タスクバーのアプリごとのアイコンの差し替え（アプリ ID → "ファイルパス|番号"）。</summary>
    public Dictionary<string, string> CustomIcons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>タスクバーのアプリアイコンの大きさ (px)。</summary>
    public int TaskbarIconSize { get; set; } = 24;

    // --- 外観 ---
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public BackdropKind Backdrop { get; set; } = BackdropKind.Acrylic;
    /// <summary>空ならシステムのアクセントカラー。#RRGGBB 形式。</summary>
    public string AccentColor { get; set; } = "";
    public double CornerRadius { get; set; } = 8;
    /// <summary>メニュー（右クリック・ジャンプリスト・サブメニュー）の文字サイズ (px)。</summary>
    public int MenuFontSize { get; set; } = 14;
    /// <summary>タスクバー背景の不透明度 (0 = ぼかしだけ ～ 100 = 不透明)。</summary>
    public int TaskbarOpacity { get; set; } = 60;
    public TaskbarMaterial TaskbarMaterial { get; set; } = TaskbarMaterial.Blur;
    /// <summary>スタートメニュー背景の不透明度 (0 = 標準の Acrylic/Mica のまま ～ 100 = 不透明)。</summary>
    public int StartMenuOpacity { get; set; } = 0;
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

    /// <summary>現在の設定（メニュー部品など、設定を受け取らない場所から参照する）。</summary>
    [JsonIgnore] public static Settings? Current { get; private set; }

    public static Settings Load()
    {
        var loaded = LoadCore();
        Current = loaded;
        return loaded;
    }

    private static Settings LoadCore()
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
