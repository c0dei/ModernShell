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

        // デスクトップアプリ（.lnk）
        foreach (var lnk in lnkFiles)
        {
            var name = Path.GetFileName(lnk);
            var target = ShortcutTarget(lnk);
            var app = (target != null ? _catalog.FindByExe(target) : null)
                      ?? new AppEntry { Id = lnk, Name = Path.GetFileNameWithoutExtension(lnk), ExePath = target };
            int pos = Position(name);
            found.Add((pos < 0 ? int.MaxValue - 1000 + found.Count : pos, app));
        }

        // ストアアプリ（.lnk を作らず Favorites に AUMID だけが入る）
        foreach (var app in _catalog.Apps.Where(a => a.IsStoreApp))
        {
            int pos = Position(app.Id);
            if (pos >= 0 && found.All(f => !string.Equals(f.app.Id, app.Id, StringComparison.OrdinalIgnoreCase)))
                found.Add((pos, app));
        }

        return found.OrderBy(f => f.pos).Select(f => f.app)
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    // ---- .lnk のリンク先 ----
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr pfd, uint flags);
        // 以降のメソッドは使わないので宣言を省略（vtable の先頭だけ利用）
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
    }

    internal static string? ShortcutTarget(string lnk)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var path = Environment.ExpandEnvironmentVariables(sb.ToString());
            return path.Length > 0 ? path : null;
        }
        catch { return null; }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }

    private void OnCatalogChanged() => ThreadPool.QueueUserWorkItem(_ => Refresh(force: true));

    public void Dispose()
    {
        _catalog.Changed -= OnCatalogChanged;
        _poll.Dispose();
        _watcher?.Dispose();
    }
}
