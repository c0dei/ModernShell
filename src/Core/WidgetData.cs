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
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static Report? _cache;
    private static string? _cacheCity;

    static Weather()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ModernShell/0.2");
    }

    public static async Task<Report?> GetAsync(string city)
    {
        if (string.IsNullOrWhiteSpace(city)) return null;
        if (_cache != null && _cacheCity == city && DateTime.Now - _cache.FetchedAt < TimeSpan.FromMinutes(20)) return _cache;
        try
        {
            var geoUrl = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=ja&format=json&name=" + Uri.EscapeDataString(city.Trim());
            using var geo = JsonDocument.Parse(await Http.GetStringAsync(geoUrl));
            if (!geo.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
            var place = results[0];
            double lat = place.GetProperty("latitude").GetDouble(), lon = place.GetProperty("longitude").GetDouble();
            string name = place.TryGetProperty("name", out var n) ? n.GetString() ?? city : city;

            var url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            _cache = new Report(name,
                cur.GetProperty("temperature_2m").GetDouble(),
                daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                cur.GetProperty("weather_code").GetInt32(),
                DateTime.Now);
            _cacheCity = city;
            return _cache;
        }
        catch (Exception ex)
        {
            Log.Error("weather", ex);
            return null;
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

    public (double percent, double freeGb) SystemDrive()
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            return (100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize, d.AvailableFreeSpace / 1073741824.0);
        }
        catch { return (0, 0); }
    }
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
