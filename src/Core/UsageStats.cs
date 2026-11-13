using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ModernShell.Core;

/// <summary>
/// アプリを開いた回数と最後に開いた日時（スタートメニュー・タスクバー・検索から開いたもの）。
/// 「最近使った」「よく使う」の表示に使う。%AppData%\ModernShell\usage.json に保存する（この PC の中だけで、外へは送らない）。
/// </summary>
public static class UsageStats
{
    public sealed class Entry
    {
        public int Count { get; set; }
        public DateTime Last { get; set; }
    }

    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? _data;
    private static string PathName => Path.Combine(Settings.Directory, "usage.json");

    private static Dictionary<string, Entry> Data
    {
        get
        {
            if (_data != null) return _data;
            try
            {
                if (File.Exists(PathName))
                    _data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(PathName), Options)
                        ?? new(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Log.Error("usage load", ex); }
            _data = new Dictionary<string, Entry>(_data ?? new(), StringComparer.OrdinalIgnoreCase);
            return _data;
        }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static void Record(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (Gate)
        {
            var d = Data;
            if (!d.TryGetValue(id, out var e)) d[id] = e = new Entry();
            e.Count++;
            e.Last = DateTime.Now;
            // 古い履歴が増え続けないよう、多すぎるときは古いものから捨てる
            if (d.Count > 400)
                foreach (var k in d.OrderBy(x => x.Value.Last).Take(d.Count - 300).Select(x => x.Key).ToList()) d.Remove(k);
            Save();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Directory);
            File.WriteAllText(PathName, JsonSerializer.Serialize(Data, Options));
        }
        catch (Exception ex) { Log.Error("usage save", ex); }
    }

    /// <summary>最近開いた順。</summary>
    public static List<string> Recent(int max)
    {
        lock (Gate) return Data.OrderByDescending(x => x.Value.Last).Select(x => x.Key).Take(max).ToList();
    }

    /// <summary>よく開く順（2 回以上開いたもの）。exclude に含まれるものは除く。</summary>
    public static List<string> Frequent(int max, ISet<string>? exclude = null)
    {
        lock (Gate)
            return Data.Where(x => x.Value.Count >= 2 && (exclude == null || !exclude.Contains(x.Key)))
                .OrderByDescending(x => x.Value.Count).ThenByDescending(x => x.Value.Last)
                .Select(x => x.Key).Take(max).ToList();
    }

    public static void Remove(string id)
    {
        lock (Gate) { if (Data.Remove(id)) Save(); }
    }

    public static void Clear()
    {
        lock (Gate) { Data.Clear(); Save(); }
    }
}
