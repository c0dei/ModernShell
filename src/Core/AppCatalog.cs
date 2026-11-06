using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace ModernShell.Core;

/// <summary>
/// shell:AppsFolder の 1 アイテム。デスクトップアプリ（.lnk 由来）とストアアプリ（UWP/MSIX）の両方を含む。
/// </summary>
public sealed class AppEntry
{
    /// <summary>shell:AppsFolder 内の解析名。UWP は AUMID、デスクトップアプリは AUMID か {KnownFolder}\path。</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>判明していれば実体 exe のフルパス（タスクバーでの実行中ウィンドウとの突き合わせに使う）。</summary>
    public string? ExePath { get; init; }
    /// <summary>スタートメニューのフォルダ名（「7-Zip」など）。フォルダに入っていなければ null。</summary>
    public string? Folder { get; init; }
    /// <summary>アプリが自分のウィンドウに付ける AppUserModelID（分かれば）。</summary>
    public string? Aumid { get; init; }
    /// <summary>ショートカットの引数（ランチャー経由で起動するアプリの照合に使う）。</summary>
    public string? LaunchArgs { get; init; }
    /// <summary>このアプリのショートカット (.lnk)。右クリックメニューを Windows のタスクバーと同じ内容（プロパティ等）にするのに使う。</summary>
    public string? ShortcutPath { get; init; }

    /// <summary>
    /// ピン留めのショートカットを持たせた複製。一覧側で実行ファイルが分からないアプリ（ID が AUMID だけのもの）でも、
    /// ショートカットのリンク先・引数を引き継いで、実行中のウィンドウと照合できるようにする。
    /// </summary>
    public AppEntry WithShortcut(string lnk, ShortcutInfo? info = null) => new()
    {
        Id = Id, Name = Name, Folder = Folder, ShortcutPath = lnk,
        ExePath = ExePath ?? (info?.Target is { } t && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t : null),
        Aumid = Aumid ?? info?.Aumid,
        LaunchArgs = LaunchArgs ?? info?.Arguments,
    };

    /// <summary>Id がファイルパス（Windows のタスクバーにピン留めされた .lnk など）か。</summary>
    public bool IsFile => Path.IsPathRooted(Id);
    public string ShellPath => IsFile ? Id : @"shell:AppsFolder\" + Id;
    public ImageSource? Icon(int size = 48) => IconHelper.Get(ShellPath, size);
    public bool IsStoreApp => !IsFile && Id.Contains('!');

    public void Launch(bool asAdmin = false)
    {
        try
        {
            if (asAdmin && ExePath != null)
                Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, Verb = "runas" });
            else if (IsFile)
                Process.Start(new ProcessStartInfo(Id) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ShellPath + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error($"launch {Id}", ex); }
    }

    public void OpenFileLocation()
    {
        if (ExePath == null) return;
        try { Process.Start("explorer.exe", $"/select,\"{ExePath}\""); }
        catch (Exception ex) { Log.Error("open location", ex); }
    }

    public override string ToString() => Name;
}

public sealed class AppCatalog
{
    private List<AppEntry> _apps = new();
    private Dictionary<string, AppEntry> _byId = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher[] _watchers = Array.Empty<FileSystemWatcher>();
    private Timer? _debounce;

    public IReadOnlyList<AppEntry> Apps => _apps;
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    public AppEntry? Find(string id) => _byId.TryGetValue(id, out var a) ? a : null;

    /// <summary>実行ファイルパスから一致するアプリを探す。</summary>
    public AppEntry? FindByExe(string exePath) =>
        _apps.FirstOrDefault(a => a.ExePath != null && string.Equals(a.ExePath, exePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>exe のファイル名だけで探す（インストール先が違う同じアプリ用）。候補が 1 つのときだけ返す。</summary>
    public AppEntry? FindByExeName(string fileName)
    {
        var hits = _apps.Where(a => a.ExePath != null && string.Equals(Path.GetFileName(a.ExePath), fileName, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    public Task LoadAsync()
    {
        // Shell.Application は STA を要求するので専用スレッドで列挙する
        var tcs = new TaskCompletionSource();
        var t = new Thread(() =>
        {
            try
            {
                var (list, aliases) = Enumerate();
                _apps = list;
                // まとめて消えた重複エントリの ID でも、残したエントリが見つかるようにする（ピン留めが消えないように）
                _byId = aliases;
                Log.Info($"apps loaded: {list.Count}");
                tcs.SetResult();
            }
            catch (Exception ex) { Log.Error("enumerate apps", ex); tcs.SetResult(); }
        }) { IsBackground = true, Name = "AppCatalog" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    /// <summary>スタートメニューフォルダの変化（インストール/アンインストール）を監視して再読み込み。</summary>
    public void Watch()
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        _watchers = dirs.Where(Directory.Exists).Select(d =>
        {
            var w = new FileSystemWatcher(d) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            w.Created += (_, _) => Kick(); w.Deleted += (_, _) => Kick(); w.Renamed += (_, _) => Kick();
            return w;
        }).ToArray();
        // ストアアプリのインストールはフォルダ監視では拾えないため、定期的にも再読み込みする
        _debounce = new Timer(async _ => { await LoadAsync(); Changed?.Invoke(); }, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    private void Kick() => _debounce?.Change(TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(10));

    private static (List<AppEntry> apps, Dictionary<string, AppEntry> byId) Enumerate()
    {
        var folders = StartMenuFolders();
        var result = new List<AppEntry>();
        var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application not available");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                try
                {
                    string name = item.Name;
                    string path = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                    // ショートカットの情報（リンク先・AUMID・引数）を名前で引く
                    folders.byNameInfo.TryGetValue(name.Trim(), out var info);
                    var exe = ResolveExe(path)
                              ?? (info?.Target is { } tg && tg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(tg) ? tg : null);
                    // AppsFolder の ID は、明示的な AUMID を持つアプリではその AUMID そのもの（パス区切りを含まない）
                    var aumid = !path.Contains('\\') && !path.Contains('/') ? path : info?.Aumid;
                    string? group = (exe != null && folders.byExe.TryGetValue(exe, out var f1)) ? f1
                                  : folders.byName.TryGetValue(name.Trim(), out var f2) ? f2 : null;
                    // フォルダの中身は OS と同じくすべて出す（ヘルプやアンインストールも）。直下の雑多なものだけ除外
                    if (group == null && IsNoise(name, path)) continue;
                    result.Add(new AppEntry { Id = path, Name = name, ExePath = exe, Folder = group, Aumid = aumid, LaunchArgs = info?.Arguments, ShortcutPath = info?.Path });
                }
                catch { /* 個別アイテムの失敗は無視 */ }
                finally { Marshal.FinalReleaseComObject(item); }
            }
            Marshal.FinalReleaseComObject(folder);
        }
        finally { Marshal.FinalReleaseComObject(shell); }

        // 同じアプリが複数登録されていることがある（ユーザー用と全ユーザー用のショートカット、32/64bit 版など）。
        // 名前（とフォルダ）が同じものは 1 つにまとめる。実体の exe が分かっているものを優先して残す
        var byId = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<AppEntry>();
        foreach (var g in result.GroupBy(a => (a.Folder ?? "") + "|" + a.Name.Trim(), StringComparer.CurrentCultureIgnoreCase))
        {
            var keep = g.OrderByDescending(a => a.ExePath != null).ThenByDescending(a => a.IsStoreApp).First();
            kept.Add(keep);
            foreach (var a in g) byId.TryAdd(a.Id, keep);
        }

        // 中身が 1 つしかないフォルダは、OS と同じくフォルダにせず直接並べる
        var single = kept.Where(a => a.Folder != null).GroupBy(a => a.Folder!, StringComparer.CurrentCultureIgnoreCase)
            .Where(g => g.Count() < 2).SelectMany(g => g).ToList();
        foreach (var a in single)
        {
            var flat = new AppEntry { Id = a.Id, Name = a.Name, ExePath = a.ExePath, Folder = null, Aumid = a.Aumid, LaunchArgs = a.LaunchArgs, ShortcutPath = a.ShortcutPath };
            if (IsNoise(flat.Name, flat.Id)) { kept.Remove(a); continue; }
            kept[kept.IndexOf(a)] = flat;
            foreach (var key in byId.Where(kv => kv.Value == a).Select(kv => kv.Key).ToList()) byId[key] = flat;
        }

        return (kept.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), byId);
    }

    // OS がフォルダとして見せない（中身を直接並べる / 「Windows ツール」にまとめる）システム系フォルダ
    private static readonly HashSet<string> FlattenFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Startup", "Accessories", "Accessibility", "Administrative Tools", "System Tools", "Maintenance",
        "Windows Accessories", "Windows Administrative Tools", "Windows Ease of Access", "Windows System",
        "Windows PowerShell", "Windows Tools",
    };

    /// <summary>スタートメニューの Programs フォルダを走査し、ショートカット名 / リンク先 exe → 直下のフォルダ名 を作る。</summary>
    private static (Dictionary<string, string> byName, Dictionary<string, string> byExe, Dictionary<string, ShortcutInfo> byNameInfo) StartMenuFolders()
    {
        var byName = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        var byExe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byNameInfo = new Dictionary<string, ShortcutInfo>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var lnk in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    var dir = Path.GetDirectoryName(lnk);
                    if (dir == null) continue;
                    var display = Path.GetFileNameWithoutExtension(lnk).Trim();
                    var info = lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Shortcut.Read(lnk) : null;
                    if (info != null) byNameInfo.TryAdd(display, info);
                    var rel = Path.GetRelativePath(root, dir);
                    if (rel == ".") continue;
                    var top = rel.Split(Path.DirectorySeparatorChar)[0];
                    if (FlattenFolders.Contains(top)) continue;
                    byName.TryAdd(display, top);
                    if (info?.Target is { } target) byExe.TryAdd(target, top);
                }
            }
            catch (Exception ex) { Log.Error("start menu folders", ex); }
        }
        return (byName, byExe, byNameInfo);
    }

    private static readonly Regex Noise = new(@"(uninstall|アンインストール|readme|help|ヘルプ|website|web site|documentation)", RegexOptions.IgnoreCase);

    private static bool IsNoise(string name, string path) =>
        Noise.IsMatch(name) || path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".chm", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex KnownFolderPrefix = new(@"^\{([0-9A-Fa-f\-]{36})\}\\(.*)$");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    /// <summary>"{KnownFolderGuid}\sub\app.exe" 形式やフルパスを実パスへ解決。</summary>
    internal static string? ResolveExe(string parsingName)
    {
        try
        {
            string? full = null;
            var m = KnownFolderPrefix.Match(parsingName);
            if (m.Success && SHGetKnownFolderPath(Guid.Parse(m.Groups[1].Value), 0, IntPtr.Zero, out var p) == 0)
            {
                var root = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                if (root != null) full = Path.Combine(root, m.Groups[2].Value);
            }
            else if (Path.IsPathRooted(parsingName)) full = parsingName;

            if (full != null && full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(full)) return full;
        }
        catch { }
        return null;
    }
}
