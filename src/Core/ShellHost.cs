using System;
using System.Collections.Generic;
using System.Linq;
using ManagedShell;
using ManagedShell.Common.Enums;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;
using ModernShell.Native;

namespace ModernShell.Core;

/// <summary>
/// 独自タスクバー使用中だけ動かす「シェル機能」一式（ManagedShell）。
///  - 通知領域（トレイアイコン）: 自前の Shell_TrayWnd を作り、アプリからのアイコン登録を受け取る。
///    explorer のトレイにある既存アイコンも引き取る。終了時は TaskbarCreated を流して explorer に返す
///  - タスク一覧: シェルフックで正確な「アクティブ」状態を追跡（クリックで最小化/復元の判定に使う）
///  - 純正タスクバーの非表示/復元
/// いずれも explorer.exe のプロセスにコードを入れない（RetroBar と同じ方式）。
/// </summary>
public sealed class ShellHost : IDisposable
{
    public ShellManager Manager { get; }
    public NotificationArea? Tray => Manager.NotificationArea;
    public Tasks? Tasks => Manager.Tasks;
    public TasksService? TasksService => Manager.TasksService;

    /// <summary>独自のクイック設定ボタンで代替するため、トレイには出さないシステムアイコン。</summary>
    public static readonly HashSet<string> HiddenSystemIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        NotificationArea.NETWORK_GUID, NotificationArea.VOLUME_GUID, NotificationArea.POWER_GUID,
    };

    private bool _disposed;

    public ShellHost(Settings settings)
    {
        StallDetector.Phase = "shell host (tray/tasks)";
        // 強制終了されても次回起動時 / ウォッチドッグで元に戻せるよう、純正タスクバーの状態を先に記録
        NativeTaskbar.RememberOriginalState();

        var config = ShellManager.DefaultShellConfig;
        config.TaskIconSize = IconSize.Large;
        config.PinnedNotifyIcons = settings.PinnedTrayIcons.ToArray();
        Manager = new ShellManager(config);
        Log.Info($"shell host started (tray failed={Manager.NotificationArea?.IsFailed})");
        if (Launcher.IsElevated) AllowTrayFromNormalApps();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr changeInfo);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SendNotifyMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// ModernShell が管理者として動いているとき、普通の権限のアプリからのトレイアイコンの登録を受け取れるようにする。
    /// アプリはトレイ用のウィンドウ（Shell_TrayWnd）に WM_COPYDATA を送ってアイコンを登録するが、管理者のウィンドウへの
    /// このメッセージは Windows が止めてしまう（UIPI）。ModernShell のトレイ用ウィンドウだけ受け取りを許可し、
    /// 「タスクバーができた」通知を送り直して、各アプリにアイコンを登録し直してもらう。
    /// </summary>
    private static void AllowTrayFromNormalApps()
    {
        try
        {
            const uint WM_COPYDATA = 0x004A, MSGFLT_ALLOW = 1;
            int allowed = 0;
            for (var h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null); h != IntPtr.Zero; h = FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null))
            {
                Win32.GetWindowThreadProcessId(h, out var pid);
                if (pid != (uint)Environment.ProcessId) continue;
                if (ChangeWindowMessageFilterEx(h, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero)) allowed++;
            }
            // 許可する前に送られた登録は止められているので、各アプリに登録し直してもらう
            SendNotifyMessage((IntPtr)0xFFFF, Win32.RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
            Log.Info($"elevated: allowed tray messages on {allowed} window(s)");
        }
        catch (Exception ex) { Log.Error("allow tray messages", ex); }
    }

    private bool _nativeHidden;

    /// <summary>
    /// 純正タスクバーを隠す。ModernShell のタスクバーが実際に画面に描かれてから呼ぶ。
    /// PC の起動直後などは最初の描画に数十秒かかることがあり、先に隠すとその間タスクバーが使えなくなるため。
    /// </summary>
    public void HideNativeTaskbar()
    {
        if (_disposed || _nativeHidden) return;
        _nativeHidden = true;
        Manager.ExplorerHelper.HideExplorerTaskbar = true;
        Log.Timing("native taskbar hidden");
    }

    /// <summary>現在タスクバーに固定されているトレイアイコンの識別子を設定に保存する。</summary>
    public void SavePinnedTray(Settings settings)
    {
        if (Tray?.PinnedNotifyIcons is { } ids)
        {
            settings.PinnedTrayIcons = ids.ToList();
            settings.SaveQuiet();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Manager.ExplorerHelper.HideExplorerTaskbar = false; } catch (Exception ex) { Log.Error("show explorer taskbar", ex); }
        try { Manager.Dispose(); } catch (Exception ex) { Log.Error("shell host dispose", ex); }
        NativeTaskbar.Restore();
        Log.Info("shell host stopped");
    }
}
