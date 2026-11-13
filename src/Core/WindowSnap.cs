using System;
using System.Linq;
using System.Runtime.InteropServices;
using ModernShell.Native;
using Forms = System.Windows.Forms;

namespace ModernShell.Core;

public enum SnapKind { Left, Right, Top, Bottom, Maximize, Restore, NextMonitor }

/// <summary>ウィンドウを画面の半分・最大化・次のモニターなどへ動かす（タスクバーのアイコンのメニューから）。</summary>
public static class WindowSnap
{
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetRect(IntPtr hwnd, int attr, out Win32.RECT value, int size);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int SW_MAXIMIZE = 3;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    public static void Apply(IntPtr hwnd, SnapKind kind)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            if (kind == SnapKind.Maximize) { Win32.ShowWindow(hwnd, SW_MAXIMIZE); return; }
            if (kind == SnapKind.Restore) { Win32.ShowWindow(hwnd, Win32.SW_RESTORE); return; }

            // 最大化・最小化のままでは位置を変えられないので、元に戻してから動かす
            bool wasMaximized = IsZoomed(hwnd);
            if (wasMaximized || IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);

            var mon = Win32.MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
            var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (!Win32.GetMonitorInfo(mon, ref mi)) return;
            var work = mi.rcWork;

            if (kind == SnapKind.NextMonitor)
            {
                var screens = Forms.Screen.AllScreens;
                if (screens.Length < 2) { if (wasMaximized) Win32.ShowWindow(hwnd, SW_MAXIMIZE); return; }
                var cur = Forms.Screen.FromHandle(hwnd);
                int i = Array.FindIndex(screens, s => s.DeviceName == cur.DeviceName);
                var next = screens[(i + 1) % screens.Length].WorkingArea;
                Win32.GetWindowRect(hwnd, out var r);
                // 元のモニターでの相対的な位置・大きさを保ったまま、次のモニターへ
                double fx = (r.Left - work.Left) / (double)Math.Max(1, work.Width), fy = (r.Top - work.Top) / (double)Math.Max(1, work.Height);
                double fw = r.Width / (double)Math.Max(1, work.Width), fh = r.Height / (double)Math.Max(1, work.Height);
                int w = (int)Math.Min(next.Width, Math.Round(fw * next.Width)), h = (int)Math.Min(next.Height, Math.Round(fh * next.Height));
                int x = next.Left + (int)Math.Round(fx * next.Width), y = next.Top + (int)Math.Round(fy * next.Height);
                x = Math.Clamp(x, next.Left, Math.Max(next.Left, next.Right - w));
                y = Math.Clamp(y, next.Top, Math.Max(next.Top, next.Bottom - h));
                Win32.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
                if (wasMaximized) Win32.ShowWindow(hwnd, SW_MAXIMIZE);
                return;
            }

            int l = work.Left, t = work.Top, rr = work.Right, b = work.Bottom, mx = (l + rr) / 2, my = (t + b) / 2;
            switch (kind)
            {
                case SnapKind.Left: rr = mx; break;
                case SnapKind.Right: l = mx; break;
                case SnapKind.Top: b = my; break;
                case SnapKind.Bottom: t = my; break;
            }
            // 見えない枠（影・リサイズ用）の分を補正して、見た目の端を合わせる
            Win32.GetWindowRect(hwnd, out var win);
            int il = 0, it = 0, ir = 0, ib = 0;
            if (DwmGetRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var vis, Marshal.SizeOf<Win32.RECT>()) == 0)
            {
                il = vis.Left - win.Left; it = vis.Top - win.Top; ir = win.Right - vis.Right; ib = win.Bottom - vis.Bottom;
            }
            Win32.SetWindowPos(hwnd, IntPtr.Zero, l - il, t - it, (rr - l) + il + ir, (b - t) + it + ib, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch (Exception ex) { Log.Error("window snap", ex); }
    }

    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
}
