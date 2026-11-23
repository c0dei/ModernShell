using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ModernShell.Native;

namespace ModernShell.Core;

/// <summary>仮想デスクトップ上のウィンドウ 1 つ分（画面座標のピクセル）。</summary>
public sealed record DesktopWindow(IntPtr Handle, Guid DesktopId, int Left, int Top, int Right, int Bottom);

/// <summary>
/// 仮想デスクトップごとの、開いているウィンドウの一覧（フライアウトのプレビュー用）。
/// ウィンドウの所属デスクトップは、Windows が公開している IVirtualDesktopManager で調べる（ビルドごとに変わる内部 COM は使わない）。
/// </summary>
public static class DesktopWindows
{
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] out bool onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, ref Guid desktopId);
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    private static IVirtualDesktopManager? _manager;

    private static IVirtualDesktopManager? Manager()
    {
        if (_manager != null) return _manager;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"));
            if (type != null) _manager = (IVirtualDesktopManager?)Activator.CreateInstance(type);
        }
        catch (Exception ex) { Log.Error("virtual desktop manager", ex); }
        return _manager;
    }

    /// <summary>メイン画面の大きさ（ピクセル）。</summary>
    public static (int Width, int Height) ScreenSize() => (Math.Max(1, GetSystemMetrics(0)), Math.Max(1, GetSystemMetrics(1)));

    /// <summary>
    /// 全デスクトップのウィンドウ。前面にあるものが先（EnumWindows の順）。
    /// <paramref name="currentId"/> は今のデスクトップの ID（デスクトップが 1 つだけのときは Guid.Empty で、全部そこに入る）。
    /// </summary>
    public static List<DesktopWindow> List(Guid currentId)
    {
        var result = new List<DesktopWindow>();
        var mgr = Manager();
        uint self = (uint)Environment.ProcessId;
        var sb = new StringBuilder(128);
        Win32.EnumWindows((h, _) =>
        {
            try
            {
                if (!Win32.IsWindowVisible(h) || Win32.IsIconic(h)) return true;
                Win32.GetWindowThreadProcessId(h, out var pid);
                if (pid == self) return true;
                long ex = Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64();
                bool owned = Win32.GetWindow(h, Win32.GW_OWNER) != IntPtr.Zero;
                if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 && (ex & Win32.WS_EX_APPWINDOW) == 0) return true;
                if ((ex & Win32.WS_EX_NOACTIVATE) != 0 || (owned && (ex & Win32.WS_EX_APPWINDOW) == 0)) return true;
                if (Win32.GetWindowTextLength(h) == 0) return true;
                sb.Clear();
                Win32.GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                if (!Win32.GetWindowRect(h, out var r) || r.Right - r.Left < 60 || r.Bottom - r.Top < 40) return true;

                Guid id = currentId;
                if (mgr != null)
                {
                    if (mgr.GetWindowDesktopId(h, out var wid) == 0 && wid != Guid.Empty) id = wid;
                    // 今のデスクトップで隠れているウィンドウ（停止中のストア アプリなど）は除く
                    bool cloaked = DwmGetWindowAttribute(h, Win32.DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0;
                    if (cloaked && (currentId == Guid.Empty || id == currentId)) return true;
                }
                result.Add(new DesktopWindow(h, id, r.Left, r.Top, r.Right, r.Bottom));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
