using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Automation;
using ModernShell.Native;

namespace ModernShell.Core;

/// <summary>
/// 純正タスクバー (Shell_TrayWnd) を「外から」操作する。
/// explorer の内部には一切触れず、ShowWindow / AppBar API / UI Automation だけを使う。
/// 終了時・クラッシュ時には必ず Restore() で元に戻す。
/// </summary>
public static class NativeTaskbar
{
    private static bool _hidden;
    private static int _originalState = -1;
    private static readonly object Gate = new();

    public static IEnumerable<IntPtr> TrayWindows()
    {
        var main = Win32.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        IntPtr h = IntPtr.Zero;
        while ((h = Win32.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return h;
    }

    /// <summary>純正タスクバーを自動的に隠す設定にしてから非表示にする（作業領域を解放するため）。</summary>
    public static void Hide()
    {
        lock (Gate)
        {
            var main = Win32.FindWindow("Shell_TrayWnd", null);
            if (main == IntPtr.Zero) return;
            var abd = Win32.APPBARDATA.Create(main);
            if (_originalState < 0)
            {
                _originalState = (int)Win32.SHAppBarMessage(Win32.ABM_GETSTATE, ref abd);
                Persist(_originalState);
            }
            abd.lParam = (IntPtr)Win32.ABS_AUTOHIDE;
            Win32.SHAppBarMessage(Win32.ABM_SETSTATE, ref abd);
            foreach (var h in TrayWindows()) Win32.ShowWindow(h, Win32.SW_HIDE);
            _hidden = true;
        }
    }

    /// <summary>explorer が勝手に再表示した場合に再度隠す（タイマーから呼ぶ）。</summary>
    public static void EnforceHidden()
    {
        if (!_hidden) return;
        foreach (var h in TrayWindows())
            if (Win32.IsWindowVisible(h)) Win32.ShowWindow(h, Win32.SW_HIDE);
    }

    public static void Restore()
    {
        lock (Gate)
        {
            int state = _originalState >= 0 ? _originalState : LoadPersisted();
            if (!_hidden && state < 0) return;
            var main = Win32.FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero)
            {
                if (state >= 0)
                {
                    var abd = Win32.APPBARDATA.Create(main);
                    abd.lParam = (IntPtr)state;
                    Win32.SHAppBarMessage(Win32.ABM_SETSTATE, ref abd);
                }
                foreach (var h in TrayWindows()) Win32.ShowWindow(h, Win32.SW_SHOWNOACTIVATE);
            }
            _hidden = false;
            _originalState = -1;
            Persist(-1);
        }
    }

    // 強制終了された場合でも次回起動時/--restore で元の自動非表示状態に戻せるよう保存しておく
    private static string StateFile => System.IO.Path.Combine(Settings.Directory, "taskbar-state.txt");
    private static void Persist(int state)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Settings.Directory);
            if (state < 0) System.IO.File.Delete(StateFile);
            else System.IO.File.WriteAllText(StateFile, state.ToString());
        }
        catch { }
    }
    private static int LoadPersisted()
    {
        try { return int.Parse(System.IO.File.ReadAllText(StateFile)); } catch { return -1; }
    }

    public static Win32.RECT? GetTaskbarRect()
    {
        var main = Win32.FindWindow("Shell_TrayWnd", null);
        return main != IntPtr.Zero && Win32.GetWindowRect(main, out var r) ? r : null;
    }

    /// <summary>
    /// UI Automation で純正スタートボタンの位置（物理ピクセル）を取得する。
    /// Win11: XAML の AutomationId "StartButton"、Win10: クラス "Start" の HWND。
    /// UIA はそこそこ重いので UI スレッドやフック内では呼ばないこと。
    /// </summary>
    public static Win32.RECT? FindStartButton()
    {
        try
        {
            var main = Win32.FindWindow("Shell_TrayWnd", null);
            if (main == IntPtr.Zero || !Win32.IsWindowVisible(main)) return null;

            var legacy = Win32.FindWindowEx(main, IntPtr.Zero, "Start", null);
            if (legacy != IntPtr.Zero && Win32.GetWindowRect(legacy, out var lr)) return lr;

            var root = AutomationElement.FromHandle(main);
            var el = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
            if (el == null) return null;
            var b = el.Current.BoundingRectangle;
            if (b.IsEmpty || b.Width < 1) return null;
            return new Win32.RECT { Left = (int)b.Left, Top = (int)b.Top, Right = (int)b.Right, Bottom = (int)b.Bottom };
        }
        catch (Exception ex)
        {
            Log.Error("find start button", ex);
            return null;
        }
    }

    /// <summary>バックグラウンドでスタートボタン位置を定期更新する。</summary>
    public static Timer WatchStartButton(Action<Win32.RECT?> update, Func<bool> enabled)
    {
        int busy = 0;
        return new Timer(_ =>
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return; // UIA が遅いときに多重実行しない
            try { update(enabled() ? FindStartButton() : null); }
            catch { update(null); }
            finally { Volatile.Write(ref busy, 0); }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(4));
    }
}
