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
                    IsFullscreenAppActive = lParam != IntPtr.Zero;
                    FullscreenChanged?.Invoke(IsFullscreenAppActive);
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
        Remove();
        _src.Dispose();
    }
}
