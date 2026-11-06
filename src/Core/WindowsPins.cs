using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace ModernShell.Core;

/// <summary>
/// Windows のタスクバーにピン留めされたアプリ（と並び順）を読み取る。読み取り専用。
///  - ピン留めの実体: %AppData%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\*.lnk（デスクトップアプリ）
///  - 並び順とストアアプリ: HKCU\...\Explorer\Taskband の "Favorites"（非公開のバイナリ形式）
/// Favorites は形式が公開されていないため完全には解析せず、中に含まれる .lnk 名と AUMID の出現位置で並び順を推定する。
/// Windows はアプリからのピン留め変更を API として提供していない（意図的にブロックされている）ため、書き込みはしない。
/// </summary>
public sealed class WindowsPins : IDisposable
{
    private const string TaskbandKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";
    private static string PinnedFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    private readonly AppCatalog _catalog;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _poll;
    private string _lastSignature = "";

    public IReadOnlyList<AppEntry> Apps { get; private set; } = Array.Empty<AppEntry>();
    public event Action? Changed;

    public WindowsPins(AppCatalog catalog)
    {
        _catalog = catalog;
        Refresh();
        try
        {
            if (Directory.Exists(PinnedFolder))
            {
                _watcher = new FileSystemWatcher(PinnedFolder, "*.lnk") { EnableRaisingEvents = true };
                _watcher.Created += (_, _) => Refresh();
                _watcher.Deleted += (_, _) => Refresh();
                _watcher.Renamed += (_, _) => Refresh();
            }
        }
        catch (Exception ex) { Log.Error("pins watcher", ex); }
        // 並び替えはレジストリだけが変わるので、軽くポーリングして差分があれば更新する
        _poll = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _catalog.Changed += OnCatalogChanged;
    }

    private readonly object _gate = new();

    public void Refresh(bool force = false)
    {
        lock (_gate) RefreshCore(force);
    }

    private void RefreshCore(bool force)
    {
        try
        {
            var blob = ReadFavorites();
            var files = Directory.Exists(PinnedFolder) ? Directory.GetFiles(PinnedFolder, "*.lnk") : Array.Empty<string>();
            var signature = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(blob)) + "|" + string.Join("|", files) + "|" + _catalog.Apps.Count;
            if (!force && signature == _lastSignature) return;
            _lastSignature = signature;
            Apps = Build(blob, files);
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Error("windows pins", ex); }
    }

    private static byte[] ReadFavorites()
    {
        using var key = Registry.CurrentUser.OpenSubKey(TaskbandKey);
        return key?.GetValue("Favorites") as byte[] ?? Array.Empty<byte>();
    }

    private List<AppEntry> Build(byte[] blob, string[] lnkFiles)
    {
        // バイナリを UTF-16 として 2 通りの境界で読み、文字列の出現位置を並び順の手がかりにする
        string text0 = Encoding.Unicode.GetString(blob);
        string text1 = blob.Length > 1 ? Encoding.Unicode.GetString(blob, 1, blob.Length - 1) : "";
        int Position(string needle)
        {
            if (string.IsNullOrEmpty(needle)) return -1;
            int a = text0.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            int b = text1.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            return a < 0 ? b : b < 0 ? a : Math.Min(a, b);
        }

        var found = new List<(int pos, AppEntry app)>();
        bool haveOrder = blob.Length > 16;

        // デスクトップアプリ（.lnk）
        foreach (var lnk in lnkFiles)
        {
            var name = Path.GetFileName(lnk);
            var info = Shortcut.Read(lnk);
            var target = info?.Target;
            // AUMID が書かれていればそれで一覧のアプリを探す（一番確実）。なければリンク先の exe で探す
            // 一覧のアプリが見つかったら、右クリックメニュー用にピン留めのショートカットを持たせる（Windows と同じくプロパティ等が出る）
            var app = (info?.Aumid != null ? _catalog.Find(info.Aumid)?.WithShortcut(lnk) : null)
                      ?? (target != null ? _catalog.FindByExe(target)?.WithShortcut(lnk) : null)
                      ?? new AppEntry
                      {
                          Id = lnk, Name = Path.GetFileNameWithoutExtension(lnk), ExePath = target,
                          Aumid = info?.Aumid, LaunchArgs = info?.Arguments, ShortcutPath = lnk,
                      };

            // 並び順データの中での位置。ショートカット名だけでなく、8.3 形式の短い名前・AUMID・リンク先 exe 名でも探す
            var candidates = new[] { name, ShortName(lnk), info?.Aumid, target != null ? Path.GetFileName(target) : null };
            // 優先順（ショートカット名 → 短い名前 → AUMID → exe 名）で最初に見つかった位置を使う。
            // exe 名などは他のピン留めの中にも出てくることがあるので、全候補の最小値は取らない
            int pos = candidates.Where(c => !string.IsNullOrEmpty(c)).Select(c => Position(c!)).FirstOrDefault(p => p >= 0, -1);
            if (pos < 0 && haveOrder)
            {
                // 並び順データに無いショートカットは、ピン留めを外したあとに残った古いファイルとみなして除外する
                Log.Info($"windows pins: skip stale shortcut {name}");
                continue;
            }
            found.Add((pos < 0 ? int.MaxValue - 1000 + found.Count : pos, app));
        }

        // ストアアプリ（.lnk を作らず Favorites に AUMID だけが入る）
        foreach (var app in _catalog.Apps.Where(a => a.IsStoreApp))
        {
            int pos = Position(app.Id);
            if (pos >= 0 && found.All(f => !string.Equals(f.app.Id, app.Id, StringComparison.OrdinalIgnoreCase)))
                found.Add((pos, app));
        }

        foreach (var f in found.OrderBy(f => f.pos)) Log.Info($"windows pins: {f.pos,6} {f.app.Name}");
        return found.OrderBy(f => f.pos).Select(f => f.app)
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    internal static string? ShortcutTarget(string lnk) => Shortcut.Read(lnk)?.Target;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);

    /// <summary>8.3 形式の短いファイル名（並び順データに短い名前で入っていることがある）。</summary>
    private static string? ShortName(string path)
    {
        var sb = new StringBuilder(512);
        return GetShortPathName(path, sb, (uint)sb.Capacity) > 0 ? Path.GetFileName(sb.ToString()) : null;
    }

    private void OnCatalogChanged() => ThreadPool.QueueUserWorkItem(_ => Refresh(force: true));

    public void Dispose()
    {
        _catalog.Changed -= OnCatalogChanged;
        _poll.Dispose();
        _watcher?.Dispose();
    }
}
