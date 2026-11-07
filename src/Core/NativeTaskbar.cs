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
    private static readonly object Gate = new();

    public static IEnumerable<IntPtr> TrayWindows()
    {
        var main = Win32.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        IntPtr h = IntPtr.Zero;
        while ((h = Win32.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return h;
    }

    /// <summary>
    /// 純正タスクバーを隠す直前に呼ぶ。元の「自動的に隠す」状態をファイルに記録しておき、
    /// ModernShell が強制終了されてもウォッチドッグ / 次回起動 / --restore で戻せるようにする。
    /// ※ ManagedShell が自前の Shell_TrayWnd を作る前に呼ぶこと（FindWindow が explorer のものを返すうちに）。
    /// </summary>
    public static void RememberOriginalState()
    {
        lock (Gate)
        {
            if (LoadPersisted() >= 0) return; // 前回分が残っていればそれが本来の状態
            var main = Win32.FindWindow("Shell_TrayWnd", null);
            if (main == IntPtr.Zero) return;
            var abd = Win32.APPBARDATA.Create(main);
            Persist((int)Win32.SHAppBarMessage(Win32.ABM_GETSTATE, ref abd));
        }
    }

    /// <summary>記録があれば純正タスクバーを元の状態に戻して表示する。記録がなければ何もしない。</summary>
    public static bool Restore()
    {
        RestoreStartAlignment();
        lock (Gate)
        {
            int state = LoadPersisted();
            if (state < 0) return false;
            var main = Win32.FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero)
            {
                var abd = Win32.APPBARDATA.Create(main);
                abd.lParam = (IntPtr)state;
                Win32.SHAppBarMessage(Win32.ABM_SETSTATE, ref abd);
                foreach (var h in TrayWindows()) Win32.ShowWindow(h, Win32.SW_SHOWNOACTIVATE);
            }
            // トレイを預かっていた場合に備え、アプリにアイコンを explorer へ登録し直させる
            Win32.PostMessage((IntPtr)0xFFFF /*HWND_BROADCAST*/, Win32.RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
            Persist(-1);
            Log.Info("native taskbar restored");
            return true;
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
    // ---- Windows のタスクバーの配置（Windows 標準のスタートメニューを開く位置もこれに従う） ----
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private static string AlignFile => System.IO.Path.Combine(Settings.Directory, "taskbar-align.txt");

    /// <summary>
    /// Windows 標準のスタートを左/中央のどちらに開くか（= Windows の「タスクバーの配置」）を変える。
    /// ModernShell のタスクバー使用中だけ使い、元の値は記録しておいて終了時・ウォッチドッグで戻す。
    /// </summary>
    public static void SetStartAlignment(bool center)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AdvancedKey);
            int current = key.GetValue("TaskbarAl") is int v ? v : 1;
            int want = center ? 1 : 0;
            if (current == want) return;
            if (!System.IO.File.Exists(AlignFile))
            {
                System.IO.Directory.CreateDirectory(Settings.Directory);
                System.IO.File.WriteAllText(AlignFile, current.ToString());
            }
            key.SetValue("TaskbarAl", want, Microsoft.Win32.RegistryValueKind.DWord);
            // 全ウィンドウへの通知は 1 つにつき最大 1 秒待つので、開いているウィンドウが多いと数秒〜十数秒かかる。
            // タスクバーのスレッドで待つと固まる（起動直後に砂時計・クリックできない）ので、別スレッドで送る
            System.Threading.Tasks.Task.Run(NotifyTraySettings);
        }
        catch (Exception ex) { Log.Error("start alignment", ex); }
    }

    /// <summary>ModernShell が変えた「タスクバーの配置」を元に戻す。</summary>
    public static void RestoreStartAlignment()
    {
        try
        {
            if (!System.IO.File.Exists(AlignFile)) return;
            int original = int.Parse(System.IO.File.ReadAllText(AlignFile));
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AdvancedKey);
            key.SetValue("TaskbarAl", original, Microsoft.Win32.RegistryValueKind.DWord);
            System.IO.File.Delete(AlignFile);
            NotifyTraySettings();
        }
        catch (Exception ex) { Log.Error("restore start alignment", ex); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    /// <summary>explorer に設定の変更を知らせる（Windows の設定アプリで切り替えたときと同じ通知）。</summary>
    private static void NotifyTraySettings()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SendMessageTimeout((IntPtr)0xFFFF, Win32.WM_SETTINGCHANGE, IntPtr.Zero, "TraySettings", 0x2 /*SMTO_ABORTIFHUNG*/, 1000, out _);
        Log.Info($"timing: tray settings broadcast took {sw.ElapsedMilliseconds} ms");
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
