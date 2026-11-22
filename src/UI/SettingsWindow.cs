using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
/// 左: 検索とカテゴリ（選択中はアクセント色の縦線） / 右: カード型の設定行。関連する行は見出し付きのグループにまとめる。
/// </summary>
public sealed class SettingsWindow : Window
{
    private enum Page { Start, Taskbar, TaskbarItems, Pins, Widgets, Look, General, About }

    // 表示言語を切り替えたら名前も変わるので、毎回作る
    private static (Page page, string glyph, string label)[] Pages => new[]
    {
        (Page.Start, "", L.T("スタート メニュー")),
        (Page.Taskbar, "", L.T("タスクバー")),
        (Page.TaskbarItems, "", L.T("ボタンと操作")),
        (Page.Pins, "", L.T("ピン留めとアイコン")),
        (Page.Widgets, "", L.T("ウィジェット")),
        (Page.Look, "", L.T("外観")),
        (Page.General, "", L.T("一般")),
        (Page.About, "", L.T("バージョン情報")),
    };

    private readonly Settings _s;
    private Page _page = Page.Taskbar;
    private string _query = "";
    /// <summary>検索結果を作っている間は、グループの見出しを付けずに行だけを並べる。</summary>
    private bool _flat;
    private bool _pickerOpen;
    private string? _builtLanguage;
    private ScrollViewer? _scroll;
    public event Action? ExitRequested;
    /// <summary>設定ファイルを置き換えたあと、読み込み直すために再起動してほしいとき。</summary>
    public event Action? RestartRequested;
    public event Action? RestartSafeRequested;
    /// <summary>管理者として再起動してほしいとき（UAC の確認が出る）。</summary>
    public event Action? RestartElevatedRequested;
    /// <summary>Windows のタスクバーのピン留め（並び順どおりの ID 一覧）を返す。App が設定する。</summary>
    public Func<List<string>>? ImportWindowsPins { get; set; }

    public SettingsWindow(Settings settings)
    {
        _s = settings;
        Title = L.T("ModernShell の設定");
        Width = 1080;
        Height = 760;
        MinWidth = 760;
        MinHeight = 500;
        FontFamily = Theme.TextFont;
        FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SourceInitialized += (_, _) => Build();
        // Theme は全体で 1 つ（static）なので、閉じたら購読を外す。外さないと閉じた設定画面が丸ごとメモリに残り続ける
        Action onTheme = () => Dispatcher.BeginInvoke(() => { if (IsLoaded) Build(); });
        Theme.Changed += onTheme;
        Closed += (_, _) => { Theme.Changed -= onTheme; Content = null; MemoryHousekeeping.CompactLater("settings closed", 3000); }; // 3 秒待って、ウィンドウが片付いてから詰める
    }

    private Theme T => Theme.Current;

    private void Build()
    {
        Backdrop.ApplyMica(this, T);
        Title = L.T("ModernShell の設定");
        _builtLanguage = _s.Language;
        Foreground = T.Text;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Nav());

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Grid.SetColumn(_scroll, 1);
        root.Children.Add(_scroll);
        Content = root;
        _root = root;
        RenderPage();
        FitToScreen();
    }

    private Grid? _root;

    /// <summary>
    /// 最大化したウィンドウは、枠の分だけ画面の外にはみ出す。内容の端やスクロールバーが枠・画面の外に隠れるので、
    /// はみ出した分だけ内容を内側に寄せる（最大化でないときは余白なし）。
    /// </summary>
    private void FitToScreen()
    {
        if (_root == null) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (WindowState != WindowState.Maximized || hwnd == IntPtr.Zero || !ModernShell.Native.Win32.GetWindowRect(hwnd, out var r))
        {
            _root.Margin = new Thickness(0);
            return;
        }
        var work = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        uint dpi = ModernShell.Native.Win32.GetDpiForWindow(hwnd);
        double k = 96.0 / (dpi > 0 ? dpi : 96);
        double L0 = Math.Max(0, work.Left - r.Left), T0 = Math.Max(0, work.Top - r.Top),
               R0 = Math.Max(0, r.Right - work.Right), B0 = Math.Max(0, r.Bottom - work.Bottom);
        _root.Margin = new Thickness(L0 * k, T0 * k, R0 * k, B0 * k);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // 最大化の直後は位置が確定していないので、少し待ってから合わせる
        Dispatcher.BeginInvoke(new Action(FitToScreen), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>設定を保存して、依存する項目の有効/無効を反映するため現在のページを描き直す。</summary>
    private void Commit()
    {
        _s.Save();
        // 表示言語を変えたときは、左の一覧やタイトルも含めて全体を作り直す
        if (_s.Language != _builtLanguage) { Build(); return; }
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
        names.Children.Add(UiKit.Label(L.T("バージョン ") + Version, 12, T.TextSecondary));
        var header = UiKit.Row(logo, names);
        header.Margin = new Thickness(8, 8, 8, 16);
        p.Children.Add(header);
        p.Children.Add(SearchBox());

        foreach (var (page, glyph, label) in Pages)
        {
            bool cur = page == _page && _query.Length == 0;
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
            tile.Click += _ => { _page = target; _query = ""; Build(); };
            p.Children.Add(tile);
        }
        return p;
    }

    /// <summary>「設定を検索」ボックス。入力するたびに全ページから一致する行を集めて表示する。</summary>
    private FrameworkElement SearchBox()
    {
        var box = new TextBox
        {
            Text = _query, Padding = new Thickness(10, 6, 32, 6), Background = T.ControlFill, Foreground = T.Text, CaretBrush = T.Text,
            BorderBrush = T.ControlBorder, BorderThickness = new Thickness(1, 1, 1, 2), VerticalContentAlignment = VerticalAlignment.Center,
        };
        var hint = UiKit.Label(L.T("設定の検索"), 14, T.TextSecondary);
        hint.Margin = new Thickness(12, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.IsHitTestVisible = false;
        hint.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var icon = UiKit.Glyph("", 14, T.TextSecondary);
        icon.HorizontalAlignment = HorizontalAlignment.Right;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.IsHitTestVisible = false;
        box.TextChanged += (_, _) =>
        {
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _query = box.Text.Trim();
            RenderPage();
            _scroll?.ScrollToTop();
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Escape) box.Text = ""; };
        var g = new Grid { Height = 36, Margin = new Thickness(4, 0, 4, 12) };
        g.Children.Add(box);
        g.Children.Add(hint);
        g.Children.Add(icon);
        if (_query.Length > 0) Dispatcher.BeginInvoke(() => { box.Focus(); box.CaretIndex = box.Text.Length; });
        return g;
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
            Text = _query.Length > 0 ? L.F("「{0}」の検索結果", _query) : Pages.First(x => x.page == _page).label,
            FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = T.Text, Margin = new Thickness(0, 0, 0, 24),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (_query.Length > 0) SearchResults(p);
        else RenderInto(_page, p);
        _scroll.Content = p;
    }

    private void RenderInto(Page page, StackPanel p)
    {
        switch (page)
        {
            case Page.Start: StartPage(p); break;
            case Page.Taskbar: TaskbarPage(p); break;
            case Page.TaskbarItems: TaskbarItemsPage(p); break;
            case Page.Pins: PinsPage(p); break;
            case Page.Widgets: WidgetsPage(p); break;
            case Page.Look: LookPage(p); break;
            case Page.General: GeneralPage(p); break;
            default: AboutPage(p); break;
        }
    }

    /// <summary>
    /// 各ページを作って、検索語を含む行（タイトル・説明・選択肢のどれか）だけを抜き出して並べる。
    /// 行はそのまま使うので、検索結果の上で設定を変えてもページと同じように動く。
    /// </summary>
    private void SearchResults(StackPanel p)
    {
        var words = _query.Split(' ', '　').Where(w => w.Length > 0).ToArray();
        bool Match(string s) => words.All(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));
        int found = 0;
        _flat = true;
        try
        {
            foreach (var (page, _, label) in Pages)
            {
                if (page == Page.About) continue;
                var tmp = new StackPanel();
                RenderInto(page, tmp);
                var hits = new List<FrameworkElement>();
                Collect(tmp, label, Match, hits);
                if (hits.Count == 0) continue;
                var link = new Tile { Margin = new Thickness(0, found == 0 ? 0 : 20, 0, 6), Padding = new Thickness(4, 2, 4, 2), HorizontalAlignment = HorizontalAlignment.Left };
                link.Child = UiKit.Row(UiKit.Label(label, 14, T.Text, FontWeights.SemiBold), Spaced(UiKit.Glyph("", 10, T.TextSecondary), 8));
                var target = page;
                link.Click += _ => { _page = target; _query = ""; Build(); };
                p.Children.Add(link);
                foreach (var h in hits)
                {
                    if (h.Parent is Panel parent) parent.Children.Remove(h);
                    p.Children.Add(h);
                }
                found += hits.Count;
            }
        }
        finally { _flat = false; }
        if (found == 0)
            p.Children.Add(Note(L.T("一致する設定はありません。別の言葉で検索してみてください。")));
    }

    private static void Collect(Panel panel, string pageLabel, Func<string, bool> match, List<FrameworkElement> hits)
    {
        foreach (var child in panel.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is string text) { if (match(text + " " + pageLabel)) hits.Add(child); }
            else if (child is Panel inner) Collect(inner, pageLabel, match, hits);
        }
    }

    private void StartPage(StackPanel p)
    {
        var which = new[] { ("ModernShell", L.T("ModernShell のスタート")), ("Windows", L.T("Windows 標準のスタート")) };
        Group(p, "", L.T("開き方"), L.T("どのスタートを、どこに開くか"),
            DropDownRow(L.T("Win キーで開くスタート"), L.T("Win+E などの組み合わせはどちらでもそのまま使えます"),
                which, _s.ReplaceStartMenu ? "ModernShell" : "Windows", v => _s.ReplaceStartMenu = v == "ModernShell"),
            DropDownRow(L.T("スタート ボタンで開くスタート"), L.T("タスクバーのスタートボタン（ModernShell / 純正どちらのタスクバーでも）をクリックしたとき"),
                which, _s.InterceptNativeStartButton ? "ModernShell" : "Windows", v => _s.InterceptNativeStartButton = v == "ModernShell"),
            DropDownRow(L.T("スタート メニューを開く位置"), L.T("自動はスタートボタンの位置に合わせます。Windows 標準のスタートも、ModernShell のタスクバー使用中はこの位置に開きます"),
                new[] { ("Auto", L.T("自動")), ("Left", L.T("左")), ("Center", L.T("中央")) },
                _s.StartMenuPosition.ToString(), v => _s.StartMenuPosition = Enum.Parse<StartMenuPosition>(v)));

        Group(p, "", L.T("レイアウト"), "",
            ToggleRow(L.T("開いたときに「すべてのアプリ」を表示"), L.T("ピン留めではなくアプリの一覧から始めます"), _s.ShowAllAppsFirst, v => _s.ShowAllAppsFirst = v),
            NumberRow(L.T("ピン留めの列数"), "", 3, 8, _s.PinnedColumns, L.T("列"), v => _s.PinnedColumns = (int)v, def: 6),
            ToggleRow(L.T("「最近使った」を表示"), L.T("ピン留めの下に、最近開いたアプリを 1 行で表示します（開いた記録はこの PC の中だけに保存されます）"), _s.ShowRecentApps, v => _s.ShowRecentApps = v),
            ToggleRow(L.T("「よく使う」を表示"), L.T("ピン留めの下に、よく開くアプリ（2 回以上）を 1 行で表示します"), _s.ShowFrequentApps, v => _s.ShowFrequentApps = v),
            ButtonRow(L.T("利用履歴を消去"), L.T("「最近使った」「よく使う」の元になっている、アプリを開いた記録を消します"), L.T("消去"),
                () => { if (Confirm(L.T("アプリを開いた記録を消しますか？"))) { UsageStats.Clear(); _s.Save(); } }),
            ButtonRow(L.T("ウィジェット"), L.T("スタート メニューの右側に出すウィジェット（天気・時計など）の設定は、「ウィジェット」ページにあります"), L.T("開く"),
                () => { _page = Page.Widgets; Build(); }));

        p.Children.Add(Note(L.T("ピン留めはスタート メニュー上でドラッグして並べ替え、右クリックで追加・削除できます。初期状態に戻すには「ピン留めとアイコン」へ。")));
    }

    /// <summary>余白の選択肢（0 = なし / 画面端に密着）。</summary>
    private static (string value, string label)[] Margins(bool floating) => new[]
    {
        ("0", floating ? "0 px" : L.T("なし（画面端に密着）")), ("4", "4 px"), ("8", "8 px"), ("12", "12 px"), ("16", "16 px"), ("24", "24 px"),
    };

    private void TaskbarPage(StackPanel p)
    {
        bool on = _s.UseCustomTaskbar;
        bool vertical = _s.TaskbarEdge is TaskbarEdge.Left or TaskbarEdge.Right;
        bool floating = _s.TaskbarStyle == TaskbarStyle.Floating;

        if (!_flat) p.Children.Add(Preview());
        p.Children.Add(ToggleRow(L.T("ModernShell のタスクバーを使う"),
            L.T("純正タスクバーを隠して、下で選んだ見た目で表示します。トレイアイコンも表示されます。終了すると純正に戻ります"),
            on, v => _s.UseCustomTaskbar = v));

        p.Children.Add(Section(L.T("スタイル")));
        p.Children.Add(ChoiceCards(L.T("スタイル"), new[]
        {
            (L.T("帯"), L.T("画面の端に密着した、従来型のタスクバー"), TaskbarStyle.Band == _s.TaskbarStyle, (Action)(() => _s.TaskbarStyle = TaskbarStyle.Band), Art.Band),
            (L.T("アイランド"), L.T("端から浮いた島。アプリ数に合わせて伸び縮み"), floating, () => _s.TaskbarStyle = TaskbarStyle.Floating, Art.Island),
        }, on));

        p.Children.Add(Section(L.T("スタート・検索・タスク ビューの位置")));
        p.Children.Add(ChoiceCards(L.T("スタートボタンの位置"), new[]
        {
            (L.T("アプリと一緒"), L.T("アプリのボタンと 1 つのかたまりにする（下の「アプリの配置」で中央か左）"), _s.StartButtonPosition == StartButtonPosition.WithApps,
                (Action)(() => _s.StartButtonPosition = StartButtonPosition.WithApps), Art.WithApps),
            (L.T("左端"), L.T("常に画面の左端に置き、アプリは別に並べる"), _s.StartButtonPosition == StartButtonPosition.LeftEdge,
                () => _s.StartButtonPosition = StartButtonPosition.LeftEdge, Art.LeftEdge),
        }, on));

        p.Children.Add(Section(L.T("詳細")));
        Group(p, "", L.T("配置と大きさ"), L.T("位置・高さ・余白"),
            DropDownRow(L.T("アプリの配置"), L.T("アプリのボタン（「アプリと一緒」のときはスタートボタンも）の位置。「すべて中央」は、スタート・アプリ・ウィジェット・トレイを 1 つのかたまりにして中央に置きます"),
                new[] { ("Center", L.T("中央")), ("AllCenter", L.T("すべて中央")), ("Left", L.T("左")) }, _s.TaskbarAlignment.ToString(), v => _s.TaskbarAlignment = Enum.Parse<TaskbarAlignment>(v), on),
            DropDownRow(L.T("画面上の位置"), L.T("左・右に置くと、縦向きのタスクバーになります（ボタンが縦に並びます）"),
                new[] { ("Bottom", L.T("下")), ("Top", L.T("上")), ("Left", L.T("左")), ("Right", L.T("右")) }, _s.TaskbarEdge.ToString(),
                v => _s.TaskbarEdge = Enum.Parse<TaskbarEdge>(v), on),
            DropDownRow(L.T("タスクバーの大きさ"), L.T("バーの高さ（縦向きなら幅）とアプリ アイコンの大きさが、3 段階で一緒に変わります。小: 40 px・アイコン 20 px / 中: 44 px・24 px / 大: 48 px・28 px"),
                new[] { ("Small", L.T("小")), ("Medium", L.T("中（標準）")), ("Large", L.T("大")) }, _s.TaskbarSize.ToString(), v => _s.TaskbarSize = Enum.Parse<TaskbarSize>(v), on),
            // 余白は左右と上下を別々に（画面を基準にした向き）。帯なら 0 で画面端に密着、アイランドなら島と画面の端のあいだの距離
            NumberRow(floating ? L.T("アイランドの余白（左右）") : L.T("帯の余白（左右）"),
                floating ? L.T("島と画面の左右の端とのあいだの距離") : L.T("帯と画面の左右の端とのあいだの距離。0 より大きいと帯は端から離れて角丸になります"),
                0, 64, floating ? _s.IslandMarginX : _s.BandMarginX, "px", v => { if (floating) _s.IslandMarginX = (int)v; else _s.BandMarginX = (int)v; }, on, def: floating ? 8 : 0),
            NumberRow(floating ? L.T("アイランドの余白（上下）") : L.T("帯の余白（上下）"),
                floating ? L.T("島と画面の上下の端とのあいだの距離") : L.T("帯と画面の上下の端とのあいだの距離。Windows は画面端に接したウィンドウの角を丸めないので、角丸にしたいときは余白を付けてください"),
                0, 64, floating ? _s.IslandMarginY : _s.BandMarginY, "px", v => { if (floating) _s.IslandMarginY = (int)v; else _s.BandMarginY = (int)v; }, on, def: floating ? 8 : 0),
            NumberRow(L.T("画面端の位置調整"), L.T("標準は 0 px（ぴったり）です。画面の端とバーのあいだに細い隙間が見えるときだけ、マイナスにして、バーを画面の端の外へ動かします（帯型で余白が 0 のときに効きます）"),
                -32, 0, _s.TaskbarEdgeOffset, "px", v => _s.TaskbarEdgeOffset = (int)v, on && !floating, def: 0));

        p.Children.Add(Section(L.T("見た目")));
        Group(p, "\uE75B", L.T("タスクバー"), L.T("タスクバーと、そこから開くプレビュー・一覧・メニューの背景"),
            DropDownRow(L.T("背景"), L.T("透過なし: テーマの色（ライト/ダーク）。透過: テーマの色を半透明で。ぼかし: 壁紙を強くぼかした下地にテーマの色を重ねる"),
                new[] { ("Solid", L.T("透過なし")), ("Transparent", L.T("透過")), ("Blur", L.T("ぼかし")) }, _s.TaskbarBackground.ToString(), v => _s.TaskbarBackground = Enum.Parse<SurfaceStyle>(v), _s.UseCustomTaskbar),
            NumberRow(L.T("不透明度"), L.T("重ねるテーマの色の濃さ。0 で下地のまま、100 で透過なしと同じ"), 0, 100, _s.TaskbarOpacity, "%", v => _s.TaskbarOpacity = (int)v, _s.UseCustomTaskbar && _s.TaskbarBackground != SurfaceStyle.Solid, def: 60),
            ToggleRow(L.T("バーをピル型にする"), L.T("バーの角を、高さの半分まで丸めます。画面の端から離す（余白を付ける）と、浮いたピルのように見えます"), _s.TaskbarPill, v => _s.TaskbarPill = v, _s.UseCustomTaskbar));
        p.Children.Add(Section(L.T("タスクバーの背景に重ねる色")));
        p.Children.Add(BarTintSwatches());

        if (!_flat)
            p.Children.Add(InfoBar(L.T("困ったときは Ctrl + Alt + Shift + F12"),
                L.T("いつでも ModernShell を終了して純正タスクバーに戻せます。トレイの「^」→ ModernShell アイコンの右クリック、スタートの電源メニューからも終了できます。")));
    }

    /// <summary>「ボタンと操作」ページ: ボタン・時計・プレビュー・メニューの位置。</summary>
    private void TaskbarItemsPage(StackPanel p)
    {
        bool on = _s.UseCustomTaskbar;
        bool vertical = _s.TaskbarEdge is TaskbarEdge.Left or TaskbarEdge.Right;
        if (!on && !_flat) p.Children.Add(Note(L.T("「タスクバー」ページで ModernShell のタスクバーを使うと、ここの設定が反映されます。")));
        p.Children.Add(Section(L.T("ボタンと時計")));
        Group(p, "", L.T("ボタン"), L.T("タスクバーに並べるもの"),
            ToggleRow(L.T("タスクバーを自動的に隠す"), L.T("使わないときは画面の外に隠し、画面の端（タスクバーのある側）にカーソルを当てると出します。作業領域は確保しません"), _s.AutoHideTaskbar, v => _s.AutoHideTaskbar = v, on),
            ToggleRow(L.T("隠している間、画面の端に細い線を残す"), L.T("線の区切りは開いているアプリです。前面のアプリはアクセント色。カーソルを端に当てるとバーが出ます。全画面のアプリの間は出しません"), _s.AutoHideHairline, v => _s.AutoHideHairline = v, on && _s.AutoHideTaskbar),
            ToggleRow(L.T("複数のモニターにタスクバーを表示"), L.T("メイン以外のモニターにもタスクバーを出し、そのモニターにあるウィンドウのボタンだけを並べます（時計付き）。メインのモニターには、メインにあるウィンドウだけが出ます"), _s.MultiMonitorTaskbar, v => _s.MultiMonitorTaskbar = v, on),
            ToggleRow(L.T("検索ボタンを表示"), "", _s.ShowSearchButton, v => _s.ShowSearchButton = v, on),
            ToggleRow(L.T("タスク ビュー ボタンを表示"), "", _s.ShowTaskViewButton, v => _s.ShowTaskViewButton = v, on),
            ToggleRow(L.T("同じアプリのボタンをまとめる"), L.T("複数のウィンドウは一覧で選べます"), _s.GroupTaskbarButtons, v => _s.GroupTaskbarButtons = v, on),
            ToggleRow(L.T("グループのあいだに区切り線を引く"), L.T("スタート・アプリ・ウィジェット・トレイのあいだに、細い線を引きます"), _s.ShowBarDividers, v => _s.ShowBarDividers = v, on),
            ToggleRow(L.T("ウィンドウの数を点で表示"), L.T("同じアプリのウィンドウが複数あるとき、実行中の印をウィンドウの数（最大 3）だけ並べます"), _s.ShowWindowCountDots, v => _s.ShowWindowCountDots = v, on),
            ToggleRow(L.T("進捗とバッジを表示"), L.T("ダウンロードなどの進捗バーと、通知の数などの小さな印（アプリが付けるもの）をボタンに表示します"), _s.ShowTaskbarBadges, v => _s.ShowTaskbarBadges = v, on),
            ToggleRow(L.T("ボタンにウィンドウ名を表示"), "", _s.ShowTaskbarLabels, v => _s.ShowTaskbarLabels = v, on),
            ToggleRow(L.T("ホバー時にアイコンを拡大"), L.T("カーソルを置いたアプリのアイコンを少し大きくします"), _s.MagnifyOnHover, v => _s.MagnifyOnHover = v, on),
            NumberRow(L.T("拡大率"), L.T("カーソルを置いたアイコンの最大の拡大率。バーの太さ・ボタンの大きさに収まる範囲で効きます"), 100, 250, _s.MagnifyScale, "%", v => _s.MagnifyScale = (int)v, on && _s.MagnifyOnHover, step: 5, def: 130),
            ToggleRow(L.T("両隣のアイコンも拡大"), L.T("Mac のドックのように、カーソルの下を最大に、両隣も距離に応じてなめらかに拡大します。オフのときは、カーソルの下のアイコンだけ"),
                _s.MagnifyNeighbors, v => _s.MagnifyNeighbors = v, on && _s.MagnifyOnHover),
            ToggleRow(L.T("時計に秒を表示"), "", _s.ShowSeconds, v => _s.ShowSeconds = v, on));

        Group(p, "", L.T("時計"), L.T("右端の日時の表示（縦のバーでは、幅に合わせて自動で短くします）"),
            DropDownRow(L.T("文字の揃え"), "", new[] { ("Left", L.T("左揃え")), ("Center", L.T("中央揃え")), ("Right", L.T("右揃え")) },
                _s.ClockAlign.ToString(), v => _s.ClockAlign = Enum.Parse<ClockAlign>(v), on && !vertical),
            DropDownRow(L.T("時刻の形式"), L.T("「システム」は、Windows の地域設定（設定 → 時刻と言語 → 日付と時刻）の短い時刻の形です"),
                new[] { ("System", L.T("システムに合わせる")), ("H24", L.T("24 時間（9:05）")), ("H24Pad", L.T("24 時間（09:05）")), ("H12", L.T("12 時間（午前/午後）")), ("Hidden", L.T("表示しない")) },
                _s.ClockTimeFormat.ToString(), v => _s.ClockTimeFormat = Enum.Parse<ClockTimeFormat>(v), on && !vertical),
            DropDownRow(L.T("日付の形式"), L.T("「システム」は、Windows の地域設定の短い日付の形です"),
                new[] { ("System", L.T("システムに合わせる")), ("YearMonthDay", "2026/11/16"), ("MonthDay", "11/16"), ("WithWeekday", L.T("11月16日 (月)")), ("Hidden", L.T("表示しない")) },
                _s.ClockDateFormat.ToString(), v => _s.ClockDateFormat = Enum.Parse<ClockDateFormat>(v), on && !vertical));

        p.Children.Add(Section(L.T("ポップアップ")));
        Group(p, "", L.T("プレビュー"), L.T("カーソルを置いたときに出る一覧"),
            ToggleRow(L.T("ウィンドウのプレビューを表示"), L.T("実行中のアプリのボタンに置くと、ウィンドウの中身を縮小表示します。クリックで切り替え、× で閉じます。複数ウィンドウのボタンをクリックしたときもプレビューを出します（オフのときは、ウィンドウ名のリストを出します）"),
                _s.ShowThumbnailPreview, v => _s.ShowThumbnailPreview = v, on),
            ToggleRow(L.T("プレビューにメディア操作を表示"), L.T("音楽や動画を再生中のアプリのプレビューに、前の曲・再生/一時停止・次の曲のボタンを出します"),
                _s.ShowPreviewMediaControls, v => _s.ShowPreviewMediaControls = v, on && _s.ShowThumbnailPreview),
            ToggleRow(L.T("タスク ビュー ボタンで仮想デスクトップを表示"), L.T("カーソルを置くとデスクトップの一覧を表示し、クリックで切り替え、＋ で追加します"),
                _s.ShowDesktopsOnTaskView, v => _s.ShowDesktopsOnTaskView = v, on && _s.ShowTaskViewButton),
            NumberRow(L.T("表示までの待ち時間"), "", 0, 2000, _s.PreviewDelayMs, "ms", v => _s.PreviewDelayMs = (int)v, on && (_s.ShowThumbnailPreview || _s.ShowDesktopsOnTaskView), step: 50, def: 400),
            DropDownRow(L.T("プレビューの大きさ"), "",
                new[] { ("Small", L.T("小")), ("Medium", L.T("中（標準）")), ("Large", L.T("大")) },
                _s.PreviewSize, v => _s.PreviewSize = v, on && _s.ShowThumbnailPreview));

        Group(p, "", L.T("メニューとポップアップの位置"), L.T("ボタンやカーソルのどこを基準に出すか（縦のバーでは 左 = 上、右 = 下）"),
            DropDownRow(L.T("アイコンの右クリック"), "", AlignOptions(), _s.IconMenuAlign.ToString(), v => _s.IconMenuAlign = Enum.Parse<MenuAlign>(v), on),
            DropDownRow(L.T("タスクバーの右クリック"), L.T("空いている場所を右クリックしたとき。カーソルの位置が基準です"), AlignOptions(), _s.BarMenuAlign.ToString(), v => _s.BarMenuAlign = Enum.Parse<MenuAlign>(v), on),
            DropDownRow(L.T("ウィンドウのプレビュー"), "", AlignOptions(), _s.PreviewAlign.ToString(), v => _s.PreviewAlign = Enum.Parse<MenuAlign>(v), on),
            DropDownRow(L.T("ウィンドウ名のリスト"), L.T("プレビューをオフにしたときに出るリスト"), AlignOptions(), _s.WindowListAlign.ToString(), v => _s.WindowListAlign = Enum.Parse<MenuAlign>(v), on),
            DropDownRow(L.T("仮想デスクトップの一覧"), "", AlignOptions(), _s.DesktopsAlign.ToString(), v => _s.DesktopsAlign = Enum.Parse<MenuAlign>(v), on),
            DropDownRow(L.T("隠れているトレイアイコンの一覧"), L.T("「^」ボタンで開く一覧"), AlignOptions(), _s.TrayOverflowAlign.ToString(), v => _s.TrayOverflowAlign = Enum.Parse<MenuAlign>(v), on));
    }

    private void PinsPage(StackPanel p)
    {
        bool on = _s.UseCustomTaskbar;
        p.Children.Add(Section(L.T("タスクバーのピン留め"), first: true));
        p.Children.Add(ChoiceCards(L.T("タスクバーのピン留め"), new[]
        {
            (L.T("Windows と共有"), L.T("Windows のタスクバーと同じアプリ・同じ並び順を表示（自動で追従）"),
                _s.TaskbarPinSource == TaskbarPinSource.Windows, (Action)(() => _s.TaskbarPinSource = TaskbarPinSource.Windows), Art.PinsWindows),
            (L.T("ModernShell で管理"), L.T("右クリックでピン留め、ドラッグで並べ替え"),
                _s.TaskbarPinSource == TaskbarPinSource.ModernShell, () => _s.TaskbarPinSource = TaskbarPinSource.ModernShell, Art.PinsOwn),
        }, on));
        if (_s.TaskbarPinSource == TaskbarPinSource.Windows)
            p.Children.Add(NoteRow(L.T("Windows はアプリからピン留めを変更する手段を提供していないため、共有は「Windows → ModernShell」の一方向です。") +
                                   L.T("ModernShell のタスクバーで右クリックしてピン留めしたものは、Windows のピン留めの後ろに追加で並びます。")));
        else
            p.Children.Add(ButtonRow(L.T("Windows のピン留めを取り込む"), L.T("Windows のタスクバーと同じ内容・並び順で置き換えます（その後は ModernShell で自由に変更できます）"),
                L.T("取り込む"), () =>
                {
                    var ids = ImportWindowsPins?.Invoke();
                    if (ids == null || ids.Count == 0) return;
                    _s.PinnedTaskbar = ids;
                    Commit();
                }, enabled: on));

        p.Children.Add(Section(L.T("アイコン")));
        int custom = _s.CustomIcons.Count;
        p.Children.Add(ButtonRow(L.T("変更したアイコンを元に戻す"),
            custom > 0 ? L.F("タスクバーのボタンで「アイコンの変更」をした {0} 個のアプリを、元のアイコンに戻します", custom)
                       : L.T("タスクバーのボタンを右クリック →「アイコンの変更」で、アプリごとに好きなアイコンを選べます"),
            L.T("すべて元に戻す"), () => { _s.CustomIcons.Clear(); Commit(); }, enabled: custom > 0));

        p.Children.Add(Section(L.T("スタート メニューのピン留め")));
        p.Children.Add(ButtonRow(L.T("スタート メニューのピン留めを初期状態に戻す"), L.T("スタートメニュー上でドラッグして並べ替え、右クリックで追加・削除できます"),
            L.T("初期状態に戻す"), () =>
            {
                if (!Confirm(L.T("スタート メニューのピン留めを初期状態に戻しますか？"))) return;
                _s.PinnedStart.Clear();
                _s.PinnedStart.AddRange(Settings.DefaultPinnedStart);
                Commit();
            }));

        p.Children.Add(Section(L.T("通知領域（トレイ）")));
        p.Children.Add(NoteRow(L.T("トレイのアイコンは、タスクバーと「^」の一覧のあいだをドラッグして、常に表示するかどうかを切り替えられます。並び順もドラッグで変えられます。")));
    }

    private void WidgetsPage(StackPanel p)
    {
        p.Children.Add(ToggleRow(L.T("スタート メニューにウィジェットを表示"), L.T("スタート メニューの右側に表示します"), _s.ShowWidgets, v => _s.ShowWidgets = v));
        bool on = _s.ShowWidgets;
        p.Children.Add(Section(L.T("スタート メニューのウィジェット")));
        if (!_flat) p.Children.Add(Note(L.T("↑↓ で並び順を変えられます。スタート メニュー上でもウィジェットの見出しをドラッグして並べ替えできます。")));
        var order = _s.OrderedWidgets();
        for (int i = 0; i < order.Count; i++)
        {
            var id = order[i];
            var (title, desc) = id switch
            {
                "clock" => (L.T("時計とカレンダー"), ""),
                "weather" => (L.T("天気"), L.T("Open-Meteo（無料・登録不要）から取得します。地点の名前が送信されます")),
                "system" => (L.T("システム"), L.T("CPU・メモリ・ディスクの使用状況")),
                "folders" => (L.T("フォルダー"), L.T("ドキュメント・ダウンロードなどへのショートカット")),
                "recent" => (L.T("最近使ったファイル"), ""),
                _ => (L.T("メモ"), L.T("書いた内容は自動で保存されます")),
            };
            p.Children.Add(WidgetRow(id, title, desc, i, order.Count, on));
            if (id == "weather")
                p.Children.Add(TextRow(L.T("天気の地点"), L.T("都市名（例: Tokyo, Osaka, 札幌）"), _s.WeatherCity, v => _s.WeatherCity = v, on && _s.WidgetWeather));
            if (id == "weather")
                p.Children.Add(NumberRow(L.T("天気の更新間隔"), L.T("スタート メニューを開いている間も、この間隔で自動更新します（カードの更新ボタンで今すぐ更新もできます）"), 5, 360, _s.WeatherRefreshMinutes, L.T("分"), v => _s.WeatherRefreshMinutes = (int)v, on && _s.WidgetWeather, step: 5, def: 20));
        }
        BarWidgetsSection(p);
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
        right.Children.Add(Arrow("", L.T("上へ"), index > 0, index - 1));
        right.Children.Add(Arrow("", L.T("下へ"), index < count - 1, index + 2));
        right.Children.Add(sw);
        return Card(Texts(title, desc), right, enabled);
    }

    /// <summary>タスクバーに並べるウィジェットと、区切り線・スペース（「ウィジェット」ページの後半）。</summary>
    private void BarWidgetsSection(StackPanel p)
    {
        bool on = _s.UseCustomTaskbar;
        p.Children.Add(Section(L.T("タスクバーのウィジェット")));
        if (!on && !_flat) p.Children.Add(Note(L.T("「タスク バー」ページで ModernShell のタスクバーを使うと、ここで選んだウィジェットが並びます。")));
        p.Children.Add(Note(L.T("ウィジェットをクリックすると、タスクバーに追加／削除できます。位置はバー上でドラッグして変えられます。")));
        var wrap = new WrapPanel { Margin = new Thickness(-4, 4, -4, 0), IsEnabled = on, Opacity = on ? 1 : 0.45 };
        foreach (var w in BarWidgetCatalog.All) wrap.Children.Add(WidgetPreviewCard(w));
        p.Children.Add(wrap);

        // 区切り線とスペース: 何個でも足せる
        var partRows = new List<Border>();
        foreach (var pid in _s.BarWidgets.Where(BarWidgetCatalog.IsPart).ToList())
        {
            var info = BarWidgetCatalog.Find(pid);
            if (info == null) continue;
            string id = pid;
            partRows.Add(ButtonRow(L.T(info.Title), L.T(info.Description), L.T("削除"), () => { _s.BarWidgets.Remove(id); _s.BarWidgetZones.Remove(id); Commit(); }, enabled: on));
        }
        partRows.Add(ButtonRow(L.T("区切り線を追加"), L.T("グループのあいだに細い線を入れます"), L.T("追加"), () => { _s.BarWidgets.Add(BarWidgetCatalog.NewPartId("divider")); Commit(); }, enabled: on));
        partRows.Add(ButtonRow(L.T("スペースを追加"), L.T("ウィジェットやボタンのあいだに空きを入れます"), L.T("追加"), () => { _s.BarWidgets.Add(BarWidgetCatalog.NewPartId("spacer")); Commit(); }, enabled: on));
        Group(p, "", L.T("バーのパーツ"), L.T("区切り線とスペースを足して、並びを整えられます"), partRows.ToArray());
    }

    /// <summary>ウィジェットの見た目を表す静的な絵（サンプルの値）。実物のウィジェットと同じ寸法・配色にそろえてある。</summary>
    private FrameworkElement StaticPreview(string id)
    {
        TextBlock Txt(string s, double size, Brush? fg = null, FontWeight? fw = null) { var t = UiKit.Label(s, size, fg ?? T.Text, fw ?? FontWeights.SemiBold); t.TextTrimming = TextTrimming.None; return t; }
        Brush track = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
        var green = new SolidColorBrush(Color.FromRgb(0x2E, 0xB8, 0x72));
        // 円グラフ（実物の Ring と同じ: 太さ 3、中に数字かアイコン）
        FrameworkElement Ring(double size, Brush color, double pct, string text, string? glyph = null)
        {
            var g = new Grid { Width = size, Height = size };
            g.Children.Add(new Ellipse { Stroke = track, StrokeThickness = 3, Width = size - 3, Height = size - 3 });
            double r = (size - 3) / 2, c = size / 2, a = Math.Min(359.9, pct / 100 * 360) * Math.PI / 180;
            var fig = new PathFigure { StartPoint = new Point(c, c - r) };
            fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true));
            g.Children.Add(new System.Windows.Shapes.Path { Data = new PathGeometry(new[] { fig }), Stroke = color, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            if (glyph != null) g.Children.Add(UiKit.Glyph(glyph, size * 0.4, color));
            else g.Children.Add(Center(Txt(text, 9)));
            return g;
        }
        static FrameworkElement Pad(FrameworkElement e, double left) { e.Margin = new Thickness(left, 0, 0, 0); return e; }
        static FrameworkElement Center(FrameworkElement e, double bottom = 0) { e.HorizontalAlignment = HorizontalAlignment.Center; e.Margin = new Thickness(0, 0, 0, bottom); return e; }
        StackPanel Row(params UIElement[] c)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var x in c) sp.Children.Add(x);
            return sp;
        }
        StackPanel Col(double left, params UIElement[] c)
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 0, 0) };
            foreach (var x in c) sp.Children.Add(x);
            return sp;
        }
        // 他のウィジェットと同じ、薄い地と縁のある角丸のカード
        Border Card(FrameworkElement child, double padX) { child.Margin = new Thickness(padX, 0, padX, 0); return new Border { Height = 38, Background = T.Hover, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6), Child = child }; }
        FrameworkElement VolBar(double pct, double left)
        {
            var fill = new Border { CornerRadius = new CornerRadius(3), Background = T.AccentBrush, VerticalAlignment = VerticalAlignment.Bottom, Height = 22 * pct / 100 };
            return new Border { Width = 6, Height = 22, CornerRadius = new CornerRadius(3), Background = track, Child = fill, Margin = new Thickness(left, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        }
        var dim = T.TextSecondary;
        return id switch
        {
            "system" => Card(Row(Ring(26, T.AccentBrush, 23, "23"), Pad(Ring(26, green, 58, "58"), 6)), 8),
            "network" => Card(Row(UiKit.Glyph("", 18), Pad(Txt("イーサネット", 12), 8)), 10),
            "netspeed" => Card(Row(
                Ring(30, new SolidColorBrush(NetSpeedColors.Down), 40, "", NetSpeedColors.DownGlyph), Col(6, Txt("156", 13), Txt("Kbps", 10, dim, FontWeights.Normal)),
                Pad(Ring(30, new SolidColorBrush(NetSpeedColors.Up), 25, "", NetSpeedColors.UpGlyph), 8), Col(6, Txt("74", 13), Txt("Kbps", 10, dim, FontWeights.Normal))), 10),
            "volume" => Card(Row(UiKit.Glyph(VolumeGlyph.For(50, false), 16), VolBar(60, 6)), 10),
            "quicksettings" => Card(Row(UiKit.Glyph("", 17), Pad(UiKit.Glyph(VolumeGlyph.For(50, false), 17), 12), VolBar(60, 8)), 10),
            "ime" => Card(new Border { Width = 32, Height = 32, CornerRadius = T.Corner(8), Background = T.AccentBrush, Child = Center(Txt("あ", 16, ImeLook.OnAccent(T))) }, 8),
            "battery" => Row(Ring(28, green, 87, "87"), Pad(UiKit.Glyph("", 10), 4)),
            "nowplaying" => NowPlayingPreview(track),
            "weather" => Card(Row(WeatherSymbols.Create(0, true, 30), Col(8, Txt("18°", 13), Txt("横浜市  快晴", 11, dim, FontWeights.Normal))), 8),
            "calendar" => Card(Row(
                new Border
                {
                    Width = 30, Height = 32, CornerRadius = T.Corner(6), Background = T.Surface, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
                    Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Center(Txt("月", 8.5, T.AccentBrush), -2), Center(Txt("23", 14)) } },
                },
                Col(8, Txt("11月23日(月)", 13), Txt("勤労感謝の日", 11, dim, FontWeights.Normal))), 6),
            "clock" => Card(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Center(Txt("12:34", 15)), Center(Txt("11月23日(月)", 12, T.Text, FontWeights.Normal)) } }, 12),
            "notification" => Card(new Grid { Width = 24, Height = 24, Children = { UiKit.Glyph("", 19), new Border { MinWidth = 15, Height = 15, CornerRadius = new CornerRadius(8), Background = NotificationLook.Badge, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -7, 0), Child = Center(Txt("4", 10, Brushes.White)) } } }, 14),
            "pomodoro" => new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Center(Txt("Focus 1/4", 10, dim, FontWeights.Normal)), Center(Txt("25:00", 15)) } },
            _ => Txt(id, 13),
        };
    }

    /// <summary>「再生中の曲」の絵: ジャケット・曲名とアーティスト・再生ボタン・再生位置の下線。</summary>
    private FrameworkElement NowPlayingPreview(Brush track)
    {
        var cover = new Border { Width = 28, Height = 28, CornerRadius = T.Corner(6), Background = T.Hover, VerticalAlignment = VerticalAlignment.Center, Child = UiKit.Glyph("", 14, T.TextSecondary) };
        var title = UiKit.Label("曲名", 13, T.Text, FontWeights.Medium); title.TextTrimming = TextTrimming.None;
        var artist = UiKit.Label("アーティスト", 11, T.TextSecondary); artist.TextTrimming = TextTrimming.None;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Children = { title, artist } };
        var play = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = T.Hover, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = UiKit.Glyph("", 12) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 3), HorizontalAlignment = HorizontalAlignment.Left, Children = { cover, text, play } };
        var line = new Border { Height = 2, Background = track, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 6, 3), CornerRadius = new CornerRadius(1) };
        line.Child = new Border { Width = 40, Height = 2, HorizontalAlignment = HorizontalAlignment.Left, Background = T.AccentBrush, CornerRadius = new CornerRadius(1) };
        return new Border { Height = 38, Background = T.Hover, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6), Child = new Grid { Children = { row, line } } };
    }

    /// <summary>ウィジェットの絵を載せた、クリックで追加／削除するカード。</summary>
    private Tile WidgetPreviewCard(BarWidgetInfo w)
    {
        bool added = _s.BarWidgets.Contains(w.Id);
        var preview = StaticPreview(w.Id);
        preview.IsHitTestVisible = false;
        preview.HorizontalAlignment = HorizontalAlignment.Center;
        preview.VerticalAlignment = VerticalAlignment.Center;

        var stage = new Border
        {
            Height = 64, Background = T.SolidBackground, CornerRadius = T.Corner(6), Margin = new Thickness(0, 0, 0, 8),
            Child = new Grid { Children = { preview }, ClipToBounds = true }, ClipToBounds = true,
        };
        var title = UiKit.Label(L.T(w.Title), 13);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var mark = UiKit.Glyph(added ? "" : "", 12, added ? T.AccentBrush : T.TextSecondary);
        mark.Margin = new Thickness(0, 0, 6, 0);
        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        line.Children.Add(mark);
        line.Children.Add(title);

        var body = new StackPanel { Margin = new Thickness(10) };
        body.Children.Add(stage);
        body.Children.Add(line);
        var card = new Tile
        {
            Width = 236, Margin = new Thickness(4), Child = body, CornerRadius = T.Corner(8), Background = T.Surface,
            BorderThickness = new Thickness(added ? 2 : 1), BorderBrush = added ? T.AccentBrush : T.Stroke,
            ToolTip = L.T(w.Description), Tag = L.T(w.Title) + " " + L.T(w.Description),
        };
        // Tile は、マウスが離れると背景と枠を透明に戻してしまうので、カードの見た目をここで保つ
        var normalBorder = card.BorderBrush;
        void Restore() { card.Background = card.IsMouseOver ? T.Hover : T.Surface; card.BorderBrush = normalBorder; }
        card.MouseEnter += (_, _) => Restore();
        card.MouseLeave += (_, _) => Restore();
        card.MouseLeftButtonDown += (_, _) => Restore();
        card.GotKeyboardFocus += (_, _) => Restore();
        card.LostKeyboardFocus += (_, _) => Restore();
        card.Click += _ =>
        {
            if (added) { _s.BarWidgets.Remove(w.Id); _s.BarWidgetZones.Remove(w.Id); }
            else _s.BarWidgets.Add(w.Id);
            Commit();
        };
        return card;
    }

    private void LookPage(StackPanel p)
    {
        p.Children.Add(Section(L.T("モード"), first: true));
        p.Children.Add(ChoiceCards(L.T("モード テーマ ライト ダーク"), new[]
        {
            (L.T("Windows に合わせる"), "", _s.Theme == ThemeMode.System, (Action)(() => _s.Theme = ThemeMode.System), Art.ThemeSystem),
            (L.T("ライト"), "", _s.Theme == ThemeMode.Light, () => _s.Theme = ThemeMode.Light, Art.ThemeLight),
            (L.T("ダーク"), "", _s.Theme == ThemeMode.Dark, () => _s.Theme = ThemeMode.Dark, Art.ThemeDark),
        }, true));

        p.Children.Add(Section(L.T("アクセント カラー")));
        p.Children.Add(Swatches());

        p.Children.Add(Section(L.T("背景と効果")));
        // 3 択はどれも ModernShell が自分で描くので、GPU が無い PC でも同じ見た目になる
        Group(p, "\uE8FC", L.T("スタート メニュー"), L.T("スタート メニューと、そこから開くメニューの背景"),
            DropDownRow(L.T("背景"), L.T("透過なし: テーマの色（ライト/ダーク）。透過: テーマの色を半透明で。ぼかし: 壁紙を強くぼかした下地にテーマの色を重ねる"),
                new[] { ("Solid", L.T("透過なし")), ("Transparent", L.T("透過")), ("Blur", L.T("ぼかし")) }, _s.StartMenuBackground.ToString(), v => _s.StartMenuBackground = Enum.Parse<SurfaceStyle>(v)),
            NumberRow(L.T("不透明度"), L.T("重ねるテーマの色の濃さ。0 で下地のまま、100 で透過なしと同じ"), 0, 100, _s.StartMenuOpacity, "%",
                v => _s.StartMenuOpacity = (int)v, _s.StartMenuBackground != SurfaceStyle.Solid, def: 70));

        p.Children.Add(Note(L.T("タスクバーの背景・色・形は「タスクバー」ページで変えられます。")));

        Group(p, "\uE916", L.T("アニメーション"), L.T("開くときに滑り込ませるか、パッと表示するか"),
            ToggleRow(L.T("GPU が無い PC では軽く動かす"),
                L.F("グラフィックスの描画が GPU でないこと（ソフトウェア描画）を自動で見つけて、アニメーションを短くし、アイコンの拡大を省きます。今の PC: {0}", Anim.NoGpu ? L.T("GPU なし（軽量）") : L.T("GPU あり")),
                _s.AutoLightMode, v => _s.AutoLightMode = v),
            DropDownRow(L.T("アニメーションの速さ"), L.T("開くときの動きの長さ（スタート メニュー・プレビュー・メニューなど）"),
                new[] { ("Fast", L.T("速い")), ("Normal", L.T("標準")), ("Slow", L.T("ゆっくり")) },
                _s.AnimationSpeed.ToString(), v => _s.AnimationSpeed = Enum.Parse<AnimationSpeed>(v),
                _s.AnimateStartMenu || _s.AnimatePreviews || _s.AnimateTrayOverflow || _s.AnimateMenus),
            ToggleRow(L.T("スタート メニュー"), L.T("下から滑り込みながら表示します（フォルダーを開くときの拡大も）"), _s.AnimateStartMenu, v => _s.AnimateStartMenu = v),
            ToggleRow(L.T("ウィンドウのプレビューと仮想デスクトップの一覧"), "", _s.AnimatePreviews, v => _s.AnimatePreviews = v),
            ToggleRow(L.T("通知領域（「^」）の展開"), "", _s.AnimateTrayOverflow, v => _s.AnimateTrayOverflow = v),
            ToggleRow(L.T("右クリック メニュー・ジャンプリスト・ドロップダウン"), "", _s.AnimateMenus, v => _s.AnimateMenus = v));

        Group(p, "", L.T("形と文字"), "",
            FontRow(),
            NumberRow(L.T("角の丸み"), "", 0, 24, _s.CornerRadius, "px", v => _s.CornerRadius = v, def: 8),
            NumberRow(L.T("スタート メニューの文字サイズ"), L.T("アプリ名・見出し・検索欄・ウィジェットなど、スタート メニューの文字すべて"), 80, 200, _s.StartMenuTextScale, "%", v => _s.StartMenuTextScale = (int)v, step: 5, def: 100),
            NumberRow(L.T("メニューの文字サイズ"), "", 10, 20, _s.MenuFontSize, "px", v => _s.MenuFontSize = (int)v, def: 14));

        if (!_flat)
            p.Children.Add(InfoBar(L.T("ぼかしについて"),
                L.T("「ぼかし」は、後ろのウィンドウではなく壁紙をぼかした色を下地にします（Windows 11 の Mica と同じ考え方）。") +
                L.T("GPU を使わずに描けて、画面共有やスクリーンショットにもそのまま写ります。")));
    }

    private void GeneralPage(StackPanel p)
    {
        p.Children.Add(DropDownRow(L.T("表示言語"), L.T("ModernShell の画面とメニューの言語"),
            new[] { ("", L.T("自動（Windows に合わせる）")), ("ja", "日本語"), ("en", "English") },
            _s.Language, v => _s.Language = v));
        p.Children.Add(ToggleRow(L.T("サインイン時に起動"), L.T("Windows にサインインすると自動で常駐します"),
            _s.RunAtStartup, v =>
            {
                if (!SystemActions.SetStartup(v, v && _s.RunElevatedAtStartup)) return;
                _s.RunAtStartup = v;
            }));
        p.Children.Add(ToggleRow(L.T("管理者として起動する"),
            L.T("管理者として動いているアプリ（管理者の Terminal など）でも、IME の表示や Win キーなどが効くようになります。ModernShell から開いたアプリは普通の権限で起動します。タスク スケジューラに登録するため、切り替えるときに確認が出ることがあります") +
            (Launcher.IsElevated ? L.T("（現在: 管理者として動作中）") : L.T("（現在: 普通の権限で動作中。次のサインインから管理者で起動します）")),
            _s.RunElevatedAtStartup, v =>
            {
                if (!SystemActions.SetStartup(true, v)) return; // UAC で断られたら変えない
                _s.RunElevatedAtStartup = v;
                if (v && !Launcher.IsElevated && Confirm(L.T("今すぐ管理者として再起動しますか？")))
                {
                    _s.SaveQuiet();
                    RestartElevatedRequested?.Invoke();
                }
            }, _s.RunAtStartup));
        p.Children.Add(ToggleRow(L.T("描画に GPU を使う"),
            L.T("オフにすると GPU ドライバーを読み込まないので、使用メモリが減ります（アニメーションは CPU で描画）。見た目は変わりません。変更すると ModernShell を再起動します"),
            _s.UseGpuRendering, v =>
            {
                _s.UseGpuRendering = v;
                _s.SaveQuiet();
                if (Confirm(L.T("ModernShell を再起動して反映しますか？"))) RestartRequested?.Invoke();
            }));

        p.Children.Add(Section(L.T("設定の管理")));
        Group(p, "", L.T("バックアップ"), L.T("設定（ピン留め・並び順・アイコンを含む）を 1 つのファイルに保存して、別の PC に移したり元に戻したりできます"),
            ButtonRow(L.T("設定を書き出す"), L.T("settings.json を好きな場所に保存します"), L.T("書き出す"), ExportSettings),
            ButtonRow(L.T("設定を読み込む"), L.T("書き出したファイルで今の設定を置き換えて、ModernShell を再起動します"), L.T("読み込む"), ImportSettings),
            ButtonRow(L.T("設定を初期状態に戻す"), L.T("すべての設定を最初の状態に戻して、ModernShell を再起動します"), L.T("初期化"), ResetSettings));

        p.Children.Add(Section(L.T("トラブルシューティング")));
        if (Diagnostics.SafeMode)
            p.Children.Add(InfoBar(L.T("セーフ モードで動いています"), L.T("起動に続けて失敗したため、純正タスクバーのままで動いています。設定は保存されません。原因の調査のため、デバッグ ログはオンです")));
        p.Children.Add(ButtonRow(L.T("診断情報を書き出す"), L.T("環境・設定・ログを 1 つの zip にまとめます（不具合の報告用。設定には地点やフォルダーのパスが含まれます）"),
            L.T("書き出す"), ExportDiagnostics));
        p.Children.Add(ButtonRow(L.T("セーフ モードで再起動"), L.T("純正タスクバーのまま、ウィジェットとアニメーションをオフにして起動します。起動に失敗するときの切り分け用です"),
            L.T("再起動"), () => { if (Confirm(L.T("セーフ モードで再起動しますか？"))) RestartSafeRequested?.Invoke(); }));
        Group(p, "", L.T("デバッグ"), "",
            ToggleRow(L.T("デバッグ ログを書き出す"), L.T("不具合の調査用です。通常はオフのままで構いません。オンにすると、動作の記録を modernshell.log に書きます"),
                _s.DebugLog, v => _s.DebugLog = v));
        p.Children.Add(ButtonRow(L.T("設定とログ"), L.T("%AppData%\\ModernShell に settings.json と modernshell.log があります"),
            L.T("フォルダーを開く"), () => Launcher.Open("explorer.exe", "\"" + Settings.Directory + "\"")));
        p.Children.Add(ButtonRow(L.T("ModernShell を終了"), L.T("スタートメニューとタスクバーが純正に戻ります（Ctrl + Alt + Shift + F12 でも可）"),
            L.T("終了"), () => ExitRequested?.Invoke(), accent: true));
        p.Children.Add(Note(L.T("終了できなくなった場合は、ModernShell.exe --restore を実行するか PC を再起動してください。")));
    }

    private void ExportDiagnostics()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("診断情報を書き出す"), Filter = "zip (*.zip)|*.zip", DefaultExt = ".zip",
            FileName = $"ModernShell-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
        };
        if (dlg.ShowDialog(this) != true) return;
        try { Diagnostics.Export(_s, dlg.FileName); }
        catch (Exception ex) { Log.Error("export diagnostics", ex); MessageBox.Show(this, ex.Message, "書き出せませんでした", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ExportSettings()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("設定を書き出す"), Filter = L.T("ModernShell の設定 (*.json)|*.json"), DefaultExt = ".json",
            FileName = $"ModernShell-settings-{DateTime.Now:yyyyMMdd}.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _s.SaveQuiet();
            File.Copy(Settings.FilePath, dlg.FileName, overwrite: true);
        }
        catch (Exception ex) { Log.Error("export settings", ex); MessageBox.Show(this, ex.Message, "書き出せませんでした", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ImportSettings()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("設定を読み込む"), Filter = L.T("ModernShell の設定 (*.json)|*.json|すべてのファイル (*.*)|*.*") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var text = File.ReadAllText(dlg.FileName);
            if (!Settings.IsValid(text)) { MessageBox.Show(this, L.T("ModernShell の設定ファイルではないようです。"), L.T("読み込めませんでした"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (!Confirm(L.T("今の設定を置き換えて、ModernShell を再起動しますか？"))) return;
            Settings.Replace(text);
            RestartRequested?.Invoke();
        }
        catch (Exception ex) { Log.Error("import settings", ex); MessageBox.Show(this, ex.Message, "読み込めませんでした", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ResetSettings()
    {
        if (!Confirm(L.T("すべての設定を初期状態に戻して、ModernShell を再起動しますか？\n（今の設定を残したいときは、先に「設定を書き出す」をしてください）"))) return;
        Settings.Replace(null);
        RestartRequested?.Invoke();
    }

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, "ModernShell", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    private void AboutPage(StackPanel p)
    {
        p.Children.Add(ButtonRow("ModernShell " + Version, L.T("explorer.exe にコードを注入しない、別プロセス型のスタートメニュー / タスクバー"),
            L.T("Releases を開く"), () => Open("https://github.com/c0dei/ModernShell/releases")));
        p.Children.Add(ButtonRow(L.T("ソースコード"), "github.com/c0dei/ModernShell", L.T("GitHub で開く"), () => Open("https://github.com/c0dei/ModernShell")));
        p.Children.Add(Note(L.T("通知領域とタスク一覧には ManagedShell (Apache-2.0, cairoshell) を使用しています。")));
        p.Children.Add(Note(L.T("天気のアイコンは Meteocons (MIT License, © Bas Milius) を使用しています。")));
    }

    private static void Open(string url)
    {
        try { Launcher.Open(url); } catch { }
    }

    // ------------------------------------------------------------------
    // グループ（Windows 11 の設定アプリの展開カードと同じ見た目: 見出しのカードの下に行がつながる）

    private void Group(StackPanel p, string glyph, string title, string desc, params Border[] rows)
    {
        if (_flat) { foreach (var r in rows) p.Children.Add(r); return; }
        var r0 = T.Corner(6).TopLeft;
        var icon = UiKit.Glyph(glyph, 18);
        icon.Width = 24;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var texts = Texts(title, desc);
        texts.Tag = null;
        texts.Margin = new Thickness(18, 0, 0, 0);
        var head = Card(UiKit.Row(icon, texts), null);
        head.Margin = new Thickness(0, 4, 0, 0);
        head.CornerRadius = new CornerRadius(r0, r0, 0, 0);
        head.MinHeight = 64;
        p.Children.Add(head);
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            bool last = i == rows.Length - 1;
            row.Margin = new Thickness(0, 0, 0, last ? 4 : 0);
            row.BorderThickness = new Thickness(1, 0, 1, 1);
            row.CornerRadius = last ? new CornerRadius(0, 0, r0, r0) : new CornerRadius(0);
            row.Padding = new Thickness(62, 10, 16, 10);
            p.Children.Add(row);
        }
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
            Tag = left.Tag, // 検索で使う（タイトル・説明・選択肢）
        };
    }

    /// <summary>行の左側（タイトルと説明）。Tag に検索用の文字列を持たせる。</summary>
    private FrameworkElement Texts(string title, string desc)
    {
        var s = new StackPanel { Tag = title + " " + desc };
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

    /// <summary>
    /// フォントの選択。一覧の各フォント名はそのフォントで表示する。名前は表示言語に合わせて（日本語なら「游ゴシック」など）。
    /// タスクバー・スタートメニュー・メニューに使い、設定画面はシステムのフォントのまま。
    /// </summary>
    private Border FontRow()
    {
        var lang = System.Windows.Markup.XmlLanguage.GetLanguage(L.IsEnglish ? "en-us" : "ja-jp");
        var fonts = Fonts.SystemFontFamilies
            .Where(f => !f.Source.StartsWith("@"))
            .Select(f => (value: f.Source, label: f.FamilyNames.TryGetValue(lang, out var n) ? n : f.Source))
            .GroupBy(x => x.value).Select(g => g.First())
            .OrderBy(x => x.label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        fonts.Insert(0, ("", L.T("標準（Segoe UI Variable）")));
        var combo = new FluentComboBox(fonts.ToArray(), _s.UiFont, v => { _s.UiFont = v; Commit(); })
        {
            ItemFont = v => v.Length == 0 ? Theme.TextFont : new FontFamily(v),
        };
        // 検索で全フォント名に一致しないよう、選択肢は検索用の文字に入れない
        return Card(Texts(L.T("フォント"), L.T("タスクバー・スタート メニュー・メニューの文字。設定画面には使いません")), combo);
    }

    private static (string value, string label)[] AlignOptions() =>
        new[] { ("Left", L.T("左")), ("Center", L.T("中央")), ("Right", L.T("右")) };

    private Border DropDownRow(string title, string desc, (string value, string label)[] options, string current, Action<string> set, bool enabled = true)
    {
        var combo = new FluentComboBox(options, current, v => { set(v); Commit(); });
        var left = Texts(title, desc);
        left.Tag += " " + string.Join(" ", options.Select(o => o.label));
        return Card(left, combo, enabled);
    }

    /// <summary>スライダー行（0–100%）。値は離したときに保存する。</summary>
    /// <summary>数値の設定: スライダーと、直接入力の欄（単位付き）。入力した値は範囲内に丸める。「既定値」を渡すと、違うときだけ戻すボタンが出る。</summary>
    private Border NumberRow(string title, string desc, double min, double max, double value, string unit, Action<double> set,
        bool enabled = true, double step = 1, double? def = null)
    {
        string Fmt(double v) => step < 1 ? v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : ((int)Math.Round(v)).ToString();
        var slider = new FluentSlider(min, max, value, 180, step);
        var box = new TextBox
        {
            Text = Fmt(slider.Value), Width = 60, Padding = new Thickness(6, 4, 6, 4), TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center,
            Background = T.SolidBackground, Foreground = T.Text, CaretBrush = T.Text, BorderBrush = T.Stroke, BorderThickness = new Thickness(1, 1, 1, 2), IsEnabled = enabled,
        };
        slider.ValueChanged += v => box.Text = Fmt(v);
        slider.ValueCommitted += v => { set(v); Commit(); };
        void Apply()
        {
            var m = System.Text.RegularExpressions.Regex.Match(box.Text.Replace('，', '.').Replace('．', '.'), @"-?\d+(\.\d+)?");
            if (!m.Success || !double.TryParse(m.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) { box.Text = Fmt(slider.Value); return; }
            v = slider.Snap(v);
            if (Math.Abs(v - slider.Value) < 1e-9) { box.Text = Fmt(v); return; }
            slider.SetValue(v);
            box.Text = Fmt(v);
            set(v);
            Commit();
        }
        box.LostFocus += (_, _) => Apply();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Apply(); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down)
            {
                double cur = double.TryParse(box.Text, out var c) ? c : slider.Value;
                box.Text = Fmt(slider.Snap(cur + (e.Key == Key.Up ? step : -step)));
                Apply();
                e.Handled = true;
            }
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(slider);
        right.Children.Add(Spaced(box, 12));
        var unitLabel = UiKit.Label(unit, 13, T.TextSecondary);
        unitLabel.MinWidth = 32;
        unitLabel.Margin = new Thickness(6, 0, 0, 0);
        unitLabel.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(unitLabel);
        if (def is { } d)
        {
            var reset = new Tile
            {
                Width = 28, Height = 28, Margin = new Thickness(6, 0, 0, 0), CornerRadius = T.Corner(4), Child = UiKit.Glyph("\uE777", 12, T.TextSecondary),
                ToolTip = L.F("既定値（{0}）に戻す", Fmt(d)), Opacity = Math.Abs(value - d) < 1e-9 ? 0.25 : 1, IsEnabled = Math.Abs(value - d) >= 1e-9,
            };
            reset.Click += _ => { set(d); Commit(); };
            right.Children.Add(reset);
        }
        else right.Children.Add(new Border { Width = 34 }); // ほかの行と右端をそろえる
        return Card(Texts(title, desc), right, enabled);
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

    private Border ButtonRow(string title, string desc, string buttonText, Action onClick, bool accent = false, bool enabled = true)
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
        return Card(Texts(title, desc), b, enabled);
    }

    /// <summary>説明だけのカード（グループの中や、検索結果に出したい補足）。</summary>
    private Border NoteRow(string text)
    {
        var t = UiKit.Label(text, 13, T.TextSecondary);
        t.TextWrapping = TextWrapping.Wrap;
        t.Tag = text;
        return Card(t, null);
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
    private FrameworkElement ChoiceCards(string name, (string title, string desc, bool selected, Action select, Art art)[] items, bool enabled)
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
        var wrap = new Border
        {
            Margin = new Thickness(-4, 0, -4, 0), Child = grid,
            Tag = name + " " + string.Join(" ", items.Select(x => x.title + " " + x.desc)),
        };
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
            var edge = _s.TaskbarEdge;
            bool top = edge == TaskbarEdge.Top, vertical = edge is TaskbarEdge.Left or TaskbarEdge.Right;
            var flow = vertical ? Orientation.Vertical : Orientation.Horizontal;
            bool floating = _s.TaskbarStyle == TaskbarStyle.Floating;
            bool left = _s.StartButtonPosition == StartButtonPosition.LeftEdge;
            bool center = _s.TaskbarAlignment == TaskbarAlignment.Center;
            // 余白は左右 X・上下 Y（画面を基準）。プレビューは縮小して描くので 0.7 倍
            double h = _s.TaskbarHeight * 0.7;
            double mx = (floating ? _s.IslandMarginX : _s.BandMarginX) * 0.7, my = (floating ? _s.IslandMarginY : _s.BandMarginY) * 0.7;

            var bar = vertical
                ? new Grid
                {
                    Width = h, HorizontalAlignment = edge == TaskbarEdge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(mx, my, mx, my),
                }
                : new Grid { Height = h, VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom, Margin = new Thickness(mx, my, mx, my) };
            if (!floating) bar.Background = T.SolidBackground;
            // 並べる向きに 2 つ並べる
            FrameworkElement Stack2(FrameworkElement a, FrameworkElement b)
            {
                var p = new StackPanel { Orientation = flow };
                p.Children.Add(a);
                p.Children.Add(b);
                return p;
            }
            // 並べる向きでの間隔
            FrameworkElement Gap(FrameworkElement e, double d) { e.Margin = vertical ? new Thickness(0, d, 0, 0) : new Thickness(d, 0, 0, 0); return e; }
            // ha は「始まり（Left）・中央・終わり（Right）」。縦のバーでは 上・中央・下
            FrameworkElement Box(FrameworkElement content, HorizontalAlignment ha)
            {
                var b = new Border { Child = content };
                if (vertical)
                {
                    content.HorizontalAlignment = HorizontalAlignment.Center;
                    b.VerticalAlignment = ha switch { HorizontalAlignment.Left => VerticalAlignment.Top, HorizontalAlignment.Right => VerticalAlignment.Bottom, _ => VerticalAlignment.Center };
                    b.Width = h; b.Padding = new Thickness(0, 6, 0, 6);
                    b.Margin = new Thickness(0, floating ? 0 : 6, 0, floating ? 0 : 6);
                }
                else
                {
                    content.VerticalAlignment = VerticalAlignment.Center;
                    b.HorizontalAlignment = ha; b.Padding = new Thickness(6, 0, 6, 0); b.Height = h;
                    b.Margin = new Thickness(floating ? 0 : 6, 0, floating ? 0 : 6, 0);
                }
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
                var r = new StackPanel { Orientation = flow };
                r.Children.Add(UiKit.StartLogo(12, T.AccentBrush));
                if (_s.ShowSearchButton) r.Children.Add(Gap(UiKit.Glyph("", 11), 8));
                if (_s.ShowTaskViewButton) r.Children.Add(Gap(UiKit.Glyph("", 11), 8));
                return r;
            }
            FrameworkElement Apps()
            {
                var r = new StackPanel { Orientation = flow };
                foreach (var c in new[] { 0xF2C94C, 0x2F80ED, 0x27AE60, 0xEB5757, 0x9B51E0 })
                    r.Children.Add(new Border
                    {
                        Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Margin = vertical ? new Thickness(0, 4, 0, 4) : new Thickness(4, 0, 4, 0),
                        Background = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c)),
                    });
                return r;
            }
            FrameworkElement Tray()
            {
                var r = new StackPanel { Orientation = flow };
                r.Children.Add(UiKit.Glyph("", 10));
                r.Children.Add(Gap(UiKit.Glyph("", 10), 6));
                if (!vertical) r.Children.Add(Gap(UiKit.Label("12:34", 10), 8)); // 縦のバーでは時刻まで並べると窮屈なので省く
                return r;
            }

            if (left && center)
            {
                bar.Children.Add(Box(StartIcons(), HorizontalAlignment.Left));
                bar.Children.Add(Box(Apps(), HorizontalAlignment.Center));
            }
            else if (left || !center)
            {
                bar.Children.Add(Box(Stack2(StartIcons(), Gap(Apps(), 4)), HorizontalAlignment.Left));
            }
            else bar.Children.Add(Box(Stack2(StartIcons(), Gap(Apps(), 4)), HorizontalAlignment.Center));
            bar.Children.Add(Box(Tray(), HorizontalAlignment.Right));
            area.Children.Add(bar);
        }
        else
        {
            var l = UiKit.Label(L.T("純正タスクバーを使用中"), 13, Brushes.White);
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

    private bool _barPickerOpen;

    /// <summary>バーの背景に重ねる色: テーマ / アクセント / 色見本 / 好きな色（カラーパレット）。</summary>
    private FrameworkElement BarTintSwatches()
    {
        string[] presets = { "#000000", "#FFFFFF", "#1F2937", "#0067C0", "#00706A", "#107C10", "#6B3FB5", "#B4009E", "#C50F1F", "#A34A00" };
        var custom = _s.TaskbarCustomColor.Trim();
        var colors = presets.ToList();
        if (custom.Length > 0 && !colors.Contains(custom, StringComparer.OrdinalIgnoreCase) && Theme.ParseColor(custom) != null) colors.Add(custom.ToUpperInvariant());
        var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };

        Tile Make(Brush fill, string label, bool sel, string tip, Action onClick)
        {
            var inner = new Grid();
            if (label.Length > 0) inner.Children.Add(UiKit.Label(label, 10, Brushes.White));
            if (sel) inner.Children.Add(UiKit.Glyph("\uE73E", 14, Brushes.White));
            foreach (UIElement c in inner.Children) if (c is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.VerticalAlignment = VerticalAlignment.Center; }
            var tile = new Tile
            {
                Width = 44, Height = 44, Margin = new Thickness(0, 0, 8, 8), CornerRadius = T.Corner(4), Background = fill,
                BorderThickness = new Thickness(2), BorderBrush = sel ? T.Text : Brushes.Transparent, Child = inner, ToolTip = tip,
            };
            tile.MouseLeave += (_, _) => { tile.Background = fill; tile.BorderBrush = sel ? T.Text : Brushes.Transparent; };
            tile.MouseEnter += (_, _) => tile.Background = fill;
            tile.Click += _ => onClick();
            return tile;
        }

        wrap.Children.Add(Make(new SolidColorBrush(T.SolidColor), L.T("テーマ"), _s.TaskbarTint == BarTintMode.Theme, L.T("ライト/ダークのテーマの色"),
            () => { _s.TaskbarTint = BarTintMode.Theme; _barPickerOpen = false; Commit(); }));
        wrap.Children.Add(Make(new SolidColorBrush(T.Accent), L.T("アクセント"), _s.TaskbarTint == BarTintMode.Accent, L.T("Windows のアクセント カラー"),
            () => { _s.TaskbarTint = BarTintMode.Accent; _barPickerOpen = false; Commit(); }));
        wrap.Children.Add(Make(new SolidColorBrush(MicaLike.AverageColor ?? System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x80)), L.T("壁紙"), _s.TaskbarTint == BarTintMode.Wallpaper, L.T("壁紙の平均色に合わせる（壁紙を変えると自動で変わります）"),
            () => { _s.TaskbarTint = BarTintMode.Wallpaper; _barPickerOpen = false; Commit(); }));
        foreach (var hex in colors)
        {
            bool sel = _s.TaskbarTint == BarTintMode.Custom && string.Equals(custom, hex, StringComparison.OrdinalIgnoreCase);
            var h = hex;
            wrap.Children.Add(Make(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), "", sel, hex,
                () => { _s.TaskbarTint = BarTintMode.Custom; _s.TaskbarCustomColor = h; _barPickerOpen = false; Commit(); }));
        }
        var plus = new Tile
        {
            Width = 44, Height = 44, Margin = new Thickness(0, 0, 8, 8), CornerRadius = T.Corner(4), Background = T.ControlFill,
            BorderThickness = new Thickness(1), BorderBrush = T.ControlBorder, Child = UiKit.Glyph("\uE710", 16), ToolTip = L.T("色を選ぶ"),
        };
        plus.MouseLeave += (_, _) => { plus.Background = T.ControlFill; plus.BorderBrush = T.ControlBorder; };
        plus.Click += _ => { _barPickerOpen = !_barPickerOpen; Commit(); };
        wrap.Children.Add(plus);

        var content = new StackPanel();
        content.Children.Add(wrap);
        if (_barPickerOpen)
        {
            var start = Theme.ParseColor(custom) ?? T.Accent;
            var picker = new ColorPicker(start) { Margin = new Thickness(0, 8, 0, 14) };
            picker.Applied += c => { _s.TaskbarTint = BarTintMode.Custom; _s.TaskbarCustomColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; _barPickerOpen = false; Commit(); };
            picker.Cancelled += () => { _barPickerOpen = false; Commit(); };
            content.Children.Add(picker);
        }
        return new Border
        {
            Background = T.Surface, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6),
            Padding = new Thickness(20, 14, 12, 6), Child = content, Tag = L.T("タスクバーの背景に重ねる色"),
        };
    }

    private FrameworkElement Swatches()
    {
        var colors = new List<string> { "", "#0067C0", "#0078D4", "#00706A", "#107C10", "#6B3FB5", "#B4009E", "#C50F1F", "#A34A00" };
        // 一覧にない色（カラーピッカーで選んだ色）も見本として並べる
        if (_s.AccentColor.Length > 0 && !colors.Contains(_s.AccentColor, StringComparer.OrdinalIgnoreCase) && Theme.ParseColor(_s.AccentColor) != null)
            colors.Add(_s.AccentColor.ToUpperInvariant());
        var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var hex in colors)
        {
            bool sel = string.Equals(_s.AccentColor, hex, StringComparison.OrdinalIgnoreCase);
            var fill = hex.Length == 0 ? new SolidColorBrush(Theme.SystemAccent()) : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            var inner = new Grid();
            if (hex.Length == 0) inner.Children.Add(UiKit.Label(L.T("自動"), 10, Brushes.White));
            if (sel) inner.Children.Add(UiKit.Glyph("", 14, Brushes.White));
            foreach (UIElement c in inner.Children) if (c is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.VerticalAlignment = VerticalAlignment.Center; }
            var tile = new Tile
            {
                Width = 44, Height = 44, Margin = new Thickness(0, 0, 8, 8), CornerRadius = T.Corner(4), Background = fill,
                BorderThickness = new Thickness(2), BorderBrush = sel ? T.Text : Brushes.Transparent, Child = inner,
                ToolTip = hex.Length == 0 ? L.T("Windows のアクセント カラー") : hex,
            };
            tile.MouseLeave += (_, _) => { tile.Background = fill; tile.BorderBrush = sel ? T.Text : Brushes.Transparent; };
            tile.MouseEnter += (_, _) => tile.Background = fill;
            var value = hex;
            tile.Click += _ => { _s.AccentColor = value; _pickerOpen = false; Commit(); };
            wrap.Children.Add(tile);
        }

        // 「カスタム」: 押すとカラーピッカーをその場に開く
        var plus = new Tile
        {
            Width = 44, Height = 44, Margin = new Thickness(0, 0, 8, 8), CornerRadius = T.Corner(4), Background = T.ControlFill,
            BorderThickness = new Thickness(1), BorderBrush = T.ControlBorder, Child = UiKit.Glyph("\uE710", 16), ToolTip = L.T("色を選ぶ"),
        };
        plus.MouseLeave += (_, _) => { plus.Background = T.ControlFill; plus.BorderBrush = T.ControlBorder; };
        plus.Click += _ => { _pickerOpen = !_pickerOpen; Commit(); };
        wrap.Children.Add(plus);

        var content = new StackPanel();
        content.Children.Add(wrap);
        if (_pickerOpen)
        {
            var start = Theme.ParseColor(_s.AccentColor) ?? Theme.SystemAccent();
            var picker = new ColorPicker(start) { Margin = new Thickness(0, 8, 0, 14) };
            picker.Applied += c => { _s.AccentColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; _pickerOpen = false; Commit(); };
            picker.Cancelled += () => { _pickerOpen = false; Commit(); };
            content.Children.Add(picker);
        }
        var card = new Border
        {
            Background = T.Surface, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6),
            Padding = new Thickness(20, 14, 12, 6), Child = content, Tag = L.T("アクセント カラー 色"),
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

    public bool IsOn { get => _on; set { _on = value; Refresh(); } }

    public ToggleSwitch(bool on, bool showLabel = true)
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
        if (showLabel) Children.Add(_label);
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
        _label.Text = _on ? L.T("オン") : L.T("オフ");
        _track.Background = _on ? t.AccentBrush : Brushes.Transparent;
        _track.BorderBrush = _on ? t.AccentBrush : t.TextSecondary;
        _knob.Fill = _on ? t.OnAccent : t.TextSecondary;
        _knob.Margin = _on ? new Thickness(23, 0, 0, 0) : new Thickness(3, 0, 0, 0);
    }
}
