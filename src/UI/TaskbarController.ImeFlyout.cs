using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ModernShell.Core;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>
/// Win+Space の入力方式の切り替えポップアップを、ModernShell の IME 表示の真上（バーが上なら真下）に移す。
/// このポップアップは explorer が「純正タスクバーの IME 表示」を基準に出すもので、純正タスクバーを隠していると基準を失い、
/// マルチモニターでは別のモニターに出てしまう。explorer には手を入れず、出てきたウィンドウを外から動かす。
/// </summary>
public sealed partial class TaskbarController
{
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    private DispatcherTimer? _imeFlyoutTimer;
    private DateTime _imeFlyoutUntil;
    private bool _imeFlyoutLogged;

    /// <summary>Win+Space が押された。ポップアップが出るまで少し待ちながら探し、見つけたら IME 表示の上へ動かす。</summary>
    public void MoveImeSwitcher()
    {
        if (_imeText == null || _disposed) return;
        _imeFlyoutUntil = DateTime.Now.AddMilliseconds(1200);
        _imeFlyoutTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Normal, (_, _) => ImeFlyoutTick(), _dispatcher);
        if (!_imeFlyoutTimer.IsEnabled) _imeFlyoutTimer.Start();
    }

    private void ImeFlyoutTick()
    {
        if (_disposed || _imeText == null || DateTime.Now > _imeFlyoutUntil) { _imeFlyoutTimer?.Stop(); return; }
        try
        {
            var fly = FindImeFlyout();
            if (fly == IntPtr.Zero || !Win32.GetWindowRect(fly, out var r)) return;
            if (!TryImeTileRect(out var tile)) return;

            int w = r.Right - r.Left, h = r.Bottom - r.Top, gap = (int)Math.Round(12 * VisualTreeHelperDpi());
            var mon = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(tile.Left, tile.Top, tile.Right - tile.Left, tile.Bottom - tile.Top)).WorkingArea;
            int cx = (tile.Left + tile.Right) / 2, cy = (tile.Top + tile.Bottom) / 2;
            int x, y;
            switch (Edge)
            {
                case TaskbarEdge.Top: x = cx - w / 2; y = tile.Bottom + gap; break;
                case TaskbarEdge.Left: x = tile.Right + gap; y = cy - h / 2; break;
                case TaskbarEdge.Right: x = tile.Left - w - gap; y = cy - h / 2; break;
                default: x = cx - w / 2; y = tile.Top - h - gap; break;
            }
            x = Math.Max(mon.Left, Math.Min(x, mon.Right - w));
            y = Math.Max(mon.Top, Math.Min(y, mon.Bottom - h));
            // すでにその位置なら触らない（ポップアップ側の再配置と取り合わない）
            if (Math.Abs(r.Left - x) <= 2 && Math.Abs(r.Top - y) <= 2) return;
            Win32.SetWindowPos(fly, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }
        catch (Exception ex) { Log.Error("ime flyout move", ex); _imeFlyoutTimer?.Stop(); }
    }

    private double VisualTreeHelperDpi() => _imeText == null ? 1.0 : System.Windows.Media.VisualTreeHelper.GetDpi(_imeText).DpiScaleX;

    /// <summary>IME 表示の画面上の矩形（物理ピクセル）。</summary>
    private bool TryImeTileRect(out Win32.RECT rect)
    {
        rect = default;
        if (_imeText == null || !_imeText.IsLoaded || PresentationSource.FromVisual(_imeText) == null) return false;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_imeText);
        var tl = _imeText.PointToScreen(new Point(0, 0));
        rect = new Win32.RECT
        {
            Left = (int)tl.X, Top = (int)tl.Y,
            Right = (int)(tl.X + _imeText.ActualWidth * dpi.DpiScaleX), Bottom = (int)(tl.Y + _imeText.ActualHeight * dpi.DpiScaleY),
        };
        return true;
    }

    /// <summary>
    /// 入力方式の切り替えポップアップのウィンドウ。Windows のバージョンで作りが違うので、入力・シェル系のプロセスが持つ
    /// 表示中の小さなウィンドウを探す。最初に見つけたとき、候補を記録する（見つからない・動かない場合の調査用）。
    /// </summary>
    private IntPtr FindImeFlyout()
    {
        IntPtr found = IntPtr.Zero;
        var seen = new List<string>();
        Win32.EnumWindows((h, _) =>
        {
            if (!Win32.IsWindowVisible(h)) return true;
            if (DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out var cloaked, 4) == 0 && cloaked != 0) return true;
            Win32.GetWindowThreadProcessId(h, out var pid);
            string proc;
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return true; }
            if (!proc.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase)
                && !proc.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                && !proc.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return true;
            var sb = new StringBuilder(128); Win32.GetClassName(h, sb, sb.Capacity);
            var cls = sb.ToString();
            if (!Win32.GetWindowRect(h, out var r)) return true;
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            // 画面いっぱいのウィンドウ（デスクトップ・バー）は除く。切り替えポップアップは小さい
            if (w < 80 || ht < 40 || w > 700 || ht > 700) return true;
            if (proc.Equals("explorer", StringComparison.OrdinalIgnoreCase) && cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return true;
            seen.Add($"{proc}/{cls} {w}x{ht}");
            if (found == IntPtr.Zero && (cls.Contains("CoreWindow", StringComparison.OrdinalIgnoreCase) || cls.Contains("Xaml", StringComparison.OrdinalIgnoreCase)
                || proc.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase))) found = h;
            return true;
        }, IntPtr.Zero);
        if (found != IntPtr.Zero && !_imeFlyoutLogged)
        {
            _imeFlyoutLogged = true;
            Log.Info("ime flyout: candidates=" + string.Join("; ", seen));
        }
        return found;
    }
}
