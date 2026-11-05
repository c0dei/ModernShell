using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using ModernShell.Core;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>
/// 独自スタートメニュー。常駐させて Hide/Show を繰り返すことで瞬時に開く。
/// </summary>
public sealed class StartMenuWindow : Window
{
    private enum Page { Pinned, AllApps, Search }

    private const double MainWidth = 640, WidgetWidth = 330, MenuHeight = 720;
    private double MenuWidth => WidgetPanel.AnyEnabled(_settings) ? MainWidth + WidgetWidth : MainWidth;
    private WidgetPanel? _widgets;

    private readonly Settings _settings;
    private readonly AppCatalog _catalog;

    private Grid _root = null!;
    private TextBox _search = null!;
    private TextBlock _placeholder = null!;
    private ContentControl _content = null!;
    private Page _page = Page.Pinned;
    private List<Tile> _results = new();
    private int _selected;
    private Popup? _openFlyout;
    private DateTime _hiddenAt = DateTime.MinValue;

    /// <summary>タスクバー側の配置情報（物理ピクセルの矩形と中央寄せかどうか）。App から供給。</summary>
    public Func<(Win32.RECT bar, bool top, bool center)?>? AnchorProvider { get; set; }
    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;

    public StartMenuWindow(Settings settings, AppCatalog catalog)
    {
        _settings = settings;
        _catalog = catalog;

        Title = "ModernShell Start";
        // 公式の背景効果 (Mica/Acrylic) は枠付きウィンドウでしか描かれないので SingleBorderWindow にし、
        // WindowChrome で枠とタイトルバーを見えなくする（WindowStyle=None だと不透明になる）
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = MenuWidth;
        Height = MenuHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = Theme.TextFont;
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(-1),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0),
            NonClientFrameEdges = NonClientFrameEdges.None,
        });

        Deactivated += (_, _) => HideMenu();
        PreviewKeyDown += OnPreviewKeyDown;
        Theme.Changed += () => Dispatcher.BeginInvoke(Rebuild);
        // ウィジェットの表示切替などを反映（開いている最中は閉じたときに反映される）
        _settings.Changed += () => Dispatcher.BeginInvoke(() => { if (!IsVisible) Rebuild(); });
        _catalog.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible && _page != Page.Search) ShowPage(_page); });

        // 不可視のままハンドルを作っておく（TaskbarCreated 等のブロードキャストを受け取るため）
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        long ex = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, (IntPtr)(ex | Win32.WS_EX_TOOLWINDOW));
        // 枠付きウィンドウにしたため、DWM が右上に閉じる/最小化ボタンを描かないようシステムメニューを外す
        long style = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE);
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, (IntPtr)(style & ~(0x00080000L /*WS_SYSMENU*/ | 0x00020000L /*WS_MINIMIZEBOX*/ | 0x00010000L /*WS_MAXIMIZEBOX*/)));
        Rebuild();
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    // ------------------------------------------------------------------
    // 表示制御

    public void Toggle()
    {
        if (IsVisible) { HideMenu(); return; }
        // スタートボタンのクリックで Deactivated→Hide された直後の再表示を防ぐ
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 250) return;
        ShowMenu();
    }

    public void ShowMenu(string? initialSearch = null)
    {
        _search.Text = initialSearch ?? "";
        if (_search.Text.Length == 0) ShowPage(_settings.ShowAllAppsFirst ? Page.AllApps : Page.Pinned);
        _root.Opacity = 0;
        Show();          // WPF は Show 時に自前の Left/Top を適用するので、位置決めは Show の後に行う
        PositionMenu();
        Foregrounder.Force(Handle);
        Activate();
        _widgets?.Open();
        _search.Focus();
        _search.CaretIndex = _search.Text.Length;
        UiKit.FadeSlideIn(_root, AnchorProvider?.Invoke()?.top == true ? -24 : 24);
    }

    public void HideMenu()
    {
        if (!IsVisible) return;
        if (_openFlyout?.IsOpen == true) return; // 右クリックメニュー表示中は閉じない
        _hiddenAt = DateTime.Now;
        _widgets?.Close();
        Hide();
    }

    private void PositionMenu()
    {
        var src = PresentationSource.FromVisual(this);
        double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? Win32.GetDpiForWindow(Handle) / 96.0;
        if (scale <= 0) scale = 1;
        int w = (int)(MenuWidth * scale), h = (int)(MenuHeight * scale);
        int gap = (int)(12 * scale);

        var anchor = AnchorProvider?.Invoke();
        var work = SystemParameters.WorkArea; // DIP
        int x, y;
        if (anchor is { } a)
        {
            x = a.center ? a.bar.Left + (a.bar.Width - w) / 2 : a.bar.Left + gap;
            y = a.top ? a.bar.Bottom + gap : a.bar.Top - h - gap;
        }
        else
        {
            x = (int)((work.Left + (work.Width - MenuWidth) / 2) * scale);
            y = (int)(work.Bottom * scale) - h - gap;
        }
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, x, y, w, h, Win32.SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------
    // UI 構築

    private void Rebuild()
    {
        var t = Theme.Current;
        Backdrop.Apply(this, _settings.Backdrop, _settings.BackdropEngine, t, alwaysInactive: false);
        Foreground = t.Text;

        Width = MenuWidth;
        // 外枠: [メイン列 | ウィジェット列] の下にフッター
        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MainWidth) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(main);

        _widgets?.Close();
        _widgets = null;
        if (WidgetPanel.AnyEnabled(_settings))
        {
            _widgets = new WidgetPanel(_settings) { Margin = new Thickness(4, 28, 24, 12) };
            _widgets.LaunchedSomething += HideMenu;
            Grid.SetColumn(_widgets, 1);
            _root.Children.Add(_widgets);
        }

        // 検索欄
        _search = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = t.Text,
            CaretBrush = t.Text,
            FontSize = 14,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(36, 0, 12, 0),
        };
        _placeholder = UiKit.Label("アプリ、設定、ドキュメントを検索", 14, t.TextSecondary);
        _placeholder.Margin = new Thickness(38, 0, 0, 0);
        _placeholder.IsHitTestVisible = false;
        var searchIcon = UiKit.Glyph("", 14, t.TextSecondary);
        searchIcon.HorizontalAlignment = HorizontalAlignment.Left;
        searchIcon.Margin = new Thickness(14, 0, 0, 0);
        _search.TextChanged += (_, _) => OnSearchChanged();

        var searchGrid = new Grid();
        searchGrid.Children.Add(searchIcon);
        searchGrid.Children.Add(_placeholder);
        searchGrid.Children.Add(_search);
        var searchBox = new Border
        {
            Height = 38,
            Margin = new Thickness(28, 28, 28, 12),
            CornerRadius = new CornerRadius(19),
            Background = t.Surface,
            BorderBrush = t.Stroke,
            BorderThickness = new Thickness(1),
            Child = searchGrid,
        };
        main.Children.Add(searchBox);

        _content = new ContentControl { Margin = new Thickness(20, 4, 20, 0), Focusable = false };
        Grid.SetRow(_content, 1);
        main.Children.Add(_content);

        var footer = BuildFooter(t);
        Grid.SetRow(footer, 1);
        Grid.SetColumnSpan(footer, 2);
        _root.Children.Add(footer);

        Content = _root;
        ShowPage(_page == Page.Search ? Page.Pinned : _page);
    }

    private FrameworkElement BuildFooter(Theme t)
    {
        var g = new Grid { Background = t.FooterBand };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new Border { Height = 1, Background = t.Stroke, VerticalAlignment = VerticalAlignment.Top });
        Grid.SetColumnSpan(g.Children[0], 3);

        var name = SystemActions.UserDisplayName();
        var avatar = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = t.AccentBrush,
            Child = UiKit.Label(name.Length > 0 ? name.Substring(0, 1).ToUpper() : "?", 14, t.OnAccent, FontWeights.SemiBold),
        };
        ((TextBlock)avatar.Child).HorizontalAlignment = HorizontalAlignment.Center;
        var user = new Tile
        {
            Margin = new Thickness(40, 0, 0, 0), Padding = new Thickness(10, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Child = UiKit.Row(avatar, Spaced(UiKit.Label(name, 13), 12)),
        };
        user.Click += _ => { HideMenu(); SystemActions.OpenUserAccount(); };
        user.RightClick += tile => OpenFlyout(tile, new[]
        {
            new MenuItemSpec("", "ロック", () => { HideMenu(); SystemActions.Lock(); }),
            new MenuItemSpec("", "サインアウト", () => { HideMenu(); SystemActions.SignOut(); }),
        });
        g.Children.Add(user);

        var gear = IconTile("", "ModernShell の設定");
        gear.Click += _ => { HideMenu(); OpenSettingsRequested?.Invoke(); };
        Grid.SetColumn(gear, 1);
        g.Children.Add(gear);

        var power = IconTile("", "電源");
        power.Margin = new Thickness(4, 0, 40, 0);
        power.Click += tile => OpenFlyout(tile, new[]
        {
            new MenuItemSpec("", "ロック", () => { HideMenu(); SystemActions.Lock(); }),
            new MenuItemSpec("", "スリープ", () => { HideMenu(); SystemActions.Sleep(); }),
            new MenuItemSpec("", "サインアウト", () => { HideMenu(); SystemActions.SignOut(); }),
            MenuItemSpec.Separator,
            new MenuItemSpec("", "再起動", () => { HideMenu(); SystemActions.Restart(); }),
            new MenuItemSpec("", "シャットダウン", () => { HideMenu(); SystemActions.Shutdown(); }),
            MenuItemSpec.Separator,
            new MenuItemSpec("\uE8BB", "ModernShell を終了（純正に戻す）", () => { HideMenu(); ExitRequested?.Invoke(); }),
        }, PlacementMode.Top);
        Grid.SetColumn(power, 2);
        g.Children.Add(power);
        return g;
    }

    private static Tile IconTile(string glyph, string tooltip) => new()
    {
        Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center,
        Child = UiKit.Glyph(glyph, 16), ToolTip = tooltip,
    };

    private static FrameworkElement Spaced(FrameworkElement e, double left) { e.Margin = new Thickness(left, 0, 0, 0); return e; }

    private void OpenFlyout(UIElement target, IEnumerable<MenuItemSpec> items, PlacementMode mode = PlacementMode.MousePoint)
    {
        _openFlyout = Flyout.Show(target, items, mode);
        _openFlyout.Closed += (_, _) => { if (IsVisible) Activate(); };
    }

    // ------------------------------------------------------------------
    // ページ

    private void ShowPage(Page page)
    {
        _page = page;
        _content.Content = page switch
        {
            Page.AllApps => BuildAllApps(),
            Page.Search => BuildSearch(_search.Text),
            _ => BuildPinned(),
        };
    }

    private FrameworkElement Header(string title, string buttonText, string glyph, bool glyphFirst, Action onClick)
    {
        var t = Theme.Current;
        var g = new Grid { Margin = new Thickness(20, 8, 8, 8) };
        g.Children.Add(UiKit.Label(title, 14, t.Text, FontWeights.SemiBold));
        var label = UiKit.Label(buttonText, 12);
        var icon = UiKit.Glyph(glyph, 10);
        var row = glyphFirst ? UiKit.Row(icon, Spaced(label, 8)) : UiKit.Row(label, Spaced(icon, 8));
        var btn = new Tile
        {
            HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(10, 4, 10, 4),
            Background = t.Surface, Child = row,
        };
        btn.Click += _ => onClick();
        g.Children.Add(btn);
        return g;
    }

    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, // 旧来のスクロールバーは見た目が浮くため非表示（ホイールは有効）
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
        PanningMode = PanningMode.VerticalOnly,
    };

    // --- ピン留め ---
    private FrameworkElement BuildPinned()
    {
        var dock = new DockPanel();
        var header = Header("ピン留め済み", "すべてのアプリ", "", false, () => ShowPage(Page.AllApps));
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        var grid = new UniformGrid { Columns = Math.Clamp(_settings.PinnedColumns, 3, 8), VerticalAlignment = VerticalAlignment.Top };
        var pinned = _settings.PinnedStart.Select(id => _catalog.Find(id)).Where(a => a != null).Cast<AppEntry>().ToList();
        foreach (var app in pinned) grid.Children.Add(PinnedTile(app));

        if (pinned.Count == 0)
        {
            var hint = UiKit.Label(_catalog.Apps.Count == 0 ? "アプリを読み込んでいます…" : "「すべてのアプリ」で右クリック →「スタートにピン留め」", 13, Theme.Current.TextSecondary);
            hint.Margin = new Thickness(20);
            dock.Children.Add(hint);
        }
        else dock.Children.Add(Scroll(grid));
        return dock;
    }

    private Tile PinnedTile(AppEntry app)
    {
        var img = UiKit.Icon(null, 32);
        LoadIconAsync(img, app, 32);
        var label = UiKit.Label(app.Name, 12);
        label.TextAlignment = TextAlignment.Center;
        label.TextWrapping = TextWrapping.Wrap;
        label.MaxHeight = 32;
        label.Margin = new Thickness(4, 6, 4, 0);
        label.HorizontalAlignment = HorizontalAlignment.Center;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(img);
        stack.Children.Add(label);
        var tile = new Tile { Height = 90, Margin = new Thickness(2), Child = stack, Payload = app, ToolTip = app.Name, AllowDrop = true };
        tile.Click += _ => Launch(app);
        tile.RightClick += t => OpenFlyout(t, AppMenu(app, pinnedPage: true));
        EnableReorder(tile, app);
        return tile;
    }

    // ピン留めのドラッグ＆ドロップ並べ替え
    private Point _dragStart;
    private void EnableReorder(Tile tile, AppEntry app)
    {
        tile.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
        tile.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(this) - _dragStart;
            if (Math.Abs(d.X) < 8 && Math.Abs(d.Y) < 8) return;
            DragDrop.DoDragDrop(tile, new DataObject("ModernShell.App", app.Id), DragDropEffects.Move);
        };
        tile.Drop += (_, e) =>
        {
            if (e.Data.GetData("ModernShell.App") is not string draggedId || draggedId == app.Id) return;
            var list = _settings.PinnedStart;
            list.Remove(draggedId);
            int idx = list.IndexOf(app.Id);
            list.Insert(idx < 0 ? list.Count : idx, draggedId);
            _settings.Save();
            ShowPage(Page.Pinned);
        };
    }

    // --- すべてのアプリ ---
    private FrameworkElement BuildAllApps()
    {
        var dock = new DockPanel();
        var header = Header("すべてのアプリ", "戻る", "", true, () => ShowPage(Page.Pinned));
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        var list = new StackPanel { Margin = new Thickness(12, 0, 12, 12) };
        foreach (var group in _catalog.Apps.GroupBy(a => GroupKey(a.Name)))
        {
            var h = UiKit.Label(group.Key, 13, Theme.Current.Text, FontWeights.SemiBold);
            h.Margin = new Thickness(12, 12, 0, 4);
            list.Children.Add(h);
            foreach (var app in group) list.Children.Add(ListTile(app, AppMenu(app, pinnedPage: false)));
        }
        dock.Children.Add(Scroll(list));
        return dock;
    }

    private Tile ListTile(AppEntry app, IEnumerable<MenuItemSpec> menu, string? subtitle = null)
    {
        var img = UiKit.Icon(null, 24);
        LoadIconAsync(img, app, 24);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(UiKit.Label(app.Name, 13));
        if (subtitle != null) text.Children.Add(UiKit.Label(subtitle, 11, Theme.Current.TextSecondary));
        var tile = new Tile { Height = subtitle == null ? 40 : 48, Padding = new Thickness(12, 0, 12, 0), Payload = app, Child = UiKit.Row(img, text) };
        ((StackPanel)tile.Child).VerticalAlignment = VerticalAlignment.Center;
        tile.Click += _ => Launch(app);
        tile.RightClick += t => OpenFlyout(t, menu);
        return tile;
    }

    /// <summary>A–Z / # / 五十音の行（あかさたな…）でグループ化。</summary>
    internal static string GroupKey(string name)
    {
        if (string.IsNullOrEmpty(name)) return "#";
        char c = char.ToUpperInvariant(name[0]);
        if (c is >= 'A' and <= 'Z') return c.ToString();
        if (char.IsDigit(c) || char.IsPunctuation(c) || char.IsSymbol(c)) return "#";
        // 全角英字
        if (c is >= 'Ａ' and <= 'Ｚ') return ((char)('A' + (c - 'Ａ'))).ToString();
        if (c is >= 'ａ' and <= 'ｚ') return ((char)('A' + (c - 'ａ'))).ToString();
        // カタカナ → ひらがな
        if (c is >= 'ァ' and <= 'ヶ') c = (char)(c - 0x60);
        if (c is >= 'ぁ' and <= 'ゖ')
        {
            const string rows = "あかさたなはまやらわ";
            const string bounds = "ぁかさたなはまゃらゎ"; // 各行の先頭文字（小書き含む）
            for (int i = bounds.Length - 1; i >= 0; i--)
                if (c >= bounds[i]) return rows[i].ToString();
        }
        return "その他";
    }

    // --- 検索 ---
    private void OnSearchChanged()
    {
        _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_search.Text.Length == 0) { if (_page == Page.Search) ShowPage(Page.Pinned); return; }
        ShowPage(Page.Search);
    }

    private FrameworkElement BuildSearch(string query)
    {
        var t = Theme.Current;
        var list = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };
        _results = new List<Tile>();

        var matches = Rank(_catalog.Apps, query).Take(12).ToList();
        if (matches.Count > 0)
        {
            var h = UiKit.Label("アプリ", 13, t.Text, FontWeights.SemiBold);
            h.Margin = new Thickness(12, 0, 0, 6);
            list.Children.Add(h);
            foreach (var app in matches)
            {
                var tile = ListTile(app, AppMenu(app, pinnedPage: false), app.IsStoreApp ? "アプリ" : "デスクトップアプリ");
                _results.Add(tile);
                list.Children.Add(tile);
            }
        }

        var h2 = UiKit.Label("その他", 13, t.Text, FontWeights.SemiBold);
        h2.Margin = new Thickness(12, 12, 0, 6);
        list.Children.Add(h2);
        var run = ActionTile("", $"実行: {query}", () => { if (SystemActions.TryRun(query)) HideMenu(); });
        var web = ActionTile("", $"Web で検索: {query}", () => { HideMenu(); SystemActions.WebSearch(query); });
        _results.Add(run); list.Children.Add(run);
        _results.Add(web); list.Children.Add(web);

        _selected = 0;
        UpdateSelection();
        return Scroll(list);
    }

    private Tile ActionTile(string glyph, string text, Action action)
    {
        var box = new Border { Width = 24, Height = 24, Child = UiKit.Glyph(glyph, 16) };
        var label = UiKit.Label(text, 13);
        label.Margin = new Thickness(14, 0, 0, 0);
        var row = UiKit.Row(box, label);
        row.VerticalAlignment = VerticalAlignment.Center;
        var tile = new Tile { Height = 40, Padding = new Thickness(12, 0, 12, 0), Child = row };
        tile.Click += _ => action();
        return tile;
    }

    /// <summary>前方一致 > 単語先頭一致 > 部分一致 > 頭文字一致 の順でスコアリング。</summary>
    internal static IEnumerable<AppEntry> Rank(IEnumerable<AppEntry> apps, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return Enumerable.Empty<AppEntry>();
        var cmp = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions opt = CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

        int Score(AppEntry a)
        {
            var n = a.Name;
            if (cmp.IsPrefix(n, q, opt)) return 100 - Math.Min(n.Length, 50);
            int idx = cmp.IndexOf(n, q, opt);
            if (idx > 0 && (n[idx - 1] == ' ' || n[idx - 1] == '-' || n[idx - 1] == '(')) return 60;
            if (idx >= 0) return 40;
            var initials = new string(n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]).ToArray());
            if (cmp.IsPrefix(initials, q, opt)) return 30;
            return 0;
        }

        return apps.Select(a => (a, s: Score(a))).Where(x => x.s > 0).OrderByDescending(x => x.s).ThenBy(x => x.a.Name).Select(x => x.a);
    }

    private void UpdateSelection()
    {
        for (int i = 0; i < _results.Count; i++) _results[i].IsSelected = i == _selected;
        if (_selected >= 0 && _selected < _results.Count) _results[_selected].BringIntoView();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_search.Text.Length > 0) _search.Text = "";
                else if (_page == Page.AllApps) ShowPage(Page.Pinned);
                else HideMenu();
                e.Handled = true;
                break;
            case Key.Down when _page == Page.Search && _results.Count > 0:
                _selected = Math.Min(_selected + 1, _results.Count - 1); UpdateSelection(); e.Handled = true;
                break;
            case Key.Up when _page == Page.Search && _results.Count > 0:
                _selected = Math.Max(_selected - 1, 0); UpdateSelection(); e.Handled = true;
                break;
            case Key.Enter when _page == Page.Search && _results.Count > 0 && Keyboard.FocusedElement == _search:
                var tile = _results[Math.Clamp(_selected, 0, _results.Count - 1)];
                if (tile.Payload is AppEntry app) Launch(app, asAdmin: Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
                else tile.PerformClick();
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------

    private IEnumerable<MenuItemSpec> AppMenu(AppEntry app, bool pinnedPage)
    {
        bool pinned = _settings.PinnedStart.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
        bool onBar = _settings.PinnedTaskbar.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
        var items = new List<MenuItemSpec>
        {
            pinned
                ? new("", "スタートからピン留めを外す", () => { _settings.PinnedStart.RemoveAll(x => x.Equals(app.Id, StringComparison.OrdinalIgnoreCase)); _settings.Save(); ShowPage(_page); })
                : new("", "スタートにピン留めする", () => { _settings.PinnedStart.Add(app.Id); _settings.Save(); ShowPage(_page); }),
        };
        if (pinnedPage && pinned)
            items.Add(new("", "先頭に移動", () => { _settings.PinnedStart.Remove(app.Id); _settings.PinnedStart.Insert(0, app.Id); _settings.Save(); ShowPage(_page); }));
        // Windows と共有している場合、タスクバーのピン留めは Windows 側でしか変更できない
        if (_settings.TaskbarPinSource == TaskbarPinSource.ModernShell)
            items.Add(onBar
                ? new("", "タスクバーからピン留めを外す", () => { _settings.PinnedTaskbar.RemoveAll(x => x.Equals(app.Id, StringComparison.OrdinalIgnoreCase)); _settings.Save(); })
                : new("", "タスクバーにピン留めする", () => { _settings.PinnedTaskbar.Add(app.Id); _settings.Save(); }));
        if (app.ExePath != null)
        {
            items.Add(MenuItemSpec.Separator);
            items.Add(new("", "管理者として実行", () => Launch(app, asAdmin: true)));
            items.Add(new("", "ファイルの場所を開く", () => { HideMenu(); app.OpenFileLocation(); }));
        }
        return items;
    }

    private void Launch(AppEntry app, bool asAdmin = false)
    {
        HideMenu();
        app.Launch(asAdmin);
    }

    private static void LoadIconAsync(Image img, AppEntry app, int size)
    {
        Task.Run(() => app.Icon(size * 2)).ContinueWith(t =>
        {
            if (t.Result != null) img.Dispatcher.BeginInvoke(() => img.Source = t.Result);
        }, TaskScheduler.Default);
    }
}
