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
    /// <summary>Win+Space を押した時点で見えていたウィンドウ。ここに無いものが「新しく出てきたポップアップ」。</summary>
    private HashSet<IntPtr> _imeFlyoutBaseline = new();

    /// <summary>Win+Space が押された。ポップアップが出るまで少し待ちながら探し、見つけたら IME 表示の上へ動かす。</summary>
    public void MoveImeSwitcher()
    {
        if (ImeAnchor == null || _disposed) return;
        _imeFlyoutBaseline = SnapshotVisibleWindows();
        _imeFlyoutLogged = false;
        _imeFlyoutUntil = DateTime.Now.AddMilliseconds(1200);
        _imeFlyoutTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Normal, (_, _) => ImeFlyoutTick(), _dispatcher);
        if (!_imeFlyoutTimer.IsEnabled) _imeFlyoutTimer.Start();
    }

    private void ImeFlyoutTick()
    {
        if (_disposed || ImeAnchor == null || DateTime.Now > _imeFlyoutUntil) { _imeFlyoutTimer?.Stop(); return; }
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

    /// <summary>IME 表示の基準にする要素（IME のウィジェットがあればそのカード、なければバー標準の表示）。</summary>
    private FrameworkElement? ImeAnchor => ImeWidgetCard();

    private double VisualTreeHelperDpi() => ImeAnchor == null ? 1.0 : System.Windows.Media.VisualTreeHelper.GetDpi(ImeAnchor).DpiScaleX;

    /// <summary>IME 表示の画面上の矩形（物理ピクセル）。</summary>
    private bool TryImeTileRect(out Win32.RECT rect)
    {
        rect = default;
        var el = ImeAnchor;
        if (el == null || !el.IsLoaded || PresentationSource.FromVisual(el) == null) return false;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(el);
        var tl = el.PointToScreen(new Point(0, 0));
        rect = new Win32.RECT
        {
            Left = (int)tl.X, Top = (int)tl.Y,
            Right = (int)(tl.X + el.ActualWidth * dpi.DpiScaleX), Bottom = (int)(tl.Y + el.ActualHeight * dpi.DpiScaleY),
        };
        return true;
    }

    private static bool IsShownWindow(IntPtr h) =>
        Win32.IsWindowVisible(h) && !(DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out var cloaked, 4) == 0 && cloaked != 0);

    private static HashSet<IntPtr> SnapshotVisibleWindows()
    {
        var set = new HashSet<IntPtr>();
        Win32.EnumWindows((h, _) => { if (IsShownWindow(h)) set.Add(h); return true; }, IntPtr.Zero);
        return set;
    }

    /// <summary>
    /// 入力方式の切り替えポップアップのウィンドウ。Windows のバージョンで作り（プロセス・クラス）が違うので、プロセスでは決め打たず、
    /// Win+Space を押す前には無かった小さな最前面級ウィンドウ（= 新しく出たもの）を探す。連打でポップアップが出たままのときは
    /// 差分が取れないので、入力・シェル系のプロセスが持つ小さなウィンドウにフォールバックする。候補は押すたびに最初の 1 回記録する。
    /// </summary>
    private IntPtr FindImeFlyout()
    {
        IntPtr fresh = IntPtr.Zero, known = IntPtr.Zero;
        int selfPid = Environment.ProcessId;
        var seen = new List<string>();
        Win32.EnumWindows((h, _) =>
        {
            if (!IsShownWindow(h)) return true;
            if (!Win32.GetWindowRect(h, out var r)) return true;
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            // 画面いっぱいのウィンドウ（デスクトップ・バー）は除く。切り替えポップアップは小さい
            if (w < 80 || ht < 40 || w > 700 || ht > 700) return true;
            Win32.GetWindowThreadProcessId(h, out var pid);
            if ((int)pid == selfPid) return true;
            var sb = new StringBuilder(128); Win32.GetClassName(h, sb, sb.Capacity);
            var cls = sb.ToString();
            if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or "tooltips_class32") return true;
            string proc;
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return true; }
            bool isNew = !_imeFlyoutBaseline.Contains(h);
            bool shellProc = proc.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase)
                || proc.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                || proc.Equals("explorer", StringComparison.OrdinalIgnoreCase)
                || proc.Equals("InputSwitch", StringComparison.OrdinalIgnoreCase);
            if (!_imeFlyoutLogged) seen.Add($"{(isNew ? "new " : "")}{proc}/{cls} {w}x{ht} @{r.Left},{r.Top}");
            // 新しく出たもの: シェル系を優先（なければ、新しく出た小さな窓なら何でも）
            if (isNew && (fresh == IntPtr.Zero || shellProc)) fresh = h;
            if (!isNew && shellProc && known == IntPtr.Zero
                && (cls.Contains("CoreWindow", StringComparison.OrdinalIgnoreCase) || cls.Contains("Xaml", StringComparison.OrdinalIgnoreCase)
                    || proc.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase))) known = h;
            return true;
        }, IntPtr.Zero);
        var found = fresh != IntPtr.Zero ? fresh : known;
        if (found != IntPtr.Zero && !_imeFlyoutLogged)
        {
            _imeFlyoutLogged = true;
            Log.Info("ime flyout: candidates=" + string.Join("; ", seen));
        }
        return found;
    }
}
