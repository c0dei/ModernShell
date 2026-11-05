using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using ModernShell.Native;

namespace ModernShell.Core;

public sealed class TaskWindow
{
    public IntPtr Handle { get; init; }
    public string Title { get; set; } = "";
    public string? ExePath { get; init; }
    public string? Aumid { get; init; }
    /// <summary>グループ化キー（AUMID 優先、なければ exe パス）。</summary>
    public string GroupKey => Aumid ?? ExePath ?? Handle.ToString();

    private ImageSource? _icon;
    public ImageSource? Icon => _icon ??= LoadIcon();

    private ImageSource? LoadIcon()
    {
        if (Aumid != null && IconHelper.Get(@"shell:AppsFolder\" + Aumid, 32) is { } a) return a;
        var exe = ExePath;
        if (exe != null && !exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
            && IconHelper.Get(exe, 32) is { } e) return e;
        return IconHelper.FromWindow(Handle);
    }
}

/// <summary>
/// タスクバーに並べるべきトップレベルウィンドウを追跡する。
/// SetWinEventHook (WINEVENT_OUTOFCONTEXT) を使うので explorer や他プロセスへ DLL は注入されない。
/// </summary>
public sealed class WindowTracker : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounce;
    private readonly List<IntPtr> _hooks = new();
    private readonly Win32.WinEventProc _proc; // GC 防止のためフィールド保持
    private Dictionary<IntPtr, TaskWindow> _windows = new();
    private readonly HashSet<IntPtr> _ignored = new();

    public IReadOnlyCollection<TaskWindow> Windows => _windows.Values;
    public IntPtr Foreground { get; private set; }

    public event Action? WindowsChanged;
    public event Action? ForegroundChanged;

    public WindowTracker(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = OnWinEvent;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => { _debounce!.Stop(); Refresh(); }, dispatcher);
        _debounce.Stop();
    }

    /// <summary>自アプリのウィンドウ（タスクバー/スタート自身）を除外対象に登録。</summary>
    public void Ignore(IntPtr hwnd) => _ignored.Add(hwnd);

    public void Start()
    {
        void Hook(uint min, uint max) =>
            _hooks.Add(Win32.SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT));

        Hook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND);
        Hook(Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND);
        Hook(Win32.EVENT_OBJECT_CREATE, Win32.EVENT_OBJECT_HIDE); // CREATE/DESTROY/SHOW/HIDE
        Hook(Win32.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_OBJECT_NAMECHANGE);
        Hook(Win32.EVENT_OBJECT_CLOAKED, Win32.EVENT_OBJECT_UNCLOAKED);
        Refresh();
        Foreground = Win32.GetForegroundWindow();
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Win32.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;

        switch (ev)
        {
            case Win32.EVENT_SYSTEM_FOREGROUND:
                Foreground = hwnd;
                ForegroundChanged?.Invoke();
                if (!_windows.ContainsKey(hwnd)) Kick();
                break;
            case Win32.EVENT_OBJECT_NAMECHANGE:
                if (_windows.TryGetValue(hwnd, out var w))
                {
                    w.Title = GetTitle(hwnd);
                    Kick();
                }
                break;
            case Win32.EVENT_OBJECT_CREATE:
                break; // SHOW で拾う（CREATE 時点ではスタイル未確定のことが多い）
            default:
                Kick();
                break;
        }
    }

    private void Kick()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Refresh()
    {
        var next = new Dictionary<IntPtr, TaskWindow>();
        Win32.EnumWindows((h, _) =>
        {
            if (IsTaskbarWindow(h))
                next[h] = _windows.TryGetValue(h, out var old) ? old : Create(h);
            return true;
        }, IntPtr.Zero);

        foreach (var w in next.Values) w.Title = GetTitle(w.Handle);
        _windows = next;
        WindowsChanged?.Invoke();
    }

    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    private bool IsTaskbarWindow(IntPtr h)
    {
        if (_ignored.Contains(h) || !Win32.IsWindowVisible(h)) return false;
        long ex = (long)Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE);
        long style = (long)Win32.GetWindowLongPtr(h, Win32.GWL_STYLE);
        if ((style & Win32.WS_CHILD) != 0) return false;
        bool appWindow = (ex & Win32.WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if ((ex & Win32.WS_EX_TOOLWINDOW) != 0) return false;
            if ((ex & Win32.WS_EX_NOACTIVATE) != 0) return false;
            if (Win32.GetWindow(h, Win32.GW_OWNER) != IntPtr.Zero) return false;
        }
        if (Win32.IsCloaked(h)) return false; // 別仮想デスクトップ / 中断中の UWP
        if (Win32.GetWindowTextLength(h) == 0) return false;
        var cls = new StringBuilder(256);
        Win32.GetClassName(h, cls, cls.Capacity);
        return !ExcludedClasses.Contains(cls.ToString());
    }

    private static TaskWindow Create(IntPtr h)
    {
        Win32.GetWindowThreadProcessId(h, out var pid);
        return new TaskWindow { Handle = h, ExePath = Win32.GetProcessPath(pid), Aumid = WindowProps.GetAumid(h) };
    }

    private static string GetTitle(IntPtr h)
    {
        int len = Win32.GetWindowTextLength(h);
        var sb = new StringBuilder(Math.Max(len + 1, 2));
        Win32.GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // ウィンドウ操作
    public void Activate(IntPtr h)
    {
        if (Win32.IsIconic(h)) Win32.ShowWindow(h, Win32.SW_RESTORE);
        Foregrounder.Force(h);
    }

    /// <summary>タスクバーボタンのクリック挙動: 前面なら最小化、そうでなければ前面化。</summary>
    public void Toggle(IntPtr h)
    {
        if (Foreground == h && !Win32.IsIconic(h)) Win32.ShowWindow(h, Win32.SW_MINIMIZE);
        else Activate(h);
    }

    public static void Close(IntPtr h) => Win32.PostMessage(h, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public void Dispose()
    {
        foreach (var h in _hooks) if (h != IntPtr.Zero) Win32.UnhookWinEvent(h);
        _hooks.Clear();
    }
}

/// <summary>前面化制限 (ForegroundLockTimeout) を回避して確実にウィンドウを前に出す。</summary>
internal static class Foregrounder
{
    public static void Force(IntPtr h)
    {
        if (Win32.SetForegroundWindow(h)) return;
        var fg = Win32.GetForegroundWindow();
        uint fgThread = Win32.GetWindowThreadProcessId(fg, out _);
        uint me = Win32.GetCurrentThreadId();
        if (fgThread != 0 && fgThread != me)
        {
            Win32.AttachThreadInput(me, fgThread, true);
            Win32.BringWindowToTop(h);
            Win32.SetForegroundWindow(h);
            Win32.AttachThreadInput(me, fgThread, false);
        }
        if (Win32.GetForegroundWindow() != h)
        {
            // 最終手段: ALT を一度押下して「直前に入力があった」状態にする
            Input.Tap(Win32.VK_MENU);
            Win32.SetForegroundWindow(h);
        }
    }
}

/// <summary>ウィンドウのプロパティストアから AppUserModelID を取得（UWP / Chrome PWA 等の識別に使う）。</summary>
internal static class WindowProps
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY k);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pv);

    private static PROPERTYKEY AppUserModelId = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
    private const ushort VT_LPWSTR = 31;

    public static string? GetAumid(IntPtr hwnd)
    {
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return null;
            var key = AppUserModelId;
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt == VT_LPWSTR ? Marshal.PtrToStringUni(pv.p) : null; }
            finally { PropVariantClear(ref pv); }
        }
        catch { return null; }
        finally { if (store != null) Marshal.ReleaseComObject(store); }
    }
}

/// <summary>既知 AUMID → exe 名（ピン留めと実行中ウィンドウの突き合わせ用）。</summary>
internal static class KnownApps
{
    private static readonly Dictionary<string, string> AumidToExe = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Windows.Explorer"] = "explorer.exe",
        ["MSEdge"] = "msedge.exe",
        ["Chrome"] = "chrome.exe",
        ["Microsoft.Office.WINWORD.EXE.15"] = "winword.exe",
        ["Microsoft.Office.EXCEL.EXE.15"] = "excel.exe",
        ["Microsoft.Office.POWERPNT.EXE.15"] = "powerpnt.exe",
        ["Microsoft.Office.OUTLOOK.EXE.15"] = "outlook.exe",
    };

    public static bool Matches(AppEntry app, TaskWindow w)
    {
        if (w.Aumid != null && string.Equals(w.Aumid, app.Id, StringComparison.OrdinalIgnoreCase)) return true;
        if (w.ExePath == null) return false;
        if (app.ExePath != null) return string.Equals(app.ExePath, w.ExePath, StringComparison.OrdinalIgnoreCase);
        return AumidToExe.TryGetValue(app.Id, out var exe)
               && string.Equals(Path.GetFileName(w.ExePath), exe, StringComparison.OrdinalIgnoreCase);
    }
}
