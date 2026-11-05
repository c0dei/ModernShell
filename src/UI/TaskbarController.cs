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

    private readonly WindowsPins _windowsPins;
    private bool PinsFromWindows => _settings.TaskbarPinSource == TaskbarPinSource.Windows;

    // ---- 大きさ（設定の高さ・アイコンサイズから算出）----
    /// <summary>ボタンの高さ。バーの高さより少し小さくする。</summary>
    private double TileH => Math.Max(22, Math.Min(40, _settings.TaskbarHeight - 6));
    private double IconSz => Math.Clamp(_settings.TaskbarIconSize, 12, 40);
    /// <summary>ボタンの幅（アイコンが大きいほど広く、バーが低いほど詰める）。</summary>
    private double TileW => Math.Max(TileH, IconSz + 16);
    private double GlyphSz => Math.Clamp(IconSz * 0.7, 11, 22);
    private bool Compact => _settings.TaskbarHeight < 40;

    public TaskbarController(Settings settings, AppCatalog catalog, ShellHost shell, Dispatcher dispatcher)
    {
        _settings = settings;
        _catalog = catalog;
        _shell = shell;
        _dispatcher = dispatcher;
        _windowsPins = new WindowsPins(catalog);
        _windowsPins.Changed += OnPinsChanged;

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

    private void OnPinsChanged() => _dispatcher.BeginInvoke(() => { if (PinsFromWindows) Kick(_appsDebounce); });
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
        // 空いている場所でも右クリックを受け取れるよう、透明の背景を付ける
        if (content is Panel panel && panel.Background == null) panel.Background = Brushes.Transparent;
        var s = new BarSurface(anchor, h, _settings) { Content = content, Foreground = Theme.Current.Text };
        // ボタン以外の場所を右クリック → Windows のタスクバーと同じく「タスク マネージャー」「タスクバーの設定」
        s.MouseRightButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            Flyout.Show(content, new[]
            {
                new MenuItemSpec("\uE9D9", "タスク マネージャー", SystemActions.OpenTaskManager),
                MenuItemSpec.Separator,
                new MenuItemSpec("\uE713", "タスクバーの設定", () => OpenSettingsRequested?.Invoke()),
            }, PlacementMode.MousePoint);
        };
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
        var start = new Tile { Width = TileW, Height = TileH, Margin = new Thickness(2, 0, 2, 0), Child = UiKit.StartLogo(Math.Round(IconSz * 0.75), t.AccentBrush), ToolTip = "スタート" };
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

    private Tile SmallButton(string glyph, string tip, Action onClick)
    {
        var tile = new Tile { Width = TileW, Height = TileH, Margin = new Thickness(2, 0, 2, 0), Child = UiKit.Glyph(glyph, GlyphSz), ToolTip = tip };
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
            Width = 28, Height = TileH, Margin = new Thickness(2, 0, 0, 0),
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
            Padding = new Thickness(10, 0, 10, 0), Height = TileH, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
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
        if (Compact) _date.Visibility = Visibility.Collapsed; // 低いバーでは日付を省略（ツールチップで確認）
        var clock = new Tile
        {
            Padding = new Thickness(10, 0, 10, 0), Height = TileH, Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = clockStack, ToolTip = "通知とカレンダー (Win+N)",
        };
        clock.Click += _ => SystemActions.NotificationCenter();
        p.Children.Add(clock);
        return p;
    }

    private Tile ShowDesktopSliver()
    {
        var desk = new Tile { Width = 10, Height = TileH, CornerRadius = new CornerRadius(0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "デスクトップの表示" };
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
        var pinned = Icons(_shell.Tray?.PinnedIcons).OrderBy(i => i.PinOrder).ToList();
        var reorder = new LiveReorder(_trayIcons, Orientation.Horizontal, (from, to) => MoveTrayIcon(pinned, from, to));
        foreach (var icon in pinned)
        {
            Watch(icon);
            _trayIcons.Children.Add(TrayIconTile(icon, editable: false, reorder));
        }
        _overflowButton.Visibility = Icons(_shell.Tray?.UnpinnedIcons).Any() || _trayEditMode ? Visibility.Visible : Visibility.Collapsed;
        if (_overflowPopup?.IsOpen == true) FillOverflow();
    }

    /// <summary>トレイアイコン 1 個。マウス操作はそのままアプリへ転送する（右クリックメニューもアプリ自身が出す）。</summary>
    private Tile TrayIconTile(ManagedShell.WindowsTray.NotifyIcon icon, bool editable, LiveReorder? reorder = null)
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
        var tile = new Tile { Width = Compact ? 26 : 32, Height = Math.Min(36, TileH), Margin = new Thickness(1, 0, 1, 0), Child = grid, ToolTip = icon.Title };

        if (editable && _trayEditMode)
        {
            tile.Click += _ =>
            {
                if (icon.IsPinned) icon.Unpin(); else icon.Pin();
                _shell.SavePinnedTray(_settings);
                RebuildTray();
            };
            if (icon.IsPinned) reorder?.Add(tile); // 常に表示するアイコン同士はドラッグで並べ替え
            return tile;
        }

        int dbl = Forms.SystemInformation.DoubleClickTime;
        tile.MouseEnter += (_, _) => { SetPlacement(icon, tile); icon.IconMouseEnter(MouseHelper.GetCursorPositionParam()); };
        tile.MouseLeave += (_, _) => icon.IconMouseLeave(MouseHelper.GetCursorPositionParam());
        tile.MouseMove += (_, _) => icon.IconMouseMove(MouseHelper.GetCursorPositionParam());
        // 並べ替え可能なアイコンは、左ボタンの押下をすぐにはアプリへ送らず、離したときに「ドラッグでなかった」場合だけ
        // 押下 → 解放の順に送る（ドラッグ中にアプリのメニューが開かないように）
        reorder?.Add(tile);
        tile.PreviewMouseDown += (_, e) =>
        {
            SetPlacement(icon, tile);
            if (reorder != null && e.ChangedButton == MouseButton.Left) return;
            icon.IconMouseDown(e.ChangedButton, MouseHelper.GetCursorPositionParam(), dbl);
            e.Handled = true;
        };
        tile.PreviewMouseUp += (_, e) =>
        {
            if (reorder != null && e.ChangedButton == MouseButton.Left)
            {
                if (reorder.IsDragging) return; // 並べ替えの終了は LiveReorder が処理する
                SetPlacement(icon, tile);
                icon.IconMouseDown(MouseButton.Left, MouseHelper.GetCursorPositionParam(), dbl);
            }
            SetPlacement(icon, tile);
            icon.IconMouseUp(e.ChangedButton, MouseHelper.GetCursorPositionParam(), dbl);
            if (_overflowPopup != null) _overflowPopup.IsOpen = false;
            e.Handled = true;
        };
        return tile;
    }

    /// <summary>常に表示するトレイアイコンの並べ替えを確定する。</summary>
    private void MoveTrayIcon(List<ManagedShell.WindowsTray.NotifyIcon> visible, int from, int to)
    {
        var dragged = visible[from];
        var target = visible[to];
        dragged.Pin(target.PinOrder + (to > from ? 1 : 0));
        _shell.SavePinnedTray(_settings);
        RebuildTray();
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
    private StackPanel? _overflowPanel;
    private TextBlock? _overflowHint;

    private void ShowOverflow(Tile anchor)
    {
        if (_overflowPopup?.IsOpen == true) { _overflowPopup.IsOpen = false; return; } // 編集中（固定表示）に「^」で閉じる
        var t = Theme.Current;
        _overflowPanel = new StackPanel { Margin = new Thickness(6) };
        _overflowHint = UiKit.Label("", 11, t.TextSecondary);
        _overflowHint.TextWrapping = TextWrapping.Wrap;
        _overflowHint.MaxWidth = 6 * 34;
        _overflowHint.Margin = new Thickness(8, 0, 8, 4);

        var edit = new Tile { Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Right };
        void UpdateEdit()
        {
            edit.Child = UiKit.Row(UiKit.Glyph(_trayEditMode ? "" : "", 12), Spaced(UiKit.Label(_trayEditMode ? "完了" : "表示するアイコンを選ぶ", 12)));
            _overflowHint!.Text = _trayEditMode ? "クリックで表示/非表示を切り替え、「常に表示」の中はドラッグで並べ替えできます" : "";
            _overflowHint.Visibility = _trayEditMode ? Visibility.Visible : Visibility.Collapsed;
        }
        edit.Click += _ =>
        {
            _trayEditMode = !_trayEditMode;
            // 編集中はドラッグでマウスを奪っても閉じないよう固定表示（「完了」か「^」で閉じる）
            if (_overflowPopup != null) _overflowPopup.StaysOpen = _trayEditMode;
            if (_trayEditMode) anchor.IsHitTestVisible = true; // 固定表示中は「^」を押して閉じられるように
            UpdateEdit(); FillOverflow(); RebuildTray();
        };
        UpdateEdit();

        var stack = new StackPanel();
        stack.Children.Add(_overflowPanel);
        stack.Children.Add(_overflowHint);
        stack.Children.Add(edit);

        _overflowPopup = FluentSurface.Create(stack, anchor, FlyoutPlacement);
        _overflowPopup.Closed += (_, _) => { if (_trayEditMode) { _trayEditMode = false; RebuildTray(); } };
        FillOverflow();
        _overflowPopup.IsOpen = true;
        Flyout.ToggleGuard(_overflowPopup, anchor);
    }

    private void FillOverflow()
    {
        if (_overflowPanel == null) return;
        _overflowPanel.Children.Clear();
        var t = Theme.Current;

        WrapPanel Section(string? title)
        {
            if (title != null)
            {
                var h = UiKit.Label(title, 11, t.TextSecondary, FontWeights.SemiBold);
                h.Margin = new Thickness(4, _overflowPanel!.Children.Count == 0 ? 0 : 8, 0, 4);
                _overflowPanel!.Children.Add(h);
            }
            var w = new WrapPanel { MaxWidth = 6 * 34 }; // Windows と同じく数個ごとに折り返す
            _overflowPanel!.Children.Add(w);
            return w;
        }

        if (_trayEditMode)
        {
            Section("常に表示（ドラッグで並べ替え）");
            _overflowPanel.Children.RemoveAt(_overflowPanel.Children.Count - 1); // 折り返しパネルの代わりに 1 行のパネルを使う
            var pinnedRow = new StackPanel { Orientation = Orientation.Horizontal };
            _overflowPanel.Children.Add(pinnedRow);
            var pinnedIcons = Icons(_shell.Tray?.PinnedIcons).OrderBy(i => i.PinOrder).ToList();
            var reorder = new LiveReorder(pinnedRow, Orientation.Horizontal, (from, to) => { MoveTrayIcon(pinnedIcons, from, to); FillOverflow(); });
            foreach (var icon in pinnedIcons)
            {
                Watch(icon);
                pinnedRow.Children.Add(TrayIconTile(icon, editable: true, reorder));
            }
            if (pinnedRow.Children.Count == 0) pinnedRow.Children.Add(UiKit.Label("なし", 12, t.TextSecondary));
            var others = Section("「^」の中に表示");
            foreach (var icon in Icons(_shell.Tray?.UnpinnedIcons))
            {
                Watch(icon);
                others.Children.Add(TrayIconTile(icon, editable: true));
            }
            if (others.Children.Count == 0) others.Children.Add(UiKit.Label("なし", 12, t.TextSecondary));
            return;
        }

        var all = Section(null);
        foreach (var icon in Icons(_shell.Tray?.UnpinnedIcons))
        {
            Watch(icon);
            all.Children.Add(TrayIconTile(icon, editable: true));
        }
        if (all.Children.Count == 0)
            all.Children.Add(new TextBlock { Text = "アイコンはありません", Margin = new Thickness(8), Foreground = t.TextSecondary });
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

    // ManagedShell は値がないとき null ではなく空文字を返すので、空文字は「なし」として扱う
    // （そうしないと AUMID を持たないアプリが全部 1 つのボタンにまとめられてしまう）
    // ウィンドウに AUMID が付いていなければ、パッケージ版アプリ（Terminal、ストア版 Claude など）はプロセスから取る
    private static string? Aumid(ApplicationWindow w) => string.IsNullOrEmpty(w.AppUserModelID) ? ProcessAumid.Get(w.Handle) : w.AppUserModelID;
    private static string? ExePath(ApplicationWindow w) => string.IsNullOrEmpty(w.WinFileName) ? null : w.WinFileName;
    private static string GroupKey(ApplicationWindow w) => Aumid(w) ?? ExePath(w) ?? w.Handle.ToString();

    private void RebuildApps()
    {
        if (_apps == null) return;
        // メニューを開いている間に作り直すと、メニューの基準のボタンが消えて画面左上に飛んでしまうので後回しにする
        if (FluentSurface.AnyOpen) { Kick(_appsDebounce); return; }
        var groups = new List<Group>();

        foreach (var app in PinnedApps())
            if (groups.All(x => x.Key != "pin:" + app.Id)) groups.Add(new Group { Key = "pin:" + app.Id, Pinned = app });

        foreach (var w in TaskWindows())
        {
            Watch(w);
            var g = groups.FirstOrDefault(x => x.Pinned != null && KnownApps.Matches(x.Pinned, Aumid(w), ExePath(w)));
            if (g == null || !_settings.GroupTaskbarButtons && g.Windows.Count > 0)
            {
                string key = _settings.GroupTaskbarButtons ? GroupKey(w) : w.Handle.ToString();
                g = groups.FirstOrDefault(x => x.Key == key);
                if (g == null) { g = new Group { Key = key }; groups.Add(g); }
            }
            g.Windows.Add(w);
            if (g.Pinned == null) LogUnmatched(w);
        }

        foreach (var g in groups.Where(x => x.Pinned == null && !_order.Contains(x.Key))) _order.Add(g.Key);
        _order.RemoveAll(k => groups.All(g => g.Key != k));
        var ordered = groups.Where(g => g.Pinned != null)
            .Concat(groups.Where(g => g.Pinned == null).OrderBy(g => _order.IndexOf(g.Key)));

        _apps.Children.Clear();
        var pinnedIds = new List<string>();
        LiveReorder? reorder = null;
        if (!PinsFromWindows)
            reorder = new LiveReorder(_apps, Orientation.Horizontal, (from, to) => CommitPinnedOrder(pinnedIds, from, to));
        foreach (var g in ordered)
        {
            var tile = AppButton(g);
            _apps.Children.Add(tile);
            // ピン留めボタンはドラッグで並べ替え（つかむとカーソルに付いてきて、他のボタンがよける）
            if (reorder != null && g.Pinned != null)
            {
                pinnedIds.Add(g.Pinned.Id);
                reorder.Add(tile);
            }
        }
        _apps.Visibility = _apps.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private Tile AppButton(Group g)
    {
        var t = Theme.Current;
        bool running = g.Windows.Count > 0;
        bool active = g.Windows.Any(w => w.State == ApplicationWindow.WindowState.Active);
        bool flashing = g.Windows.Any(w => w.State == ApplicationWindow.WindowState.Flashing);
        var icon = UiKit.Icon(g.Windows.FirstOrDefault()?.Icon ?? g.Pinned?.Icon(64), IconSz);

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
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, Compact ? 0 : 2, 0, Compact ? 0 : 2),
            });
        }

        var tile = new Tile
        {
            MinWidth = TileW, Height = TileH, Margin = new Thickness(2, 0, 2, 0), Child = content,
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

    private readonly HashSet<string> _loggedUnmatched = new();

    /// <summary>ピン留めと結び付かなかったウィンドウを 1 度だけログに残す（照合の改善用）。</summary>
    private void LogUnmatched(ApplicationWindow w)
    {
        var key = GroupKey(w);
        if (!_loggedUnmatched.Add(key) || !PinnedApps().Any()) return;
        Log.Info($"unmatched window: exe={ExePath(w)} aumid={Aumid(w)} title={w.Title}");
        if (_loggedUnmatched.Count == 1)
            foreach (var p in PinnedApps())
                Log.Info($"  pinned: name={p.Name} id={p.Id} aumid={p.Aumid} exe={p.ExePath} args={p.LaunchArgs}");
    }

    /// <summary>ピン留めアプリ（Windows と共有なら Windows のタスクバーと同じ内容・並び順）。</summary>
    private IEnumerable<AppEntry> PinnedApps() => PinsFromWindows
        ? _windowsPins.Apps.Concat(_settings.PinnedTaskbarExtra.Select(ResolvePinned).Where(a => a != null).Cast<AppEntry>())
        : _settings.PinnedTaskbar.Select(ResolvePinned).Where(a => a != null).Cast<AppEntry>();

    /// <summary>ModernShell 側で変更できるピン留めの一覧（Windows と共有中は「追加分」）。</summary>
    private List<string> EditablePins => PinsFromWindows ? _settings.PinnedTaskbarExtra : _settings.PinnedTaskbar;
    private bool IsWindowsPin(AppEntry app) => PinsFromWindows && _windowsPins.Apps.Any(a => a.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>ピン留め ID → アプリ。Windows から取り込んだショートカット (.lnk のパス) にも対応。</summary>
    private AppEntry? ResolvePinned(string id)
    {
        if (_catalog.Find(id) is { } app) return app;
        if (System.IO.Path.IsPathRooted(id) && System.IO.File.Exists(id))
            return id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? ExeEntry(id, null)
                : new AppEntry { Id = id, Name = System.IO.Path.GetFileNameWithoutExtension(id), ExePath = WindowsPins.ShortcutTarget(id) };
        return null;
    }

    /// <summary>実行ファイルからアプリ項目を作る（表示名はファイルの説明、なければファイル名）。</summary>
    private static AppEntry ExeEntry(string exe, System.Windows.Media.ImageSource? _)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(exe);
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) name = info.FileDescription.Trim();
            else if (!string.IsNullOrWhiteSpace(info.ProductName)) name = info.ProductName.Trim();
        }
        catch { }
        return new AppEntry { Id = exe, Name = name, ExePath = exe };
    }

    /// <summary>
    /// ピン留めボタンの並べ替えを確定する（ModernShell で管理しているときのみ。Windows のピン留めは書き換えられない）。
    /// 見えているピン留めの並びを変え、解決できなかった ID の位置はそのまま保つ。
    /// </summary>
    private void CommitPinnedOrder(List<string> visibleIds, int from, int to)
    {
        var moved = visibleIds[from];
        visibleIds.RemoveAt(from);
        visibleIds.Insert(to, moved);
        var list = _settings.PinnedTaskbar;
        int v = 0;
        for (int i = 0; i < list.Count; i++)
            if (visibleIds.Contains(list[i], StringComparer.OrdinalIgnoreCase)) list[i] = visibleIds[v++];
        _settings.Save();
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
        var exe = ExePath(w);
        return (Aumid(w) is { } id ? _catalog.Find(id) : null)
               ?? (exe != null ? _catalog.FindByExe(exe) : null)
               ?? (exe != null ? _catalog.FindByExeName(System.IO.Path.GetFileName(exe)) : null)
               // アプリ一覧に無いアプリでも、実行ファイルから項目を作ってピン留めできるようにする
               ?? (exe != null && !exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) ? ExeEntry(exe, w.Icon) : null);
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

        // ジャンプリスト（Windows のタスクバーの右クリックと同じ「最近使ったもの」「タスク」など）
        var w0 = g.Windows.FirstOrDefault();
        var aumid = (w0 != null ? Aumid(w0) : null) ?? app?.Aumid ?? (app != null && !app.IsFile && !app.Id.Contains('\\') ? app.Id : null);
        var exe = (w0 != null ? ExePath(w0) : null) ?? app?.ExePath;
        // パッケージ版アプリ（Terminal など）のタスクは "ms-appx:///…" でパッケージ内の画像を指すので、インストール先を求めておく
        var packageRoot = PackageInfo.RootFromExe(exe) ?? (aumid != null && aumid.Contains('!') ? PackageInfo.InstallPath(aumid) : null);
        try
        {
            foreach (var cat in JumpList.Read(aumid, exe))
            {
                items.Add(MenuItemSpec.Header(cat.Title));
                foreach (var it in cat.Items)
                {
                    if (it.IsSeparator) { items.Add(MenuItemSpec.Separator); continue; }
                    var path = it.Path;
                    bool isTask = path.StartsWith(System.IO.Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
                                  || path.Contains(@"\ModernShell\jumplist\", StringComparison.OrdinalIgnoreCase);
                    items.Add(new MenuItemSpec("", Truncate(it.Title, 48), () => OpenPath(path))
                    {
                        // アイコン: ショートカットに書かれたアイコン → シェルのアイコン取得 → アプリのアイコン の順に試す
                        // （シェルのアイコン取得は解決できないと null ではなく汎用アイコンを返すので後回し）
                        Icon = JumpIcon(it.IconFile, it.IconIndex, packageRoot)
                               ?? (it.IconFile == null && it.Target != null && !it.Target.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)
                                   ? ModernShell.Core.IconHelper.Get(path, 32) : null)
                               ?? (it.Target == null ? ModernShell.Core.IconHelper.Get(path, 32) : null)
                               ?? w0?.Icon ?? app?.Icon(32),
                        SubMenu = () => JumpItemMenu(path, isTask),
                    });
                }
                items.Add(MenuItemSpec.Separator);
            }
        }
        catch (Exception ex) { Log.Error("jumplist menu", ex); }

        if (app != null)
        {
            items.Add(new MenuItemSpec("", app.Name, () => app.Launch()) { Icon = w0?.Icon ?? app.Icon(32), SubMenu = () => AppItemMenu(app) });
            if (IsWindowsPin(app))
            {
                // Windows のピン留めはアプリから変更できない（Windows が API を提供していない）
                items.Add(new MenuItemSpec("\uE946", "Windows のタスクバーのピン留め（Windows 側で変更）", () => OpenSettingsRequested?.Invoke()));
            }
            else
            {
                var pins = EditablePins;
                bool pinned = pins.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
                items.Add(pinned
                    ? new MenuItemSpec("\uE77A", "タスクバーからピン留めを外す", () => { pins.RemoveAll(x => x.Equals(app.Id, StringComparison.OrdinalIgnoreCase)); _settings.Save(); })
                    : new MenuItemSpec("\uE718", "タスクバーにピン留めする", () => { pins.Add(app.Id); _settings.Save(); }));
            }
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

    /// <summary>メニュー内のアプリ名を右クリックしたとき（Windows と同じ: 開く / 管理者として実行 / スタート・タスクバーのピン留め）。</summary>
    private IEnumerable<MenuItemSpec> AppItemMenu(AppEntry app)
    {
        // Windows 標準の右クリックメニューをそのまま使う（取れなかったときだけ自前の項目）
        // Windows のタスクバーと同じく、ショートカットがあればその .lnk のメニューを使う（「プロパティ」も出る）
        var target = app.ShortcutPath != null && System.IO.File.Exists(app.ShortcutPath) ? app.ShortcutPath : app.ShellPath;
        var shell = ShellContextMenu.TaskbarStyle(target, OwnerHandle);
        if (shell.Count == 0) return ManualAppMenu(app);
        // Windows 側のメニューに無い（または外部アプリから動かせない）タスクバーのピン留めは ModernShell の項目で補う
        if (!IsWindowsPin(app) && !shell.Any(m => m.Text.Contains("タスク") && m.Text.Contains("ピン留め")))
        {
            var pins = EditablePins;
            bool onBar = pins.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
            int at = shell.FindIndex(m => m.IsSeparator);
            var pinItem = onBar
                ? new MenuItemSpec("\uE77A", "タスクバーからピン留めを外す", () => { pins.RemoveAll(x => x.Equals(app.Id, StringComparison.OrdinalIgnoreCase)); _settings.Save(); })
                : new MenuItemSpec("\uE718", "タスクバーにピン留めする", () => { pins.Add(app.Id); _settings.Save(); });
            shell.Insert(at < 0 ? shell.Count : at, pinItem);
        }
        return shell;
    }

    private IntPtr OwnerHandle => _surfaces.FirstOrDefault()?.Handle ?? IntPtr.Zero;

    private IEnumerable<MenuItemSpec> ManualAppMenu(AppEntry app)
    {
        yield return new MenuItemSpec("\uE8A7", "開く", () => app.Launch());
        if (app.ExePath != null) yield return new MenuItemSpec("\uE7EF", "管理者として実行", () => app.Launch(asAdmin: true));
        bool onStart = _settings.PinnedStart.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
        yield return onStart
            ? new MenuItemSpec("\uE77A", "スタートからピン留めを外す", () => { _settings.PinnedStart.RemoveAll(x => x.Equals(app.Id, StringComparison.OrdinalIgnoreCase)); _settings.Save(); })
            : new MenuItemSpec("\uE718", "スタートにピン留めする", () => { _settings.PinnedStart.Add(app.Id); _settings.Save(); });
        if (!IsWindowsPin(app))
        {
            var pins = EditablePins;
            bool onBar = pins.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
            yield return onBar
                ? new MenuItemSpec("\uE77A", "タスクバーからピン留めを外す", () => { pins.RemoveAll(x => x.Equals(app.Id, StringComparison.OrdinalIgnoreCase)); _settings.Save(); })
                : new MenuItemSpec("\uE718", "タスクバーにピン留めする", () => { pins.Add(app.Id); _settings.Save(); });
        }
        if (app.ExePath != null) yield return new MenuItemSpec("\uE838", "ファイルの場所を開く", app.OpenFileLocation);
    }

    /// <summary>ジャンプリストの項目を右クリックしたとき。</summary>
    private IEnumerable<MenuItemSpec> JumpItemMenu(string path, bool isTask)
    {
        // 最近使ったファイルは Windows 標準の右クリックメニュー（エクスプローラーと同じ）
        if (!isTask && System.IO.File.Exists(path))
        {
            var shell = ShellContextMenu.For(path, OwnerHandle);
            if (shell.Count > 0) { foreach (var m in shell) yield return m; yield break; }
        }
        yield return new MenuItemSpec("\uE8A7", "開く", () => OpenPath(path));
        if (!isTask && System.IO.File.Exists(path))
        {
            yield return new MenuItemSpec("\uE838", "ファイルの場所を開く", () => OpenPath("explorer.exe", $"/select,\"{path}\""));
            yield return new MenuItemSpec("\uE8C8", "パスをコピー", () => { try { Clipboard.SetText(path); } catch { } });
        }
    }

    /// <summary>ジャンプリスト項目のアイコン指定（DLL/EXE の番号、画像ファイル、ms-appx:/// のパッケージ内画像）を画像にする。</summary>
    private static ImageSource? JumpIcon(string? file, int index, string? packageRoot)
    {
        if (string.IsNullOrEmpty(file)) return null;
        if (file.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase))
            return PackageInfo.ResolveAsset(file, packageRoot) is { } asset ? PackageInfo.LoadImage(asset) : null;
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
            return System.IO.File.Exists(file) ? PackageInfo.LoadImage(file) : null;
        return ModernShell.Core.IconHelper.FromFile(file, index);
    }

    private static void OpenPath(string file, string args)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open", ex); }
    }

    private static void OpenPath(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open jump item", ex); }
    }

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
        _windowsPins.Changed -= OnPinsChanged;
        _windowsPins.Dispose();
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
