using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ModernShell.Core;

/// <summary>
/// アプリ・ファイル・URL を開く窓口。ModernShell が管理者として動いているときも、開いたものは普通の権限で動くようにする。
/// 管理者のプロセスからふつうに起動すると、起動されたアプリも管理者になってしまう（危険なうえ、ドラッグ＆ドロップなども効かなくなる）。
/// そこで、普通の権限で動いているエクスプローラー（デスクトップ）に「代わりに開いて」と頼む
/// （IShellDispatch2.ShellExecute。Microsoft の Raymond Chen 氏が紹介している方法）。
/// </summary>
public static class Launcher
{
    /// <summary>ModernShell が管理者として動いているか。</summary>
    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    /// <summary>ファイル・アプリ・URL・shell: のパスなどを開く（普通の権限で）。</summary>
    public static void Open(string file, string? args = null, string? workingDir = null)
    {
        if (IsElevated && TryOpenAsUser(file, args, workingDir)) return;
        Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true, WorkingDirectory = workingDir ?? "" });
    }

    /// <summary>管理者として実行（ユーザーが「管理者として実行」を選んだとき）。</summary>
    public static void OpenAsAdmin(string file, string? args = null) =>
        Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true, Verb = "runas" });

    // ---- エクスプローラーに代わりに開いてもらう ----

    [ComImport, Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
    private class ShellWindowsClass { }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        // 呼ばないメソッドも、並び順（関数表の位置）を合わせるために宣言する
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object disp, int hwnd, int swClass, out int cookie);
        void RegisterPending(int threadId, ref object loc, ref object locRoot, int swClass, out int cookie);
        void Revoke(int cookie);
        void OnNavigate(int cookie, ref object loc);
        void OnActivated(int cookie, bool active);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(ref object loc, ref object locRoot, int swClass, out int hwnd, int swfwOptions);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr obj);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(bool enter);
        void InsertMenusSB(IntPtr menu, IntPtr widths);
        void SetMenuSB(IntPtr menu, IntPtr holeMenu, IntPtr activeObject);
        void RemoveMenusSB(IntPtr menu);
        void SetStatusTextSB(IntPtr text);
        void EnableModelessSB(bool enable);
        void TranslateAcceleratorSB(IntPtr msg, short id);
        void BrowseObject(IntPtr pidl, uint flags);
        void GetViewStateStream(uint mode, out IntPtr stream);
        void GetControlWindow(uint id, out IntPtr hwnd);
        void SendControlMsg(uint id, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
        [PreserveSig] int QueryActiveShellView(out IShellView view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(bool enter);
        void TranslateAccelerator(IntPtr msg);
        void EnableModeless(bool enable);
        void UIActivate(uint state);
        void Refresh();
        void CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rect, out IntPtr hwnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr settings);
        void AddPropertySheetPages(uint reserved, IntPtr fn, IntPtr lParam);
        void SaveViewState();
        void SelectItem(IntPtr pidl, uint flags);
        [PreserveSig] int GetItemObject(uint item, ref Guid riid, out IntPtr obj);
    }

    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");

    /// <summary>
    /// デスクトップ（エクスプローラー）のフォルダー表示から Shell.Application を取り出し、その ShellExecute で開く。
    /// デスクトップは普通の権限で動いているので、開かれたものも普通の権限になる。
    /// </summary>
    private static bool TryOpenAsUser(string file, string? args, string? workingDir)
    {
        try
        {
            var windows = (IShellWindows)new ShellWindowsClass();
            object loc = 0 /*CSIDL_DESKTOP*/, root = Type.Missing;
            var desktop = windows.FindWindowSW(ref loc, ref root, 8 /*SWC_DESKTOP*/, out _, 1 /*SWFO_NEEDDISPATCH*/);
            if (desktop is not IServiceProvider sp) return false;
            var sid = SID_STopLevelBrowser;
            var iid = IID_IShellBrowser;
            if (sp.QueryService(ref sid, ref iid, out var browserPtr) != 0 || browserPtr == IntPtr.Zero) return false;
            var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPtr);
            Marshal.Release(browserPtr);
            if (browser.QueryActiveShellView(out var view) != 0 || view == null) return false;
            var idisp = IID_IDispatch;
            if (view.GetItemObject(0 /*SVGIO_BACKGROUND*/, ref idisp, out var folderViewPtr) != 0 || folderViewPtr == IntPtr.Zero) return false;
            dynamic folderView = Marshal.GetObjectForIUnknown(folderViewPtr);
            Marshal.Release(folderViewPtr);
            dynamic shell = folderView.Application; // IShellDispatch2
            shell.ShellExecute(file, args ?? "", workingDir ?? "", "", 1 /*SW_SHOWNORMAL*/);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("open as user", ex);
            return false;
        }
    }
}
