using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WF = global::Windows.Devices.WiFi;
using WN = global::Windows.Networking.Connectivity;
using WR = global::Windows.Devices.Radios;

namespace ModernShell.Core;

/// <summary>近くの Wi-Fi 1 件（同じ名前のアクセスポイントは、電波の強い 1 件にまとめる）。</summary>
internal sealed record WifiNet(string Ssid, int Bars, bool Secure, double GHz);

/// <summary>いまのネットワークの状態。</summary>
internal sealed class NetStatus
{
    public bool HasWifi;
    public bool RadioOn = true;
    public bool Connected;
    public bool Wired;
    public bool Secure;
    public string Name = "";
    /// <summary>電波の強さ 0–4。</summary>
    public int Bars;
    public long Mbps;
    public double GHz;
}

/// <summary>
/// Windows のネットワークの入り口（接続中のもの・近くの Wi-Fi・Wi-Fi のオン/オフ・接続/切断）。
/// 変化は OS のイベント（NetworkStatusChanged）で受け取る。電波の強さの細かい変化はイベントが来ないので、呼び出し側が時々読み直す。
/// </summary>
internal static class NetworkControl
{
    private static WF.WiFiAdapter? _adapter;
    private static bool _hooked;

    /// <summary>位置情報の許可が無くて、Wi-Fi の一覧を取れない（Windows の仕様: 近くの Wi-Fi の取得には位置情報が要る）。</summary>
    public static bool Denied { get; private set; }

    /// <summary>接続・切断・ネットワークの切り替わり（OS から届く。呼ばれるスレッドは UI とは限らない）。</summary>
    public static event Action? Changed;

    public static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        try { WN.NetworkInformation.NetworkStatusChanged += _ => Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("network hook", ex); _hooked = false; }
    }

    private static async Task<WF.WiFiAdapter?> AdapterAsync()
    {
        if (_adapter != null) return _adapter;
        try
        {
            var access = await WF.WiFiAdapter.RequestAccessAsync();
            if (access != WF.WiFiAccessStatus.Allowed) { Denied = true; return null; }
            Denied = false;
            var all = await WF.WiFiAdapter.FindAllAdaptersAsync();
            _adapter = all.FirstOrDefault();
        }
        catch (Exception ex) { Log.Error("wifi adapter", ex); }
        return _adapter;
    }

    private static bool IsSecure(WN.NetworkSecuritySettings? s) =>
        s != null && s.NetworkAuthenticationType is not (WN.NetworkAuthenticationType.None or WN.NetworkAuthenticationType.Unknown or WN.NetworkAuthenticationType.Open80211);

    private static int ToBars(int raw) => raw >= 5 ? 4 : Math.Clamp(raw, 0, 4);

    private static double Ghz(int khz) => khz <= 0 ? 0 : khz < 3_000_000 ? 2.4 : khz < 5_900_000 ? 5 : 6;

    private static async Task<WR.Radio?> WifiRadioAsync()
    {
        try
        {
            var radios = await WR.Radio.GetRadiosAsync();
            return radios.FirstOrDefault(r => r.Kind == WR.RadioKind.WiFi);
        }
        catch { return null; }
    }

    /// <summary>いまの状態（接続先・電波・速度・Wi-Fi のオン/オフ）。</summary>
    public static async Task<NetStatus> GetStatusAsync()
    {
        var s = new NetStatus();
        var radio = await WifiRadioAsync();
        s.HasWifi = radio != null;
        s.RadioOn = radio == null || radio.State == WR.RadioState.On;
        try
        {
            var p = WN.NetworkInformation.GetInternetConnectionProfile()
                ?? WN.NetworkInformation.GetConnectionProfiles().FirstOrDefault(x => x.GetNetworkConnectivityLevel() != WN.NetworkConnectivityLevel.None);
            if (p != null)
            {
                s.Connected = true;
                s.Wired = !p.IsWlanConnectionProfile;
                s.Name = s.Wired ? p.ProfileName : (p.WlanConnectionProfileDetails?.GetConnectedSsid() is { Length: > 0 } ssid ? ssid : p.ProfileName);
                s.Secure = IsSecure(p.NetworkSecuritySettings);
                // 信号は 0–5 の段階で返る。4 本で表す
                s.Bars = ToBars(p.GetSignalBars() ?? 0);
                var ad = p.NetworkAdapter;
                if (ad != null) s.Mbps = (long)(Math.Max(ad.OutboundMaxBitsPerSecond, ad.InboundMaxBitsPerSecond) / 1_000_000);
                if (!s.Wired && _adapter?.NetworkReport?.AvailableNetworks is { } list)
                {
                    var me = list.Where(n => n.Ssid == s.Name).OrderByDescending(n => n.SignalBars).FirstOrDefault();
                    if (me != null) s.GHz = Ghz(me.ChannelCenterFrequencyInKilohertz);
                }
            }
        }
        catch (Exception ex) { Log.Error("network status", ex); }
        return s;
    }

    /// <summary>近くの Wi-Fi の一覧。rescan なら、先に探し直す（数秒かかる）。許可が無い・アダプターが無いときは空。</summary>
    public static async Task<List<WifiNet>> NetworksAsync(bool rescan)
    {
        var ad = await AdapterAsync();
        if (ad == null) return new();
        if (rescan) { try { await ad.ScanAsync(); } catch (Exception ex) { Log.Error("wifi scan", ex); } }
        try
        {
            return ad.NetworkReport.AvailableNetworks
                .Where(n => !string.IsNullOrEmpty(n.Ssid))
                .GroupBy(n => n.Ssid)
                .Select(g => g.OrderByDescending(n => n.SignalBars).First())
                .Select(n => new WifiNet(n.Ssid, ToBars(n.SignalBars), IsSecure(n.SecuritySettings), Ghz(n.ChannelCenterFrequencyInKilohertz)))
                .ToList();
        }
        catch (Exception ex) { Log.Error("wifi list", ex); return new(); }
    }

    /// <summary>保存済み・オープンなネットワークへ接続する。パスワードが要る新規のものは失敗する（呼び出し側が Windows の画面へ案内する）。</summary>
    public static async Task<bool> ConnectAsync(string ssid)
    {
        var ad = await AdapterAsync();
        if (ad == null) return false;
        try
        {
            var net = ad.NetworkReport.AvailableNetworks.Where(n => n.Ssid == ssid).OrderByDescending(n => n.SignalBars).FirstOrDefault();
            if (net == null) return false;
            var r = await ad.ConnectAsync(net, WF.WiFiReconnectionKind.Automatic);
            return r.ConnectionStatus == WF.WiFiConnectionStatus.Success;
        }
        catch (Exception ex) { Log.Error("wifi connect", ex); return false; }
    }

    public static void Disconnect()
    {
        try { _adapter?.Disconnect(); } catch (Exception ex) { Log.Error("wifi disconnect", ex); }
    }

    /// <summary>Wi-Fi のオン/オフ。成功したか。</summary>
    public static async Task<bool> SetRadioAsync(bool on)
    {
        try
        {
            if (await WR.Radio.RequestAccessAsync() != WR.RadioAccessStatus.Allowed) return false;
            var r = await WifiRadioAsync();
            if (r == null) return false;
            return await r.SetStateAsync(on ? WR.RadioState.On : WR.RadioState.Off) == WR.RadioAccessStatus.Allowed;
        }
        catch (Exception ex) { Log.Error("wifi radio", ex); return false; }
    }
}
