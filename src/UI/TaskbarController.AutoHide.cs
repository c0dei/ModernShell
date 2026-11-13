using System;
using System.Linq;
using System.Windows.Threading;
using ModernShell.Core;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>
/// タスクバーの自動非表示: 使わないときはウィンドウごと隠し、画面の端（バーのある側）にカーソルを当てるとスライドして出す。
/// 作業領域は確保しない（<see cref="AppBarReservation.AutoHide"/>）。メニュー・プレビュー・スタートメニューが開いている間や、
/// カーソルがバーの上にある間は隠さない。
/// </summary>
public sealed partial class TaskbarController
{
    private DispatcherTimer? _autoHideWatch, _autoHideAnim;
    private bool _barHidden;
    private DateTime _leftBarAt = DateTime.MaxValue;
    private int _animStep;
    private bool _animShowing;

    /// <summary>スタートメニューなど、開いている間はバーを隠したくないものがあるか（App から設定）。</summary>
    public Func<bool>? KeepBarVisible { get; set; }

    private bool AutoHideActive => _settings.AutoHideTaskbar && _settings.UseCustomTaskbar;

    private void StartAutoHide()
    {
        StopAutoHide();
        if (!AutoHideActive) return;
        _barHidden = false;
        _leftBarAt = DateTime.Now.AddSeconds(1.5); // 起動直後は少し見せてから隠す
        _autoHideWatch = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => AutoHideTick(), _dispatcher);
        _autoHideWatch.Start();
    }

    private void StopAutoHide()
    {
        _autoHideWatch?.Stop(); _autoHideWatch = null;
        _autoHideAnim?.Stop(); _autoHideAnim = null;
        _barHidden = false;
        foreach (var s in _surfaces) { s.OffsetX = s.OffsetY = 0; if (s.Hidden && s.IsLoaded) s.ShowBar(); }
    }

    /// <summary>Arrange のあと: 隠している間は表示しないままにしておく（MoveTo は隠れたウィンドウを出さない）。</summary>
    private void AutoHideArranged() { }

    private (int dx, int dy) FullOffset()
    {
        var r = _reservation.Rect;
        int span = Vertical ? r.Width : r.Height;
        int away = span + (int)Math.Round(Margins.across * (_surfaces.FirstOrDefault()?.Scale ?? 1)) + 2;
        return Edge switch
        {
            TaskbarEdge.Top => (0, -away),
            TaskbarEdge.Left => (-away, 0),
            TaskbarEdge.Right => (away, 0),
            _ => (0, away),
        };
    }

    private bool CursorAtEdge(Win32.POINT p)
    {
        var r = _reservation.Rect;
        const int band = 1;
        return Edge switch
        {
            TaskbarEdge.Top => p.Y <= r.Top + band && p.X >= r.Left && p.X <= r.Right,
            TaskbarEdge.Left => p.X <= r.Left + band && p.Y >= r.Top && p.Y <= r.Bottom,
            TaskbarEdge.Right => p.X >= r.Right - band - 1 && p.Y >= r.Top && p.Y <= r.Bottom,
            _ => p.Y >= r.Bottom - band - 1 && p.X >= r.Left && p.X <= r.Right,
        };
    }

    private bool CursorOverBar(Win32.POINT p)
    {
        foreach (var s in _surfaces)
        {
            if (!Win32.GetWindowRect(s.Handle, out var wr)) continue;
            const int slop = 6;
            if (p.X >= wr.Left - slop && p.X <= wr.Right + slop && p.Y >= wr.Top - slop && p.Y <= wr.Bottom + slop) return true;
        }
        return false;
    }

    private void AutoHideTick()
    {
        if (_disposed || !AutoHideActive || _surfaces.Count == 0) return;
        if (_reservation.IsFullscreenAppActive) return;
        if (!Win32.GetCursorPos(out var p)) return;

        if (_barHidden)
        {
            if (CursorAtEdge(p)) RevealBar();
            return;
        }
        bool keep = CursorOverBar(p) || FluentSurface.AnyOpen || _preview.IsOpen || _overflowPopup?.IsOpen == true || KeepBarVisible?.Invoke() == true;
        if (keep) { _leftBarAt = DateTime.Now; return; }
        if (DateTime.Now - _leftBarAt >= TimeSpan.FromMilliseconds(500)) ConcealBar();
    }

    private void RevealBar()
    {
        _barHidden = false;
        _leftBarAt = DateTime.Now;
        var (dx, dy) = FullOffset();
        foreach (var s in _surfaces) { s.OffsetX = dx; s.OffsetY = dy; s.ShowBar(); }
        Slide(showing: true);
    }

    private void ConcealBar()
    {
        _barHidden = true;
        Slide(showing: false);
    }

    private void Slide(bool showing)
    {
        _autoHideAnim?.Stop();
        _animShowing = showing;
        _animStep = 0;
        int steps = Math.Max(1, (int)Math.Round(10 * (Anim.Factor <= 0 ? 1 : Anim.Factor)));
        if (!_settings.AnimateStartMenu && !_settings.AnimatePreviews) steps = 1;
        var (dx, dy) = FullOffset();
        _autoHideAnim = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) =>
        {
            _animStep++;
            double t = Math.Min(1.0, _animStep / (double)steps);
            double eased = 1 - Math.Pow(1 - t, 3);
            double k = _animShowing ? 1 - eased : eased; // 1 = 完全に隠れた位置
            foreach (var s in _surfaces)
            {
                s.OffsetX = (int)Math.Round(dx * k);
                s.OffsetY = (int)Math.Round(dy * k);
                if (!s.Hidden) s.Reposition();
            }
            if (t >= 1.0)
            {
                _autoHideAnim?.Stop();
                if (!_animShowing) foreach (var s in _surfaces) s.HideBar();
            }
        }, _dispatcher);
        _autoHideAnim.Start();
    }
}
