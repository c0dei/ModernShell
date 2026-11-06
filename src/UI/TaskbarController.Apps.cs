using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;
using ModernShell.Core;
using ModernShell.Native;
using Forms = System.Windows.Forms;

namespace ModernShell.UI;

/// <summary>タスクバーのアプリボタン: ピン留めと実行中ウィンドウのまとめ、右クリックメニュー（ジャンプリスト）、アイコン。</summary>
public sealed partial class TaskbarController
{
    // ------------------------------------------------------------------
    // アプリボタン

    private sealed class Group
    {
        public required string Key;
        public AppEntry? Pinned;
        public List<ApplicationWindow> Windows = new();
    }

    private IEnumerable<ApplicationWindow> TaskWindows() =>
        _shell.Tasks?.GroupedWindows?.Cast<ApplicationWindow>().Where(w => w.ShowInTaskbar) ?? Enumerable.Empty<ApplicationWindow>();

    // ManagedShell は値がないとき null ではなく空文字を返すので、空文字は「なし」として扱う
    // （そうしないと AUMID を持たないアプリが全部 1 つのボタンにまとめられてしまう）
    // ウィンドウに AUMID が付いていなければ、パッケージ版アプリ（Terminal、ストア版 Claude など）はプロセスから取る
    private static string? Aumid(ApplicationWindow w) => string.IsNullOrEmpty(w.AppUserModelID) ? ProcessAumid.Get(w.Handle) : w.AppUserModelID;
    private static string? ExePath(ApplicationWindow w) => string.IsNullOrEmpty(w.WinFileName) ? null : w.WinFileName;
    private static string GroupKey(ApplicationWindow w) => Aumid(w) ?? ExePath(w) ?? w.Handle.ToString();

    private void RebuildApps()
    {
        if (_apps == null) return;
        // メニューを開いている間に作り直すと、メニューの基準のボタンが消えて画面左上に飛んでしまうので後回しにする
        if (FluentSurface.AnyOpen) { Kick(_appsDebounce); return; }
        var groups = new List<Group>();

        foreach (var app in PinnedApps())
            if (groups.All(x => x.Key != "pin:" + app.Id)) groups.Add(new Group { Key = "pin:" + app.Id, Pinned = app });

        foreach (var w in TaskWindows())
        {
            Watch(w);
            var g = groups.FirstOrDefault(x => x.Pinned != null && KnownApps.Matches(x.Pinned, Aumid(w), ExePath(w)));
            if (g == null || !_settings.GroupTaskbarButtons && g.Windows.Count > 0)
            {
                string key = _settings.GroupTaskbarButtons ? GroupKey(w) : w.Handle.ToString();
                g = groups.FirstOrDefault(x => x.Key == key);
                if (g == null) { g = new Group { Key = key }; groups.Add(g); }
            }
            g.Windows.Add(w);
            if (g.Pinned == null) LogUnmatched(w);
        }

        foreach (var g in groups.Where(x => x.Pinned == null && !_order.Contains(x.Key))) _order.Add(g.Key);
        _order.RemoveAll(k => groups.All(g => g.Key != k));
        var ordered = groups.Where(g => g.Pinned != null)
            .Concat(groups.Where(g => g.Pinned == null).OrderBy(g => _order.IndexOf(g.Key)));

        _apps.Children.Clear();
        _buttonFor.Clear();
        var pinnedIds = new List<string>();
        LiveReorder? reorder = null;
        if (!PinsFromWindows)
            reorder = new LiveReorder(_apps, Orientation.Horizontal, (from, to) => CommitPinnedOrder(pinnedIds, from, to));
        foreach (var g in ordered)
        {
            var tile = AppButton(g);
            foreach (var w in g.Windows) _buttonFor[w.Handle] = tile;
            _apps.Children.Add(tile);
            // ピン留めボタンはドラッグで並べ替え（つかむとカーソルに付いてきて、他のボタンがよける）
            if (reorder != null && g.Pinned != null)
            {
                pinnedIds.Add(g.Pinned.Id);
                reorder.Add(tile);
            }
        }
        _apps.Visibility = _apps.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PruneWatched();
    }

    private Tile AppButton(Group g)
    {
        var t = Theme.Current;
        bool running = g.Windows.Count > 0;
        bool active = g.Windows.Any(w => w.State == ApplicationWindow.WindowState.Active);
        bool flashing = g.Windows.Any(w => w.State == ApplicationWindow.WindowState.Flashing);
        var icon = UiKit.Icon(CustomIcon(g) ?? ButtonIcon(g), IconSz);

        var content = new Grid();
        FrameworkElement main = icon;
        if (_settings.ShowTaskbarLabels && running)
        {
            var title = UiKit.Label(g.Windows[0].Title ?? "", 12);
            title.MaxWidth = 140;
            title.Margin = new Thickness(8, 0, 0, 0);
            main = UiKit.Row(icon, title);
            main.Margin = new Thickness(10, 0, 10, 0);
        }
        main.HorizontalAlignment = HorizontalAlignment.Center;
        main.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(main);

        if (running)
        {
            var indicator = flashing ? new SolidColorBrush(Color.FromRgb(0xF7, 0xA4, 0x00)) : active ? t.AccentBrush : t.TextSecondary;
            content.Children.Add(new Border
            {
                Height = 3, Width = active || flashing ? 16 : 6, CornerRadius = new CornerRadius(1.5), Background = indicator,
                VerticalAlignment = IsTop ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, Compact ? 0 : 2, 0, Compact ? 0 : 2),
            });
        }

        var tile = new Tile
        {
            MinWidth = TileW, Height = TileH, Margin = new Thickness(2, 0, 2, 0), Child = content,
            Background = active ? t.Hover : Brushes.Transparent,
            ToolTip = running ? string.Join("\n", g.Windows.Select(w => w.Title)) : g.Pinned?.Name,
        };
        tile.MouseLeave += (_, _) => { if (active) tile.Background = t.Hover; };
        if (_settings.MagnifyOnHover) AddMagnify(tile, icon);

        // カーソルを置くとウィンドウのプレビュー（実行中のときだけ）。クリック・右クリック・ドラッグで閉じる
        if (running)
        {
            tile.MouseEnter += (_, _) => _preview.Hover(tile, () => g.Windows, IsTop);
            tile.MouseLeave += (_, _) => _preview.Leave();
        }
        tile.PreviewMouseDown += (_, _) => _preview.Cancel();

        tile.Click += x =>
        {
            if (g.Windows.Count == 0) { TakeForeground(x); g.Pinned?.Launch(); }
            else if (g.Windows.Count == 1) ToggleWindow(g.Windows[0], x);
            else WindowList(x, g);
        };
        tile.MiddleClick += x => { TakeForeground(x); (g.Pinned ?? FindApp(g.Windows.FirstOrDefault()))?.Launch(); };
        tile.RightClick += x => Flyout.Show(x, GroupMenu(g), FlyoutPlacement);
        return tile;
    }

    private readonly HashSet<string> _loggedUnmatched = new();

    /// <summary>ピン留めと結び付かなかったウィンドウを 1 度だけログに残す（照合の改善用）。</summary>
    private void LogUnmatched(ApplicationWindow w)
    {
        var key = GroupKey(w);
        if (!_loggedUnmatched.Add(key) || !PinnedApps().Any()) return;
        Log.Info($"unmatched window: exe={ExePath(w)} aumid={Aumid(w)} title={w.Title}");
        if (_loggedUnmatched.Count == 1)
            foreach (var p in PinnedApps())
                Log.Info($"  pinned: name={p.Name} id={p.Id} aumid={p.Aumid} exe={p.ExePath} args={p.LaunchArgs}");
    }

    /// <summary>ピン留めアプリ（Windows と共有なら Windows のタスクバーと同じ内容・並び順）。</summary>
    private IEnumerable<AppEntry> PinnedApps() => PinsFromWindows
        ? _windowsPins.Apps.Concat(_settings.PinnedTaskbarExtra.Select(ResolvePinned).Where(a => a != null).Cast<AppEntry>())
        : _settings.PinnedTaskbar.Select(ResolvePinned).Where(a => a != null).Cast<AppEntry>();

    /// <summary>ModernShell 側で変更できるピン留めの一覧（Windows と共有中は「追加分」）。</summary>
    private List<string> EditablePins => PinsFromWindows ? _settings.PinnedTaskbarExtra : _settings.PinnedTaskbar;
    private bool IsWindowsPin(AppEntry app) => PinsFromWindows && _windowsPins.Apps.Any(a => a.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase));

    /// <summary>ピン留め ID → アプリ。Windows から取り込んだショートカット (.lnk のパス) にも対応。</summary>
    private AppEntry? ResolvePinned(string id)
    {
        if (_catalog.Find(id) is { } app) return app;
        if (System.IO.Path.IsPathRooted(id) && System.IO.File.Exists(id))
            return id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? ExeEntry(id, null)
                : new AppEntry { Id = id, Name = System.IO.Path.GetFileNameWithoutExtension(id), ExePath = WindowsPins.ShortcutTarget(id) };
        return null;
    }

    /// <summary>実行ファイルからアプリ項目を作る（表示名はファイルの説明、なければファイル名）。</summary>
    private static AppEntry ExeEntry(string exe, System.Windows.Media.ImageSource? _)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(exe);
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) name = info.FileDescription.Trim();
            else if (!string.IsNullOrWhiteSpace(info.ProductName)) name = info.ProductName.Trim();
        }
        catch { }
        return new AppEntry { Id = exe, Name = name, ExePath = exe };
    }

    /// <summary>
    /// ピン留めボタンの並べ替えを確定する（ModernShell で管理しているときのみ。Windows のピン留めは書き換えられない）。
    /// 見えているピン留めの並びを変え、解決できなかった ID の位置はそのまま保つ。
    /// </summary>
    private void CommitPinnedOrder(List<string> visibleIds, int from, int to)
    {
        var moved = visibleIds[from];
        visibleIds.RemoveAt(from);
        visibleIds.Insert(to, moved);
        var list = _settings.PinnedTaskbar;
        int v = 0;
        for (int i = 0; i < list.Count; i++)
            if (visibleIds.Contains(list[i], StringComparer.OrdinalIgnoreCase)) list[i] = visibleIds[v++];
        _settings.Save();
    }

    /// <summary>前面（アクティブ）なら最小化、それ以外は前面化（最小化中なら復元）。</summary>
    private static void ToggleWindow(ApplicationWindow w, DependencyObject from)
    {
        if (w.State == ApplicationWindow.WindowState.Active && !w.IsMinimized) w.Minimize();
        else { TakeForeground(from); w.BringToFront(); }
    }

    /// <summary>
    /// クリックを受けたタスクバー（またはポップアップ）を一瞬前面にする。
    /// タスクバーはクリックしてもアクティブにならない作りなので、そのままだと Windows から「前面のアプリ」と見なされず、
    /// 他のアプリ（管理者として動くタスク マネージャーや、開いている音量・通知のフライアウトなど）が前面にあるとき、
    /// アプリを前面に出したりトレイのフライアウトを開いたりする権利（前面ウィンドウの切り替え）を渡せない。
    /// 直前にクリックを受けたのは自分なので、Windows はこの切り替えを許可する。
    /// 「前面のアプリをクリックで最小化」の判定には影響しない（判定のあと、前面に出すときだけ呼ぶ）。
    /// </summary>
    internal static void TakeForeground(DependencyObject from)
    {
        if (PresentationSource.FromDependencyObject(from) is not System.Windows.Interop.HwndSource src) return;
        var h = src.Handle;
        if (Win32.GetForegroundWindow() == h) return;
        if (!Win32.SetForegroundWindow(h)) Log.Info("take foreground failed");
    }

    private AppEntry? FindApp(ApplicationWindow? w)
    {
        if (w == null) return null;
        var exe = ExePath(w);
        return (Aumid(w) is { } id ? _catalog.Find(id) : null)
               ?? (exe != null ? _catalog.FindByExe(exe) : null)
               ?? (exe != null ? _catalog.FindByExeName(System.IO.Path.GetFileName(exe)) : null)
               // アプリ一覧に無いアプリでも、実行ファイルから項目を作ってピン留めできるようにする
               ?? (exe != null && !exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) ? ExeEntry(exe, w.Icon) : null);
    }

    /// <summary>アイランド型のホバー拡大（Mac のドック風）。レイアウトは変えず描画だけ拡大する。</summary>
    private void AddMagnify(Tile tile, Image icon)
    {
        var scale = new ScaleTransform(1, 1);
        icon.RenderTransform = scale;
        icon.RenderTransformOrigin = new Point(0.5, IsTop ? 0.2 : 0.8);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        void To(double v)
        {
            var a = new DoubleAnimation(v, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        tile.MouseEnter += (_, _) => To(1.25);
        tile.MouseLeave += (_, _) => To(1.0);
    }

    private void WindowList(UIElement target, Group g) =>
        Flyout.Show(target, g.Windows.Select(w =>
            new MenuItemSpec("", Truncate(w.Title ?? "", 48), () => { TakeForeground(target); w.BringToFront(); })), FlyoutPlacement);

    private IEnumerable<MenuItemSpec> GroupMenu(Group g)
    {
        var app = g.Pinned ?? FindApp(g.Windows.FirstOrDefault());
        var items = new List<MenuItemSpec>();

        // ジャンプリスト（Windows のタスクバーの右クリックと同じ「最近使ったもの」「タスク」など）
        var w0 = g.Windows.FirstOrDefault();
        var aumid = (w0 != null ? Aumid(w0) : null) ?? app?.Aumid ?? (app != null && !app.IsFile && !app.Id.Contains('\\') ? app.Id : null);
        var exe = (w0 != null ? ExePath(w0) : null) ?? app?.ExePath;
        // パッケージ版アプリ（Terminal など）のタスクは "ms-appx:///…" でパッケージ内の画像を指すので、インストール先を求めておく
        var packageRoot = PackageInfo.RootFromExe(exe) ?? (aumid != null && aumid.Contains('!') ? PackageInfo.InstallPath(aumid) : null);
        try
        {
            foreach (var cat in JumpList.Read(aumid, exe))
            {
                items.Add(MenuItemSpec.Header(cat.Title));
                foreach (var it in cat.Items)
                {
                    if (it.IsSeparator) { items.Add(MenuItemSpec.Separator); continue; }
                    var path = it.Path;
                    bool isTask = path.StartsWith(System.IO.Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
                                  || path.Contains(@"\ModernShell\jumplist\", StringComparison.OrdinalIgnoreCase);
                    items.Add(new MenuItemSpec("", Truncate(it.Title, 48), () => OpenPath(path))
                    {
                        // アイコン: ショートカットに書かれたアイコン → シェルのアイコン取得 → アプリのアイコン の順に試す
                        // （シェルのアイコン取得は解決できないと null ではなく汎用アイコンを返すので後回し）
                        Icon = JumpIcon(it.IconFile, it.IconIndex, packageRoot)
                               ?? (it.IconFile == null && it.Target != null && !it.Target.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)
                                   ? ModernShell.Core.IconHelper.Get(path, 32) : null)
                               ?? (it.Target == null ? ModernShell.Core.IconHelper.Get(path, 32) : null)
                               ?? w0?.Icon ?? app?.Icon(32),
                        SubMenu = () => JumpItemMenu(path, isTask),
                    });
                }
                items.Add(MenuItemSpec.Separator);
            }
        }
        catch (Exception ex) { Log.Error("jumplist menu", ex); }

        if (app != null)
        {
            items.Add(new MenuItemSpec("", app.Name, () => app.Launch()) { Icon = CustomIcon(g) ?? app.Icon(32) ?? w0?.Icon, SubMenu = () => AppItemMenu(app) });
            if (IsWindowsPin(app))
            {
                // Windows のピン留めはアプリから変更できない（Windows が API を提供していない）
                items.Add(new MenuItemSpec("\uE946", "Windows のタスクバーのピン留め（Windows 側で変更）", () => OpenSettingsRequested?.Invoke()));
            }
            else
            {
                var pins = EditablePins;
                bool pinned = pins.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
                items.Add(pinned
                    ? new MenuItemSpec("\uE77A", "タスクバーからピン留めを外す", () => { pins.RemoveAll(x => x.Equals(app.Id, StringComparison.OrdinalIgnoreCase)); _settings.Save(); })
                    : new MenuItemSpec("\uE718", "タスクバーにピン留めする", () => { pins.Add(app.Id); _settings.Save(); }));
            }
        }
        // アイコンの差し替え（ピン留め・実行中どちらのボタンにも効く）
        var iconKey = IconKey(g);
        items.Add(MenuItemSpec.Separator);
        items.Add(new MenuItemSpec("\uE790", "アイコンを変更...", () => ChooseIcon(iconKey)));
        if (_settings.CustomIcons.ContainsKey(iconKey))
            items.Add(new MenuItemSpec("\uE7A7", "アイコンを元に戻す", () => { _settings.CustomIcons.Remove(iconKey); _settings.Save(); }));
        if (g.Windows.Count > 0)
        {
            items.Add(MenuItemSpec.Separator);
            items.Add(new MenuItemSpec("", g.Windows.Count > 1 ? "すべてのウィンドウを閉じる" : "ウィンドウを閉じる",
                () => { foreach (var w in g.Windows) w.Close(); }, Danger: true));
        }
        return items;
    }

    /// <summary>
    /// ボタンのアイコン。Windows のタスクバーと同じく、まとめて表示するボタンは「アプリのアイコン」を使う
    /// （ウィンドウのアイコンは中身で変わることがある: エクスプローラーは開いている場所のアイコンになる）。
    /// まとめない設定のときは、ウィンドウごとのアイコンを優先する。
    /// </summary>
    private ImageSource? ButtonIcon(Group g)
    {
        var w0 = g.Windows.FirstOrDefault();
        var windowIcon = w0?.Icon;
        return _settings.GroupTaskbarButtons ? AppIcon(g) ?? windowIcon : windowIcon ?? AppIcon(g);
    }

    /// <summary>
    /// アプリのアイコン。Windows のタスクバーと同じく ショートカットのアイコン → 実行ファイルのアイコン を優先し、
    /// どちらも無いとき（ストアアプリ等）だけアプリ一覧のアイコンを使う。表示サイズに合う絵を直接取り出す。
    /// </summary>
    private ImageSource? AppIcon(Group g)
    {
        int px = (int)Math.Round(IconSz * (_surfaces.FirstOrDefault()?.Scale ?? 1.0));
        var w0 = g.Windows.FirstOrDefault();
        var app = g.Pinned ?? (w0 != null ? FindApp(w0) : null);
        if (app?.ShortcutPath is { } lnk && System.IO.File.Exists(lnk) && ModernShell.Core.IconHelper.ForShortcut(lnk, px) is { } fromLnk) return fromLnk;
        var exe = app?.ExePath ?? (w0 != null ? ExePath(w0) : null);
        if (exe != null && !exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
            && ModernShell.Core.IconHelper.Exact(exe, 0, px) is { } fromExe) return fromExe;
        return app?.Icon(64);
    }

    // ---- アイコンの差し替え ----

    /// <summary>差し替えの記録に使うキー（ピン留めならアプリ ID、実行中だけならグループのキー = AUMID か exe）。</summary>
    private static string IconKey(Group g) => g.Pinned?.Id ?? g.Key;

    private ImageSource? CustomIcon(Group g)
    {
        if (!_settings.CustomIcons.TryGetValue(IconKey(g), out var spec)) return null;
        var parts = spec.Split('|');
        return ModernShell.Core.IconHelper.FromSpec(parts[0], parts.Length > 1 && int.TryParse(parts[1], out var i) ? i : 0);
    }

    /// <summary>ファイルを選んでアイコンを差し替える（.ico / .png などの画像、または .exe / .dll の最初のアイコン）。</summary>
    private void ChooseIcon(string key)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "アイコンを選ぶ",
            Filter = "アイコン・画像・プログラム (*.ico;*.png;*.jpg;*.exe;*.dll)|*.ico;*.png;*.jpg;*.jpeg;*.exe;*.dll|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        _settings.CustomIcons[key] = dlg.FileName + "|0";
        _settings.Save();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

    /// <summary>メニュー内のアプリ名を右クリックしたとき（Windows と同じ: 開く / 管理者として実行 / スタート・タスクバーのピン留め）。</summary>
    private IEnumerable<MenuItemSpec> AppItemMenu(AppEntry app)
    {
        // Windows 標準の右クリックメニューをそのまま使う（取れなかったときだけ自前の項目）
        // Windows のタスクバーと同じく、ショートカットがあればその .lnk のメニューを使う（「プロパティ」も出る）
        var target = app.ShortcutPath != null && System.IO.File.Exists(app.ShortcutPath) ? app.ShortcutPath : app.ShellPath;
        var shell = ShellContextMenu.TaskbarStyle(target, OwnerHandle);
        if (shell.Count == 0) return ManualAppMenu(app);
        // Windows 側のメニューに無い（または外部アプリから動かせない）タスクバーのピン留めは ModernShell の項目で補う
        if (!IsWindowsPin(app) && !shell.Any(m => m.Text.Contains("タスク") && m.Text.Contains("ピン留め")))
        {
            var pins = EditablePins;
            bool onBar = pins.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
            int at = shell.FindIndex(m => m.IsSeparator);
            var pinItem = onBar
                ? new MenuItemSpec("\uE77A", "タスクバーからピン留めを外す", () => { pins.RemoveAll(x => x.Equals(app.Id, StringComparison.OrdinalIgnoreCase)); _settings.Save(); })
                : new MenuItemSpec("\uE718", "タスクバーにピン留めする", () => { pins.Add(app.Id); _settings.Save(); });
            shell.Insert(at < 0 ? shell.Count : at, pinItem);
        }
        return shell;
    }

    private IntPtr OwnerHandle => _surfaces.FirstOrDefault()?.Handle ?? IntPtr.Zero;

    private IEnumerable<MenuItemSpec> ManualAppMenu(AppEntry app)
    {
        yield return new MenuItemSpec("\uE8A7", "開く", () => app.Launch());
        if (app.ExePath != null) yield return new MenuItemSpec("\uE7EF", "管理者として実行", () => app.Launch(asAdmin: true));
        bool onStart = _settings.PinnedStart.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
        yield return onStart
            ? new MenuItemSpec("\uE77A", "スタートからピン留めを外す", () => { _settings.PinnedStart.RemoveAll(x => x.Equals(app.Id, StringComparison.OrdinalIgnoreCase)); _settings.Save(); })
            : new MenuItemSpec("\uE718", "スタートにピン留めする", () => { _settings.PinnedStart.Add(app.Id); _settings.Save(); });
        if (!IsWindowsPin(app))
        {
            var pins = EditablePins;
            bool onBar = pins.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
            yield return onBar
                ? new MenuItemSpec("\uE77A", "タスクバーからピン留めを外す", () => { pins.RemoveAll(x => x.Equals(app.Id, StringComparison.OrdinalIgnoreCase)); _settings.Save(); })
                : new MenuItemSpec("\uE718", "タスクバーにピン留めする", () => { pins.Add(app.Id); _settings.Save(); });
        }
        if (app.ExePath != null) yield return new MenuItemSpec("\uE838", "ファイルの場所を開く", app.OpenFileLocation);
    }

    /// <summary>ジャンプリストの項目を右クリックしたとき。</summary>
    private IEnumerable<MenuItemSpec> JumpItemMenu(string path, bool isTask)
    {
        // 最近使ったファイルは Windows 標準の右クリックメニュー（エクスプローラーと同じ）
        if (!isTask && System.IO.File.Exists(path))
        {
            var shell = ShellContextMenu.For(path, OwnerHandle);
            if (shell.Count > 0) { foreach (var m in shell) yield return m; yield break; }
        }
        yield return new MenuItemSpec("\uE8A7", "開く", () => OpenPath(path));
        if (!isTask && System.IO.File.Exists(path))
        {
            yield return new MenuItemSpec("\uE838", "ファイルの場所を開く", () => OpenPath("explorer.exe", $"/select,\"{path}\""));
            yield return new MenuItemSpec("\uE8C8", "パスをコピー", () => { try { Clipboard.SetText(path); } catch { } });
        }
    }

    /// <summary>ジャンプリスト項目のアイコン指定（DLL/EXE の番号、画像ファイル、ms-appx:/// のパッケージ内画像）を画像にする。</summary>
    private static ImageSource? JumpIcon(string? file, int index, string? packageRoot) =>
        ModernShell.Core.IconHelper.FromSpec(file, index, packageRoot);

    private static void OpenPath(string file, string args)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open", ex); }
    }

    private static void OpenPath(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open jump item", ex); }
    }
}
