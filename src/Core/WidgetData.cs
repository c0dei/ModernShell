using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModernShell.Core;

/// <summary>天気（Open-Meteo: API キー不要・無料）。都市名 → 緯度経度 → 現在の天気。結果は 20 分キャッシュ。</summary>
public static class Weather
{
    public sealed record Report(string Place, double Temperature, double Max, double Min, int Code, DateTime FetchedAt)
    {
        public string Description => Describe(Code);
        public bool IsClear => Code is 0 or 1;
        /// <summary>詳細（体感温度・湿度・風・時間ごと・週間）。取れなかったときは null。</summary>
        public MoreInfo? More { get; init; }
    }

    public sealed record DayForecast(DateTime Date, int Code, double Max, double Min, int? Rain);
    public sealed record HourForecast(DateTime Time, int Code, double Temp, int? Rain);
    public sealed record MoreInfo(double? Feels, int? Humidity, double? Wind, List<DayForecast> Days, List<HourForecast> Hours, string? Sunrise, string? Sunset);

    /// <summary>天気コード → 記号の色（晴れは黄、曇りは青みの灰、雨は青、雪は水色、雷は紫）。一目で天気が分かるよう、アクセント色ではなく天気ごとの色にする。</summary>
    public static (byte r, byte g, byte b) SymbolRgb(int code) => code switch
    {
        0 or 1 => (0xF2, 0xA9, 0x00),
        2 => (0xE0, 0xA0, 0x30),
        3 or 45 or 48 => (0x8D, 0xA7, 0xC6),
        >= 51 and <= 67 or >= 80 and <= 82 => (0x3B, 0x8E, 0xEA),
        >= 71 and <= 77 or 85 or 86 => (0x5F, 0xC1, 0xE6),
        >= 95 => (0x8E, 0x63, 0xD6),
        _ => (0x8D, 0xA7, 0xC6),
    };

    /// <summary>天気コード → 小さな記号（Segoe UI Symbol の単色の文字）。</summary>
    public static string Symbol(int code) => code switch
    {
        0 or 1 => "\u2600",          // ☀
        2 => "\u26C5",               // ⛅
        3 or 45 or 48 => "\u2601",   // ☁
        >= 51 and <= 67 or >= 80 and <= 82 => "\u2602", // ☂
        >= 71 and <= 77 or 85 or 86 => "\u2744",        // ❄
        >= 95 => "\u26A1",           // ⚡
        _ => "\u2601",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static Report? _cache;
    private static string? _cacheCity;

    static Weather()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ModernShell/0.2");
    }

    private static TimeSpan Fresh => TimeSpan.FromMinutes(Math.Clamp(Settings.Current?.WeatherRefreshMinutes ?? 20, 5, 360));
    private static Task<Report?>? _inflight;
    private static string? _inflightCity;
    private static DateTime _failedAt = DateTime.MinValue;

    /// <summary>前回の取得結果（古くても返す）。表示を「読み込み中」にしないため、まずこれを出す。</summary>
    public static Report? Cached(string city) => _cache != null && _cacheCity == city ? _cache : null;

    /// <summary>取得結果が新しい（20 分以内）か。</summary>
    public static bool IsFresh(string city) => Cached(city) is { } c && DateTime.Now - c.FetchedAt < Fresh;

    /// <summary>
    /// 天気を返す。新しければキャッシュ、古ければ取りに行く。取得中に同じ地点がもう一度頼まれたら、同じ取得を待つ
    /// （スタートメニューを続けて開いても、通信は 1 回）。失敗した直後は 2 分のあいだ取りに行かない（オフラインで毎回待たされないように）。
    /// 取りに行って失敗したときは、古い結果があればそれを返す。
    /// </summary>
    public static Task<Report?> GetAsync(string city)
    {
        if (string.IsNullOrWhiteSpace(city)) return Task.FromResult<Report?>(null);
        if (IsFresh(city)) return Task.FromResult(Cached(city));
        if (DateTime.Now - _failedAt < TimeSpan.FromMinutes(2)) return Task.FromResult(Cached(city));
        if (_inflight is { IsCompleted: false } && _inflightCity == city) return _inflight;
        _inflightCity = city;
        return _inflight = FetchAsync(city);
    }

    /// <summary>手動の更新: 新しさやバックオフに関係なく取りに行く（取得中ならそれを待つ）。失敗したら古い結果を返す。</summary>
    public static Task<Report?> RefreshAsync(string city)
    {
        if (string.IsNullOrWhiteSpace(city)) return Task.FromResult<Report?>(null);
        if (_inflight is { IsCompleted: false } && _inflightCity == city) return _inflight;
        _inflightCity = city;
        return _inflight = FetchAsync(city);
    }

    public enum FailureKind { None, NotFound, Network }
    /// <summary>直近の取得に失敗した理由（画面のメッセージの出し分け用）。</summary>
    public static FailureKind LastFailure { get; private set; }

    /// <summary>一時的な不調に備えて、失敗したら少し待って 2 回まで取り直す（1 秒後、2 秒後）。</summary>
    private static async Task<string> GetWithRetryAsync(string url)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await Http.GetStringAsync(url); }
            catch (Exception ex) when (attempt < 2)
            {
                Log.Info($"weather: retry {attempt + 1} ({ex.GetType().Name})");
                await Task.Delay(1000 * (attempt + 1));
            }
        }
    }

    /// <summary>詳細（週間・時間ごと）を読み取る。形が想定と違っても、基本の天気は出せるよう、失敗したら null にする。</summary>
    private static MoreInfo? ParseMore(JsonElement root, JsonElement cur, JsonElement daily)
    {
        try
        {
            static double? D(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            var days = new List<DayForecast>();
            var dt = daily.GetProperty("time"); var codes = daily.GetProperty("weather_code");
            var mx = daily.GetProperty("temperature_2m_max"); var mn = daily.GetProperty("temperature_2m_min");
            daily.TryGetProperty("precipitation_probability_max", out var rain);
            for (int i = 0; i < dt.GetArrayLength(); i++)
                days.Add(new DayForecast(DateTime.Parse(dt[i].GetString()!, System.Globalization.CultureInfo.InvariantCulture), codes[i].GetInt32(), mx[i].GetDouble(), mn[i].GetDouble(),
                    rain.ValueKind == JsonValueKind.Array && rain[i].ValueKind == JsonValueKind.Number ? rain[i].GetInt32() : null));

            var hours = new List<HourForecast>();
            if (root.TryGetProperty("hourly", out var h))
            {
                var ht = h.GetProperty("time"); var hc = h.GetProperty("weather_code"); var htemp = h.GetProperty("temperature_2m");
                h.TryGetProperty("precipitation_probability", out var hr);
                var start = DateTime.Now.AddMinutes(-30); // 今の時間帯から
                for (int i = 0; i < ht.GetArrayLength() && hours.Count < 24; i++)
                {
                    var t = DateTime.Parse(ht[i].GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                    if (t < start) continue;
                    hours.Add(new HourForecast(t, hc[i].GetInt32(), htemp[i].GetDouble(), hr.ValueKind == JsonValueKind.Array && hr[i].ValueKind == JsonValueKind.Number ? hr[i].GetInt32() : null));
                }
            }
            string? Time(string name) => daily.TryGetProperty(name, out var v) && v.GetArrayLength() > 0 ? DateTime.Parse(v[0].GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToString("H:mm") : null;
            return new MoreInfo(D(cur, "apparent_temperature"), (int?)D(cur, "relative_humidity_2m"), D(cur, "wind_speed_10m"), days, hours, Time("sunrise"), Time("sunset"));
        }
        catch (Exception ex) { Log.Error("weather details", ex); return null; }
    }

    private static async Task<Report?> FetchAsync(string city)
    {
        try
        {
            var geoUrl = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=ja&format=json&name=" + Uri.EscapeDataString(city.Trim());
            LastFailure = FailureKind.None;
            using var geo = JsonDocument.Parse(await GetWithRetryAsync(geoUrl));
            if (!geo.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            {
                LastFailure = FailureKind.NotFound;
                _failedAt = DateTime.Now; // 地点が見つからない: 設定を直すまで毎回取りに行かない
                return null;
            }
            var place = results[0];
            double lat = place.GetProperty("latitude").GetDouble(), lon = place.GetProperty("longitude").GetDouble();
            string name = place.TryGetProperty("name", out var n) ? n.GetString() ?? city : city;

            var url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code,relative_humidity_2m,apparent_temperature,wind_speed_10m&hourly=temperature_2m,weather_code,precipitation_probability&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset&timezone=auto&forecast_days=7");
            using var doc = JsonDocument.Parse(await GetWithRetryAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            _cache = new Report(name,
                cur.GetProperty("temperature_2m").GetDouble(),
                daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                cur.GetProperty("weather_code").GetInt32(),
                DateTime.Now) { More = ParseMore(doc.RootElement, cur, daily) };
            _cacheCity = city;
            return _cache;
        }
        catch (Exception ex)
        {
            Log.Error("weather", ex);
            LastFailure = FailureKind.Network;
            _failedAt = DateTime.Now;
            return Cached(city); // 古い結果があればそれを返す
        }
    }

    /// <summary>WMO 天気コード → 日本語。</summary>
    public static string Describe(int code) => code switch
    {
        0 => L.T("快晴"),
        1 => L.T("晴れ"),
        2 => L.T("晴れ時々くもり"),
        3 => L.T("くもり"),
        45 or 48 => L.T("霧"),
        >= 51 and <= 57 => L.T("霧雨"),
        >= 61 and <= 67 => L.T("雨"),
        >= 71 and <= 77 => L.T("雪"),
        >= 80 and <= 82 => L.T("にわか雨"),
        85 or 86 => L.T("にわか雪"),
        >= 95 => L.T("雷雨"),
        _ => "—",
    };
}

/// <summary>CPU / メモリ / ディスク使用率。</summary>
public sealed class SystemStats
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    private ulong _lastIdle, _lastTotal;

    /// <summary>前回呼び出しからの CPU 使用率 (0–100)。</summary>
    public double CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        ulong total = kernel.Value + user.Value; // kernel には idle が含まれる
        ulong dTotal = total - _lastTotal, dIdle = idle.Value - _lastIdle;
        _lastTotal = total;
        _lastIdle = idle.Value;
        return dTotal == 0 ? 0 : Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
    }

    public (double percent, double usedGb, double totalGb) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        double total = m.ullTotalPhys / 1073741824.0, used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        return (m.dwMemoryLoad, used, total);
    }

    /// <summary>内蔵・外付けの固定ドライブのルート（"C:\\" など）。システムドライブを先頭に。</summary>
    public static List<string> FixedDrives()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .OrderBy(r => string.Equals(r, system, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(r => r)
                .ToList();
        }
        catch { return new List<string> { system }; }
    }

    /// <summary>ドライブの使用率・使用量・全体 (GB)。</summary>
    public static (double percent, double usedGb, double totalGb) Drive(string root)
    {
        try
        {
            var d = new DriveInfo(root);
            double used = d.TotalSize - d.AvailableFreeSpace;
            return (100.0 * used / d.TotalSize, used / 1073741824.0, d.TotalSize / 1073741824.0);
        }
        catch { return (0, 0, 0); }
    }

    public enum NetKind { None, Wifi, Ethernet, Other }

    private long _lastRx, _lastTx;
    private readonly System.Diagnostics.Stopwatch _netClock = new();

    /// <summary>
    /// インターネットにつながっているアダプター（既定のゲートウェイがあるもの）の、前回からの受信・送信の速さ (バイト/秒) と接続の種類。
    /// アダプターの一覧を読むのは数秒かかることがあるので、UI スレッドでは呼ばないこと。
    /// </summary>
    public (NetKind kind, double downBps, double upBps) Network()
    {
        long rx = 0, tx = 0;
        var kind = NetKind.None;
        long best = -1;
        try
        {
            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                var props = n.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue; // ネットにつながっていない（仮想スイッチなど）
                var s = n.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
                // いちばん通信量の多いアダプターの種類を「接続の種類」にする
                long total = s.BytesReceived + s.BytesSent;
                if (total > best)
                {
                    best = total;
                    kind = n.NetworkInterfaceType switch
                    {
                        System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 => NetKind.Wifi,
                        System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                            or System.Net.NetworkInformation.NetworkInterfaceType.GigabitEthernet
                            or System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetT
                            or System.Net.NetworkInformation.NetworkInterfaceType.FastEthernetFx => NetKind.Ethernet,
                        _ => NetKind.Other,
                    };
                }
            }
        }
        catch { }
        double sec = _netClock.IsRunning ? _netClock.Elapsed.TotalSeconds : 0;
        double down = sec > 0 && rx >= _lastRx ? (rx - _lastRx) / sec : 0;
        double up = sec > 0 && tx >= _lastTx ? (tx - _lastTx) / sec : 0;
        _lastRx = rx;
        _lastTx = tx;
        _netClock.Restart();
        return (kind, down, up);
    }

    /// <summary>速さを「1.2 MB/s」のように読みやすく。</summary>
    public static string Speed(double bytesPerSec) => bytesPerSec switch
    {
        >= 1024 * 1024 => $"{bytesPerSec / 1048576:0.0} MB/s",
        >= 1024 => $"{bytesPerSec / 1024:0} KB/s",
        _ => $"{bytesPerSec:0} B/s",
    };
}

public static class UserFolders
{
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    private static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string? DownloadsPath()
    {
        if (SHGetKnownFolderPath(Downloads, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>(表示名, Segoe Fluent Icons のグリフ, パス)</summary>
    public static IEnumerable<(string name, string glyph, string path)> Common()
    {
        var list = new List<(string, string, string?)>
        {
            (L.T("ドキュメント"), "\uE8A5", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            (L.T("ダウンロード"), "\uE896", DownloadsPath()),
            (L.T("ピクチャ"), "\uE91B", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            (L.T("ミュージック"), "\uE8D6", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            (L.T("ビデオ"), "\uE714", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
            (L.T("デスクトップ"), "\uE7F4", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
        };
        return list.Where(x => !string.IsNullOrEmpty(x.Item3) && Directory.Exists(x.Item3)).Select(x => (x.Item1, x.Item2, x.Item3!));
    }

    /// <summary>最近使ったファイル（%AppData%\Microsoft\Windows\Recent のショートカット）。フォルダは除く。</summary>
    public static List<(string name, string lnk)> Recent(int count)
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            return new DirectoryInfo(dir).EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => (name: Path.GetFileNameWithoutExtension(f.Name), lnk: f.FullName))
                .Where(x => Path.HasExtension(x.name)) // 拡張子のないもの = フォルダへのリンクが多いので除外
                .Take(count)
                .ToList();
        }
        catch { return new(); }
    }
}

public static class Notes
{
    private static string FilePath => Path.Combine(Settings.Directory, "notes.txt");

    public static string Load()
    {
        try { return File.Exists(FilePath) ? File.ReadAllText(FilePath) : ""; } catch { return ""; }
    }

    public static void Save(string text)
    {
        try { Directory.CreateDirectory(Settings.Directory); File.WriteAllText(FilePath, text); }
        catch (Exception ex) { Log.Error("notes save", ex); }
    }
}
