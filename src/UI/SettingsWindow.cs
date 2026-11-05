using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>
/// Windows 11 の設定アプリに合わせた Fluent デザインの設定画面。
/// 左: カテゴリ（選択中はアクセント色の縦線） / 右: カード型の設定行（トグル・ドロップダウン・選択カード）。
/// </summary>
public sealed class SettingsWindow : Window
{
    private enum Page { Start, Taskbar, Widgets, Look, General, About }

    private readonly Settings _s;
    private Page _page = Page.Taskbar;
    private ScrollViewer? _scroll;
    public event Action? ExitRequested;
    /// <summary>Windows のタスクバーのピン留め（並び順どおりの ID 一覧）を返す。App が設定する。</summary>
    public Func<List<string>>? ImportWindowsPins { get; set; }

    public SettingsWindow(Settings settings)
    {
        _s = settings;
        Title = "ModernShell の設定";
        Width = 1080;
        Height = 760;
        MinWidth = 760;
        MinHeight = 500;
        FontFamily = Theme.TextFont;
        FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SourceInitialized += (_, _) => Build();
        Theme.Changed += () => Dispatcher.BeginInvoke(() => { if (IsLoaded) Build(); });
    }

    private Theme T => Theme.Current;

    private void Build()
    {
        Backdrop.Apply(this, BackdropKind.Mica, _s.BackdropEngine, T, alwaysInactive: false);
        Foreground = T.Text;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Nav());

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Grid.SetColumn(_scroll, 1);
        root.Children.Add(_scroll);
        Content = root;
        RenderPage();
    }

    /// <summary>設定を保存して、依存する項目の有効/無効を反映するため現在のページを描き直す。</summary>
    private void Commit()
    {
        _s.Save();
        double offset = _scroll?.VerticalOffset ?? 0;
        RenderPage();
        _scroll?.ScrollToVerticalOffset(offset);
    }

    // ------------------------------------------------------------------
    // ナビゲーション

    private FrameworkElement Nav()
    {
        var p = new StackPanel { Margin = new Thickness(16, 8, 8, 16) };

        var logo = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = T.AccentBrush,
            Child = UiKit.StartLogo(24, T.OnAccent),
        };
        ((FrameworkElement)logo.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((FrameworkElement)logo.Child).VerticalAlignment = VerticalAlignment.Center;
        var names = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(UiKit.Label("ModernShell", 14, T.Text, FontWeights.SemiBold));
        names.Children.Add(UiKit.Label("バージョン " + Version, 12, T.TextSecondary));
        var header = UiKit.Row(logo, names);
        header.Margin = new Thickness(8, 8, 8, 20);
        p.Children.Add(header);

        foreach (var (page, glyph, label) in new[]
        {
            (Page.Start, "", "スタート メニュー"),
            (Page.Taskbar, "", "タスクバー"),
            (Page.Widgets, "", "ウィジェット"),
            (Page.Look, "", "外観"),
            (Page.General, "", "一般"),
            (Page.About, "", "バージョン情報"),
        })
        {
            bool cur = page == _page;
            var pill = new Border
            {
                Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left,
                Background = cur ? T.AccentBrush : Brushes.Transparent,
            };
            var row = new Grid();
            row.Children.Add(pill);
            var content = UiKit.Row(UiKit.Glyph(glyph, 16), Spaced(UiKit.Label(label, 14), 16));
            content.Margin = new Thickness(16, 0, 0, 0);
            content.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(content);
            var tile = new Tile { Height = 36, Margin = new Thickness(0, 2, 0, 2), Child = row };
            if (cur) tile.IsSelected = true;
            var target = page;
            tile.Click += _ => { _page = target; Build(); };
            p.Children.Add(tile);
        }
        return p;
    }

    private static string Version => typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // ------------------------------------------------------------------
    // ページ

    private void RenderPage()
    {
        if (_scroll == null) return;
        var p = new StackPanel { Margin = new Thickness(24, 8, 40, 40), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        p.Children.Add(new TextBlock
        {
            Text = _page switch
            {
                Page.Start => "スタート メニュー", Page.Taskbar => "タスクバー", Page.Widgets => "ウィジェット",
                Page.Look => "外観", Page.General => "一般", _ => "バージョン情報",
            },
            FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = T.Text, Margin = new Thickness(0, 0, 0, 24),
        });

        switch (_page)
        {
            case Page.Start: StartPage(p); break;
            case Page.Taskbar: TaskbarPage(p); break;
            case Page.Widgets: WidgetsPage(p); break;
            case Page.Look: LookPage(p); break;
            case Page.General: GeneralPage(p); break;
            default: AboutPage(p); break;
        }
        _scroll.Content = p;
    }

    private void StartPage(StackPanel p)
    {
        p.Children.Add(ToggleRow("Win キーで ModernShell のスタートを開く", "Win+E などの組み合わせはそのまま使えます",
            _s.ReplaceStartMenu, v => _s.ReplaceStartMenu = v));
        p.Children.Add(ToggleRow("純正のスタートボタンも置き換える", "純正タスクバーを使っているときに有効です",
            _s.InterceptNativeStartButton, v => _s.InterceptNativeStartButton = v, enabled: _s.ReplaceStartMenu && !_s.UseCustomTaskbar));
        p.Children.Add(ToggleRow("開いたときに「すべてのアプリ」を表示", "", _s.ShowAllAppsFirst, v => _s.ShowAllAppsFirst = v));
        p.Children.Add(DropDownRow("ピン留めの列数", "", new[] { ("4", "4 列"), ("5", "5 列"), ("6", "6 列"), ("7", "7 列"), ("8", "8 列") },
            _s.PinnedColumns.ToString(), v => _s.PinnedColumns = int.Parse(v)));

        p.Children.Add(Section("ピン留め"));
        p.Children.Add(ButtonRow("ピン留めしたアプリ", "スタートメニュー上でドラッグして並べ替え、右クリックで追加・削除できます",
            "初期状態に戻す", () => { _s.PinnedStart.Clear(); _s.PinnedStart.AddRange(Settings.DefaultPinnedStart); Commit(); }));
    }

    private void TaskbarPage(StackPanel p)
    {
        bool on = _s.UseCustomTaskbar;
        bool floating = _s.TaskbarStyle == TaskbarStyle.Floating;

        p.Children.Add(Preview());
        p.Children.Add(ToggleRow("ModernShell のタスクバーを使う",
            "純正タスクバーを隠して、下で選んだ見た目で表示します。トレイアイコンも表示されます。終了すると純正に戻ります",
            on, v => _s.UseCustomTaskbar = v));

        p.Children.Add(Section("スタイル"));
        p.Children.Add(ChoiceCards(new[]
        {
            ("帯", "画面の端に密着した、従来型のタスクバー", TaskbarStyle.Band == _s.TaskbarStyle, (Action)(() => _s.TaskbarStyle = TaskbarStyle.Band), Art.Band),
            ("アイランド", "端から浮いた島。アプリ数に合わせて伸び縮み", floating, () => _s.TaskbarStyle = TaskbarStyle.Floating, Art.Island),
        }, on));

        p.Children.Add(Section("スタート・検索・タスク ビューの位置"));
        p.Children.Add(ChoiceCards(new[]
        {
            ("アプリと一緒", "アプリのボタンと 1 つのかたまりにする（下の「配置」で中央か左）", _s.StartButtonPosition == StartButtonPosition.WithApps,
                (Action)(() => _s.StartButtonPosition = StartButtonPosition.WithApps), Art.WithApps),
            ("左端", "常に画面の左端に置き、アプリは別に並べる", _s.StartButtonPosition == StartButtonPosition.LeftEdge,
                () => _s.StartButtonPosition = StartButtonPosition.LeftEdge, Art.LeftEdge),
        }, on));

        p.Children.Add(Section("ピン留めしたアプリ"));
        p.Children.Add(ChoiceCards(new[]
        {
            ("Windows と共有", "Windows のタスクバーと同じアプリ・同じ並び順を表示（自動で追従）",
                _s.TaskbarPinSource == TaskbarPinSource.Windows, (Action)(() => _s.TaskbarPinSource = TaskbarPinSource.Windows), Art.PinsWindows),
            ("ModernShell で管理", "右クリックでピン留め、ドラッグで並べ替え",
                _s.TaskbarPinSource == TaskbarPinSource.ModernShell, () => _s.TaskbarPinSource = TaskbarPinSource.ModernShell, Art.PinsOwn),
        }, on));
        if (_s.TaskbarPinSource == TaskbarPinSource.Windows)
            p.Children.Add(Note("Windows はアプリからピン留めを変更する手段を提供していないため、共有は「Windows → ModernShell」の一方向です。" +
                               "追加・削除・並べ替えは Windows 側（エクスプローラーでアプリを右クリック →「タスクバーにピン留めする」など）で行ってください。"));
        else
            p.Children.Add(ButtonRow("Windows のピン留めを取り込む", "Windows のタスクバーと同じ内容・並び順で置き換えます（その後は ModernShell で自由に変更できます）",
                "取り込む", () =>
                {
                    var ids = ImportWindowsPins?.Invoke();
                    if (ids == null || ids.Count == 0) return;
                    _s.PinnedTaskbar = ids;
                    Commit();
                }));

        p.Children.Add(Section("配置と動作"));
        p.Children.Add(DropDownRow("アプリの配置", "アプリのボタン（「アプリと一緒」のときはスタートボタンも）の位置",
            new[] { ("Center", "中央"), ("Left", "左") }, _s.TaskbarAlignment.ToString(), v => _s.TaskbarAlignment = Enum.Parse<TaskbarAlignment>(v), on));
        p.Children.Add(DropDownRow("画面上の位置", "", new[] { ("Bottom", "下"), ("Top", "上") }, _s.TaskbarEdge.ToString(),
            v => _s.TaskbarEdge = Enum.Parse<TaskbarEdge>(v), on));
        p.Children.Add(DropDownRow("高さ", "40 px 未満では時計の日付を省略します",
            new[] { ("28", "28 px"), ("32", "32 px"), ("36", "36 px"), ("40", "40 px"), ("44", "44 px"), ("48", "48 px（標準）"), ("56", "56 px"), ("64", "64 px") },
            _s.TaskbarHeight.ToString(), v => _s.TaskbarHeight = int.Parse(v), on));
        p.Children.Add(DropDownRow("アプリ アイコンの大きさ", "",
            new[] { ("16", "16 px（小）"), ("20", "20 px"), ("24", "24 px（標準）"), ("28", "28 px"), ("32", "32 px（大）") },
            _s.TaskbarIconSize.ToString(), v => _s.TaskbarIconSize = int.Parse(v), on));
        p.Children.Add(DropDownRow("アイランドの余白", "画面の端と島のあいだの距離", new[] { ("4", "4 px"), ("8", "8 px"), ("12", "12 px"), ("16", "16 px") },
            _s.IslandMargin.ToString(), v => _s.IslandMargin = int.Parse(v), on && floating));
        p.Children.Add(ToggleRow("検索ボタンを表示", "", _s.ShowSearchButton, v => _s.ShowSearchButton = v, on));
        p.Children.Add(ToggleRow("タスク ビュー ボタンを表示", "", _s.ShowTaskViewButton, v => _s.ShowTaskViewButton = v, on));
        p.Children.Add(ToggleRow("ホバー時にアイコンを拡大", "アイランドのときだけ有効です", _s.MagnifyOnHover, v => _s.MagnifyOnHover = v, on && floating));
        p.Children.Add(ToggleRow("同じアプリのボタンをまとめる", "複数のウィンドウは一覧で選べます", _s.GroupTaskbarButtons, v => _s.GroupTaskbarButtons = v, on));
        p.Children.Add(ToggleRow("ボタンにウィンドウ名を表示", "", _s.ShowTaskbarLabels, v => _s.ShowTaskbarLabels = v, on));
        p.Children.Add(ToggleRow("時計に秒を表示", "", _s.ShowSeconds, v => _s.ShowSeconds = v, on));

        p.Children.Add(InfoBar("困ったときは Ctrl + Alt + Shift + F12",
            "いつでも ModernShell を終了して純正タスクバーに戻せます。トレイの「^」→ ModernShell アイコンの右クリック、スタートの電源メニューからも終了できます。"));
    }

    private void WidgetsPage(StackPanel p)
    {
        p.Children.Add(ToggleRow("スタート メニューにウィジェットを表示", "スタート メニューの右側に表示します", _s.ShowWidgets, v => _s.ShowWidgets = v));
        bool on = _s.ShowWidgets;
        p.Children.Add(Section("表示するウィジェットと並び順"));
        p.Children.Add(Note("↑↓ で並び順を変えられます。スタート メニュー上でもウィジェットの見出しをドラッグして並べ替えできます。"));
        var order = _s.OrderedWidgets();
        for (int i = 0; i < order.Count; i++)
        {
            var id = order[i];
            var (title, desc) = id switch
            {
                "clock" => ("時計とカレンダー", ""),
                "weather" => ("天気", "Open-Meteo（無料・登録不要）から取得します。地点の名前が送信されます"),
                "system" => ("システム", "CPU・メモリ・ディスクの使用状況"),
                "folders" => ("フォルダー", "ドキュメント・ダウンロードなどへのショートカット"),
                "recent" => ("最近使ったファイル", ""),
                _ => ("メモ", "書いた内容は自動で保存されます"),
            };
            p.Children.Add(WidgetRow(id, title, desc, i, order.Count, on));
            if (id == "weather")
                p.Children.Add(TextRow("天気の地点", "都市名（例: Tokyo, Osaka, 札幌）", _s.WeatherCity, v => _s.WeatherCity = v, on && _s.WidgetWeather));
        }
    }

    /// <summary>ウィジェット 1 行: 名前 / ↑ / ↓ / トグル。</summary>
    private Border WidgetRow(string id, string title, string desc, int index, int count, bool enabled)
    {
        Tile Arrow(string glyph, string tip, bool can, int to)
        {
            var b = new Tile
            {
                Width = 32, Height = 32, CornerRadius = T.Corner(4), Child = UiKit.Glyph(glyph, 12), ToolTip = tip,
                IsEnabled = can, Opacity = can ? 1 : 0.3, Margin = new Thickness(0, 0, 4, 0),
            };
            b.Click += _ => { _s.MoveWidget(id, to); Commit(); };
            return b;
        }
        var sw = new ToggleSwitch(_s.IsWidgetEnabled(id));
        sw.Toggled += v => { _s.SetWidgetEnabled(id, v); Commit(); };
        sw.Margin = new Thickness(12, 0, 0, 0);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Arrow("\uE70E", "上へ", index > 0, index - 1));
        right.Children.Add(Arrow("\uE70D", "下へ", index < count - 1, index + 2));
        right.Children.Add(sw);
        return Card(Texts(title, desc), right, enabled);
    }

    private void LookPage(StackPanel p)
    {
        p.Children.Add(Section("モード", first: true));
        p.Children.Add(ChoiceCards(new[]
        {
            ("Windows に合わせる", "", _s.Theme == ThemeMode.System, (Action)(() => _s.Theme = ThemeMode.System), Art.ThemeSystem),
            ("ライト", "", _s.Theme == ThemeMode.Light, () => _s.Theme = ThemeMode.Light, Art.ThemeLight),
            ("ダーク", "", _s.Theme == ThemeMode.Dark, () => _s.Theme = ThemeMode.Dark, Art.ThemeDark),
        }, true));

        p.Children.Add(Section("アクセント カラー"));
        p.Children.Add(Swatches());

        p.Children.Add(Section("効果"));
        p.Children.Add(DropDownRow("背景効果", "スタートメニューとタスクバーの素材",
            new[] { ("Acrylic", "Acrylic"), ("Mica", "Mica"), ("Solid", "なし（単色）") }, _s.Backdrop.ToString(), v => _s.Backdrop = Enum.Parse<BackdropKind>(v)));
        p.Children.Add(DropDownRow("背景効果の描画方式", "透けない場合は「Acrylic ぼかし」を試してください",
            new[] { ("Auto", "自動"), ("SystemBackdrop", "Windows 11 標準（22H2 以降）"), ("AccentBlur", "Acrylic ぼかし（互換）") },
            _s.BackdropEngine.ToString(), v => _s.BackdropEngine = Enum.Parse<BackdropEngine>(v)));
        p.Children.Add(DropDownRow("角の丸み", "", new[] { ("0", "なし"), ("4", "小"), ("8", "標準"), ("12", "大") },
            ((int)_s.CornerRadius).ToString(), v => _s.CornerRadius = double.Parse(v)));
        p.Children.Add(InfoBar("透けて見えないときは",
            "Windows の「設定 → 個人用設定 → 色 → 透明効果」がオンになっているか確認してください。オフだとどの方式でも不透明になります。"));
    }

    private void GeneralPage(StackPanel p)
    {
        p.Children.Add(ToggleRow("サインイン時に起動", "Windows にサインインすると自動で常駐します",
            _s.RunAtStartup, v => { _s.RunAtStartup = v; SystemActions.SetRunAtStartup(v); }));
        p.Children.Add(ButtonRow("設定とログ", @"%AppData%\ModernShell に settings.json と modernshell.log があります",
            "フォルダーを開く", () => Process.Start("explorer.exe", "\"" + Settings.Directory + "\"")));

        p.Children.Add(Section("トラブルシューティング"));
        p.Children.Add(ButtonRow("ModernShell を終了", "スタートメニューとタスクバーが純正に戻ります（Ctrl + Alt + Shift + F12 でも可）",
            "終了", () => ExitRequested?.Invoke(), accent: true));
        p.Children.Add(Note(@"終了できなくなった場合は、ModernShell.exe --restore を実行するか PC を再起動してください。"));
    }

    private void AboutPage(StackPanel p)
    {
        p.Children.Add(ButtonRow("ModernShell " + Version, "explorer.exe にコードを注入しない、別プロセス型のスタートメニュー / タスクバー",
            "Releases を開く", () => Open("https://github.com/c0dei/ModernShell/releases")));
        p.Children.Add(ButtonRow("ソースコード", "github.com/c0dei/ModernShell", "GitHub で開く", () => Open("https://github.com/c0dei/ModernShell")));
        p.Children.Add(Note("通知領域とタスク一覧には ManagedShell (Apache-2.0, cairoshell) を使用しています。"));
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ------------------------------------------------------------------
    // 部品

    private FrameworkElement Section(string text, bool first = false) => new TextBlock
    {
        Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = T.Text, Margin = new Thickness(0, first ? 0 : 24, 0, 8),
    };

    private FrameworkElement Note(string text) => new TextBlock
    {
        Text = text, FontSize = 12, Foreground = T.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0),
    };

    private Border Card(FrameworkElement left, FrameworkElement? right, bool enabled = true)
    {
        var g = new Grid { MinHeight = 44 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(left);
        if (right != null)
        {
            right.VerticalAlignment = VerticalAlignment.Center;
            right.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
        }
        return new Border
        {
            Background = T.Surface, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6),
            Padding = new Thickness(20, 12, 16, 12), Margin = new Thickness(0, 2, 0, 2), Child = g,
            IsEnabled = enabled, Opacity = enabled ? 1 : 0.45,
        };
    }

    private FrameworkElement Texts(string title, string desc)
    {
        var s = new StackPanel();
        var t = UiKit.Label(title, 14);
        t.TextWrapping = TextWrapping.Wrap;
        s.Children.Add(t);
        if (desc.Length > 0)
        {
            var d = UiKit.Label(desc, 12, T.TextSecondary);
            d.TextWrapping = TextWrapping.Wrap;
            s.Children.Add(d);
        }
        return s;
    }

    private Border ToggleRow(string title, string desc, bool value, Action<bool> set, bool enabled = true)
    {
        var sw = new ToggleSwitch(value);
        sw.Toggled += v => { set(v); Commit(); };
        return Card(Texts(title, desc), sw, enabled);
    }

    private Border DropDownRow(string title, string desc, (string value, string label)[] options, string current, Action<string> set, bool enabled = true)
    {
        var combo = new FluentComboBox(options, current, v => { set(v); Commit(); });
        return Card(Texts(title, desc), combo, enabled);
    }

    private Border TextRow(string title, string desc, string value, Action<string> set, bool enabled = true)
    {
        var box = new TextBox
        {
            Text = value, MinWidth = 200, Padding = new Thickness(8, 5, 8, 5), Background = T.SolidBackground, Foreground = T.Text,
            CaretBrush = T.Text, BorderBrush = T.Stroke, BorderThickness = new Thickness(1, 1, 1, 2),
        };
        void Apply() { var v = box.Text.Trim(); if (v != value) { set(v); _s.Save(); } }
        box.LostFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        return Card(Texts(title, desc), box, enabled);
    }

    private Border ButtonRow(string title, string desc, string buttonText, Action onClick, bool accent = false)
    {
        // WinUI のボタン: 標準は半透明の地 + 下端の濃い枠、アクセントはアクセント色の地
        var normal = accent ? T.AccentBrush : T.ControlFill;
        var b = new Tile
        {
            Padding = new Thickness(16, 5, 16, 6), CornerRadius = T.Corner(4), MinWidth = 120,
            Background = normal, BorderBrush = T.ControlBorder, BorderThickness = new Thickness(1),
            Child = UiKit.Label(buttonText, 14, accent ? T.OnAccent : T.Text),
        };
        ((TextBlock)b.Child).HorizontalAlignment = HorizontalAlignment.Center;
        b.MouseEnter += (_, _) => { b.Background = accent ? normal : T.ControlFillHover; if (accent) b.Opacity = 0.9; };
        b.MouseLeave += (_, _) => { b.Background = normal; b.BorderBrush = T.ControlBorder; b.Opacity = 1; };
        b.PreviewMouseLeftButtonDown += (_, _) => { if (!accent) b.Background = T.ControlFillPressed; else b.Opacity = 0.8; };
        b.Click += _ => onClick();
        return Card(Texts(title, desc), b);
    }

    private Border InfoBar(string title, string body)
    {
        var icon = new Grid { Width = 16, Height = 16, Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.Children.Add(new Ellipse { Fill = T.AccentBrush });
        icon.Children.Add(UiKit.Label("i", 11, T.OnAccent, FontWeights.Bold));
        ((TextBlock)icon.Children[1]).HorizontalAlignment = HorizontalAlignment.Center;
        var texts = new StackPanel();
        var t = UiKit.Label(title, 14, T.Text, FontWeights.SemiBold);
        t.TextWrapping = TextWrapping.Wrap;
        texts.Children.Add(t);
        var b = UiKit.Label(body, 13, T.TextSecondary);
        b.TextWrapping = TextWrapping.Wrap;
        texts.Children.Add(b);
        var g = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        g.Children.Add(icon);
        g.Children.Add(texts);
        return new Border
        {
            Background = T.Surface, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(8),
            Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 20, 0, 0), Child = g,
        };
    }

    private enum Art { Band, Island, WithApps, LeftEdge, ThemeSystem, ThemeLight, ThemeDark, PinsWindows, PinsOwn }

    /// <summary>図付きの選択カード（ラジオボタン相当）。</summary>
    private FrameworkElement ChoiceCards((string title, string desc, bool selected, Action select, Art art)[] items, bool enabled)
    {
        var grid = new UniformGrid { Columns = items.Length, IsEnabled = enabled, Opacity = enabled ? 1 : 0.45 };
        foreach (var it in items)
        {
            var radio = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            radio.Children.Add(new Ellipse
            {
                Fill = it.selected ? T.AccentBrush : Brushes.Transparent,
                Stroke = it.selected ? T.AccentBrush : T.TextSecondary, StrokeThickness = 1,
            });
            if (it.selected) radio.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = T.OnAccent });

            var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            texts.Children.Add(UiKit.Label(it.title, 14));
            if (it.desc.Length > 0)
            {
                var d = UiKit.Label(it.desc, 12, T.TextSecondary);
                d.TextWrapping = TextWrapping.Wrap;
                texts.Children.Add(d);
            }
            var bottom = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
            DockPanel.SetDock(radio, Dock.Left);
            bottom.Children.Add(radio);
            bottom.Children.Add(texts);

            var stack = new StackPanel();
            stack.Children.Add(Illustration(it.art));
            stack.Children.Add(bottom);
            var card = new Tile
            {
                Margin = new Thickness(it.selected ? 3 : 4), Child = stack, CornerRadius = T.Corner(8),
                BorderThickness = new Thickness(it.selected ? 2 : 1), BorderBrush = it.selected ? T.AccentBrush : T.Stroke, Background = T.Surface,
                ClipToBounds = true,
            };
            var normalBorder = card.BorderBrush;
            card.MouseLeave += (_, _) => { card.Background = T.Surface; card.BorderBrush = normalBorder; };
            card.GotKeyboardFocus += (_, _) => card.BorderBrush = normalBorder;
            var select = it.select;
            card.Click += _ => { select(); Commit(); };
            grid.Children.Add(card);
        }
        var wrap = new Border { Margin = new Thickness(-4, 0, -4, 0), Child = grid };
        return wrap;
    }

    private static readonly SolidColorBrush Desktop = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x55, 0x70)));
    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private FrameworkElement Illustration(Art art)
    {
        var area = new Grid { Height = 92, Background = Desktop };
        Brush bar = T.SolidBackground;
        Border Isle(double w, HorizontalAlignment ha, double m = 10) => new()
        {
            Width = w, Height = 18, CornerRadius = new CornerRadius(6), Background = bar, BorderBrush = T.Stroke, BorderThickness = new Thickness(1),
            HorizontalAlignment = ha, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(m, 0, m, 10),
        };
        switch (art)
        {
            case Art.Band:
                area.Children.Add(new Border { Height = 22, Background = bar, VerticalAlignment = VerticalAlignment.Bottom });
                break;
            case Art.Island:
                area.Children.Add(Isle(150, HorizontalAlignment.Center));
                area.Children.Add(Isle(56, HorizontalAlignment.Right));
                break;
            case Art.WithApps:
                area.Children.Add(Isle(160, HorizontalAlignment.Center));
                area.Children.Add(Isle(56, HorizontalAlignment.Right));
                area.Children.Add(StartDot(HorizontalAlignment.Center, -66));
                break;
            case Art.PinsWindows:
            case Art.PinsOwn:
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (art == Art.PinsWindows)
                {
                    row.Children.Add(UiKit.StartLogo(28, Brushes.White));
                    row.Children.Add(Spaced(UiKit.Glyph("\uE72A", 18, Brushes.White), 14));
                    row.Children.Add(Spaced(UiKit.StartLogo(28, T.AccentBrush), 14));
                }
                else
                {
                    foreach (var c in new[] { 0x2F80ED, 0xF2C94C, 0x27AE60 })
                        row.Children.Add(new Border
                        {
                            Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Margin = new Thickness(5, 0, 5, 0),
                            Background = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c)),
                        });
                    row.Children.Add(Spaced(UiKit.Glyph("\uE7C2", 16, Brushes.White), 8));
                }
                area.Children.Add(row);
                break;
            }
            case Art.LeftEdge:
                area.Children.Add(Isle(44, HorizontalAlignment.Left));
                area.Children.Add(Isle(120, HorizontalAlignment.Center));
                area.Children.Add(Isle(56, HorizontalAlignment.Right));
                area.Children.Add(StartDot(HorizontalAlignment.Left, 18));
                break;
            default:
                Brush win = art switch
                {
                    Art.ThemeLight => new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)),
                    Art.ThemeDark => new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
                    _ => new LinearGradientBrush(new GradientStopCollection
                    {
                        new(Color.FromRgb(0xF3, 0xF3, 0xF3), 0), new(Color.FromRgb(0xF3, 0xF3, 0xF3), 0.5),
                        new(Color.FromRgb(0x20, 0x20, 0x20), 0.5), new(Color.FromRgb(0x20, 0x20, 0x20), 1),
                    }, 0),
                };
                area.Children.Add(new Border
                {
                    Width = 150, Height = 64, CornerRadius = new CornerRadius(6, 6, 0, 0), Background = win,
                    VerticalAlignment = VerticalAlignment.Bottom, BorderBrush = T.Stroke, BorderThickness = new Thickness(1, 1, 1, 0),
                });
                break;
        }
        return area;
    }

    private FrameworkElement StartDot(HorizontalAlignment ha, double offset)
    {
        var logo = UiKit.StartLogo(10, T.AccentBrush);
        logo.HorizontalAlignment = ha;
        logo.VerticalAlignment = VerticalAlignment.Bottom;
        logo.Margin = ha == HorizontalAlignment.Left ? new Thickness(offset, 0, 0, 14) : new Thickness(0, 0, 0, 14);
        if (ha == HorizontalAlignment.Center) logo.RenderTransform = new TranslateTransform(offset, 0);
        return logo;
    }

    /// <summary>現在の設定で描いたタスクバーのプレビュー。</summary>
    private FrameworkElement Preview()
    {
        var area = new Grid { Height = 170, Background = Desktop, ClipToBounds = true };
        if (_s.UseCustomTaskbar)
        {
            bool top = _s.TaskbarEdge == TaskbarEdge.Top;
            bool floating = _s.TaskbarStyle == TaskbarStyle.Floating;
            bool left = _s.StartButtonPosition == StartButtonPosition.LeftEdge;
            bool center = _s.TaskbarAlignment == TaskbarAlignment.Center;
            double h = _s.TaskbarHeight * 0.7, m = floating ? _s.IslandMargin : 0;

            var bar = new Grid { Height = h, VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom, Margin = new Thickness(m) };
            if (!floating) bar.Background = T.SolidBackground;
            FrameworkElement Box(FrameworkElement content, HorizontalAlignment ha)
            {
                content.VerticalAlignment = VerticalAlignment.Center;
                var b = new Border
                {
                    Child = content, HorizontalAlignment = ha, Padding = new Thickness(6, 0, 6, 0), Height = h,
                    Margin = new Thickness(floating ? 0 : 6, 0, floating ? 0 : 6, 0),
                };
                if (floating)
                {
                    b.Background = T.SolidBackground;
                    b.CornerRadius = new CornerRadius(T.Radius);
                    b.BorderBrush = T.Stroke;
                    b.BorderThickness = new Thickness(1);
                }
                return b;
            }
            FrameworkElement StartIcons()
            {
                var r = UiKit.Row(UiKit.StartLogo(12, T.AccentBrush));
                if (_s.ShowSearchButton) r.Children.Add(Spaced(UiKit.Glyph("", 11), 8));
                if (_s.ShowTaskViewButton) r.Children.Add(Spaced(UiKit.Glyph("", 11), 8));
                return r;
            }
            FrameworkElement Apps()
            {
                var r = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var c in new[] { 0xF2C94C, 0x2F80ED, 0x27AE60, 0xEB5757, 0x9B51E0 })
                    r.Children.Add(new Border
                    {
                        Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Margin = new Thickness(4, 0, 4, 0),
                        Background = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c)),
                    });
                return r;
            }
            FrameworkElement Tray() => UiKit.Row(UiKit.Glyph("", 10), Spaced(UiKit.Glyph("", 10), 6), Spaced(UiKit.Label("12:34", 10), 8));

            if (left && center)
            {
                bar.Children.Add(Box(StartIcons(), HorizontalAlignment.Left));
                bar.Children.Add(Box(Apps(), HorizontalAlignment.Center));
            }
            else if (left || !center)
            {
                bar.Children.Add(Box(UiKit.Row(StartIcons(), Spaced(Apps(), 4)), HorizontalAlignment.Left));
            }
            else bar.Children.Add(Box(UiKit.Row(StartIcons(), Spaced(Apps(), 4)), HorizontalAlignment.Center));
            bar.Children.Add(Box(Tray(), HorizontalAlignment.Right));
            area.Children.Add(bar);
        }
        else
        {
            var l = UiKit.Label("純正タスクバーを使用中", 13, Brushes.White);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            area.Children.Add(l);
        }
        return new Border
        {
            Background = T.Surface, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(8),
            Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 6),
            Child = new Border { CornerRadius = T.Corner(6), ClipToBounds = true, Child = area },
        };
    }

    private FrameworkElement Swatches()
    {
        var colors = new[] { "", "#0067C0", "#0078D4", "#00706A", "#107C10", "#6B3FB5", "#B4009E", "#C50F1F", "#A34A00" };
        var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var hex in colors)
        {
            bool sel = string.Equals(_s.AccentColor, hex, StringComparison.OrdinalIgnoreCase);
            var fill = hex.Length == 0 ? new SolidColorBrush(Theme.SystemAccent()) : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            var inner = new Grid();
            if (hex.Length == 0) inner.Children.Add(UiKit.Label("自動", 10, Brushes.White));
            if (sel) inner.Children.Add(UiKit.Glyph("", 14, Brushes.White));
            foreach (UIElement c in inner.Children) if (c is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.VerticalAlignment = VerticalAlignment.Center; }
            var tile = new Tile
            {
                Width = 44, Height = 44, Margin = new Thickness(0, 0, 8, 8), CornerRadius = T.Corner(4), Background = fill,
                BorderThickness = new Thickness(2), BorderBrush = sel ? T.Text : Brushes.Transparent, Child = inner,
                ToolTip = hex.Length == 0 ? "Windows のアクセント カラー" : hex,
            };
            tile.MouseLeave += (_, _) => { tile.Background = fill; tile.BorderBrush = sel ? T.Text : Brushes.Transparent; };
            tile.MouseEnter += (_, _) => tile.Background = fill;
            var value = hex;
            tile.Click += _ => { _s.AccentColor = value; Commit(); };
            wrap.Children.Add(tile);
        }
        var card = new Border
        {
            Background = T.Surface, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6),
            Padding = new Thickness(20, 14, 12, 6), Child = wrap,
        };
        return card;
    }

    private static FrameworkElement Spaced(FrameworkElement e, double left) { e.Margin = new Thickness(left, 0, 0, 0); return e; }
}

/// <summary>Fluent のトグルスイッチ（40×20 のピル、オン時はアクセント色）。「オン / オフ」の文字付き。</summary>
public sealed class ToggleSwitch : StackPanel
{
    private bool _on;
    private readonly Border _track;
    private readonly Ellipse _knob;
    private readonly TextBlock _label;
    public event Action<bool>? Toggled;

    public ToggleSwitch(bool on)
    {
        _on = on;
        Orientation = Orientation.Horizontal;
        Focusable = true;
        FocusVisualStyle = null;
        Background = Brushes.Transparent;
        _label = UiKit.Label("", 14);
        _label.MinWidth = 32;
        _label.TextAlignment = TextAlignment.Right;
        _label.Margin = new Thickness(0, 0, 12, 0);
        _knob = new Ellipse { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        _track = new Border { Width = 40, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = _knob };
        Children.Add(_label);
        Children.Add(_track);
        Refresh();
        MouseLeftButtonUp += (_, e) => { Flip(); e.Handled = true; };
        KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { Flip(); e.Handled = true; } };
        MouseEnter += (_, _) => _knob.Width = _knob.Height = 14;
        MouseLeave += (_, _) => _knob.Width = _knob.Height = 12;
    }

    private void Flip()
    {
        _on = !_on;
        Refresh();
        Toggled?.Invoke(_on);
    }

    private void Refresh()
    {
        var t = Theme.Current;
        _label.Text = _on ? "オン" : "オフ";
        _track.Background = _on ? t.AccentBrush : Brushes.Transparent;
        _track.BorderBrush = _on ? t.AccentBrush : t.TextSecondary;
        _knob.Fill = _on ? t.OnAccent : t.TextSecondary;
        _knob.Margin = _on ? new Thickness(23, 0, 0, 0) : new Thickness(3, 0, 0, 0);
    }
}
