using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WN = global::Windows.UI.Notifications;
using WNM = global::Windows.UI.Notifications.Management;

namespace ModernShell.Core;

/// <summary>通知センターに溜まっている通知 1 件。</summary>
internal sealed record NotificationItem(uint Id, string AppId, string AppName, string Title, string Body, DateTime Time);

/// <summary>
/// Windows の通知（通知センターの一覧）の入り口。一覧の読み取り・1 件ずつ/まとめての消去・「応答不可」の状態の読み取り（切り替えは Windows の通知設定で行う）。
/// 一覧の読み取りには、Windows の「通知へのアクセス」の許可が要る（許可されていないときは <see cref="Access"/> が Allowed 以外になる）。
/// </summary>
internal static class NotificationControl
{
    private static WNM.UserNotificationListener? _listener;
    private static Task<bool>? _init;
    private static bool _allowed;
    private static int _busy;
    private static readonly Dictionary<string, BitmapSource?> Logos = new();

    /// <summary>通知の数（読めないときは -1）。バーのバッジに使う。</summary>
    public static int Count { get; private set; } = -1;

    /// <summary>通知が増減した（呼ばれるスレッドは UI とは限らない）。</summary>
    public static event Action? Changed;

    public static bool Allowed => _allowed;

    /// <summary>通知へのアクセスを許可してもらう（1 度だけ。すでに許可されていれば確認は出ない）。</summary>
    public static Task<bool> EnsureAsync() => _init ??= InitAsync();

    private static async Task<bool> InitAsync()
    {
        try
        {
            _listener = WNM.UserNotificationListener.Current;
            var st = await _listener.RequestAccessAsync();
            _allowed = st == WNM.UserNotificationListenerAccessStatus.Allowed;
            if (_allowed)
            {
                // パッケージ化されていないアプリでは届かないことがあるので、一覧は定期的な読み直しでも追う
                try { _listener.NotificationChanged += (_, _) => { _ = RefreshCountAsync(); }; }
                catch (Exception ex) { Log.Info("notification changed event unavailable: " + ex.Message); }
            }
            else Log.Info("notification access: " + st);
        }
        catch (Exception ex) { Log.Error("notification listener", ex); _allowed = false; }
        return _allowed;
    }

    /// <summary>通知の数だけ読み直す（バッジ用）。</summary>
    public static async Task RefreshCountAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            int n = -1;
            if (await EnsureAsync() && _listener != null)
            {
                var list = await _listener.GetNotificationsAsync(WN.NotificationKinds.Toast);
                n = list.Count;
            }
            if (n != Count) { Count = n; Changed?.Invoke(); }
        }
        catch (Exception ex) { Log.Error("notification count", ex); }
        finally { _busy = 0; }
    }

    /// <summary>通知の一覧（新しい順）。読めないときは null。</summary>
    public static async Task<List<NotificationItem>?> GetAsync()
    {
        try
        {
            if (!await EnsureAsync() || _listener == null) return null;
            var list = await _listener.GetNotificationsAsync(WN.NotificationKinds.Toast);
            var items = new List<NotificationItem>();
            foreach (var n in list)
            {
                string title = "", body = "";
                try
                {
                    var binding = n.Notification.Visual.GetBinding(WN.KnownNotificationBindings.ToastGeneric);
                    var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
                    if (texts.Count > 0) title = texts[0];
                    if (texts.Count > 1) body = string.Join("\n", texts.Skip(1));
                }
                catch { }
                string app = "";
                try { app = n.AppInfo.DisplayInfo.DisplayName; } catch { }
                string appId = "";
                try { appId = n.AppInfo.AppUserModelId; } catch { }
                items.Add(new NotificationItem(n.Id, appId, app, title, body, n.CreationTime.LocalDateTime));
            }
            Count = items.Count;
            return items.OrderByDescending(i => i.Time).ToList();
        }
        catch (Exception ex) { Log.Error("notification list", ex); return null; }
    }

    public static void Remove(uint id)
    {
        try { _listener?.RemoveNotification(id); if (Count > 0) Count--; Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("notification remove", ex); }
    }

    /// <summary>
    /// すべて消す。<c>ClearNotifications</c> はパッケージ化されていないアプリでは例外になるので、1 件ずつ消す。
    /// </summary>
    public static async Task ClearAllAsync()
    {
        try
        {
            if (!await EnsureAsync() || _listener == null) return;
            var list = await _listener.GetNotificationsAsync(WN.NotificationKinds.Toast);
            foreach (var n in list)
            {
                try { _listener.RemoveNotification(n.Id); }
                catch (Exception ex) { Log.Error("notification remove " + n.Id, ex); }
            }
            Count = 0;
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Error("notification clear", ex); }
    }

    /// <summary>通知元アプリのアイコン（取れなければ null）。</summary>
    public static async Task<BitmapSource?> LogoAsync(NotificationItem item)
    {
        if (string.IsNullOrEmpty(item.AppId)) return null;
        if (Logos.TryGetValue(item.AppId, out var cached)) return cached;
        BitmapSource? bmp = null;
        try
        {
            var list = await _listener!.GetNotificationsAsync(WN.NotificationKinds.Toast);
            var src = list.FirstOrDefault(n => n.Id == item.Id) ?? list.FirstOrDefault(n => n.AppInfo.AppUserModelId == item.AppId);
            if (src != null)
            {
                var stream = await src.AppInfo.DisplayInfo.GetLogo(new global::Windows.Foundation.Size(48, 48)).OpenReadAsync();
                using var net = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream);
                var ms = new MemoryStream();
                await net.CopyToAsync(ms);
                ms.Position = 0;
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                bmp = img;
            }
        }
        catch { }
        Logos[item.AppId] = bmp;
        return bmp;
    }

    // ---- 応答不可 ----

    private const string CloudRoot = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current";
    private const string QuietSuffix = "$windows.data.donotdisturb.quiethourssettings";
    private static readonly byte[] UnrestrictedName = System.Text.Encoding.Unicode.GetBytes("Microsoft.QuietHoursProfile.Unrestricted");

    /// <summary>
    /// Windows の「応答不可」がオンか。Windows が持っている状態（CloudStore の「静かな時間」のプロファイル）を読むだけで、書き換えはしない
    /// （非公開の内部データで、外から書き換えても Windows は反映しない）。
    /// プロファイルが「制限なし」以外（優先通知のみ・アラームのみ）なら応答不可。読めないときは、通知の全体設定の値で見る。
    /// </summary>
    public static bool DoNotDisturb
    {
        get
        {
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(CloudRoot);
                if (root != null)
                {
                    foreach (var name in root.GetSubKeyNames())
                    {
                        if (!name.EndsWith(QuietSuffix, StringComparison.OrdinalIgnoreCase) || name.StartsWith("default$", StringComparison.OrdinalIgnoreCase)) continue;
                        using var sub = root.OpenSubKey(name + @"\windows.data.donotdisturb.quiethourssettings");
                        if (sub?.GetValue("Data") is byte[] data && data.Length > 0)
                            return !Contains(data, UnrestrictedName);
                    }
                }
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings");
                return k?.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED") is int v && v == 0;
            }
            catch { return false; }
        }
    }

    private static bool Contains(byte[] hay, byte[] needle)
    {
        for (int i = 0; i <= hay.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    public const string SettingsUri = "ms-settings:notifications";
    public const string AccessSettingsUri = "ms-settings:privacy-notifications";
}
