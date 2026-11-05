using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using ModernShell.Core;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>
/// 独自タスクバー。SHAppBarMessage で AppBar として登録し、作業領域を確保する。
/// 純正タスクバーは NativeTaskbar.Hide() で隠すだけで、explorer 自体には手を入れない。
/// </summary>
public sealed class TaskbarWindow : Window
{
    private readonly Settings _settings;
    private readonly AppCatalog _catalog;
    private readonly WindowTracker _tracker;
    private readonly uint _callbackMsg = Win32.RegisterWindowMessage("ModernShell.AppBar");
    private bool _registered;
    private bool _fullscreen;
    private bool _closed;

    private StackPanel _apps = null!;
    private TextBlock _time = null!, _date = null!;
    private readonly DispatcherTimer _clock;
    private readonly List<string> _order = new(); // ボタン順序を安定させるためのグループキー履歴

    public event Action? StartClicked;
    public event Action? SearchClicked;
    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public TaskbarWindow(Settings settings, AppCatalog catalog, WindowTracker tracker)
    {
        _settings = settings;
        _catalog = catalog;
        _tracker = tracker;

        Title = "ModernShell Taskbar";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        FontFamily = Theme.TextFont;
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(-1),
        });

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        long ex = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        // TOOLWINDOW: Alt+Tab に出さない / NOACTIVATE: クリックしても前面ウィンドウを奪わない（最小化トグル判定に必須）
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, (IntPtr)(ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE));
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        _tracker.Ignore(hwnd);

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => UpdateClock(), Dispatcher);
        Action apps = () => { if (!_closed) RebuildApps(); };
        Action settingsChanged = () => Dispatcher.BeginInvoke(() => { if (_closed) return; Rebuild(); RegisterAppBar(); });
        Action catalogChanged = () => Dispatcher.BeginInvoke(apps);
        Action themeChanged = () => Dispatcher.BeginInvoke(() => { if (!_closed) Rebuild(); });
        _tracker.WindowsChanged += apps;
        _tracker.ForegroundChanged += apps;
        _settings.Changed += settingsChanged;
        _catalog.Changed += catalogChanged;
        Theme.Changed += themeChanged;

        Loaded += (_, _) => { RegisterAppBar(); _clock.Start(); };
        Closed += (_, _) =>
        {
            _closed = true;
            _clock.Stop();
            _tracker.WindowsChanged -= apps;
            _tracker.ForegroundChanged -= apps;
            _settings.Changed -= settingsChanged;
            _catalog.Changed -= catalogChanged;
            Theme.Changed -= themeChanged;
            UnregisterAppBar();
        };
        Rebuild();
    }

    // ------------------------------------------------------------------
    // AppBar

    private double Scale => Win32.GetDpiForWindow(Handle) is var d and > 0 ? d / 96.0 : 1.0;

    public Win32.RECT BarRect { get; private set; }
    public bool IsTop => _settings.TaskbarEdge == TaskbarEdge.Top;

    public void RegisterAppBar()
    {
        if (_closed || Handle == IntPtr.Zero) return;
        var abd = Win32.APPBARDATA.Create(Handle);
        if (!_registered)
        {
            abd.uCallbackMessage = _callbackMsg;
            Win32.SHAppBarMessage(Win32.ABM_NEW, ref abd);
            _registered = true;
        }

        var mon = PrimaryMonitor();
        int h = (int)Math.Round(_settings.TaskbarHeight * Scale);
        abd.uEdge = IsTop ? Win32.ABE_TOP : Win32.ABE_BOTTOM;
        abd.rc = mon;
        if (IsTop) abd.rc.Bottom = mon.Top + h; else abd.rc.Top = mon.Bottom - h;

        Win32.SHAppBarMessage(Win32.ABM_QUERYPOS, ref abd);
        // QUERYPOS で調整された辺に合わせて高さを再設定
        if (IsTop) abd.rc.Bottom = abd.rc.Top + h; else abd.rc.Top = abd.rc.Bottom - h;
        Win32.SHAppBarMessage(Win32.ABM_SETPOS, ref abd);

        BarRect = abd.rc;
        Win32.SetWindowPos(Handle, _fullscreen ? IntPtr.Zero : Win32.HWND_TOPMOST, abd.rc.Left, abd.rc.Top, abd.rc.Width, abd.rc.Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }

    public void UnregisterAppBar()
    {
        if (!_registered) return;
        var abd = Win32.APPBARDATA.Create(Handle);
        Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref abd);
        _registered = false;
    }

    private static Win32.RECT PrimaryMonitor()
    {
        var mon = MonitorFromPoint(new Win32.POINT { X = 0, Y = 0 }, 1 /*MONITOR_DEFAULTTOPRIMARY*/);
        var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(mon, ref mi);
        return mi.rcMonitor;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Win32.POINT pt, uint flags);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _callbackMsg)
        {
            switch ((int)wParam)
            {
                case Win32.ABN_POSCHANGED:
                    RegisterAppBar();
                    break;
                case Win32.ABN_FULLSCREENAPP:
                    // 全画面アプリ（動画・ゲーム）中は最前面をやめて邪魔しない
                    _fullscreen = lParam != IntPtr.Zero;
                    Win32.SetWindowPos(Handle, _fullscreen ? new IntPtr(1) /*HWND_BOTTOM*/ : Win32.HWND_TOPMOST, 0, 0, 0, 0,
                        Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                    break;
            }
            handled = true;
        }
        else if (msg is (int)Win32.WM_DISPLAYCHANGE or 0x02E0 /*WM_DPICHANGED*/)
        {
            Dispatcher.BeginInvoke(RegisterAppBar, DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------
    // UI

    private void Rebuild()
    {
        var t = Theme.Current;
        Backdrop.Apply(this, _settings.Backdrop, t, alwaysInactive: true);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new Border
        {
            Height = 1, Background = t.Stroke,
            VerticalAlignment = IsTop ? VerticalAlignment.Bottom : VerticalAlignment.Top,
        });
        Grid.SetColumnSpan(root.Children[0], 3);

        _apps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var center = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        center.Children.Add(StartButton(t));
        center.Children.Add(SmallButton("", "検索", () => SearchClicked?.Invoke()));
        center.Children.Add(SmallButton("", "タスク ビュー", SystemActions.TaskView));
        center.Children.Add(_apps);

        if (_settings.TaskbarAlignment == TaskbarAlignment.Center) Grid.SetColumn(center, 1);
        else { Grid.SetColumn(center, 0); center.HorizontalAlignment = HorizontalAlignment.Left; }
        root.Children.Add(center);

        var tray = BuildTray(t);
        Grid.SetColumn(tray, 2);
        root.Children.Add(tray);

        Content = root;
        RebuildApps();
        UpdateClock();
    }

    private Tile StartButton(Theme t)
    {
        var tile = new Tile { Width = 44, Height = 40, Margin = new Thickness(2, 0, 2, 0), Child = UiKit.StartLogo(18, t.AccentBrush), ToolTip = "スタート" };
        tile.Click += _ => StartClicked?.Invoke();
        tile.RightClick += x => Flyout.Show(x, new[]
        {
            new MenuItemSpec("", "Win+X メニュー", SystemActions.PowerUserMenu),
            new MenuItemSpec("", "タスク マネージャー", SystemActions.OpenTaskManager),
            new MenuItemSpec("", "エクスプローラー", SystemActions.OpenFileExplorer),
            new MenuItemSpec("", "Windows の設定", SystemActions.OpenSettings),
            MenuItemSpec.Separator,
            new MenuItemSpec("", "ModernShell の設定", () => OpenSettingsRequested?.Invoke()),
            new MenuItemSpec("", "ModernShell を終了", () => ExitRequested?.Invoke()),
        }, IsTop ? PlacementMode.Bottom : PlacementMode.Top);
        return tile;
    }

    private static Tile SmallButton(string glyph, string tip, Action onClick)
    {
        var tile = new Tile { Width = 44, Height = 40, Margin = new Thickness(2, 0, 2, 0), Child = UiKit.Glyph(glyph, 17), ToolTip = tip };
        tile.Click += _ => onClick();
        return tile;
    }

    private FrameworkElement BuildTray(Theme t)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch };

        var quick = new Tile
        {
            Padding = new Thickness(10, 0, 10, 0), Height = 40, VerticalAlignment = VerticalAlignment.Center,
            Child = UiKit.Row(UiKit.Glyph("", 15), Spaced(UiKit.Glyph("", 15)), Spaced(UiKit.Glyph("", 15))),
            ToolTip = "クイック設定 (Win+A)",
        };
        quick.Click += _ => SystemActions.QuickSettings();
        p.Children.Add(quick);

        _time = UiKit.Label("", 12);
        _date = UiKit.Label("", 12);
        _time.HorizontalAlignment = _date.HorizontalAlignment = HorizontalAlignment.Right;
        var clockStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        clockStack.Children.Add(_time);
        clockStack.Children.Add(_date);
        var clock = new Tile { Padding = new Thickness(10, 0, 10, 0), Height = 40, Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Child = clockStack, ToolTip = "通知とカレンダー (Win+N)" };
        clock.Click += _ => SystemActions.NotificationCenter();
        p.Children.Add(clock);

        // 右端の「デスクトップの表示」
        var desk = new Tile { Width = 10, CornerRadius = new CornerRadius(0), ToolTip = "デスクトップの表示" };
        desk.Click += _ => SystemActions.ShowDesktop();
        p.Children.Add(desk);
        return p;
    }

    private static FrameworkElement Spaced(FrameworkElement e) { e.Margin = new Thickness(10, 0, 0, 0); return e; }

    private void UpdateClock()
    {
        if (_time == null) return;
        var now = DateTime.Now;
        _time.Text = now.ToString(_settings.ShowSeconds ? "H:mm:ss" : "H:mm");
        _date.Text = now.ToString("yyyy/MM/dd");
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

        // 1) ピン留め（順序固定）
        foreach (var id in _settings.PinnedTaskbar)
        {
            var app = _catalog.Find(id);
            if (app == null) continue;
            groups.Add(new Group { Key = "pin:" + app.Id, Pinned = app });
        }

        // 2) 実行中ウィンドウを割り当て
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

        // 3) ピン留め以外は初出順で安定させる
        foreach (var g in groups.Where(x => x.Pinned == null && !_order.Contains(x.Key))) _order.Add(g.Key);
        _order.RemoveAll(k => !k.StartsWith("pin:") && groups.All(g => g.Key != k));
        var ordered = groups.Where(g => g.Pinned != null)
            .Concat(groups.Where(g => g.Pinned == null).OrderBy(g => _order.IndexOf(g.Key)));

        _apps.Children.Clear();
        foreach (var g in ordered) _apps.Children.Add(AppButton(g));
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

        tile.Click += x =>
        {
            if (g.Windows.Count == 0) g.Pinned?.Launch();
            else if (g.Windows.Count == 1) _tracker.Toggle(g.Windows[0].Handle);
            else WindowList(x, g);
        };
        tile.MiddleClick += _ => (g.Pinned ?? _catalog.FindByExe(g.Windows.FirstOrDefault()?.ExePath ?? ""))?.Launch();
        tile.RightClick += x => Flyout.Show(x, GroupMenu(g), IsTop ? PlacementMode.Bottom : PlacementMode.Top);
        return tile;
    }

    private void WindowList(UIElement target, Group g) =>
        Flyout.Show(target, g.Windows.Select(w =>
            new MenuItemSpec("", Truncate(w.Title, 48), () => _tracker.Activate(w.Handle))),
            IsTop ? PlacementMode.Bottom : PlacementMode.Top);

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
}
