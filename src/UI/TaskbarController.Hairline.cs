using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ManagedShell.WindowsTasks;
using ModernShell.Core;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>
/// ・自動非表示中の「細線」: バーを隠している間、画面の端に細い線だけ残す。線の区切りは開いているアプリ（前面のアプリはアクセント色）。
///   クリックは素通し（カーソルを端に当てるとバーが出る既存の仕組みをそのまま使う）。全画面アプリの間は出さない。
/// ・バーの色を「壁紙」にしたとき: 壁紙の平均色が変わったらバーを作り直す。
/// </summary>
public sealed partial class TaskbarController
{
    // ---- 細線 ----
    private HairlineWindow? _hairline;
    private bool _hairReady;   // 隠すアニメーションが終わった
    private int _hairTick;

    private void ShowHairlineSoon() { _hairReady = true; UpdateHairline(); }

    private void HideHairline()
    {
        _hairReady = false;
        _hairline?.Close();
        _hairline = null;
    }

    /// <summary>1 秒に 1 回くらい: 隠している間だけ、開いているアプリの区切りを更新する。</summary>
    private void HairlineTick()
    {
        if (!_barHidden || !_hairReady) return;
        if (++_hairTick % 12 == 0) UpdateHairline();
    }

    private void UpdateHairline()
    {
        if (_disposed || !_hairReady || !_barHidden || !AutoHideActive || !_settings.AutoHideHairline || _reservation.IsFullscreenAppActive)
        {
            _hairline?.Hide();
            return;
        }
        try
        {
            // 開いているアプリごとに 1 区切り（並びはバーのボタンと同じ）。前面のアプリだけアクセント色
            var groups = BuildGroups(TaskWindows(), withPins: false)
                .Where(g => g.Windows.Count > 0)
                .OrderBy(g => _order.IndexOf(g.Key) is var i && i < 0 ? int.MaxValue : i)
                .ToList();
            var segs = groups.Select(g => g.Windows.Any(w => w.State == ApplicationWindow.WindowState.Active)).ToList();
            if (segs.Count == 0) { _hairline?.Hide(); return; }

            double scale = _surfaces.FirstOrDefault()?.Scale ?? 1.0;
            var r = _reservation.Rect;
            int thick = Math.Max(2, (int)Math.Round(3 * scale));
            int span = Vertical ? r.Bottom - r.Top : r.Right - r.Left;
            int len = Math.Min((int)Math.Round(span * 0.5), (int)Math.Round((24 * segs.Count + 120) * scale));
            len = Math.Max(len, (int)Math.Round(80 * scale));
            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            int x, y, w, h;
            switch (Edge)
            {
                case TaskbarEdge.Top: (x, y, w, h) = (cx - len / 2, r.Top, len, thick); break;
                case TaskbarEdge.Left: (x, y, w, h) = (r.Left, cy - len / 2, thick, len); break;
                case TaskbarEdge.Right: (x, y, w, h) = (r.Right - thick, cy - len / 2, thick, len); break;
                default: (x, y, w, h) = (cx - len / 2, r.Bottom - thick, len, thick); break;
            }
            _hairline ??= new HairlineWindow();
            _hairline.Set(segs, Vertical, Theme.Current);
            _hairline.Place(x, y, w, h);
        }
        catch (Exception ex) { Log.Error("hairline", ex); }
    }

    /// <summary>画面の端に出す細線のウィンドウ。クリックは素通し・フォーカスは奪わない・Alt+Tab に出さない。</summary>
    private sealed class HairlineWindow : Window
    {
        private readonly Grid _grid = new();
        private bool _shown;

        public HairlineWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Title = "ModernShell Hairline";
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000; Top = -32000; Width = 1; Height = 1;
            Content = _grid;
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            long ex = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
            const long WS_EX_TRANSPARENT = 0x20;
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, (IntPtr)(ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | WS_EX_TRANSPARENT));
            Win32.SetWindowLongPtr(hwnd, -8 /*GWLP_HWNDPARENT*/, IntPtr.Zero);
            int excluded = 1;
            Win32.DwmSetWindowAttribute(hwnd, 12 /*DWMWA_EXCLUDED_FROM_PEEK*/, ref excluded, sizeof(int));
        }

        public void Set(IReadOnlyList<bool> segments, bool vertical, Theme t)
        {
            _grid.Children.Clear();
            _grid.ColumnDefinitions.Clear();
            _grid.RowDefinitions.Clear();
            var tc = t.Text is SolidColorBrush sb ? sb.Color : Colors.White;
            var plain = new SolidColorBrush(Color.FromArgb(150, tc.R, tc.G, tc.B));
            plain.Freeze();
            for (int i = 0; i < segments.Count; i++)
            {
                if (vertical) _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(segments[i] ? 2 : 1, GridUnitType.Star) });
                else _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(segments[i] ? 2 : 1, GridUnitType.Star) });
                var b = new Border { Background = segments[i] ? t.AccentBrush : plain, CornerRadius = new CornerRadius(1.5), Margin = vertical ? new Thickness(0, 0, 0, 2) : new Thickness(0, 0, 2, 0) };
                if (vertical) Grid.SetRow(b, i); else Grid.SetColumn(b, i);
                _grid.Children.Add(b);
            }
        }

        public void Place(int x, int y, int w, int h)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!_shown) { Show(); _shown = true; }
            else if (!IsVisible) Show();
            Win32.SetWindowPos(hwnd, (IntPtr)(-1) /*HWND_TOPMOST*/, x, y, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }
    }

    // ---- 壁紙の色に合わせたバー ----
    private bool _wallWatching;
    private Color? _wallTintApplied;

    /// <summary>バーの色が「壁紙」のときだけ、壁紙の変化を見張る（壁紙を読み込むのは数秒おきの変更確認で、色は読み込み時に 1 回だけ計算する）。</summary>
    private void SyncWallpaperWatch()
    {
        bool want = !_disposed && _settings.TaskbarTint == BarTintMode.Wallpaper;
        _wallTintApplied = want ? MicaLike.WallpaperTint(Theme.Current.SolidColor) : null;
        if (want == _wallWatching) return;
        _wallWatching = want;
        if (want) MicaLike.Changed += OnWallpaperChanged; else MicaLike.Changed -= OnWallpaperChanged;
    }

    private void StopWallpaperWatch()
    {
        if (!_wallWatching) return;
        _wallWatching = false;
        MicaLike.Changed -= OnWallpaperChanged;
    }

    private void OnWallpaperChanged()
    {
        if (_disposed || _settings.TaskbarTint != BarTintMode.Wallpaper) return;
        var now = MicaLike.WallpaperTint(Theme.Current.SolidColor);
        if (now is not { } n) return;
        if (_wallTintApplied is { } old && Math.Abs(old.R - n.R) <= 3 && Math.Abs(old.G - n.G) <= 3 && Math.Abs(old.B - n.B) <= 3) return; // ほぼ同じ色なら作り直さない
        Rebuild();
    }
}
