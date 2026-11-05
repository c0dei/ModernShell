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

    public string ShellPath => @"shell:AppsFolder\" + Id;
    public ImageSource? Icon(int size = 48) => IconHelper.Get(ShellPath, size);
    public bool IsStoreApp => Id.Contains('!');

    public void Launch(bool asAdmin = false)
    {
        try
        {
            if (asAdmin && ExePath != null)
                Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, Verb = "runas" });
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

    public Task LoadAsync()
    {
        // Shell.Application は STA を要求するので専用スレッドで列挙する
        var tcs = new TaskCompletionSource();
        var t = new Thread(() =>
        {
            try
            {
                var list = Enumerate();
                _apps = list;
                _byId = list.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
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

    private static List<AppEntry> Enumerate()
    {
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
                    if (IsNoise(name, path)) continue;
                    result.Add(new AppEntry { Id = path, Name = name, ExePath = ResolveExe(path) });
                }
                catch { /* 個別アイテムの失敗は無視 */ }
                finally { Marshal.FinalReleaseComObject(item); }
            }
            Marshal.FinalReleaseComObject(folder);
        }
        finally { Marshal.FinalReleaseComObject(shell); }

        return result.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
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
