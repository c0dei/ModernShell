using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;
using ModernShell.Core;
using ModernShell.Native;
using Forms = System.Windows.Forms;

namespace ModernShell.UI;

/// <summary>
/// 独自タスクバー全体の制御。
///   スタートボタン類の位置: アプリと一緒（中央/左） / 常に左端
///   スタイル            : 帯 / アイランド
/// の組み合わせで見た目が決まる。作業領域の確保は AppBarReservation が 1 つだけ行う。
/// タスク一覧とトレイアイコンは ManagedShell (ShellHost) から受け取る。
/// </summary>
public sealed class TaskbarController : IDisposable
{
    private readonly Settings _settings;
    private readonly AppCatalog _catalog;
    private readonly ShellHost _shell;
    private readonly Dispatcher _dispatcher;
    private readonly AppBarReservation _reservation = new();
    private readonly List<BarSurface> _surfaces = new();
    private readonly DispatcherTimer _clock, _status, _appsDebounce, _trayDebounce;
    private readonly List<string> _order = new(); // 実行中ボタンの並び順を安定させる
    private readonly HashSet<INotifyPropertyChanged> _watched = new();
    private bool _disposed;

    private StackPanel? _apps, _trayIcons;
    private Tile? _overflowButton;
    private FrameworkElement? _trayHost;
    private TextBlock? _time, _date, _network, _battery;
    private Popup? _overflowPopup;
    private bool _trayEditMode;

    public event Action? StartClicked;
    public event Action? SearchClicked;
    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;

    public bool IsTop => _settings.TaskbarEdge == TaskbarEdge.Top;
    private bool Floating => _settings.TaskbarStyle == TaskbarStyle.Floating;
    private bool StartAtLeftEdge => _settings.StartButtonPosition == StartButtonPosition.LeftEdge;
    public Win32.RECT BarRect => _reservation.Rect;
    /// <summary>スタートメニューを中央に出すか（スタートボタンが中央のかたまりにあるとき）。</summary>
    public bool StartCentered => !StartAtLeftEdge && _settings.TaskbarAlignment == TaskbarAlignment.Center;

    public TaskbarController(Settings settings, AppCatalog catalog, ShellHost shell, Dispatcher dispatcher)
    {
        _settings = settings;
        _catalog = catalog;
        _shell = shell;
        _dispatcher = dispatcher;

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => UpdateClock(), dispatcher);
        _status = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => UpdateStatus(), dispatcher);
        _appsDebounce = Debouncer(RebuildApps);
        _trayDebounce = Debouncer(RebuildTray);

        if (_shell.Tasks?.GroupedWindows is INotifyCollectionChanged tasks) tasks.CollectionChanged += OnTasksChanged;
        if (_shell.Tray?.PinnedIcons is INotifyCollectionChanged pinned) pinned.CollectionChanged += OnTrayChanged;
        if (_shell.Tray?.UnpinnedIcons is INotifyCollectionChanged unpinned) unpinned.CollectionChanged += OnTrayChanged;

        _catalog.Changed += OnCatalogChanged;
        _settings.Changed += OnSettingsChanged;
        Theme.Changed += OnSettingsChanged;
        _reservation.PositionChanged += Arrange;
        _reservation.FullscreenChanged += _ => Arrange();
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    private DispatcherTimer Debouncer(Action a)
    {
        DispatcherTimer? t = null;
        t = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => { t!.Stop(); if (!_disposed) a(); }, _dispatcher);
        t.Stop();
        return t;
    }

    private static void Kick(DispatcherTimer t) { t.Stop(); t.Start(); }

    public void Start()
    {
        Rebuild();
        _clock.Start();
        _status.Start();
    }

    private void OnTasksChanged(object? s, NotifyCollectionChangedEventArgs e) => _dispatcher.BeginInvoke(() => Kick(_appsDebounce));
    private void OnTrayChanged(object? s, NotifyCollectionChangedEventArgs e) => _dispatcher.BeginInvoke(() => Kick(_trayDebounce));
    private void OnItemChanged(object? s, PropertyChangedEventArgs e)
    {
        if (s is ApplicationWindow && e.PropertyName is "State" or "Title" or "Icon" or "ProgressState" or "ProgressValue")
            _dispatcher.BeginInvoke(() => Kick(_appsDebounce));
        else if (s is ManagedShell.WindowsTray.NotifyIcon && e.PropertyName is "Icon" or "Title" or "IsHidden")
            _dispatcher.BeginInvoke(() => Kick(_trayDebounce));
    }
    private void Watch(INotifyPropertyChanged o) { if (_watched.Add(o)) o.PropertyChanged += OnItemChanged; }

    private void OnCatalogChanged() => _dispatcher.BeginInvoke(() => Kick(_appsDebounce));
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
        _trayHost = tray;

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

            bool center = _settings.TaskbarAlignment == TaskbarAlignment.Center;
            if (StartAtLeftEdge)
            {
                if (center)
                {
                    Place(grid, startGroup, 0, HorizontalAlignment.Left);
                    Place(grid, _apps, 1, HorizontalAlignment.Center);
                }
                else Place(grid, Horizontal(startGroup, _apps), 0, HorizontalAlignment.Left);
            }
            else Place(grid, Horizontal(startGroup, _apps), center ? 1 : 0, center ? HorizontalAlignment.Center : HorizontalAlignment.Left);

            tray.Children.Add(ShowDesktopSliver());
            Place(grid, tray, 2, HorizontalAlignment.Right);
            AddSurface(BarSurface.Anchor.Fill, h, grid);
            _reservation.Reserve(IsTop, h);
        }
        else
        {
            // ---- アイランド型: 島ごとに別ウィンドウ ----
            var appsAnchor = _settings.TaskbarAlignment == TaskbarAlignment.Center ? BarSurface.Anchor.Center : BarSurface.Anchor.Left;
            if (StartAtLeftEdge && appsAnchor == BarSurface.Anchor.Center)
            {
                AddSurface(BarSurface.Anchor.Left, h, Island(startGroup));
                AddSurface(BarSurface.Anchor.Center, h, Island(_apps));
            }
            else AddSurface(appsAnchor, h, Island(Horizontal(startGroup, _apps)));
            AddSurface(BarSurface.Anchor.Right, h, Island(tray));
            _reservation.Reserve(IsTop, h + _settings.IslandMargin * 2);
        }

        RebuildApps();
        RebuildTray();
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
        var s = new BarSurface(anchor, h, _settings) { Content = content, Foreground = Theme.Current.Text };
        s.ContentSizeChanged += Arrange;
        s.Loaded += (_, _) => Arrange();
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
        UpdateTrayHostData();
    }

    // ------------------------------------------------------------------
    // スタート / 検索 / タスクビュー

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
            new MenuItemSpec("", "ModernShell を終了（純正に戻す）", () => ExitRequested?.Invoke()),
        }, FlyoutPlacement);
        p.Children.Add(start);
        if (_settings.ShowSearchButton) p.Children.Add(SmallButton("", "検索", () => SearchClicked?.Invoke()));
        if (_settings.ShowTaskViewButton) p.Children.Add(SmallButton("", "タスク ビュー", SystemActions.TaskView));
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
    // 右側（トレイアイコン / クイック設定 / 時計）

    private StackPanel TrayGroup(Theme t)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _overflowButton = new Tile
        {
            Width = 28, Height = 40, Margin = new Thickness(2, 0, 0, 0),
            Child = UiKit.Glyph(IsTop ? "" : "", 12), ToolTip = "隠れているインジケーターを表示",
        };
        _overflowButton.Click += ShowOverflow;
        p.Children.Add(_overflowButton);

        _trayIcons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(_trayIcons);

        _network = UiKit.Glyph("", 15);
        _battery = UiKit.Glyph("", 15);
        _battery.Margin = new Thickness(10, 0, 0, 0);
        var volume = UiKit.Glyph("", 15);
        volume.Margin = new Thickness(10, 0, 0, 0);
        var quick = new Tile
        {
            Padding = new Thickness(10, 0, 10, 0), Height = 40, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
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

    private static bool Visible(ManagedShell.WindowsTray.NotifyIcon i) =>
        !i.IsHidden && !ShellHost.HiddenSystemIcons.Contains(i.GUID.ToString());

    private IEnumerable<ManagedShell.WindowsTray.NotifyIcon> Icons(ICollectionView? view) =>
        view?.Cast<ManagedShell.WindowsTray.NotifyIcon>().Where(Visible) ?? Enumerable.Empty<ManagedShell.WindowsTray.NotifyIcon>();

    private void RebuildTray()
    {
        if (_trayIcons == null || _overflowButton == null) return;
        _trayIcons.Children.Clear();
        foreach (var icon in Icons(_shell.Tray?.PinnedIcons))
        {
            Watch(icon);
            _trayIcons.Children.Add(TrayIconTile(icon, editable: false));
        }
        _overflowButton.Visibility = Icons(_shell.Tray?.UnpinnedIcons).Any() || _trayEditMode ? Visibility.Visible : Visibility.Collapsed;
        if (_overflowPopup?.IsOpen == true) FillOverflow();
    }

    /// <summary>トレイアイコン 1 個。マウス操作はそのままアプリへ転送する（右クリックメニューもアプリ自身が出す）。</summary>
    private Tile TrayIconTile(ManagedShell.WindowsTray.NotifyIcon icon, bool editable)
    {
        var img = UiKit.Icon(icon.Icon, 16);
        var grid = new Grid();
        grid.Children.Add(img);
        if (editable && _trayEditMode)
        {
            grid.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = icon.IsPinned ? Theme.Current.AccentBrush : Theme.Current.TextSecondary, Margin = new Thickness(0, -4, -6, 0),
            });
        }
        var tile = new Tile { Width = 32, Height = 36, Margin = new Thickness(1, 0, 1, 0), Child = grid, ToolTip = icon.Title };

        if (editable && _trayEditMode)
        {
            tile.Click += _ =>
            {
                if (icon.IsPinned) icon.Unpin(); else icon.Pin();
                _shell.SavePinnedTray(_settings);
                RebuildTray();
            };
            return tile;
        }

        int dbl = Forms.SystemInformation.DoubleClickTime;
        tile.MouseEnter += (_, _) => { SetPlacement(icon, tile); icon.IconMouseEnter(MouseHelper.GetCursorPositionParam()); };
        tile.MouseLeave += (_, _) => icon.IconMouseLeave(MouseHelper.GetCursorPositionParam());
        tile.MouseMove += (_, _) => icon.IconMouseMove(MouseHelper.GetCursorPositionParam());
        tile.PreviewMouseDown += (_, e) =>
        {
            SetPlacement(icon, tile);
            icon.IconMouseDown(e.ChangedButton, MouseHelper.GetCursorPositionParam(), dbl);
            e.Handled = true;
        };
        tile.PreviewMouseUp += (_, e) =>
        {
            SetPlacement(icon, tile);
            icon.IconMouseUp(e.ChangedButton, MouseHelper.GetCursorPositionParam(), dbl);
            if (_overflowPopup != null) _overflowPopup.IsOpen = false;
            e.Handled = true;
        };
        return tile;
    }

    /// <summary>アプリがメニューやポップアップを正しい位置に出せるよう、アイコンの画面上の矩形を渡す。</summary>
    private static void SetPlacement(ManagedShell.WindowsTray.NotifyIcon icon, FrameworkElement el)
    {
        try
        {
            var tl = el.PointToScreen(new Point(0, 0));
            var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
            icon.Placement = new NativeMethods.Rect((int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y);
        }
        catch { }
    }

    private void UpdateTrayHostData()
    {
        if (_shell.Tray == null || _trayHost == null || !_trayHost.IsLoaded) return;
        try
        {
            var tl = _trayHost.PointToScreen(new Point(0, 0));
            var br = _trayHost.PointToScreen(new Point(_trayHost.ActualWidth, _trayHost.ActualHeight));
            _shell.Tray.SetTrayHostSizeData(new TrayHostSizeData
            {
                edge = IsTop ? NativeMethods.ABEdge.ABE_TOP : NativeMethods.ABEdge.ABE_BOTTOM,
                rc = new NativeMethods.Rect((int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y),
            });
        }
        catch { }
    }

    // 「^」で開く隠れたアイコン一覧
    private WrapPanel? _overflowPanel;
    private TextBlock? _overflowHint;

    private void ShowOverflow(Tile anchor)
    {
        var t = Theme.Current;
        _overflowPanel = new WrapPanel { MaxWidth = 5 * 34, Margin = new Thickness(6) };
        _overflowHint = UiKit.Label("", 11, t.TextSecondary);
        _overflowHint.TextWrapping = TextWrapping.Wrap;
        _overflowHint.MaxWidth = 5 * 34;
        _overflowHint.Margin = new Thickness(8, 0, 8, 4);

        var edit = new Tile { Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Right };
        void UpdateEdit()
        {
            edit.Child = UiKit.Row(UiKit.Glyph(_trayEditMode ? "" : "", 12), Spaced(UiKit.Label(_trayEditMode ? "完了" : "表示するアイコンを選ぶ", 12)));
            _overflowHint!.Text = _trayEditMode ? "クリックで「タスクバーに常に表示」を切り替えます（青い点 = 表示中）" : "";
            _overflowHint.Visibility = _trayEditMode ? Visibility.Visible : Visibility.Collapsed;
        }
        edit.Click += _ => { _trayEditMode = !_trayEditMode; UpdateEdit(); FillOverflow(); RebuildTray(); };
        UpdateEdit();

        var stack = new StackPanel();
        stack.Children.Add(_overflowPanel);
        stack.Children.Add(_overflowHint);
        stack.Children.Add(edit);

        _overflowPopup = new Popup
        {
            AllowsTransparency = true, StaysOpen = false, PlacementTarget = anchor, Placement = FlyoutPlacement, PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = t.SolidBackground, BorderBrush = t.Stroke, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(t.Radius), Margin = new Thickness(8), Child = stack,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.3 },
            },
        };
        _overflowPopup.Closed += (_, _) => { if (_trayEditMode) { _trayEditMode = false; RebuildTray(); } };
        FillOverflow();
        _overflowPopup.IsOpen = true;
    }

    private void FillOverflow()
    {
        if (_overflowPanel == null) return;
        _overflowPanel.Children.Clear();
        var icons = _trayEditMode
            ? Icons(_shell.Tray?.AllIcons)
            : Icons(_shell.Tray?.UnpinnedIcons);
        foreach (var icon in icons)
        {
            Watch(icon);
            _overflowPanel.Children.Add(TrayIconTile(icon, editable: true));
        }
        if (_overflowPanel.Children.Count == 0)
            _overflowPanel.Children.Add(new TextBlock { Text = "アイコンはありません", Margin = new Thickness(8), Foreground = Theme.Current.TextSecondary });
    }

    private static FrameworkElement Spaced(FrameworkElement e) { e.Margin = new Thickness(8, 0, 0, 0); return e; }

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
        public List<ApplicationWindow> Windows = new();
    }

    private IEnumerable<ApplicationWindow> TaskWindows() =>
        _shell.Tasks?.GroupedWindows?.Cast<ApplicationWindow>().Where(w => w.ShowInTaskbar) ?? Enumerable.Empty<ApplicationWindow>();

    private static string GroupKey(ApplicationWindow w) => w.AppUserModelID ?? w.WinFileName ?? w.Handle.ToString();

    private void RebuildApps()
    {
        if (_apps == null) return;
        var groups = new List<Group>();

        foreach (var id in _settings.PinnedTaskbar)
        {
            var app = _catalog.Find(id);
            if (app != null) groups.Add(new Group { Key = "pin:" + app.Id, Pinned = app });
        }

        foreach (var w in TaskWindows())
        {
            Watch(w);
            var g = groups.FirstOrDefault(x => x.Pinned != null && KnownApps.Matches(x.Pinned, w.AppUserModelID, w.WinFileName));
            if (g == null || !_settings.GroupTaskbarButtons && g.Windows.Count > 0)
            {
                string key = _settings.GroupTaskbarButtons ? GroupKey(w) : w.Handle.ToString();
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
        bool active = g.Windows.Any(w => w.State == ApplicationWindow.WindowState.Active);
        bool flashing = g.Windows.Any(w => w.State == ApplicationWindow.WindowState.Flashing);
        var icon = UiKit.Icon(g.Windows.FirstOrDefault()?.Icon ?? g.Pinned?.Icon(48), 24);

        var content = new Grid();
        FrameworkElement main = icon;
        if (_settings.ShowTaskbarLabels && running)
        {
            var title = UiKit.Label(g.Windows[0].Title ?? "", 12);
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
            var indicator = flashing ? new SolidColorBrush(Color.FromRgb(0xF7, 0xA4, 0x00)) : active ? t.AccentBrush : t.TextSecondary;
            content.Children.Add(new Border
            {
                Height = 3, Width = active || flashing ? 16 : 6, CornerRadius = new CornerRadius(1.5), Background = indicator,
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
            else if (g.Windows.Count == 1) ToggleWindow(g.Windows[0]);
            else WindowList(x, g);
        };
        tile.MiddleClick += _ => (g.Pinned ?? FindApp(g.Windows.FirstOrDefault()))?.Launch();
        tile.RightClick += x => Flyout.Show(x, GroupMenu(g), FlyoutPlacement);
        return tile;
    }

    /// <summary>前面（アクティブ）なら最小化、それ以外は前面化（最小化中なら復元）。</summary>
    private static void ToggleWindow(ApplicationWindow w)
    {
        if (w.State == ApplicationWindow.WindowState.Active && !w.IsMinimized) w.Minimize();
        else w.BringToFront();
    }

    private AppEntry? FindApp(ApplicationWindow? w)
    {
        if (w == null) return null;
        return (w.AppUserModelID != null ? _catalog.Find(w.AppUserModelID) : null)
               ?? (w.WinFileName != null ? _catalog.FindByExe(w.WinFileName) : null);
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
            new MenuItemSpec("", Truncate(w.Title ?? "", 48), () => w.BringToFront())), FlyoutPlacement);

    private IEnumerable<MenuItemSpec> GroupMenu(Group g)
    {
        var app = g.Pinned ?? FindApp(g.Windows.FirstOrDefault());
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
                () => { foreach (var w in g.Windows) w.Close(); }, Danger: true));
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
        _appsDebounce.Stop();
        _trayDebounce.Stop();
        if (_shell.Tasks?.GroupedWindows is INotifyCollectionChanged tasks) tasks.CollectionChanged -= OnTasksChanged;
        if (_shell.Tray?.PinnedIcons is INotifyCollectionChanged pinned) pinned.CollectionChanged -= OnTrayChanged;
        if (_shell.Tray?.UnpinnedIcons is INotifyCollectionChanged unpinned) unpinned.CollectionChanged -= OnTrayChanged;
        foreach (var o in _watched) o.PropertyChanged -= OnItemChanged;
        _watched.Clear();
        _catalog.Changed -= OnCatalogChanged;
        _settings.Changed -= OnSettingsChanged;
        Theme.Changed -= OnSettingsChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        if (_overflowPopup != null) _overflowPopup.IsOpen = false;
        foreach (var s in _surfaces) s.Close();
        _surfaces.Clear();
        _reservation.Dispose();
    }
}
