using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModernShell.Core;

public enum ThemeMode { System, Light, Dark }
public enum TaskbarAlignment { Center, Left }
/// <summary>タスクバーを置く画面の端。Left / Right は縦向きのタスクバー。</summary>
public enum TaskbarEdge { Bottom, Top, Left, Right }
/// <summary>スタート・検索・タスクビューの位置。WithApps: アプリと一緒（中央 or 左） / LeftEdge: 常に左端（3 分割）</summary>
public enum StartButtonPosition { WithApps, LeftEdge }
/// <summary>
/// タスクバー・スタートメニューの背景。どれも ModernShell が自分で描くので、GPU が無い PC でも同じ見た目になる。
/// Solid: テーマ色（ライト/ダーク）の単色 / Transparent: テーマ色を半透明で（ぼかし無し） / Blur: 壁紙を強くぼかした下地にテーマ色を重ねる（Mica と同じ考え方）
/// </summary>
public enum SurfaceStyle { Solid, Transparent, Blur }
/// <summary>開くときのアニメーションの速さ（Fast = 短く / Slow = ゆっくり）。</summary>
public enum AnimationSpeed { Fast, Normal, Slow }
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
    /// <summary>
    /// アイランド型の余白 (DIP)。X = 左右、Y = 上下（画面を基準にした向き。下に置くなら Y は画面の下端との距離、
    /// X は島の左右の端と画面の端との距離。左右に置くときは X が画面の端との距離になる）。
    /// </summary>
    public int IslandMarginX { get; set; } = 8;
    public int IslandMarginY { get; set; } = 8;
    /// <summary>帯型の余白 (DIP)。X = 左右、Y = 上下。0 より大きいと帯は画面の端から離れて角丸になる。</summary>
    public int BandMarginX { get; set; } = 0;
    public int BandMarginY { get; set; } = 0;
    /// <summary>アイランド型のときホバーでアイコンを拡大する。</summary>
    public bool MagnifyOnHover { get; set; } = true;
    /// <summary>タスクバーに IME の状態（あ / A）を表示する。</summary>
    public bool ShowImeIndicator { get; set; } = true;
    /// <summary>開くときのアニメーション（それぞれ切り替えられる）。</summary>
    public bool AnimateStartMenu { get; set; } = true;
    public bool AnimatePreviews { get; set; } = true;
    public bool AnimateTrayOverflow { get; set; } = true;
    public bool AnimateMenus { get; set; } = true;
    /// <summary>開くときのアニメーション全体の速さ。</summary>
    public AnimationSpeed AnimationSpeed { get; set; } = AnimationSpeed.Normal;
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
    /// <summary>空ならシステムのアクセントカラー。#RRGGBB 形式。</summary>
    public string AccentColor { get; set; } = "";
    public double CornerRadius { get; set; } = 8;
    /// <summary>メニュー（右クリック・ジャンプリスト・サブメニュー）の文字サイズ (px)。</summary>
    public int MenuFontSize { get; set; } = 14;
    /// <summary>スタートメニュー（ウィジェットを含む）の文字の大きさ (%)。100 が標準。</summary>
    public int StartMenuTextScale { get; set; } = 100;
    /// <summary>タスクバー（とそこから開くプレビュー・一覧・メニュー）の背景。</summary>
    public SurfaceStyle TaskbarBackground { get; set; } = SurfaceStyle.Blur;
    /// <summary>タスクバー背景の上に重ねるテーマ色の濃さ (0 = 下地のまま ～ 100 = 不透明)。透過・ぼかしのとき有効。</summary>
    public int TaskbarOpacity { get; set; } = 60;
    /// <summary>スタートメニュー（とそこから開くメニュー）の背景。</summary>
    public SurfaceStyle StartMenuBackground { get; set; } = SurfaceStyle.Blur;
    /// <summary>スタートメニュー背景の上に重ねるテーマ色の濃さ (0–100)。</summary>
    public int StartMenuOpacity { get; set; } = 70;

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
    /// <summary>サインイン時に管理者として起動する（タスクスケジューラ）。RunAtStartup がオンのときだけ有効。</summary>
    public bool RunElevatedAtStartup { get; set; } = false;
    /// <summary>
    /// WPF の描画に GPU を使うか。オフにすると GPU ドライバー（NVIDIA なら 100 MB 超の DLL 群）を読み込まないので
    /// 使用メモリが減る。背景（透過・ぼかし）は CPU で描いているので見た目は変わらない。変更は再起動で反映。
    /// </summary>
    public bool UseGpuRendering { get; set; } = false;
    /// <summary>表示言語。"" = Windows に合わせる / "ja" / "en"。</summary>
    public string Language { get; set; } = "";
    /// <summary>タスクバー・スタートメニュー・メニューの文字のフォント。空なら標準（Segoe UI Variable）。設定画面には使わない。</summary>
    public string UiFont { get; set; } = "";

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
            {
                var text = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<Settings>(text, Json) ?? new Settings();
                Migrate(loaded, text);
                return loaded;
            }
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
        if (_frozen) return;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }

    private static bool _frozen;

    /// <summary>
    /// 古い背景の設定（Windows の背景効果を使っていた頃の「素材」）を、今の 3 択に読み替える。
    /// 新しい項目がまだ保存されていないときだけ行う。
    /// </summary>
    private static void Migrate(Settings s, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Old(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (!root.TryGetProperty(nameof(TaskbarBackground), out _) && Old("TaskbarMaterial") is { } material)
                s.TaskbarBackground = material switch { "Solid" => SurfaceStyle.Solid, "Clear" => SurfaceStyle.Transparent, _ => SurfaceStyle.Blur };
            // 余白は以前は 1 つ（上下左右とも同じ値）だった
            if (!root.TryGetProperty(nameof(BandMarginX), out _) && root.TryGetProperty("BandMargin", out var bm) && bm.TryGetInt32(out var bmv))
                s.BandMarginX = s.BandMarginY = bmv;
            if (!root.TryGetProperty(nameof(IslandMarginX), out _) && root.TryGetProperty("IslandMargin", out var im) && im.TryGetInt32(out var imv))
                s.IslandMarginX = s.IslandMarginY = imv;
            if (!root.TryGetProperty(nameof(StartMenuBackground), out _))
            {
                if (Old("Backdrop") == "Solid") s.StartMenuBackground = SurfaceStyle.Solid;
                if (s.StartMenuOpacity == 0) s.StartMenuOpacity = 70; // 以前の 0 は「Windows 標準の濃さ」という意味だった
            }
        }
        catch (Exception ex) { Log.Error("settings migrate", ex); }
    }

    /// <summary>ModernShell の設定として読める JSON か。</summary>
    public static bool IsValid(string json)
    {
        try { return json.TrimStart().StartsWith("{") && JsonSerializer.Deserialize<Settings>(json, Json) != null; }
        catch { return false; }
    }

    /// <summary>
    /// 設定ファイルを置き換える（null なら削除して初期状態に戻す）。このあと再起動して読み込み直す前提なので、
    /// 終了処理などで今の設定が上書き保存されないよう、以降の保存を止める。
    /// </summary>
    public static void Replace(string? json)
    {
        _frozen = true;
        System.IO.Directory.CreateDirectory(Directory);
        if (json == null) { if (File.Exists(FilePath)) File.Delete(FilePath); }
        else File.WriteAllText(FilePath, json);
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

    /// <summary>起動からの経過時間付きで記録する（どこで時間がかかっているかを調べるため）。</summary>
    public static void Timing(string phase)
    {
        double ms;
        try { using var p = System.Diagnostics.Process.GetCurrentProcess(); ms = (DateTime.Now - p.StartTime).TotalMilliseconds; }
        catch { ms = -1; }
        Write("INFO ", $"timing: {phase} (+{ms:0} ms from launch)");
    }
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
