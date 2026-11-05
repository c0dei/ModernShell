using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ModernShell.Core;
using ModernShell.Native;
using Forms = System.Windows.Forms;

namespace ModernShell.UI;

/// <summary>
/// 独自タスクバー全体の制御。
///   レイアウト: Grouped（スタート＋アプリを 1 かたまり）/ Split（左・中央・右の 3 分割）
///   スタイル  : Band（画面端に密着した帯）/ Floating（浮いた島 = アイランド）
/// の組み合わせで 4 通りの見た目になる。作業領域の確保は AppBarReservation が 1 つだけ行う。
/// </summary>
public sealed class TaskbarController : IDisposable
{
    private readonly Settings _settings;
    private readonly AppCatalog _catalog;
    private readonly WindowTracker _tracker;
    private readonly Dispatcher _dispatcher;
    private readonly AppBarReservation _reservation = new();
    private readonly List<BarSurface> _surfaces = new();
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _status;
    private readonly List<string> _order = new(); // 実行中ボタンの並び順を安定させる
    private bool _disposed;

    private StackPanel? _apps;
    private TextBlock? _time, _date;
    private TextBlock? _network, _battery;

    public event Action? StartClicked;
    public event Action? SearchClicked;
    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;

    public bool IsTop => _settings.TaskbarEdge == TaskbarEdge.Top;
    private bool Floating => _settings.TaskbarStyle == TaskbarStyle.Floating;
    /// <summary>確保した領域（物理ピクセル）。スタートメニューの位置決めに使う。</summary>
    public Win32.RECT BarRect => _reservation.Rect;
    /// <summary>スタートメニューを中央に出すか（スタートボタンが中央グループにあるとき）。</summary>
    public bool StartCentered => _settings.TaskbarLayout == TaskbarLayout.Grouped && _settings.TaskbarAlignment == TaskbarAlignment.Center;

    public TaskbarController(Settings settings, AppCatalog catalog, WindowTracker tracker, Dispatcher dispatcher)
    {
        _settings = settings;
        _catalog = catalog;
        _tracker = tracker;
        _dispatcher = dispatcher;
        _tracker.Ignore(_reservation.Handle);

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => UpdateClock(), dispatcher);
        _status = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => UpdateStatus(), dispatcher);

        _tracker.WindowsChanged += OnWindowsChanged;
        _tracker.ForegroundChanged += OnWindowsChanged;
        _catalog.Changed += OnCatalogChanged;
        _settings.Changed += OnSettingsChanged;
        Theme.Changed += OnSettingsChanged;
        _reservation.PositionChanged += Arrange;
        _reservation.FullscreenChanged += _ => Arrange();
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    public void Start()
    {
        Rebuild();
        _clock.Start();
        _status.Start();
    }

    private void OnWindowsChanged() { if (!_disposed) RebuildApps(); }
    private void OnCatalogChanged() => _dispatcher.BeginInvoke(OnWindowsChanged);
    private void OnSettingsChanged() => _dispatcher.BeginInvoke(() => { if (!_disposed) Rebuild(); });
    private void OnNetworkChanged(object? s, NetworkAvailabilityEventArgs e) => _dispatcher.BeginInvoke(UpdateStatus);

    // ------------------------------------------------------------------
    // 組み立て

    public void Rebuild()
    {
        foreach (var s in _surfaces) s.Close();
        _surfaces.Clear();

        var t = Theme.Current;
        double h = _settings.TaskbarHeight;
        var startGroup = StartGroup(t);
        _apps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var tray = TrayGroup(t);
        bool split = _settings.TaskbarLayout == TaskbarLayout.Split;

        if (!Floating)
        {
            // ---- 帯型: 1 枚のウィンドウを 3 列に分ける ----
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var line = new Border { Height = 1, Background = t.Stroke, VerticalAlignment = IsTop ? VerticalAlignment.Bottom : VerticalAlignment.Top };
            Grid.SetColumnSpan(line, 3);
            grid.Children.Add(line);

            if (split)
            {
                Place(grid, startGroup, 0, HorizontalAlignment.Left);
                Place(grid, _apps, 1, HorizontalAlignment.Center);
            }
            else
            {
                var main = Horizontal(startGroup, _apps);
                bool center = _settings.TaskbarAlignment == TaskbarAlignment.Center;
                Place(grid, main, center ? 1 : 0, center ? HorizontalAlignment.Center : HorizontalAlignment.Left);
            }
            tray.Children.Add(ShowDesktopSliver());
            Place(grid, tray, 2, HorizontalAlignment.Right);
            AddSurface(BarSurface.Anchor.Fill, h, grid);
            _reservation.Reserve(IsTop, h);
        }
        else
        {
            // ---- アイランド型: 島ごとに別ウィンドウ ----
            if (split)
            {
                AddSurface(BarSurface.Anchor.Left, h, Island(startGroup));
                AddSurface(BarSurface.Anchor.Center, h, Island(_apps));
            }
            else
            {
                var anchor = _settings.TaskbarAlignment == TaskbarAlignment.Center ? BarSurface.Anchor.Center : BarSurface.Anchor.Left;
                AddSurface(anchor, h, Island(Horizontal(startGroup, _apps)));
            }
            AddSurface(BarSurface.Anchor.Right, h, Island(tray));
            _reservation.Reserve(IsTop, h + _settings.IslandMargin * 2);
        }

        RebuildApps();
        UpdateClock();
        UpdateStatus();
        foreach (var s in _surfaces) s.Show();
        Arrange();
    }

    private static void Place(Grid g, FrameworkElement e, int col, HorizontalAlignment ha)
    {
        e.HorizontalAlignment = ha;
        e.VerticalAlignment = VerticalAlignment.Center;
        e.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(e, col);
        g.Children.Add(e);
    }

    private static StackPanel Horizontal(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    private static FrameworkElement Island(FrameworkElement content)
    {
        content.VerticalAlignment = VerticalAlignment.Center;
        content.Margin = new Thickness(4, 0, 4, 0);
        return content;
    }

    private void AddSurface(BarSurface.Anchor anchor, double h, FrameworkElement content)
    {
        var s = new BarSurface(anchor, h, _settings.Backdrop) { Content = content, Foreground = Theme.Current.Text };
        s.ContentSizeChanged += Arrange;
        s.Loaded += (_, _) => Arrange();
        _tracker.Ignore(s.Handle);
        _surfaces.Add(s);
    }

    /// <summary>確保領域の中に各ウィンドウを配置する（すべて物理ピクセル）。</summary>
    private void Arrange()
    {
        if (_disposed) return;
        var r = _reservation.Rect;
        bool bottomMost = _reservation.IsFullscreenAppActive; // 全画面アプリ中は最前面をやめる
        foreach (var s in _surfaces)
        {
            if (!s.IsLoaded) continue;
            if (s.Placement == BarSurface.Anchor.Fill)
            {
                s.MoveTo(r.Left, r.Top, r.Width, r.Height, bottomMost);
                continue;
            }
            var (w, h) = s.PixelSize;
            int m = (int)Math.Round(_settings.IslandMargin * s.Scale);
            int y = IsTop ? r.Top + m : r.Bottom - m - h;
            int x = s.Placement switch
            {
                BarSurface.Anchor.Left => r.Left + m,
                BarSurface.Anchor.Right => r.Right - m - w,
                _ => r.Left + (r.Width - w) / 2,
            };
            s.MoveTo(x, y, bottomMost: bottomMost);
        }
    }

    // ------------------------------------------------------------------
    // 左グループ（スタート / 検索 / タスクビュー）

    private StackPanel StartGroup(Theme t)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var start = new Tile { Width = 44, Height = 40, Margin = new Thickness(2, 0, 2, 0), Child = UiKit.StartLogo(18, t.AccentBrush), ToolTip = "スタート" };
        start.Click += _ => StartClicked?.Invoke();
        start.RightClick += x => Flyout.Show(x, new[]
        {
            new MenuItemSpec("", "Win+X メニュー", SystemActions.PowerUserMenu),
            new MenuItemSpec("", "タスク マネージャー", SystemActions.OpenTaskManager),
            new MenuItemSpec("", "エクスプローラー", SystemActions.OpenFileExplorer),
            new MenuItemSpec("", "Windows の設定", SystemActions.OpenSettings),
            MenuItemSpec.Separator,
            new MenuItemSpec("", "ModernShell の設定", () => OpenSettingsRequested?.Invoke()),
            new MenuItemSpec("", "ModernShell を終了", () => ExitRequested?.Invoke()),
        }, FlyoutPlacement);
        p.Children.Add(start);
        p.Children.Add(SmallButton("", "検索", () => SearchClicked?.Invoke()));
        p.Children.Add(SmallButton("", "タスク ビュー", SystemActions.TaskView));
        return p;
    }

    private PlacementMode FlyoutPlacement => IsTop ? PlacementMode.Bottom : PlacementMode.Top;

    private static Tile SmallButton(string glyph, string tip, Action onClick)
    {
        var tile = new Tile { Width = 44, Height = 40, Margin = new Thickness(2, 0, 2, 0), Child = UiKit.Glyph(glyph, 17), ToolTip = tip };
        tile.Click += _ => onClick();
        return tile;
    }

    // ------------------------------------------------------------------
    // 右グループ（システムアイコン / 時計）

    private StackPanel TrayGroup(Theme t)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _network = UiKit.Glyph("", 15);
        _battery = UiKit.Glyph("", 15);
        _battery.Margin = new Thickness(10, 0, 0, 0);
        var volume = UiKit.Glyph("", 15);
        volume.Margin = new Thickness(10, 0, 0, 0);
        var quick = new Tile
        {
            Padding = new Thickness(10, 0, 10, 0), Height = 40, VerticalAlignment = VerticalAlignment.Center,
            Child = UiKit.Row(_network, volume, _battery), ToolTip = "クイック設定 (Win+A)",
        };
        quick.Click += _ => SystemActions.QuickSettings();
        p.Children.Add(quick);

        _time = UiKit.Label("", 12);
        _date = UiKit.Label("", 12);
        _time.HorizontalAlignment = _date.HorizontalAlignment = HorizontalAlignment.Right;
        var clockStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        clockStack.Children.Add(_time);
        clockStack.Children.Add(_date);
        var clock = new Tile
        {
            Padding = new Thickness(10, 0, 10, 0), Height = 40, Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = clockStack, ToolTip = "通知とカレンダー (Win+N)",
        };
        clock.Click += _ => SystemActions.NotificationCenter();
        p.Children.Add(clock);
        return p;
    }

    private static Tile ShowDesktopSliver()
    {
        var desk = new Tile { Width = 10, Height = 40, CornerRadius = new CornerRadius(0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "デスクトップの表示" };
        desk.Click += _ => SystemActions.ShowDesktop();
        return desk;
    }

    private void UpdateClock()
    {
        if (_time == null || _date == null) return;
        var now = DateTime.Now;
        _time.Text = now.ToString(_settings.ShowSeconds ? "H:mm:ss" : "H:mm");
        _date.Text = now.ToString("yyyy/MM/dd");
    }

    /// <summary>ネットワーク接続とバッテリー残量をアイコンに反映。</summary>
    private void UpdateStatus()
    {
        if (_network == null || _battery == null) return;
        var t = Theme.Current;
        bool online = NetworkInterface.GetIsNetworkAvailable();
        _network.Foreground = online ? t.Text : t.TextSecondary;
        _network.Opacity = online ? 1 : 0.5;

        try
        {
            var ps = Forms.SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery))
            {
                _battery.Visibility = Visibility.Collapsed; // デスクトップ PC
                return;
            }
            _battery.Visibility = Visibility.Visible;
            int level = (int)Math.Round(Math.Clamp(ps.BatteryLifePercent, 0f, 1f) * 10);
            bool charging = ps.PowerLineStatus == Forms.PowerLineStatus.Online;
            // Segoe Fluent Icons: Battery0–9 = E850–E859, Battery10 = E83F, 充電中 0–10 = EBAB–EBB5
            _battery.Text = charging ? ((char)(0xEBAB + level)).ToString()
                          : level >= 10 ? "" : ((char)(0xE850 + level)).ToString();
            _battery.ToolTip = $"バッテリー {ps.BatteryLifePercent:P0}{(charging ? "（充電中）" : "")}";
        }
        catch { _battery.Visibility = Visibility.Collapsed; }
    }

    // ------------------------------------------------------------------
    // アプリボタン

    private sealed class Group
    {
        public required string Key;
        public AppEntry? Pinned;
        public List<TaskWindow> Windows = new();
    }

    private void RebuildApps()
    {
        if (_apps == null) return;
        var groups = new List<Group>();

        foreach (var id in _settings.PinnedTaskbar)
        {
            var app = _catalog.Find(id);
            if (app != null) groups.Add(new Group { Key = "pin:" + app.Id, Pinned = app });
        }

        foreach (var w in _tracker.Windows)
        {
            var g = groups.FirstOrDefault(x => x.Pinned != null && KnownApps.Matches(x.Pinned, w));
            if (g == null || !_settings.GroupTaskbarButtons && g.Windows.Count > 0)
            {
                string key = _settings.GroupTaskbarButtons ? w.GroupKey : w.Handle.ToString();
                g = groups.FirstOrDefault(x => x.Key == key);
                if (g == null) { g = new Group { Key = key }; groups.Add(g); }
            }
            g.Windows.Add(w);
        }

        foreach (var g in groups.Where(x => x.Pinned == null && !_order.Contains(x.Key))) _order.Add(g.Key);
        _order.RemoveAll(k => groups.All(g => g.Key != k));
        var ordered = groups.Where(g => g.Pinned != null)
            .Concat(groups.Where(g => g.Pinned == null).OrderBy(g => _order.IndexOf(g.Key)));

        _apps.Children.Clear();
        foreach (var g in ordered) _apps.Children.Add(AppButton(g));
        _apps.Visibility = _apps.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private Tile AppButton(Group g)
    {
        var t = Theme.Current;
        bool running = g.Windows.Count > 0;
        bool active = g.Windows.Any(w => w.Handle == _tracker.Foreground);
        var icon = UiKit.Icon(g.Pinned?.Icon(48) ?? g.Windows.FirstOrDefault()?.Icon, 24);

        var content = new Grid();
        FrameworkElement main = icon;
        if (_settings.ShowTaskbarLabels && running)
        {
            var title = UiKit.Label(g.Windows[0].Title, 12);
            title.MaxWidth = 140;
            title.Margin = new Thickness(8, 0, 0, 0);
            main = UiKit.Row(icon, title);
            main.Margin = new Thickness(10, 0, 10, 0);
        }
        main.HorizontalAlignment = HorizontalAlignment.Center;
        main.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(main);

        if (running)
        {
            content.Children.Add(new Border
            {
                Height = 3, Width = active ? 16 : 6, CornerRadius = new CornerRadius(1.5),
                Background = active ? t.AccentBrush : t.TextSecondary,
                VerticalAlignment = IsTop ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2),
            });
        }

        var tile = new Tile
        {
            MinWidth = 44, Height = 40, Margin = new Thickness(2, 0, 2, 0), Child = content,
            Background = active ? t.Hover : Brushes.Transparent,
            ToolTip = running ? string.Join("\n", g.Windows.Select(w => w.Title)) : g.Pinned?.Name,
        };
        tile.MouseLeave += (_, _) => { if (active) tile.Background = t.Hover; };
        if (Floating && _settings.MagnifyOnHover) AddMagnify(tile, icon);

        tile.Click += x =>
        {
            if (g.Windows.Count == 0) g.Pinned?.Launch();
            else if (g.Windows.Count == 1) _tracker.Toggle(g.Windows[0].Handle);
            else WindowList(x, g);
        };
        tile.MiddleClick += _ => (g.Pinned ?? _catalog.FindByExe(g.Windows.FirstOrDefault()?.ExePath ?? ""))?.Launch();
        tile.RightClick += x => Flyout.Show(x, GroupMenu(g), FlyoutPlacement);
        return tile;
    }

    /// <summary>アイランド型のホバー拡大（Mac のドック風）。レイアウトは変えず描画だけ拡大する。</summary>
    private void AddMagnify(Tile tile, Image icon)
    {
        var scale = new ScaleTransform(1, 1);
        icon.RenderTransform = scale;
        icon.RenderTransformOrigin = new Point(0.5, IsTop ? 0.2 : 0.8);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        void To(double v)
        {
            var a = new DoubleAnimation(v, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        tile.MouseEnter += (_, _) => To(1.25);
        tile.MouseLeave += (_, _) => To(1.0);
    }

    private void WindowList(UIElement target, Group g) =>
        Flyout.Show(target, g.Windows.Select(w =>
            new MenuItemSpec("", Truncate(w.Title, 48), () => _tracker.Activate(w.Handle))), FlyoutPlacement);

    private IEnumerable<MenuItemSpec> GroupMenu(Group g)
    {
        var app = g.Pinned ?? (g.Windows.FirstOrDefault() is { } w
            ? (w.Aumid != null ? _catalog.Find(w.Aumid) : null) ?? (w.ExePath != null ? _catalog.FindByExe(w.ExePath) : null)
            : null);
        var items = new List<MenuItemSpec>();
        if (app != null)
        {
            items.Add(new MenuItemSpec("", app.Name, () => app.Launch()));
            bool pinned = _settings.PinnedTaskbar.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
            items.Add(pinned
                ? new MenuItemSpec("", "タスクバーからピン留めを外す", () => { _settings.PinnedTaskbar.RemoveAll(x => x.Equals(app.Id, StringComparison.OrdinalIgnoreCase)); _settings.Save(); })
                : new MenuItemSpec("", "タスクバーにピン留めする", () => { _settings.PinnedTaskbar.Add(app.Id); _settings.Save(); }));
        }
        if (g.Windows.Count > 0)
        {
            items.Add(MenuItemSpec.Separator);
            items.Add(new MenuItemSpec("", g.Windows.Count > 1 ? "すべてのウィンドウを閉じる" : "ウィンドウを閉じる",
                () => { foreach (var w in g.Windows) WindowTracker.Close(w.Handle); }, Danger: true));
        }
        return items;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

    // ------------------------------------------------------------------

    /// <summary>explorer 再起動時など、作業領域の予約をやり直す。</summary>
    public void Reapply() => _reservation.Reserve(IsTop, Floating ? _settings.TaskbarHeight + _settings.IslandMargin * 2 : _settings.TaskbarHeight);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clock.Stop();
        _status.Stop();
        _tracker.WindowsChanged -= OnWindowsChanged;
        _tracker.ForegroundChanged -= OnWindowsChanged;
        _catalog.Changed -= OnCatalogChanged;
        _settings.Changed -= OnSettingsChanged;
        Theme.Changed -= OnSettingsChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        foreach (var s in _surfaces) s.Close();
        _surfaces.Clear();
        _reservation.Dispose();
    }
}
