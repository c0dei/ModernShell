using System;
using System.Collections.Generic;
using System.Linq;
using ManagedShell;
using ManagedShell.Common.Enums;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;

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
        // 強制終了されても次回起動時 / ウォッチドッグで元に戻せるよう、純正タスクバーの状態を先に記録
        NativeTaskbar.RememberOriginalState();

        var config = ShellManager.DefaultShellConfig;
        config.TaskIconSize = IconSize.Large;
        config.PinnedNotifyIcons = settings.PinnedTrayIcons.ToArray();
        Manager = new ShellManager(config);
        Manager.ExplorerHelper.HideExplorerTaskbar = true;
        Log.Info($"shell host started (tray failed={Manager.NotificationArea?.IsFailed})");
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
