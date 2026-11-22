using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ModernShell.Core;

/// <summary>カレンダーの予定 1 件。日付と、あれば開始の時刻（H:mm）。</summary>
public sealed class CalendarEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>yyyy-MM-dd</summary>
    public string Date { get; set; } = "";
    /// <summary>H:mm（終日の予定は空）</summary>
    public string Time { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>カレンダーの予定の保存先（設定フォルダの calendar.json）。</summary>
public static class CalendarStore
{
    private static readonly object Gate = new();
    private static List<CalendarEvent>? _all;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string PathName => Path.Combine(Settings.Directory, "calendar.json");

    /// <summary>予定が変わるたびに増える（表示の作り直しの判定に使う）。</summary>
    public static int Version { get; private set; }

    private static List<CalendarEvent> All()
    {
        if (_all != null) return _all;
        try
        {
            if (File.Exists(PathName))
                _all = JsonSerializer.Deserialize<List<CalendarEvent>>(File.ReadAllText(PathName)) ?? new();
        }
        catch (Exception ex) { Log.Error("calendar load", ex); }
        return _all ??= new();
    }

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd");

    /// <summary>その日の予定（時刻のあるものは時刻順、終日のものは先頭）。</summary>
    public static List<CalendarEvent> On(DateTime day)
    {
        lock (Gate)
        {
            string k = Key(day);
            return All().Where(e => e.Date == k).OrderBy(e => TimeOf(e) ?? TimeSpan.MinValue).ThenBy(e => e.Title).ToList();
        }
    }

    public static bool Has(DateTime day) { lock (Gate) { string k = Key(day); return All().Any(e => e.Date == k); } }

    public static TimeSpan? TimeOf(CalendarEvent e) =>
        TimeSpan.TryParseExact(e.Time, @"h\:mm", null, out var t) ? t : null;

    /// <summary>入力された時刻（9:30 / 09:30 / 0930 / 9時30分 など）を H:mm にそろえる。空は終日、読めなければ null。</summary>
    public static string? NormalizeTime(string input)
    {
        input = input.Trim().Replace('：', ':').Replace("時", ":").Replace("分", "");
        if (input.Length == 0) return "";
        if (input.EndsWith(':')) input += "00";
        if (!input.Contains(':') && input.All(char.IsDigit) && input.Length is 3 or 4) input = input[..^2] + ":" + input[^2..];
        return TimeSpan.TryParse(input, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromHours(24) ? $"{t.Hours}:{t.Minutes:00}" : null;
    }

    public static void Add(DateTime day, string time, string title)
    {
        lock (Gate) All().Add(new CalendarEvent { Date = Key(day), Time = time, Title = title.Trim() });
        Save();
    }

    public static void Remove(string id)
    {
        lock (Gate) All().RemoveAll(e => e.Id == id);
        Save();
    }

    private static void Save()
    {
        Version++;
        try
        {
            Directory.CreateDirectory(Settings.Directory);
            string json;
            lock (Gate) json = JsonSerializer.Serialize(All(), Json);
            string tmp = PathName + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, PathName, true);
        }
        catch (Exception ex) { Log.Error("calendar save", ex); }
    }
}

/// <summary>
/// 日本の祝日。holidays-jp（内閣府のデータ）を取得して設定フォルダにキャッシュし、
/// 取得できない年・オフラインのときは計算（国民の祝日・振替休日・国民の休日）で補う。
/// </summary>
public static class JpHoliday
{
    private const string Url = "https://holidays-jp.github.io/api/v1/date.json";
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private static Dictionary<string, string>? _remote;
    private static bool _started;
    private static string _first = "", _last = "";
    private static string CachePath => Path.Combine(Settings.Directory, "holidays.json");

    /// <summary>取得したデータが入るたびに増える（表示の更新の判定に使う）。</summary>
    public static int Version { get; private set; }

    private static void Use(Dictionary<string, string> data)
    {
        _first = data.Keys.Min() ?? "";
        _last = data.Keys.Max() ?? "";
        _remote = data;
    }

    private static void EnsureLoaded()
    {
        if (_started) return;
        _started = true;
        bool stale = true;
        try
        {
            if (File.Exists(CachePath))
            {
                var cached = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath));
                if (cached is { Count: > 50 }) Use(cached);
                stale = DateTime.Now - File.GetLastWriteTime(CachePath) > MaxAge;
            }
        }
        catch (Exception ex) { Log.Error("holiday cache", ex); }
        if (stale) Task.Run(Refresh);
    }

    private static async Task Refresh()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string text = await http.GetStringAsync(Url).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            if (data == null || data.Count < 50) return; // 壊れたデータは使わない
            Directory.CreateDirectory(Settings.Directory);
            File.WriteAllText(CachePath, text);
            Use(data);
            Version++;
        }
        catch (Exception ex) { Log.Error("holiday fetch", ex); }
    }

    /// <summary>祝日の名前。祝日でなければ null。</summary>
    public static string? Name(DateTime d)
    {
        EnsureLoaded();
        var remote = _remote;
        // 取得したデータが対象にしている期間の中では、それを正とする
        if (remote != null)
        {
            string key = d.ToString("yyyy-MM-dd");
            if (remote.TryGetValue(key, out var n)) return n;
            if (string.CompareOrdinal(key, _first) >= 0 && string.CompareOrdinal(key, _last) <= 0) return null;
        }
        return Computed(d);
    }

    private static string? Computed(DateTime d)
    {
        if (d.Year < 1990) return null;
        if (Base(d) is { } n) return n;
        if (d.DayOfWeek != DayOfWeek.Sunday)
        {
            // 振替休日: 直前の祝日が日曜なら、その次の祝日でない日
            for (var p = d.AddDays(-1); Base(p) != null; p = p.AddDays(-1))
                if (p.DayOfWeek == DayOfWeek.Sunday) return "振替休日";
            // 国民の休日: 前後が祝日にはさまれた平日
            if (Base(d.AddDays(-1)) != null && Base(d.AddDays(1)) != null) return "国民の休日";
        }
        return null;
    }

    private static DateTime NthMonday(int y, int m, int n)
    {
        var d = new DateTime(y, m, 1);
        while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(1);
        return d.AddDays(7 * (n - 1));
    }

    private static int Equinox(int y, bool spring)
    {
        double c = spring ? 20.8431 : 23.2488;
        return (int)(c + 0.242194 * (y - 1980) - (y - 1980) / 4);
    }

    private static string? Base(DateTime d)
    {
        int y = d.Year, m = d.Month, day = d.Day;
        switch (m)
        {
            case 1:
                if (day == 1) return "元日";
                if (y >= 2000 && d == NthMonday(y, 1, 2)) return "成人の日";
                break;
            case 2:
                if (day == 11) return "建国記念の日";
                if (y >= 2020 && day == 23) return "天皇誕生日";
                break;
            case 3:
                if (day == Equinox(y, true)) return "春分の日";
                break;
            case 4:
                if (day == 29) return y >= 2007 ? "昭和の日" : "みどりの日";
                break;
            case 5:
                if (day == 3) return "憲法記念日";
                if (day == 4 && y >= 2007) return "みどりの日";
                if (day == 5) return "こどもの日";
                break;
            case 7:
                if (y == 2020) { if (day == 23) return "海の日"; if (day == 24) return "スポーツの日"; break; }
                if (y == 2021) { if (day == 22) return "海の日"; if (day == 23) return "スポーツの日"; break; }
                if (y >= 2003 && d == NthMonday(y, 7, 3)) return "海の日";
                break;
            case 8:
                if (y == 2020 && day == 10) return "山の日";
                if (y == 2021 && day == 8) return "山の日";
                if (y >= 2016 && y != 2020 && y != 2021 && day == 11) return "山の日";
                break;
            case 9:
                if (y >= 2003 && d == NthMonday(y, 9, 3)) return "敬老の日";
                if (day == Equinox(y, false)) return "秋分の日";
                break;
            case 10:
                if (y is not (2020 or 2021) && y >= 2000 && d == NthMonday(y, 10, 2)) return y >= 2020 ? "スポーツの日" : "体育の日";
                break;
            case 11:
                if (day == 3) return "文化の日";
                if (day == 23) return "勤労感謝の日";
                break;
            case 12:
                if (y <= 2018 && day == 23) return "天皇誕生日";
                break;
        }
        return null;
    }
}
