using System;
using System.Linq;
using System.Threading.Tasks;
using BT = global::Windows.Devices.Bluetooth;
using DE = global::Windows.Devices.Enumeration;
using WR = global::Windows.Devices.Radios;

namespace ModernShell.Core;

/// <summary>機内モード・Bluetooth の接続など、無線まわりの状態（クイック設定のウィジェット用）。</summary>
internal static class RadioControl
{
    private static DateTime _btAt = DateTime.MinValue;
    private static bool _btValue, _btBusy;

    /// <summary>機内モード（無線がひとつ以上あり、すべてオフ）。Windows は機内モードを無線のオフとして扱うので、そこから判断する。</summary>
    public static async Task<bool> AirplaneAsync()
    {
        try
        {
            var radios = await WR.Radio.GetRadiosAsync();
            return radios.Count > 0 && radios.All(r => r.State != WR.RadioState.On);
        }
        catch { return false; }
    }

    /// <summary>Bluetooth がオンで、つながっている機器がある。機器の列挙は重いので、5 秒に 1 回だけ調べ直す（それまでは前の結果を返す）。</summary>
    public static async Task<bool> BluetoothConnectedAsync()
    {
        if (DateTime.UtcNow - _btAt < TimeSpan.FromSeconds(5) || _btBusy) return _btValue;
        _btBusy = true;
        try
        {
            var radios = await WR.Radio.GetRadiosAsync();
            bool on = radios.Any(r => r.Kind == WR.RadioKind.Bluetooth && r.State == WR.RadioState.On);
            bool any = false;
            if (on)
            {
                var classic = await DE.DeviceInformation.FindAllAsync(BT.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BT.BluetoothConnectionStatus.Connected));
                any = classic.Count > 0;
                if (!any)
                {
                    var le = await DE.DeviceInformation.FindAllAsync(BT.BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BT.BluetoothConnectionStatus.Connected));
                    any = le.Count > 0;
                }
            }
            _btValue = any;
        }
        catch (Exception ex) { Log.Error("bluetooth status", ex); _btValue = false; }
        finally { _btAt = DateTime.UtcNow; _btBusy = false; }
        return _btValue;
    }
}
