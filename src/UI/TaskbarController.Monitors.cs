using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ManagedShell.WindowsTasks;
using ModernShell.Core;
using ModernShell.Native;
using Forms = System.Windows.Forms;

namespace ModernShell.UI;

/// <summary>
/// 複数モニター: メイン以外のモニターにも（簡易の）タスクバーを出す。
/// 起動中のアプリのボタンはメインと同じようにすべて並べ、右端に時計を置く。
/// 設定「複数のモニターにタスクバーを表示」がオンで、モニターが 2 つ以上のときだけ動く。
/// </summary>
public sealed partial class TaskbarController
{
    private sealed class SecondaryBar
    {
        public required string Screen;
        public required AppBarReservation Reservation;
        public required BarSurface Surface;
        public required StackPanel Apps;
        public required TextBlock Time, Date;
        public MagnifyEngine? Magnify;
    }

    private readonly List<SecondaryBar> _secondaries = new();

    /// <summary>複数モニター表示が有効か（設定がオンで、モニターが 2 つ以上）。</summary>
    private bool MultiMonitorActive => _settings.MultiMonitorTaskbar && Forms.Screen.AllScreens.Length > 1;

    private static string ScreenNameOf(ApplicationWindow w)
    {
        try { return Forms.Screen.FromHandle(w.Handle).DeviceName; }
        catch { return ""; }
    }

    private static string PrimaryScreenName => Forms.Screen.PrimaryScreen?.DeviceName ?? "";

    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Win32.POINT pt, uint flags);

    private static Win32.RECT BoundsOf(string screen)
    {
        var s = Forms.Screen.AllScreens.FirstOrDefault(x => x.DeviceName == screen) ?? Forms.Screen.PrimaryScreen!;
        return new Win32.RECT { Left = s.Bounds.Left, Top = s.Bounds.Top, Right = s.Bounds.Right, Bottom = s.Bounds.Bottom };
    }

    private static double ScaleOf(string screen)
    {
        try
        {
            var b = BoundsOf(screen);
            var mon = MonitorFromPoint(new Win32.POINT { X = (b.Left + b.Right) / 2, Y = (b.Top + b.Bottom) / 2 }, 2);
            if (GetDpiForMonitor(mon, 0, out var dx, out _) == 0 && dx > 0) return dx / 96.0;
        }
        catch { }
        return 1.0;
    }

    /// <summary>メイン以外のモニターのバーを作る（作り直しのたびに呼ぶ）。</summary>
    private void BuildSecondaries()
    {
        DisposeSecondaries();
        if (!MultiMonitorActive) return;
        var t = Theme.Current;
        double h = _settings.TaskbarHeight;
        foreach (var screen in Forms.Screen.AllScreens.Where(s => !s.Primary))
        {
            string name = screen.DeviceName;
            var apps = new StackPanel { Orientation = Flow, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var time = UiKit.Label("", 13, t.Text);
            var date = UiKit.Label("", 11, t.TextSecondary);
            var clock = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            time.HorizontalAlignment = date.HorizontalAlignment = HorizontalAlignment.Center;
            time.TextAlignment = date.TextAlignment = TextAlignment.Center;
            clock.Children.Add(time);
            if (!Vertical) clock.Children.Add(date);
            if (!Vertical && TimeHidden) time.Visibility = Visibility.Collapsed;

            var grid = new Grid();
            bool center = _settings.TaskbarAlignment == TaskbarAlignment.Center;
            foreach (var len in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, center ? new GridLength(1, GridUnitType.Star) : GridLength.Auto })
            {
                if (Vertical) grid.RowDefinitions.Add(new RowDefinition { Height = len });
                else grid.ColumnDefinitions.Add(new ColumnDefinition { Width = len });
            }
            Place(grid, apps, center ? 1 : 0, center ? Alignment.Center : Alignment.Start);
            Place(grid, clock, 2, Alignment.End);

            var surface = new BarSurface(BarSurface.Anchor.Fill, h, _settings, Vertical) { Foreground = t.Text };
            surface.SetBody(grid);
            var reservation = new AppBarReservation
            {
                MonitorProvider = () => BoundsOf(name),
                ScaleProvider = () => ScaleOf(name),
            };
            var bar = new SecondaryBar { Screen = name, Reservation = reservation, Surface = surface, Apps = apps, Time = time, Date = date };
            _secondaries.Add(bar);
            surface.Show();
            reservation.PositionChanged += () => ArrangeSecondary(bar);
            reservation.FullscreenChanged += _ => ArrangeSecondary(bar);
            reservation.Reserve(Edge, h);
            Log.Info($"secondary bar: {name} {BoundsOf(name).Left},{BoundsOf(name).Top} scale={ScaleOf(name)}");
        }

        RebuildSecondaryApps();
        UpdateSecondaryClocks(DateTime.Now);
    }

    private void DisposeSecondaries()
    {
        foreach (var s in _secondaries)
        {
            try { s.Magnify?.Dispose(); } catch { }
            try { s.Surface.Close(); } catch { }
            try { s.Reservation.Dispose(); } catch { }
        }
        _secondaries.Clear();
    }

    private void ArrangeSecondary(SecondaryBar bar)
    {
        if (_disposed || !bar.Surface.IsLoaded) return;
        var r = bar.Reservation.Rect;
        if (r.Width <= 0 || r.Height <= 0) return;
        bar.Surface.MoveTo(r.Left, r.Top, r.Width, r.Height, bottomMost: bar.Reservation.IsFullscreenAppActive);
    }

    /// <summary>メイン以外のモニターのバーのボタンを作り直す（RebuildApps のあとに呼ぶ）。</summary>
    private void RebuildSecondaryApps()
    {
        foreach (var bar in _secondaries)
        {
            bar.Apps.Children.Clear();
            bar.Magnify?.Dispose();
            _currentMag = bar.Magnify = NewMagnify(bar.Apps);
            // メインと同じ並び: ピン留め（ピン留めの順）→ そうでないもの（_order の順）
            var groups = BuildGroups(TaskWindows(), withPins: true);
            foreach (var g in groups.Where(x => x.Pinned == null && !_order.Contains(x.Key))) _order.Add(g.Key);
            // 起動していないピン留めはサブには出さない
            var ordered = groups.Where(g => g.Pinned != null && g.Windows.Count > 0)
                .Concat(groups.Where(g => g.Pinned == null).OrderBy(g => _order.IndexOf(g.Key)));
            foreach (var g in ordered)
            {
                var tile = AppButton(g);
                foreach (var w in g.Windows) _buttonFor.TryAdd(w.Handle, tile);
                bar.Apps.Children.Add(tile);
            }
            bar.Apps.Visibility = bar.Apps.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        _currentMag = null;
    }

    private void UpdateSecondaryClocks(DateTime now)
    {
        foreach (var bar in _secondaries)
        {
            bar.Time.Text = ClockStacked ? now.Hour + "\n" + now.ToString("mm") : Vertical ? now.ToString("H:mm") : FormatTime(now);
            bar.Date.Text = Vertical ? now.ToString("M/d", L.Culture) : FormatDate(now);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        // モニターの増減・並べ替えのあと、少し待って（切り替えの途中は位置が定まらない）作り直す
        if (_disposed || !_settings.MultiMonitorTaskbar) return;
        Kick(_displayDebounce);
    });
}
