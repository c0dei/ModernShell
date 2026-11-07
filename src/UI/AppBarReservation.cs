using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>
/// 画面端の作業領域を確保するだけの不可視ウィンドウ（SHAppBarMessage の AppBar）。
/// 見た目（帯 / アイランド）とは切り離してあるので、島を何個並べても予約は 1 つで済む。
/// </summary>
public sealed class AppBarReservation : IDisposable
{
    private readonly HwndSource _src;
    private readonly uint _callbackMsg = Win32.RegisterWindowMessage("ModernShell.AppBar");
    private bool _registered;
    private bool _top;
    private int _thicknessPx;

    public IntPtr Handle => _src.Handle;
    /// <summary>確保した矩形（物理ピクセル）。</summary>
    public Win32.RECT Rect { get; private set; }
    public bool IsFullscreenAppActive { get; private set; }

    public event Action? PositionChanged;
    public event Action<bool>? FullscreenChanged;

    public AppBarReservation()
    {
        var p = new HwndSourceParameters("ModernShell.AppBarReservation")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP（非表示のまま）
            ExtendedWindowStyle = (int)(Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE),
            Width = 0,
            Height = 0,
        };
        _src = new HwndSource(p);
        _src.AddHook(WndProc);

        // 全画面アプリの判定: Windows の ABN_FULLSCREENAPP 通知は当てにならない（Win+D で全画面のゲームを
        // 最小化しても「終わった」が来ないことがあり、タスクバーが背面のままデスクトップの裏に隠れる）。
        // 前面のウィンドウが変わる・最小化される・動くたびに、自分で全画面かどうかを確かめる
        _winEvent = (_, _, _, _, _, _, _) => _src.Dispatcher.BeginInvoke(CheckFullscreen);
        foreach (var (min, max) in new[] { (0x0003u, 0x0003u) /*FOREGROUND*/, (0x0016u, 0x0017u) /*MINIMIZESTART/END*/ })
            _hooks.Add(SetWinEventHook(min, max, IntPtr.Zero, _winEvent, 0, 0, 0 /*WINEVENT_OUTOFCONTEXT*/));
        // ゲームが起動後に全画面へ切り替わる場合など、イベントを取りこぼしたとき用に 1 秒ごとにも確かめる
        _fullscreenTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(1),
            System.Windows.Threading.DispatcherPriority.Background, (_, _) => CheckFullscreen(), _src.Dispatcher);
        _fullscreenTimer.Start();
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    private readonly WinEventProc _winEvent; // ガベージコレクションで消されないよう保持
    private readonly System.Collections.Generic.List<IntPtr> _hooks = new();
    private readonly System.Windows.Threading.DispatcherTimer _fullscreenTimer;

    /// <summary>前面のウィンドウがタスクバーのある画面全体を覆っているか（デスクトップや ModernShell 自身は除く）。</summary>
    private void CheckFullscreen()
    {
        bool fs = false;
        try
        {
            var fg = Win32.GetForegroundWindow();
            if (fg != IntPtr.Zero && IsWindowVisible(fg) && !IsIconic(fg) && !Win32.IsCloaked(fg))
            {
                Win32.GetWindowThreadProcessId(fg, out var pid);
                var cls = new System.Text.StringBuilder(64);
                GetClassName(fg, cls, cls.Capacity);
                bool shell = cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
                if (!shell && pid != (uint)Environment.ProcessId && Win32.GetWindowRect(fg, out var r))
                {
                    var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
                    if (Win32.GetMonitorInfo(MonitorFromWindow(fg, 2 /*NEAREST*/), ref mi))
                    {
                        var m = mi.rcMonitor;
                        var bar = PrimaryMonitor();
                        bool sameMonitor = m.Left == bar.Left && m.Top == bar.Top && m.Right == bar.Right && m.Bottom == bar.Bottom;
                        fs = sameMonitor && r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
                    }
                }
            }
        }
        catch { }
        if (fs == IsFullscreenAppActive) return;
        IsFullscreenAppActive = fs;
        FullscreenChanged?.Invoke(fs);
    }

    /// <summary>プライマリモニターの DPI 倍率。</summary>
    public double Scale => Win32.GetDpiForWindow(Handle) is var d and > 0 ? d / 96.0 : 1.0;

    public void Reserve(bool top, double thicknessDip)
    {
        _top = top;
        _thicknessPx = (int)Math.Round(thicknessDip * Scale);
        Apply();
    }

    private void Apply()
    {
        var abd = Win32.APPBARDATA.Create(Handle);
        if (!_registered)
        {
            abd.uCallbackMessage = _callbackMsg;
            Win32.SHAppBarMessage(Win32.ABM_NEW, ref abd);
            _registered = true;
        }

        var mon = PrimaryMonitor();
        abd.uEdge = _top ? Win32.ABE_TOP : Win32.ABE_BOTTOM;
        abd.rc = mon;
        if (_top) abd.rc.Bottom = mon.Top + _thicknessPx; else abd.rc.Top = mon.Bottom - _thicknessPx;

        Win32.SHAppBarMessage(Win32.ABM_QUERYPOS, ref abd);
        if (_top) abd.rc.Bottom = abd.rc.Top + _thicknessPx; else abd.rc.Top = abd.rc.Bottom - _thicknessPx;
        Win32.SHAppBarMessage(Win32.ABM_SETPOS, ref abd);
        Rect = abd.rc;
        PositionChanged?.Invoke();
    }

    public static Win32.RECT PrimaryMonitor()
    {
        var mon = MonitorFromPoint(new Win32.POINT { X = 0, Y = 0 }, 1 /*MONITOR_DEFAULTTOPRIMARY*/);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(mon, ref mi);
        return mi.rcMonitor;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Win32.POINT pt, uint flags);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _callbackMsg)
        {
            switch ((int)wParam)
            {
                case Win32.ABN_POSCHANGED:
                    if (_registered) Apply();
                    break;
                case Win32.ABN_FULLSCREENAPP:
                    // 通知の中身は信用せず、きっかけとして自分で確かめる
                    CheckFullscreen();
                    break;
            }
            handled = true;
        }
        else if (msg is (int)Win32.WM_DISPLAYCHANGE or 0x02E0 /*WM_DPICHANGED*/ && _registered)
        {
            Apply();
        }
        return IntPtr.Zero;
    }

    public void Remove()
    {
        if (!_registered) return;
        var abd = Win32.APPBARDATA.Create(Handle);
        Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref abd);
        _registered = false;
    }

    public void Dispose()
    {
        _fullscreenTimer.Stop();
        foreach (var h in _hooks) if (h != IntPtr.Zero) UnhookWinEvent(h);
        _hooks.Clear();
        Remove();
        _src.Dispose();
    }
}
