using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using ModernShell.Core;
using ModernShell.Native;
using ModernShell.UI;
using Forms = System.Windows.Forms;

namespace ModernShell;

/// <summary>
/// エントリポイント。
/// 安定性のための原則:
///  1. explorer.exe へ DLL/コードを注入しない（Windhawk / ExplorerPatcher 型のクラッシュを避ける）
///  2. ModernShell 側が落ちても explorer は無傷。純正タスクバーは
///     終了時 / ウォッチドッグ（強制終了時）/ 次回起動時 / Ctrl+Alt+Shift+F12 / --restore のどれかで必ず戻る
///  3. 例外はログに記録して握りつぶし、常駐を継続する
/// </summary>
public static class Program
{
    private const string MutexName = @"Local\ModernShell.SingleInstance";
    private const string ShowSettingsEvent = @"Local\ModernShell.ShowSettings";

    private static Settings _settings = null!;
    private static AppCatalog _catalog = null!;
    private static ShellHost? _shell;
    private static InputHooks _hooks = null!;
    private static StartMenuWindow _start = null!;
    private static TaskbarController? _taskbar;
    private static SettingsWindow? _settingsWindow;
    private static Forms.NotifyIcon? _tray;
    private static Timer? _startButtonWatcher;
    private static string _themeKey = "";
    private static bool _customTaskbarApplied;
    /// <summary>このプロセスで一度でも ManagedShell を起動したか（同じプロセスでは 2 回目の起動ができない）。</summary>
    private static bool _shellStartedOnce;
    private static bool _openSettingsOnStart;

    private const int HotkeyEmergencyId = 0x4D53;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--restore", StringComparer.OrdinalIgnoreCase))
        {
            // 緊急用: 強制終了等で純正タスクバーが隠れたままになったときに実行
            NativeTaskbar.Restore();
            return 0;
        }

        int wd = Array.FindIndex(args, a => a.Equals("--watchdog", StringComparison.OrdinalIgnoreCase));
        if (wd >= 0 && wd + 1 < args.Length && int.TryParse(args[wd + 1], out int parentPid))
        {
            RunWatchdog(parentPid);
            return 0;
        }

        // 再起動時は前のプロセスの終了を待ってから二重起動チェックをする
        int after = Array.FindIndex(args, a => a.Equals("--after", StringComparison.OrdinalIgnoreCase));
        if (after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out int prevPid))
        {
            try { using var prev = Process.GetProcessById(prevPid); prev.WaitForExit(15000); } catch { }
        }
        _openSettingsOnStart = args.Contains("--settings", StringComparer.OrdinalIgnoreCase);

        using var mutex = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            // 既に起動中 → 既存インスタンスに設定画面を開かせる
            try { EventWaitHandle.OpenExisting(ShowSettingsEvent).Set(); } catch { }
            return 0;
        }

        // 前回クラッシュで隠れたままの純正タスクバーがあれば戻しておく
        NativeTaskbar.Restore();
        StartWatchdog();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("unhandled", (Exception)e.ExceptionObject);
            NativeTaskbar.Restore();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => NativeTaskbar.Restore();

        // トレイメニュー（WinForms）経由の例外でも .NET のエラーダイアログを出さず、ログに記録して続行する
        Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.CatchException);
        Forms.Application.ThreadException += (_, e) => Log.Error("winforms", e.Exception);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("dispatcher", e.Exception);
            e.Handled = true; // 1 つの UI 例外で常駐ごと落とさない
        };
        app.Startup += (_, _) => Start(app);
        app.Exit += (_, _) => Cleanup();
        return app.Run();
    }

    private static void Start(Application app)
    {
        Log.Info("start");
        _settings = Settings.Load();
        Theme.Apply(_settings);
        _themeKey = ThemeKey();

        _catalog = new AppCatalog();
        _hooks = new InputHooks(app.Dispatcher) { InterceptWinKey = () => _settings.ReplaceStartMenu };

        _start = new StartMenuWindow(_settings, _catalog) { AnchorProvider = StartAnchor };
        _start.OpenSettingsRequested += ShowSettings;
        _start.ExitRequested += Exit;
        HwndSource.FromHwnd(_start.Handle)!.AddHook(BroadcastHook);
        // 緊急用ホットキー: Ctrl+Alt+Shift+F12 で純正タスクバーに戻して終了
        if (!RegisterHotKey(_start.Handle, HotkeyEmergencyId, 0x0001 | 0x0002 | 0x0004 | 0x4000 /*ALT|CTRL|SHIFT|NOREPEAT*/, 0x7B /*F12*/))
            Log.Info("emergency hotkey registration failed");

        _hooks.StartRequested += () => _start.Toggle();
        // メニューを Esc / 他の場所のクリックで閉じる（タスクバーはアクティブにならないので全体の入力を見て判断する）
        _hooks.IsMenuOpen = () => FluentSurface.AnyOpen;
        _hooks.MenuMouseDown += FluentSurface.CloseIfOutside;
        _hooks.MenuEscape += FluentSurface.CloseTopmost;
        _hooks.Install();

        _ = _catalog.LoadAsync().ContinueWith(_ => app.Dispatcher.BeginInvoke(() => { _catalog.Watch(); _catalog.NotifyChanged(); }));

        ApplyTaskbarMode();

        // 純正スタートボタン位置の監視（純正タスクバー使用時のみ意味がある）
        _startButtonWatcher = NativeTaskbar.WatchStartButton(
            r => _hooks.NativeStartButtonRect = r,
            () => _settings.ReplaceStartMenu && _settings.InterceptNativeStartButton && !_settings.UseCustomTaskbar);

        _settings.Changed += () => app.Dispatcher.BeginInvoke(OnSettingsChanged);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                app.Dispatcher.BeginInvoke(() => Theme.Apply(_settings));
        };
        // スリープ復帰・ロック解除後は低レベルフックが外れていることがあるので付け直す
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) app.Dispatcher.BeginInvoke(_hooks.Reinstall); };
        SystemEvents.SessionSwitch += (_, e) => { if (e.Reason == SessionSwitchReason.SessionUnlock) app.Dispatcher.BeginInvoke(_hooks.Reinstall); };

        var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEvent);
        new Thread(() => { while (showSettings.WaitOne()) app.Dispatcher.BeginInvoke(ShowSettings); }) { IsBackground = true }.Start();

        CreateTrayIcon();
        if (_openSettingsOnStart) app.Dispatcher.BeginInvoke(ShowSettings, DispatcherPriority.ApplicationIdle);
    }

    private static void Restart(bool openSettings)
    {
        try
        {
            var args = $"--after {Environment.ProcessId}" + (openSettings ? " --settings" : "");
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false });
            Exit();
        }
        catch (Exception ex) { Log.Error("restart", ex); }
    }

    private static string ThemeKey() => $"{_settings.Theme}|{_settings.Backdrop}|{_settings.AccentColor}|{_settings.CornerRadius}";

    private static void OnSettingsChanged()
    {
        ApplyTaskbarMode();
        var key = ThemeKey();
        if (key != _themeKey) { _themeKey = key; Theme.Apply(_settings); }
    }

    private static void ApplyTaskbarMode()
    {
        if (_settings.UseCustomTaskbar == _customTaskbarApplied) return;
        _customTaskbarApplied = _settings.UseCustomTaskbar;

        if (_settings.UseCustomTaskbar)
        {
            if (_shellStartedOnce)
            {
                // ManagedShell は一度終了すると同じプロセスで再初期化できないため、自分を再起動して有効化する
                Log.Info("restarting to re-enable custom taskbar");
                Restart(openSettings: _settingsWindow != null);
                return;
            }
            _shellStartedOnce = true;
            try
            {
                _shell = new ShellHost(_settings);
            }
            catch (Exception ex)
            {
                // シェル機能が起動できない環境では純正タスクバーのまま続行する
                Log.Error("shell host", ex);
                _shell?.Dispose();
                _shell = null;
                _customTaskbarApplied = false;
                NativeTaskbar.Restore();
                return;
            }
            _taskbar = new TaskbarController(_settings, _catalog, _shell, Application.Current.Dispatcher);
            _taskbar.StartClicked += () => _start.Toggle();
            _taskbar.SearchClicked += () => _start.ShowMenu("");
            _taskbar.OpenSettingsRequested += ShowSettings;
            _taskbar.ExitRequested += Exit;
            _taskbar.Start();
        }
        else
        {
            _taskbar?.Dispose();
            _taskbar = null;
            _shell?.Dispose();
            _shell = null;
        }
    }

    /// <summary>スタートメニューを出す基準位置（タスクバー矩形・上下・中央寄せ）。</summary>
    private static (Win32.RECT bar, bool top, bool center)? StartAnchor()
    {
        if (_taskbar != null)
            return (_taskbar.BarRect, _taskbar.IsTop, _taskbar.StartCentered);

        if (NativeTaskbar.GetTaskbarRect() is not { } r) return null;
        bool center = true;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            if (k?.GetValue("TaskbarAl") is int al) center = al == 1;
            else center = Environment.OSVersion.Version.Build >= 22000;
        }
        catch { }
        var screenH = (int)(SystemParameters.PrimaryScreenHeight * (Win32.GetDpiForWindow(_start.Handle) / 96.0));
        bool top = r.Top <= 0 && r.Bottom < screenH / 2;
        return (r, top, center);
    }

    private static uint _taskbarCreatedMsg;

    /// <summary>explorer 再起動時の "TaskbarCreated" ブロードキャストを受けて状態を再適用。</summary>
    private static IntPtr BroadcastHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMsg == 0) _taskbarCreatedMsg = Win32.RegisterWindowMessage("TaskbarCreated");
        if (msg == _taskbarCreatedMsg)
        {
            // 純正タスクバーの再隠しは ManagedShell が監視して行う。こちらは作業領域の予約だけやり直す
            Log.Info("TaskbarCreated");
            _taskbar?.Reapply();
        }
        else if (msg == 0x0312 /*WM_HOTKEY*/ && (int)wParam == HotkeyEmergencyId)
        {
            Log.Info("emergency hotkey");
            Exit();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void CreateTrayIcon()
    {
        try
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("スタートを開く", null, (_, _) => _start.ShowMenu());
            menu.Items.Add("設定", null, (_, _) => ShowSettings());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("純正タスクバーに戻して終了", null, (_, _) => Exit());

            _tray = new Forms.NotifyIcon
            {
                Text = "ModernShell",
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _start.Toggle(); };
        }
        catch (Exception ex) { Log.Error("tray", ex); }
    }

    private static void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings);
            _settingsWindow.ImportWindowsPins = () =>
            {
                using var pins = new WindowsPins(_catalog);
                return pins.Apps.Select(x => x.Id).ToList();
            };
            _settingsWindow.ExitRequested += Exit;
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        _settingsWindow.Activate();
        Foregrounder.Force(new WindowInteropHelper(_settingsWindow).Handle);
    }

    private static void Exit() => Application.Current.Shutdown();

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// 自分自身を「--watchdog 親PID」で起動しておく。本体が強制終了・クラッシュしても、
    /// ウォッチドッグが純正タスクバーとトレイアイコンを explorer に戻す。
    /// </summary>
    private static void StartWatchdog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--watchdog {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) { Log.Error("watchdog start", ex); }
    }

    private static void RunWatchdog(int parentPid)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            parent.WaitForExit();
        }
        catch { /* 既に終了している */ }
        // 正常終了なら記録は消えているので何もしない。記録が残っていれば元に戻す
        if (NativeTaskbar.Restore()) Log.Info("watchdog restored native taskbar");
    }

    private static void Cleanup()
    {
        Log.Info("exit");
        try { UnregisterHotKey(_start.Handle, HotkeyEmergencyId); } catch { }
        try { _hooks?.Dispose(); } catch { }
        try { _startButtonWatcher?.Dispose(); } catch { }
        try { _taskbar?.Dispose(); } catch { }
        try { _shell?.Dispose(); } catch { }
        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
        NativeTaskbar.Restore();
    }
}
