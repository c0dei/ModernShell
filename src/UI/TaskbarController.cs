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
public sealed partial class TaskbarController : IDisposable
{
    private readonly Settings _settings;
    private readonly AppCatalog _catalog;
    private readonly ShellHost _shell;
    private readonly Dispatcher _dispatcher;
    private readonly AppBarReservation _reservation = new();
    private readonly List<BarSurface> _surfaces = new();
    private readonly DispatcherTimer _clock, _status, _appsDebounce, _appsSoftDebounce, _trayDebounce;
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
    /// <summary>タスクバーが実際に画面に描かれたとき（純正タスクバーを隠すのはこのあと）。</summary>
    public event Action? FirstDrawn;
    public event Action? SearchClicked;
    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;

    public TaskbarEdge Edge => _settings.TaskbarEdge;
    public bool IsTop => Edge == TaskbarEdge.Top;
    /// <summary>左右の端に置く縦向きのタスクバーか。</summary>
    public bool Vertical => Edge is TaskbarEdge.Left or TaskbarEdge.Right;
    /// <summary>ボタンなどを並べる向き（横のバーなら横、縦のバーなら縦）。</summary>
    private Orientation Flow => Vertical ? Orientation.Vertical : Orientation.Horizontal;
    /// <summary>ボタンどうしのすき間（並べる向きに合わせる）。</summary>
    private Thickness Gap => Vertical ? new Thickness(0, 2, 0, 2) : new Thickness(2, 0, 2, 0);
    /// <summary>ボタンの大きさ。縦のバーでは、バーの太さ（幅）に合わせて縦横を入れ替える。</summary>
    private Size TileSize => Vertical ? new Size(TileH, TileW) : new Size(TileW, TileH);

    /// <summary>
    /// 余白（画面を基準にした左右 X・上下 Y）を、バーの向きに合わせて分ける。
    /// across = 画面の端からバーまでの距離、along = バーの両端（端に沿った方向の始まりと終わり）の余白。
    /// </summary>
    private (double across, double along) Margins =>
        Floating
            ? (Vertical ? (_settings.IslandMarginX, _settings.IslandMarginY) : (_settings.IslandMarginY, _settings.IslandMarginX))
            : (Vertical ? (_settings.BandMarginX, _settings.BandMarginY) : (_settings.BandMarginY, _settings.BandMarginX));
    private bool Floating => _settings.TaskbarStyle == TaskbarStyle.Floating;
    private bool StartAtLeftEdge => _settings.StartButtonPosition == StartButtonPosition.LeftEdge;
    public Win32.RECT BarRect => _reservation.Rect;
    /// <summary>スタートメニューを中央に出すか（スタートボタンが中央のかたまりにあるとき）。</summary>
    public bool StartCentered => !StartAtLeftEdge && _settings.TaskbarAlignment == TaskbarAlignment.Center;

    private readonly WindowsPins _windowsPins;
    private readonly ThumbnailPreview _preview;
    private readonly DesktopsFlyout _desktops;
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
        _preview = new ThumbnailPreview(settings, dispatcher);
        _desktops = new DesktopsFlyout(settings, dispatcher);
        RegisterToolTipPlacement();
        _windowsPins.Changed += OnPinsChanged;

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => UpdateClock(), dispatcher);
        _status = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => UpdateStatus(), dispatcher);
        _appsDebounce = Debouncer(RebuildApps);
        // ウィンドウのタイトル・状態が変わっただけのときの作り直し。カーソルがアプリのボタンの上にある間は後回しにする
        // （作り直すとカーソルの下のボタンが新しくなり、ホバーの拡大がやり直しになって拡大・縮小を繰り返して見えるため）
        _appsSoftDebounce = Debouncer(() =>
        {
            if (_apps?.IsMouseOver == true) { Kick(_appsSoftDebounce!); return; }
            RebuildApps();
        });
        _trayDebounce = Debouncer(RebuildTray);

        if (_shell.Tasks?.GroupedWindows is INotifyCollectionChanged tasks) tasks.CollectionChanged += OnTasksChanged;
        if (_shell.Tray?.PinnedIcons is INotifyCollectionChanged pinned) pinned.CollectionChanged += OnTrayChanged;
        if (_shell.Tray?.UnpinnedIcons is INotifyCollectionChanged unpinned) unpinned.CollectionChanged += OnTrayChanged;

        _catalog.Changed += OnCatalogChanged;
        _settings.Changed += OnSettingsChanged;
        Theme.Changed += OnThemeChanged;
        _reservation.PositionChanged += Arrange;
        _reservation.FullscreenChanged += _ => Arrange();
        _reservation.ForegroundChanged += ImeMayHaveChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (ReferenceEquals(x, ancestor)) return true;
        return false;
    }

    private static bool _toolTipHooked;

    /// <summary>
    /// タスクバーのボタンのツールチップは、カーソルの位置ではなくボタンの上（タスクバーが上なら下）に出す。
    /// 画面の一番下ではカーソルの下に出る場所が無く、ツールチップがカーソルに重なって「ボタンから出た」扱いになり、
    /// アイコンの拡大・縮小とツールチップの表示・非表示が繰り返されてしまうため。
    /// </summary>
    private static void RegisterToolTipPlacement()
    {
        if (_toolTipHooked) return;
        _toolTipHooked = true;
        EventManager.RegisterClassHandler(typeof(Tile), FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler((s, _) =>
        {
            if (s is not Tile tile || Window.GetWindow(tile) is not BarSurface) return;
            // ボタンの画面の内側に、少し離して出す（カーソルに重ならないように）
            var edge = Settings.Current?.TaskbarEdge ?? TaskbarEdge.Bottom;
            ToolTipService.SetPlacement(tile, edge switch { TaskbarEdge.Top => PlacementMode.Bottom, TaskbarEdge.Left => PlacementMode.Right, TaskbarEdge.Right => PlacementMode.Left, _ => PlacementMode.Top });
            ToolTipService.SetVerticalOffset(tile, edge switch { TaskbarEdge.Top => 6, TaskbarEdge.Bottom => -6, _ => 0 });
            ToolTipService.SetHorizontalOffset(tile, edge switch { TaskbarEdge.Left => 6, TaskbarEdge.Right => -6, _ => 0 });
        }));
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
            _dispatcher.BeginInvoke(() => Kick(_appsSoftDebounce));
        else if (s is ManagedShell.WindowsTray.NotifyIcon && e.PropertyName is "Icon" or "Title" or "IsHidden")
            _dispatcher.BeginInvoke(() => Kick(_trayDebounce));
    }
    private void Watch(INotifyPropertyChanged o)
    {
        if (!_watched.Add(o)) return;
        o.PropertyChanged += OnItemChanged;
        if (o is ApplicationWindow w) w.GetButtonRect += OnGetButtonRect(w);
    }

    /// <summary>ウィンドウ → 今それを表しているタスクバーのボタン（最小化・元に戻すアニメーションの行き先に使う）。</summary>
    private readonly Dictionary<IntPtr, FrameworkElement> _buttonFor = new();
    private readonly Dictionary<ApplicationWindow, ApplicationWindow.GetButtonRectEventHandler> _rectHandlers = new();

    /// <summary>
    /// Windows はウィンドウを最小化・元に戻すとき、「タスクバーのボタンの位置」へ縮む／から広がるアニメーションをする。
    /// その位置をシェル（ManagedShell の HSHELL_GETMINRECT）経由で問い合わせてくるので、ModernShell のボタンの位置を答える。
    /// 答えないと、隠れている純正タスクバーのボタンの位置が使われ、見当違いの場所へ縮んで見える。
    /// </summary>
    private ApplicationWindow.GetButtonRectEventHandler OnGetButtonRect(ApplicationWindow w)
    {
        ApplicationWindow.GetButtonRectEventHandler h = (ref NativeMethods.ShortRect rect) =>
        {
            if (_disposed || !_buttonFor.TryGetValue(w.Handle, out var button)) return;
            Rect? Measure()
            {
                if (!button.IsVisible || PresentationSource.FromVisual(button) == null) return null;
                var tl = button.PointToScreen(new Point(0, 0));
                var br = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
                return new Rect(tl, br);
            }
            var r = button.Dispatcher.CheckAccess() ? Measure() : button.Dispatcher.Invoke(Measure);
            if (r is not { } b || b.Width <= 0) return;
            rect = new NativeMethods.ShortRect((short)b.Left, (short)b.Top, (short)b.Right, (short)b.Bottom);
        };
        _rectHandlers[w] = h;
        return h;
    }

    private void Unwatch(INotifyPropertyChanged o)
    {
        o.PropertyChanged -= OnItemChanged;
        if (o is ApplicationWindow w && _rectHandlers.Remove(w, out var h)) w.GetButtonRect -= h;
    }

    /// <summary>
    /// 閉じたウィンドウ・消えたトレイアイコンの購読を外す。外さないと、閉じたウィンドウ（アイコンの画像なども）が
    /// 購読を通して残り続け、常駐しているうちに少しずつメモリが増えていく。
    /// </summary>
    private void PruneWatched()
    {
        var alive = new HashSet<INotifyPropertyChanged>();
        if (_shell.Tasks?.GroupedWindows is { } windows) foreach (var w in windows.Cast<INotifyPropertyChanged>()) alive.Add(w);
        if (_shell.Tray?.PinnedIcons is { } p) foreach (var i in p.Cast<INotifyPropertyChanged>()) alive.Add(i);
        if (_shell.Tray?.UnpinnedIcons is { } u) foreach (var i in u.Cast<INotifyPropertyChanged>()) alive.Add(i);
        _watched.RemoveWhere(o =>
        {
            if (alive.Contains(o)) return false;
            Unwatch(o);
            return true;
        });
    }

    private void OnPinsChanged() => _dispatcher.BeginInvoke(() => { if (PinsFromWindows) Kick(_appsDebounce); });
    private void OnCatalogChanged() => _dispatcher.BeginInvoke(() => Kick(_appsDebounce));
    /// <summary>
    /// 設定が保存されるたびに全部作り直すと、スタートのピン留め変更などでもタスクバーがちらつく。
    /// 見た目に関わる設定が変わったときだけ作り直し、それ以外はボタンの並びだけ更新する。
    /// </summary>
    private void OnSettingsChanged() => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        if (LayoutKey() != _layoutKey) Rebuild();
        else { Kick(_appsDebounce); Kick(_trayDebounce); }
    });
    private void OnThemeChanged() => _dispatcher.BeginInvoke(() => { if (!_disposed) Rebuild(); });

    private string _layoutKey = "";
    private string LayoutKey() => string.Join("|", _settings.UseCustomTaskbar, _settings.TaskbarStyle, _settings.StartButtonPosition,
        _settings.TaskbarAlignment, _settings.TaskbarEdge, _settings.TaskbarHeight, _settings.TaskbarIconSize, _settings.IslandMarginX, _settings.IslandMarginY,
        _settings.ShowSearchButton, _settings.ShowTaskViewButton, _settings.ShowSeconds,
        _settings.MagnifyOnHover, _settings.ShowTaskbarLabels, _settings.GroupTaskbarButtons, _settings.TaskbarPinSource, _settings.CornerRadius,
        _settings.TaskbarOpacity, _settings.TaskbarBackground, _settings.BandMarginX, _settings.BandMarginY, _settings.Language, _settings.ShowImeIndicator);
    private void OnNetworkChanged(object? s, NetworkAvailabilityEventArgs e)
    {
        _online = e.IsAvailable;
        _dispatcher.BeginInvoke(UpdateStatus);
    }

    // ------------------------------------------------------------------
    // 組み立て

    public void Rebuild()
    {
        StallDetector.Phase = "taskbar: rebuild";
        _layoutKey = LayoutKey();
        foreach (var s in _surfaces) s.Close();
        _surfaces.Clear();

        var t = Theme.Current;
        double h = _settings.TaskbarHeight;
        var startGroup = StartGroup(t);
        _apps = new StackPanel { Orientation = Flow, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var tray = TrayGroup(t);
        _trayHost = tray;

        // 並べる向きの「始まり」「中央」「終わり」。横のバーなら 左・中央・右、縦のバーなら 上・中央・下
        var (across, along) = Margins;
        if (!Floating)
        {
            // ---- 帯型: 1 枚のウィンドウを 3 つの領域（始まり・中央・終わり）に分ける ----
            var grid = new Grid();
            foreach (var len in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            {
                if (Vertical) grid.RowDefinitions.Add(new RowDefinition { Height = len });
                else grid.ColumnDefinitions.Add(new ColumnDefinition { Width = len });
            }
            // バーと作業領域のあいだの区切りの線（画面の中央側の辺）
            var line = Vertical
                ? new Border { Width = 1, Background = t.Stroke, HorizontalAlignment = Edge == TaskbarEdge.Left ? HorizontalAlignment.Right : HorizontalAlignment.Left }
                : new Border { Height = 1, Background = t.Stroke, VerticalAlignment = IsTop ? VerticalAlignment.Bottom : VerticalAlignment.Top };
            if (Vertical) Grid.SetRowSpan(line, 3); else Grid.SetColumnSpan(line, 3);
            grid.Children.Add(line);

            bool center = _settings.TaskbarAlignment == TaskbarAlignment.Center;
            if (StartAtLeftEdge)
            {
                if (center)
                {
                    Place(grid, startGroup, 0, Alignment.Start);
                    Place(grid, _apps, 1, Alignment.Center);
                }
                else Place(grid, Stack(startGroup, _apps), 0, Alignment.Start);
            }
            else Place(grid, Stack(startGroup, _apps), center ? 1 : 0, center ? Alignment.Center : Alignment.Start);

            tray.Children.Add(ShowDesktopSliver());
            Place(grid, tray, 2, Alignment.End);
            AddSurface(BarSurface.Anchor.Fill, h, grid);
            _reservation.Reserve(Edge, h + across * 2);
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
            else AddSurface(appsAnchor, h, Island(Stack(startGroup, _apps)));
            AddSurface(BarSurface.Anchor.Right, h, Island(tray));
            _reservation.Reserve(Edge, h + across * 2);
        }

        // どれかの処理が失敗しても、バーのウィンドウは必ず表示する（途中で止まると、領域だけ確保されて何も見えなくなる）
        Safe("apps", RebuildApps);
        Safe("tray", RebuildTray);
        StallDetector.Phase = "taskbar: clock";
        Safe("clock", UpdateClock);
        Safe("status", UpdateStatus);
        StallDetector.Phase = "taskbar: show windows";
        foreach (var s in _surfaces) s.Show();
        Arrange();
        Log.Info($"taskbar built: edge={Edge} style={(Floating ? "island" : "band")} surfaces={_surfaces.Count} reserved={_reservation.Rect.Left},{_reservation.Rect.Top},{_reservation.Rect.Right},{_reservation.Rect.Bottom}");
        // ここから先は WPF が画面に描く処理（このあと固まったら、描画の準備で待たされている）
        StallDetector.Phase = "taskbar: waiting for WPF to draw";
    }

    private static void Safe(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Error($"taskbar rebuild ({what})", ex); }
    }

    /// <summary>並べる向きでの位置（横のバーなら 左・中央・右、縦のバーなら 上・中央・下）。</summary>
    private enum Alignment { Start, Center, End }

    private void Place(Grid g, FrameworkElement e, int slot, Alignment a)
    {
        if (Vertical)
        {
            e.VerticalAlignment = a switch { Alignment.Start => VerticalAlignment.Top, Alignment.End => VerticalAlignment.Bottom, _ => VerticalAlignment.Center };
            e.HorizontalAlignment = HorizontalAlignment.Center;
            e.Margin = new Thickness(0, 8, 0, 8);
            Grid.SetRow(e, slot);
        }
        else
        {
            e.HorizontalAlignment = a switch { Alignment.Start => HorizontalAlignment.Left, Alignment.End => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
            e.VerticalAlignment = VerticalAlignment.Center;
            e.Margin = new Thickness(8, 0, 8, 0);
            Grid.SetColumn(e, slot);
        }
        g.Children.Add(e);
    }

    private StackPanel Stack(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Flow, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    private FrameworkElement Island(FrameworkElement content)
    {
        // 島の両端に少し余白を取り、右クリック（タスク マネージャー等）できる場所を確保する
        if (Vertical)
        {
            content.HorizontalAlignment = HorizontalAlignment.Center;
            content.Margin = new Thickness(0, 8, 0, 8);
        }
        else
        {
            content.VerticalAlignment = VerticalAlignment.Center;
            content.Margin = new Thickness(8, 0, 8, 0);
        }
        return content;
    }

    private void AddSurface(BarSurface.Anchor anchor, double h, FrameworkElement content)
    {
        // 空いている場所でも右クリックを受け取れるよう、透明の背景を付ける
        if (content is Panel panel && panel.Background == null) panel.Background = Brushes.Transparent;
        var s = new BarSurface(anchor, h, _settings, Vertical) { Foreground = Theme.Current.Text };
        s.SetBody(content);
        // ボタン以外の場所を右クリック → Windows のタスクバーと同じく「タスク マネージャー」「タスクバーの設定」
        s.MouseRightButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            Flyout.Show(content, new[]
            {
                new MenuItemSpec("\uE9D9", L.T("タスク マネージャー"), SystemActions.OpenTaskManager),
                MenuItemSpec.Separator,
                new MenuItemSpec("\uE713", L.T("タスクバーの設定"), () => OpenSettingsRequested?.Invoke()),
            }, PlacementMode.MousePoint);
        };
        s.ContentSizeChanged += Arrange;
        s.ContentRendered += (_, _) => FirstDrawn?.Invoke();
        // 「^」の一覧が開いているときに、タスクバーの他の場所を押したら閉じる（「^」自体は開閉の切り替えに任せる）
        s.PreviewMouseDown += (_, e) =>
        {
            if (_overflowPopup?.IsOpen != true) return;
            if (e.OriginalSource is DependencyObject d && _overflowButton != null && IsInside(d, _overflowButton)) return;
            _overflowPopup.IsOpen = false;
        };
        s.Loaded += (_, _) => Arrange();
        _surfaces.Add(s);
    }

    /// <summary>確保領域の中に各ウィンドウを配置する（すべて物理ピクセル）。</summary>
    private void Arrange()
    {
        StallDetector.Phase = "taskbar: arrange";
        if (_disposed) return;
        var r = _reservation.Rect;
        bool bottomMost = _reservation.IsFullscreenAppActive; // 全画面アプリ中は最前面をやめる
        var (acrossDip, alongDip) = Margins;
        foreach (var s in _surfaces)
        {
            if (!s.IsLoaded) continue;
            int across = (int)Math.Round(acrossDip * s.Scale), along = (int)Math.Round(alongDip * s.Scale);
            int thick = (int)Math.Round(_settings.TaskbarHeight * s.Scale); // バーの太さ（横のバーなら高さ、縦のバーなら幅）
            if (s.Placement == BarSurface.Anchor.Fill)
            {
                // 帯型: 余白 0 なら画面端に密着（Windows はこの場合角を丸めない）、余白があれば離して角丸にする
                if (Vertical)
                {
                    int x = Edge == TaskbarEdge.Left ? r.Left + across : r.Right - across - thick;
                    if (across <= 0 && along <= 0) s.MoveTo(r.Left, r.Top, r.Width, r.Height, bottomMost);
                    else s.MoveTo(x, r.Top + along, thick, r.Height - along * 2, bottomMost);
                }
                else if (across <= 0 && along <= 0) s.MoveTo(r.Left, r.Top, r.Width, r.Height, bottomMost);
                else s.MoveTo(r.Left + along, IsTop ? r.Top + across : r.Bottom - across - thick, r.Width - along * 2, thick, bottomMost);
                continue;
            }
            var (w, h) = s.PixelSize;
            if (Vertical)
            {
                int x = Edge == TaskbarEdge.Left ? r.Left + across : r.Right - across - w;
                int y = s.Placement switch
                {
                    BarSurface.Anchor.Left => r.Top + along,
                    BarSurface.Anchor.Right => r.Bottom - along - h,
                    _ => r.Top + (r.Height - h) / 2,
                };
                s.MoveTo(x, y, bottomMost: bottomMost);
            }
            else
            {
                int y = IsTop ? r.Top + across : r.Bottom - across - h;
                int x = s.Placement switch
                {
                    BarSurface.Anchor.Left => r.Left + along,
                    BarSurface.Anchor.Right => r.Right - along - w,
                    _ => r.Left + (r.Width - w) / 2,
                };
                s.MoveTo(x, y, bottomMost: bottomMost);
            }
        }
        UpdateTrayHostData();
        LogArrange();
    }

    private string _lastArrangeLog = "";

    /// <summary>各ウィンドウの実際の位置と大きさ（画面の物理ピクセル）。変わったときだけ記録する（見えないときの調査用）。</summary>
    private void LogArrange()
    {
        try
        {
            var sb = new System.Text.StringBuilder($"taskbar arrange: edge={Edge} fullscreen={_reservation.IsFullscreenAppActive}");
            foreach (var s in _surfaces)
            {
                Win32.GetWindowRect(s.Handle, out var wr);
                sb.Append($" | {s.Placement} loaded={s.IsLoaded} visible={s.IsVisible} rect={wr.Left},{wr.Top},{wr.Right},{wr.Bottom}");
            }
            var text = sb.ToString();
            if (text == _lastArrangeLog) return;
            _lastArrangeLog = text;
            Log.Info(text);
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // スタート / 検索 / タスクビュー

    private StackPanel StartGroup(Theme t)
    {
        var p = new StackPanel { Orientation = Flow, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var start = new Tile { Width = TileSize.Width, Height = TileSize.Height, Margin = Gap, Child = UiKit.StartLogo(Math.Round(IconSz * 0.75), t.AccentBrush), ToolTip = L.T("スタート") };
        start.Click += x => { TakeForeground(x); StartClicked?.Invoke(); };
        start.RightClick += x => Flyout.Show(x, new[]
        {
            new MenuItemSpec("", L.T("Win+X メニュー"), SystemActions.PowerUserMenu),
            new MenuItemSpec("", L.T("タスク マネージャー"), SystemActions.OpenTaskManager),
            new MenuItemSpec("", L.T("エクスプローラー"), SystemActions.OpenFileExplorer),
            new MenuItemSpec("", L.T("Windows の設定"), SystemActions.OpenSettings),
            MenuItemSpec.Separator,
            new MenuItemSpec("", L.T("ModernShell の設定"), () => OpenSettingsRequested?.Invoke()),
            new MenuItemSpec("", L.T("ModernShell を終了（純正に戻す）"), () => ExitRequested?.Invoke()),
        }, FlyoutPlacement);
        p.Children.Add(start);
        if (_settings.ShowSearchButton) p.Children.Add(SmallButton("", L.T("検索"), () => SearchClicked?.Invoke()));
        if (_settings.ShowTaskViewButton) p.Children.Add(SmallButton("", L.T("タスク ビュー"), SystemActions.TaskView, HookDesktops));
        return p;
    }

    /// <summary>メニューやポップアップをボタンのどちら側に出すか（バーのある端の反対側。画面の内側）。</summary>
    private PlacementMode FlyoutPlacement => Edge switch
    {
        TaskbarEdge.Top => PlacementMode.Bottom,
        TaskbarEdge.Left => PlacementMode.Right,
        TaskbarEdge.Right => PlacementMode.Left,
        _ => PlacementMode.Top,
    };

    private Tile SmallButton(string glyph, string tip, Action onClick, Action<Tile>? configure = null)
    {
        var tile = new Tile { Width = TileSize.Width, Height = TileSize.Height, Margin = Gap, Child = UiKit.Glyph(glyph, GlyphSz), ToolTip = tip };
        // 検索・タスク ビューはキー操作を送って開くので、管理者として動くアプリが前面でも届くよう先に前面になる
        tile.Click += x => { TakeForeground(x); onClick(); };
        configure?.Invoke(tile);
        return tile;
    }

    /// <summary>タスクビューボタン: カーソルを置くと仮想デスクトップの一覧（Windows 11 と同じ）。</summary>
    private void HookDesktops(Tile tv)
    {
        tv.ToolTip = null; // 一覧と重なるのでツールチップは出さない
        tv.MouseEnter += (_, _) => _desktops.Hover(tv, Edge);
        tv.MouseLeave += (_, _) => _desktops.Leave();
        tv.PreviewMouseDown += (_, _) => _desktops.Cancel();
    }

    // ------------------------------------------------------------------

    /// <summary>explorer 再起動時など、作業領域の予約をやり直す。</summary>
    public void Reapply() => _reservation.Reserve(Edge, _settings.TaskbarHeight + Margins.across * 2);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clock.Stop();
        _imeTimer?.Stop();
        _status.Stop();
        _appsDebounce.Stop();
        _appsSoftDebounce.Stop();
        _trayDebounce.Stop();
        if (_shell.Tasks?.GroupedWindows is INotifyCollectionChanged tasks) tasks.CollectionChanged -= OnTasksChanged;
        if (_shell.Tray?.PinnedIcons is INotifyCollectionChanged pinned) pinned.CollectionChanged -= OnTrayChanged;
        if (_shell.Tray?.UnpinnedIcons is INotifyCollectionChanged unpinned) unpinned.CollectionChanged -= OnTrayChanged;
        foreach (var o in _watched) Unwatch(o);
        _watched.Clear();
        _buttonFor.Clear();
        _windowsPins.Changed -= OnPinsChanged;
        _windowsPins.Dispose();
        _catalog.Changed -= OnCatalogChanged;
        _settings.Changed -= OnSettingsChanged;
        Theme.Changed -= OnThemeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        if (_overflowPopup != null) _overflowPopup.IsOpen = false;
        _preview.Cancel();
        _desktops.Cancel();
        foreach (var s in _surfaces) s.Close();
        _surfaces.Clear();
        _reservation.Dispose();
    }
}
