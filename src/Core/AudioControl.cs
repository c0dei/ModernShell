using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ModernShell.Core;

/// <summary>出力デバイス 1 つ。</summary>
internal sealed record AudioDevice(string Id, string Name, string Sub, bool IsDefault);

/// <summary>音を出しているアプリ 1 つ（同じプロセスの複数セッションは 1 つにまとめる）。</summary>
internal sealed record AudioApp(uint Pid, string Name, string? ExePath, bool Active, int Volume, bool Muted);

/// <summary>
/// Windows の音量の入り口（Core Audio）。既定の出力デバイスの音量・ミュート、出力デバイスの一覧と切り替え、アプリごとの音量。
/// COM は UI スレッド（STA）から呼ぶ。失敗しても例外は出さず、既定値を返す。
/// </summary>
internal static class AudioControl
{
    private static readonly Guid Clsid_Enumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid Clsid_PolicyConfig = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid Iid_EndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid Iid_SessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static Guid _ctx = Guid.Empty;

    /// <summary>「システム音」のセッションを表す番号（実際のプロセス番号ではない）。</summary>
    public const uint SystemSounds = uint.MaxValue;

    private const int Render = 0, Console = 0, Multimedia = 1, Communications = 2, StateActive = 1;

    private static IMMDeviceEnumerator Enumerator() =>
        (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(Clsid_Enumerator)!)!;

    private static IMMDevice? DefaultDevice()
    {
        try { Enumerator().GetDefaultAudioEndpoint(Render, Multimedia, out var d); return d; }
        catch { return null; }
    }

    private static IAudioEndpointVolume? Endpoint()
    {
        try
        {
            var d = DefaultDevice();
            if (d == null) return null;
            var iid = Iid_EndpointVolume;
            d.Activate(ref iid, 23, IntPtr.Zero, out var o);
            return (IAudioEndpointVolume)o;
        }
        catch { return null; }
    }

    /// <summary>出力デバイスがあるか。</summary>
    public static bool Available => DefaultDevice() != null;

    /// <summary>音量 0–100。</summary>
    public static int GetVolume()
    {
        try { Endpoint()!.GetMasterVolumeLevelScalar(out var v); return (int)Math.Round(v * 100); }
        catch { return 0; }
    }

    public static bool GetMuted()
    {
        try { Endpoint()!.GetMute(out var m); return m; }
        catch { return false; }
    }

    public static void SetVolume(int percent)
    {
        try { Endpoint()!.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref _ctx); }
        catch (Exception ex) { Log.Error("audio set volume", ex); }
    }

    public static void SetMuted(bool muted)
    {
        try { Endpoint()!.SetMute(muted, ref _ctx); }
        catch (Exception ex) { Log.Error("audio set mute", ex); }
    }

    // ---- 出力デバイス ----

    public static List<AudioDevice> Devices()
    {
        var list = new List<AudioDevice>();
        try
        {
            var en = Enumerator();
            string defId = "";
            try { en.GetDefaultAudioEndpoint(Render, Multimedia, out var dd); dd.GetId(out defId); } catch { }
            en.EnumAudioEndpoints(Render, StateActive, out var col);
            col.GetCount(out var n);
            for (uint i = 0; i < n; i++)
            {
                col.Item(i, out var d);
                d.GetId(out var id);
                var (name, sub) = Names(d);
                list.Add(new AudioDevice(id, name, sub, id == defId));
            }
        }
        catch (Exception ex) { Log.Error("audio devices", ex); }
        // 名前の順（同じ名前なら機器名の順）
        list.Sort((a, b) =>
        {
            int c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            return c != 0 ? c : string.Compare(a.Sub, b.Sub, StringComparison.CurrentCultureIgnoreCase);
        });
        return list;
    }

    /// <summary>名前（「スピーカー」）と、その下に出す機器名（「内蔵オーディオ」）。</summary>
    private static (string name, string sub) Names(IMMDevice d)
    {
        string name = "", sub = "";
        try
        {
            d.OpenPropertyStore(0, out var ps);
            name = ReadString(ps, new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);
            sub = ReadString(ps, new Guid("026E516E-B814-414B-83CD-856D6FEF4822"), 2);
            if (name == "") name = ReadString(ps, new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
        }
        catch { }
        return (name, sub);
    }

    private static string ReadString(IPropertyStore ps, Guid fmt, uint pid)
    {
        var key = new PropertyKey { fmtid = fmt, pid = pid };
        ps.GetValue(ref key, out var v);
        try { return v.vt == 31 && v.p != IntPtr.Zero ? Marshal.PtrToStringUni(v.p) ?? "" : ""; }
        finally { PropVariantClear(ref v); }
    }

    public static string DefaultName(out string sub)
    {
        sub = "";
        var d = DefaultDevice();
        if (d == null) return "";
        var (name, s) = Names(d);
        sub = s;
        return name;
    }

    /// <summary>既定の出力デバイスを切り替える（Windows の設定と同じ。非公開の API を使う）。</summary>
    public static bool SetDefault(string id)
    {
        try
        {
            var pc = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(Clsid_PolicyConfig)!)!;
            pc.SetDefaultEndpoint(id, Console);
            pc.SetDefaultEndpoint(id, Multimedia);
            pc.SetDefaultEndpoint(id, Communications);
            return true;
        }
        catch (Exception ex) { Log.Error("audio set default", ex); return false; }
    }

    // ---- アプリごとの音量 ----

    public static List<AudioApp> Apps()
    {
        var map = new Dictionary<uint, AudioApp>();
        try
        {
            var d = DefaultDevice();
            if (d == null) return new();
            var iid = Iid_SessionManager2;
            d.Activate(ref iid, 23, IntPtr.Zero, out var o);
            var mgr = (IAudioSessionManager2)o;
            mgr.GetSessionEnumerator(out var en);
            en.GetCount(out var n);
            for (int i = 0; i < n; i++)
            {
                try
                {
                    en.GetSession(i, out var ctl);
                    var c2 = (IAudioSessionControl2)ctl;
                    c2.GetState(out var state);
                    bool system = c2.IsSystemSoundsSession() == 0; // S_OK = システム音
                    if (state == 2 && !system) continue; // 終了したセッション
                    uint pid = SystemSounds;
                    if (!system)
                    {
                        c2.GetProcessId(out pid);
                        if (pid == 0) continue;
                    }
                    var vol = (ISimpleAudioVolume)ctl;
                    vol.GetMasterVolume(out var v);
                    vol.GetMute(out var m);
                    bool active = state == 1;
                    if (map.TryGetValue(pid, out var prev))
                    {
                        map[pid] = prev with { Active = prev.Active || active };
                        continue;
                    }
                    string sn = "";
                    try { c2.GetDisplayName(out sn); } catch { }
                    var (name, path) = system ? (L.T("システム音"), (string?)null) : ProcessInfo(pid, sn ?? "");
                    if (name == null) continue; // 終了済みのプロセス。Windows の音量ミキサーにも出ない
                    map[pid] = new AudioApp(pid, name, path, active, (int)Math.Round(v * 100), m);
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Error("audio apps", ex); }
        var list = new List<AudioApp>(map.Values);
        list.Sort((a, b) => a.Pid == SystemSounds || b.Pid == SystemSounds ? (a.Pid == SystemSounds ? -1 : 1) : a.Active != b.Active ? (a.Active ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, System.Text.StringBuilder name, ref uint size);

    /// <summary>実行ファイルの場所。管理者権限のアプリでも、通常権限のまま読める「限定的な照会」（PROCESS_QUERY_LIMITED_INFORMATION）を使う。</summary>
    private static string? ImagePath(uint pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private static (string? name, string? path) ProcessInfo(uint pid, string sessionName)
    {
        string? path = ImagePath(pid);
        string? name = null;
        if (path != null)
        {
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) name = desc;
            }
            catch { }
            name ??= System.IO.Path.GetFileNameWithoutExtension(path);
        }
        // 場所が読めないとき: セッションの表示名 → プロセス名 → PID の順
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(sessionName) && !sessionName.StartsWith("@")) name = sessionName;
        if (string.IsNullOrWhiteSpace(name))
        {
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
            catch { return (null, null); } // もう終了したプロセスの残りのセッション
        }
        return (name, path);
    }

    /// <summary>アプリの音量を、同じプロセスのすべてのセッションに設定する。</summary>
    public static void SetAppVolume(uint pid, int percent)
    {
        ForSessions(pid, v => v.SetMasterVolume(Math.Clamp(percent, 0, 100) / 100f, ref _ctx));
    }

    public static void SetAppMuted(uint pid, bool muted) => ForSessions(pid, v => v.SetMute(muted, ref _ctx));

    private static void ForSessions(uint pid, Action<ISimpleAudioVolume> act)
    {
        try
        {
            var d = DefaultDevice();
            if (d == null) return;
            var iid = Iid_SessionManager2;
            d.Activate(ref iid, 23, IntPtr.Zero, out var o);
            ((IAudioSessionManager2)o).GetSessionEnumerator(out var en);
            en.GetCount(out var n);
            for (int i = 0; i < n; i++)
            {
                en.GetSession(i, out var ctl);
                var c2 = (IAudioSessionControl2)ctl;
                bool hit;
                if (pid == SystemSounds) hit = c2.IsSystemSoundsSession() == 0;
                else { c2.GetProcessId(out var p); hit = p == pid; }
                if (hit) act((ISimpleAudioVolume)ctl);
            }
        }
        catch (Exception ex) { Log.Error("audio app volume", ex); }
    }

    // ---- COM 宣言（メソッドの順番は vtable そのもの。変えない） ----

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pv);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr groupId, uint flags, out IAudioSessionControl2 ctl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupId, uint flags, out ISimpleAudioVolume vol);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator en);
        [PreserveSig] int RegisterSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterSessionNotification(IntPtr n);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr n);
        [PreserveSig] int UnregisterDuckNotification(IntPtr n);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility();
    }
}
