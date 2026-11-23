using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.WindowsTasks;
using ModernShell.Core;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>
/// タスクバーのフォルダー: アプリのボタンをひとまとめにする。
///  - 作る: バーの空いている場所を右クリック →「フォルダーを追加」（アプリをドラッグして重ねる方法は、ピン留めの並べ替えと紛らわしいので使わない）
///  - 見た目: グリッド（2×2 のミニ アイコン）/ 重ね（設定で選ぶ）
///  - 開く: ボタンをクリック → アプリの一覧。「編集」で名前・アプリの追加・削除・並べ替え（バーの外へドラッグすると、フォルダーから出る）
/// バーの上の位置は、ピン留めの一覧の中に "folder:&lt;Id&gt;" として並ぶ（ModernShell で管理のときはドラッグで並べ替えられる）。
/// </summary>
public sealed partial class TaskbarController
{
    private const string FolderPrefix = "folder:";
    private const int FolderColumns = 4;
    private const double CellW = 84, CellH = 88, CellGap = 4;

    private readonly Dictionary<string, Tile> _folderTiles = new();
    private string? _pendingOpenFolder;
    private bool _pendingOpenEdit;
    private FolderView? _openFolder;

    private sealed record Member(AppEntry App, Group Group);

    /// <summary>開いているフォルダーの一覧（ポップアップ 1 回分）。</summary>
    private sealed class FolderView
    {
        public required PinnedFolder F;
        public required Tile Anchor;
        public bool Edit, Picker, Dirty;
        public Border Host = null!;
        public Popup Popup = null!;
    }

    private PinnedFolder? FindFolder(string slotId) => _settings.TaskbarFolders.FirstOrDefault(f => slotId == FolderPrefix + f.Id);

    /// <summary>ボタンの並びに載っていないフォルダー（ピン留めの出どころを切り替えたあとなど）を、並びの最後に加える。</summary>
    private void EnsureFolderSlots()
    {
        var pins = EditablePins;
        bool changed = false;
        foreach (var f in _settings.TaskbarFolders)
            if (!pins.Contains(FolderPrefix + f.Id)) { pins.Add(FolderPrefix + f.Id); changed = true; }
        if (changed) _settings.SaveQuiet();
    }

    /// <summary>
    /// フォルダーに入っているアプリ（フォルダーの中の並び順）と、それぞれの実行中ウィンドウ。
    /// アプリはバーの単独のボタンとも、ほかのフォルダーとも共有できるので、ウィンドウはここで改めてアプリごとに結び付ける。
    /// </summary>
    private List<Member> FolderMembers(PinnedFolder f, List<ApplicationWindow> windows)
    {
        var list = new List<Member>();
        foreach (var id in f.Items)
        {
            if (ResolvePinned(id) is not { } app) continue;
            var g = new Group { Key = "pin:" + app.Id, Pinned = app };
            g.Windows.AddRange(windows.Where(w => KnownApps.Matches(app, Aumid(w), ExePath(w))));
            list.Add(new Member(app, g));
        }
        return list;
    }

    private bool IsWindowsPinId(string id) => PinsFromWindows && _windowsPins.Apps.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------
    // バーの上のボタン

    private Tile FolderButton(PinnedFolder f, List<Member> members)
    {
        var t = Theme.Current;
        var windows = members.SelectMany(m => m.Group.Windows).Distinct().ToList();
        bool running = windows.Count > 0;
        bool active = windows.Any(w => w.State == ApplicationWindow.WindowState.Active);
        bool flashing = windows.Any(w => w.State == ApplicationWindow.WindowState.Flashing);

        var glyph = FolderGlyph(members);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var content = new Grid();
        content.Children.Add(glyph);
        if (running) content.Children.Add(RunIndicator(t, windows.Count, active, flashing));

        var tile = new Tile
        {
            Margin = Gap, Child = content,
            Background = active ? t.Hover : Brushes.Transparent,
            ToolTip = L.F("{0}（{1} 個）", f.Name, members.Count),
        };
        if (Vertical) { tile.Width = TileSize.Width; tile.Height = TileSize.Height; }
        else { tile.MinWidth = TileSize.Width; tile.Height = TileSize.Height; }
        tile.MouseLeave += (_, _) => { if (active) tile.Background = t.Hover; };
        if (_settings.MagnifyOnHover && !Anim.LightMode) AddMagnify(tile, glyph);
        tile.PreviewMouseDown += (_, _) => _preview.Cancel();
        tile.Click += _ => OpenFolder(tile, f, edit: false);
        tile.RightClick += x => ShowIconMenu(x, FolderMenu(tile, f));
        return tile;
    }

    /// <summary>フォルダーのボタンの絵。グリッド: 2×2 のミニ アイコン（最大 4 つ）/ 重ね: アイコンを少しずらして重ねる（最大 3 つ）。</summary>
    private FrameworkElement FolderGlyph(List<Member> members)
    {
        var t = Theme.Current;
        double plate = Math.Round(IconSz * 1.15);
        ImageSource? IconOf(Member m) => CustomIcon(m.Group) ?? ButtonIcon(m.Group);

        var inner = new Grid();
        if (members.Count == 0)
        {
            inner.Children.Add(UiKit.Glyph("", Math.Round(plate * 0.55), t.TextSecondary));
        }
        else if (_settings.TaskbarFolderStyle == TaskbarFolderStyle.Stack)
        {
            var shown = members.Take(3).ToList();
            double s = Math.Round(plate * 0.62);
            // 後ろのものから描く（先頭のアプリが一番手前）
            for (int k = shown.Count - 1; k >= 0; k--)
            {
                var img = UiKit.Icon(IconOf(shown[k]), s);
                img.HorizontalAlignment = HorizontalAlignment.Center;
                img.VerticalAlignment = VerticalAlignment.Center;
                var (dx, dy, angle) = k == 0 ? (shown.Count > 1 ? -0.07 : 0.0, shown.Count > 1 ? 0.06 : 0.0, 0.0)
                                    : k == 1 ? (0.13, -0.08, 9.0)
                                    : (-0.14, -0.12, -9.0);
                img.RenderTransformOrigin = new Point(0.5, 0.5);
                img.RenderTransform = new TransformGroup { Children = { new RotateTransform(angle), new TranslateTransform(dx * plate, dy * plate) } };
                if (k > 0) img.Opacity = 0.85;
                inner.Children.Add(img);
            }
        }
        else
        {
            double cell = Math.Max(6, Math.Floor((plate - 6) / 2) - 1);
            var grid = new UniformGrid { Rows = 2, Columns = 2, Margin = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (var m in members.Take(4))
            {
                var img = UiKit.Icon(IconOf(m), cell);
                img.Margin = new Thickness(0.5);
                grid.Children.Add(img);
            }
            inner.Children.Add(grid);
        }
        return new Border
        {
            Width = plate, Height = plate, CornerRadius = t.Corner(Math.Max(5, plate / 4)),
            Background = t.ControlFill, BorderBrush = t.Stroke, BorderThickness = new Thickness(1),
            Child = inner,
        };
    }

    private IEnumerable<MenuItemSpec> FolderMenu(Tile tile, PinnedFolder f) => new[]
    {
        new MenuItemSpec("", L.T("開く"), () => OpenFolder(tile, f, edit: false)),
        new MenuItemSpec("", L.T("編集"), () => OpenFolder(tile, f, edit: true)),
        new MenuItemSpec("", L.T("名前を変更"), () => RenameFolder(f)),
        MenuItemSpec.Separator,
        new MenuItemSpec("", L.T("フォルダーを解除"), () => DissolveFolder(f), Danger: true),
    };

    // ------------------------------------------------------------------
    // 追加・変更

    /// <summary>新しいフォルダーを、ピン留めの並びの最後に作って、編集の状態で開く。</summary>
    private void AddFolder()
    {
        string name = L.T("フォルダー");
        for (int n = 2; _settings.TaskbarFolders.Any(x => x.Name == name); n++) name = L.F("フォルダー {0}", n);
        var f = new PinnedFolder { Name = name };
        _settings.TaskbarFolders.Add(f);
        EditablePins.Add(FolderPrefix + f.Id);
        _pendingOpenFolder = f.Id;
        _pendingOpenEdit = true;
        _settings.Save();
    }

    /// <summary>バーを作り直したあと、開く予定のフォルダーがあれば開く（新しく作ったとき・名前を変えたあと）。</summary>
    private void OpenPendingFolder()
    {
        if (_pendingOpenFolder == null) return;
        var id = _pendingOpenFolder;
        bool edit = _pendingOpenEdit;
        _pendingOpenFolder = null;
        if (!_folderTiles.TryGetValue(id, out var tile) || FindFolder(FolderPrefix + id) is not { } f) return;
        // 画面に描かれてから（ボタンの位置が決まってから）開く
        tile.Dispatcher.BeginInvoke(() => { if (!_disposed && tile.IsLoaded && tile.IsVisible) OpenFolder(tile, f, edit); }, DispatcherPriority.Loaded);
    }

    /// <summary>設定を保存する。フォルダーを開いている間は、一覧だけ作り直し、バーは閉じたときに作り直す（開いたまま消えないように）。</summary>
    private void SaveFolders()
    {
        if (_openFolder is { } v && v.Popup.IsOpen)
        {
            v.Dirty = true;
            _settings.SaveQuiet();
            RenderFolder(v);
        }
        else _settings.Save();
    }

    private static bool SameId(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>アプリをフォルダーに入れる。バーのピン留めはそのまま残り、ほかのフォルダーにも入れられる。</summary>
    private void MoveToFolder(PinnedFolder f, string appId)
    {
        if (!f.Items.Contains(appId, StringComparer.OrdinalIgnoreCase)) f.Items.Add(appId);
        SaveFolders();
    }

    /// <summary>アプリをフォルダーから出す（バーのピン留めには影響しない）。</summary>
    private void EjectFromFolder(PinnedFolder f, string appId)
    {
        f.Items.RemoveAll(x => SameId(x, appId));
        SaveFolders();
    }

    /// <summary>フォルダーの中の並び替え（id を targetId の前（after = false）か後ろに移す）。</summary>
    private void ReorderInFolder(PinnedFolder f, string id, string targetId, bool after)
    {
        f.Items.RemoveAll(x => SameId(x, id));
        int ti = f.Items.FindIndex(x => SameId(x, targetId));
        f.Items.Insert(ti < 0 ? f.Items.Count : after ? ti + 1 : ti, id);
        SaveFolders();
    }

    /// <summary>フォルダーを解除する（フォルダーだけを消す。バーのピン留めとは無関係）。</summary>
    private void DissolveFolder(PinnedFolder f)
    {
        CloseOpenFolder();
        string slot = FolderPrefix + f.Id;
        _settings.PinnedTaskbar.Remove(slot);
        _settings.PinnedTaskbarExtra.Remove(slot);
        _settings.TaskbarFolders.Remove(f);
        _settings.Save();
    }

    private void RenameFolder(PinnedFolder f)
    {
        CloseOpenFolder();
        new NameDialog(L.T("フォルダーの名前"), f.Name, name =>
        {
            if (!string.IsNullOrWhiteSpace(name) && name != f.Name) f.Name = name.Trim();
            // 名前を付けたあとは、編集の画面に戻る
            _pendingOpenFolder = f.Id;
            _pendingOpenEdit = true;
            _settings.Save();
            Kick(_appsDebounce);
        }).Show();
    }

    // ------------------------------------------------------------------
    // フォルダーの一覧（ポップアップ）

    private void CloseOpenFolder()
    {
        if (_openFolder?.Popup is { IsOpen: true } p) p.IsOpen = false;
        _openFolder = null;
    }

    private void OpenFolder(Tile anchor, PinnedFolder f, bool edit)
    {
        _preview.Cancel();
        CloseOpenFolder();
        var v = new FolderView { F = f, Anchor = anchor, Edit = edit };
        v.Host = new Border { Padding = new Thickness(16), Width = FolderColumns * (CellW + CellGap) + 32 };
        RenderFolder(v);
        var popup = FluentSurface.Create(v.Host, anchor, PlacementMode.Custom);
        v.Popup = popup;
        popup.StaysOpen = false;
        FluentSurface.UseTaskbarLook(popup);
        FluentSurface.PlaceAligned(popup, anchor, Edge, _settings.IconMenuAlign);
        if (_settings.AnimateMenus) FluentSurface.SlideIn(popup, Edge);
        Highlight(anchor, popup);
        _openFolder = v;
        popup.Closed += (_, _) =>
        {
            if (_openFolder == v) _openFolder = null;
            // 開いている間の変更を、バーに反映する
            if (v.Dirty && !_disposed) _settings.Save();
        };
        popup.IsOpen = true;
        Flyout.ToggleGuard(popup, anchor);
    }

    private void RenderFolder(FolderView v)
    {
        v.Host.Child = v.Picker ? FolderPicker(v) : FolderContent(v);
    }

    /// <summary>一覧から開いたメニュー（右クリック・ウィンドウ名のリスト）。開いている間は、一覧が閉じないよう固定する。</summary>
    private void ShowFromFolder(FolderView v, UIElement target, IEnumerable<MenuItemSpec> items)
    {
        v.Popup.StaysOpen = true;
        var menu = Flyout.Show(target, items, PlacementMode.MousePoint, taskbarLook: true);
        menu.Closed += (_, _) => { if (v.Popup.IsOpen) v.Popup.StaysOpen = false; };
    }

    /// <summary>角丸のボタン（編集・完了）。accent: アクセント色で塗る。</summary>
    private static FrameworkElement Pill(string text, bool accent, Action click)
    {
        var t = Theme.Current;
        var label = UiKit.Label(text, 13, accent ? t.OnAccent : t.Text, accent ? FontWeights.SemiBold : FontWeights.Normal);
        var tile = new Tile
        {
            CornerRadius = new CornerRadius(15), Height = 30, Padding = new Thickness(16, 0, 16, 0), Child = label,
            BorderBrush = accent ? Brushes.Transparent : t.Stroke, BorderThickness = new Thickness(1),
        };
        tile.Click += _ => click();
        return accent
            ? new Border { CornerRadius = new CornerRadius(15), Background = t.AccentBrush, Child = tile, VerticalAlignment = VerticalAlignment.Center }
            : new Border { VerticalAlignment = VerticalAlignment.Center, Child = tile };
    }

    private FrameworkElement FolderContent(FolderView v)
    {
        var t = Theme.Current;
        var members = FolderMembers(v.F, TaskWindows().ToList());
        int runningApps = members.Count(m => m.Group.Windows.Count > 0);

        var root = new StackPanel();

        // 見出し: 名前（編集中は押すと変更）・個数・実行中の数 / 右に「編集」「完了」
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = UiKit.Label(v.F.Name, 16, t.Text, FontWeights.SemiBold);
        if (v.Edit)
        {
            var nameRow = UiKit.Row(title, UiKit.Glyph("", 12, t.TextSecondary));
            ((FrameworkElement)nameRow.Children[1]).Margin = new Thickness(8, 0, 0, 0);
            var nameTile = new Tile { Child = nameRow, Padding = new Thickness(6, 2, 8, 2), Margin = new Thickness(-6, -2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = L.T("名前を変更") };
            nameTile.Click += _ => RenameFolder(v.F);
            texts.Children.Add(nameTile);
        }
        else texts.Children.Add(title);
        texts.Children.Add(UiKit.Label(L.F("{0} 個のアプリ ・ 実行中 {1}", members.Count, runningApps), 12, t.TextSecondary));
        var toggle = Pill(v.Edit ? L.T("完了") : L.T("編集"), accent: v.Edit, () => { v.Edit = !v.Edit; RenderFolder(v); });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(toggle, Dock.Right);
        header.Children.Add(toggle);
        header.Children.Add(texts);
        root.Children.Add(header);

        // アプリ
        var grid = new UniformGrid { Columns = FolderColumns };
        for (int i = 0; i < members.Count; i++) grid.Children.Add(FolderCell(v, members, i, grid));
        if (v.Edit) grid.Children.Add(AddCell(v));
        if (members.Count == 0 && !v.Edit)
        {
            var empty = UiKit.Label(L.T("アプリがありません。「編集」で追加できます"), 13, t.TextSecondary);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 24, 0, 24);
            root.Children.Add(empty);
        }
        else root.Children.Add(grid);

        root.Children.Add(new Border { Height = 1, Background = t.Stroke, Margin = new Thickness(0, 12, 0, 10) });
        var hint = UiKit.Label(v.Edit
            ? L.T("アプリをドラッグして並べ替え。バーの外へドラッグすると、フォルダーから出せます")
            : L.T("クリックでアプリを起動・右クリックで「フォルダーから出す」など・下線は実行中のアプリ"), 12, t.TextSecondary);
        hint.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(hint);
        return root;
    }

    private FrameworkElement FolderCell(FolderView v, List<Member> members, int index, UniformGrid grid)
    {
        var t = Theme.Current;
        var m = members[index];
        var g = m.Group;
        bool running = g.Windows.Count > 0;
        bool active = g.Windows.Any(w => w.State == ApplicationWindow.WindowState.Active);

        var img = UiKit.Icon(CustomIcon(g) ?? ButtonIcon(g), 32);
        if (img.Source == null) LoadAppIcon(img, m.App, 32);
        var label = UiKit.Label(m.App.Name, 12);
        label.MaxWidth = CellW - 8;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.Margin = new Thickness(0, 6, 0, 0);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(img);
        stack.Children.Add(label);
        stack.Children.Add(new Border
        {
            Width = active ? 16 : 6, Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            Background = running ? (active ? t.AccentBrush : t.TextSecondary) : Brushes.Transparent,
        });
        var tile = new Tile { Child = stack, ToolTip = m.App.Name, CornerRadius = t.Corner(8) };
        var cell = new Grid { Width = CellW, Height = CellH, Margin = new Thickness(CellGap / 2) };

        if (!v.Edit)
        {
            cell.Children.Add(tile);
            tile.Click += x =>
            {
                if (g.Windows.Count == 0) { TakeForeground(x); CloseOpenFolder(); m.App.Launch(); }
                else if (g.Windows.Count == 1) { ToggleWindow(g.Windows[0], x); CloseOpenFolder(); }
                else ShowFromFolder(v, x, g.Windows.Select(w => new MenuItemSpec("", Truncate(w.Title ?? "", 48), () => { TakeForeground(x); w.BringToFront(); CloseOpenFolder(); })));
            };
            tile.MiddleClick += x => { TakeForeground(x); CloseOpenFolder(); m.App.Launch(); };
            tile.RightClick += x => ShowFromFolder(v, x, GroupMenu(g, v.F));
            return cell;
        }

        // 編集中: 枠のある札にして、左上の「×」でフォルダーから出す。ドラッグで並べ替え・バーの外へ出す
        cell.Children.Add(new Border { Background = t.Surface, CornerRadius = t.Corner(8) });
        cell.Children.Add(tile);
        // 「×」の色は外枠で付ける（Tile は押す・重ねると背景を差し替えるので、色は Tile の外に置く）
        var remove = new Tile { CornerRadius = new CornerRadius(11), Child = UiKit.Glyph("", 10, Brushes.White) };
        var removeHost = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x5F)),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-4, -4, 0, 0),
            ToolTip = L.T("フォルダーから出す"), Child = remove,
        };
        remove.Click += _ => EjectFromFolder(v.F, m.App.Id);
        cell.Children.Add(removeHost);
        EnableFolderDrag(v, cell, removeHost, members, index, grid);
        return cell;
    }

    /// <summary>編集中の「＋」（アプリを追加）。</summary>
    private FrameworkElement AddCell(FolderView v)
    {
        var t = Theme.Current;
        var plus = new Tile
        {
            CornerRadius = t.Corner(8), Child = UiKit.Glyph("", 18, t.TextSecondary), ToolTip = L.T("アプリを追加"),
            Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        plus.Click += _ => { v.Picker = true; RenderFolder(v); };
        var cell = new Grid { Width = CellW, Height = CellH, Margin = new Thickness(CellGap / 2) };
        cell.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 44, Height = 44, RadiusX = t.Corner(8).TopLeft, RadiusY = t.Corner(8).TopLeft,
            Stroke = t.TextSecondary, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }, Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        });
        cell.Children.Add(plus);
        return cell;
    }

    /// <summary>
    /// 編集中の札のドラッグ: つかむとカーソルに付いてくる。ほかの札の上で離すとその位置へ並べ替え、
    /// 一覧の外（バーの上など）で離すとフォルダーから出してバーに戻す。
    /// </summary>
    private void EnableFolderDrag(FolderView v, FrameworkElement cell, FrameworkElement closeButton, List<Member> members, int index, UniformGrid grid)
    {
        const double Threshold = 6;
        Point start = default;
        bool pending = false, dragging = false;
        var move = new TranslateTransform();
        cell.RenderTransform = move;

        bool Outside(MouseEventArgs e)
        {
            var p = e.GetPosition(v.Host);
            return p.X < -10 || p.Y < -10 || p.X > v.Host.ActualWidth + 10 || p.Y > v.Host.ActualHeight + 10;
        }

        cell.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, closeButton)) { pending = false; return; }
            pending = true;
            start = e.GetPosition(grid);
        };
        cell.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(grid);
            if (!dragging)
            {
                if (!pending || (p - start).Length < Threshold) return;
                dragging = true;
                cell.CaptureMouse();
                Panel.SetZIndex(cell, 10);
                cell.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.35 };
            }
            move.X = p.X - start.X;
            move.Y = p.Y - start.Y;
            cell.Opacity = Outside(e) ? 0.45 : 0.9;
        };
        cell.PreviewMouseLeftButtonUp += (_, e) =>
        {
            pending = false;
            if (!dragging) return;
            dragging = false;
            e.Handled = true;
            bool outside = Outside(e);
            var p = e.GetPosition(grid);
            cell.ReleaseMouseCapture();
            cell.Effect = null;
            cell.Opacity = 1;
            Panel.SetZIndex(cell, 0);
            if (outside) { EjectFromFolder(v.F, members[index].App.Id); return; }
            int col = Math.Clamp((int)Math.Floor(p.X / (CellW + CellGap)), 0, FolderColumns - 1);
            int row = Math.Max(0, (int)Math.Floor(p.Y / (CellH + CellGap)));
            int to = Math.Clamp(row * FolderColumns + col, 0, members.Count - 1);
            if (to == index) { move.X = move.Y = 0; return; }
            ReorderInFolder(v.F, members[index].App.Id, members[to].App.Id, after: to > index);
        };
        cell.LostMouseCapture += (_, _) =>
        {
            if (!dragging) return;
            dragging = false;
            cell.Opacity = 1;
            cell.Effect = null;
            move.X = move.Y = 0;
        };
    }

    /// <summary>「アプリを追加」の画面: バーのアプリ（ピン留め・実行中）→ すべてのアプリ。選ぶとフォルダーに入り、編集の画面に戻る。</summary>
    private FrameworkElement FolderPicker(FolderView v)
    {
        var t = Theme.Current;
        var taken = v.F.Items.ToHashSet(StringComparer.OrdinalIgnoreCase); // このフォルダーに入っているもの（ほかのフォルダーのものは選べる）

        var back = new Tile { Width = 32, Height = 32, CornerRadius = t.Corner(6), Child = UiKit.Glyph("", 14), ToolTip = L.T("戻る") };
        back.Click += _ => { v.Picker = false; RenderFolder(v); };
        var title = UiKit.Label(L.T("アプリを追加"), 16, t.Text, FontWeights.SemiBold);
        title.Margin = new Thickness(10, 0, 0, 0);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(back, Dock.Left);
        header.Children.Add(back);
        header.Children.Add(title);

        // バーに出ているアプリ（フォルダーに入っていないピン留めと、起動中のもの）
        var groups = BuildGroups(TaskWindows(), withPins: true);
        var onBar = new List<AppEntry>();
        foreach (var g in groups)
        {
            if (g.Pinned != null) onBar.Add(g.Pinned);
            else if (g.Pinned == null && g.Windows.Count > 0 && FindApp(g.Windows[0]) is { } a) onBar.Add(a);
        }
        onBar = onBar.Where(a => !taken.Contains(a.Id)).DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var barIds = onBar.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = _catalog.Apps.Where(a => !taken.Contains(a.Id) && !barIds.Contains(a.Id))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var list = new StackPanel();
        void Section(string text, List<AppEntry> apps)
        {
            if (apps.Count == 0) return;
            var h = UiKit.Label(text, 12, t.TextSecondary, FontWeights.SemiBold);
            h.Margin = new Thickness(8, list.Children.Count == 0 ? 0 : 10, 0, 4);
            list.Children.Add(h);
            foreach (var app in apps)
            {
                var icon = UiKit.Icon(null, 24);
                LoadAppIcon(icon, app, 24);
                var name = UiKit.Label(app.Name, 13);
                name.Margin = new Thickness(12, 0, 0, 0);
                var row = new Tile { Height = 36, Padding = new Thickness(8, 0, 8, 0), Child = UiKit.Row(icon, name) };
                var picked = app;
                row.Click += _ => { v.Picker = false; MoveToFolder(v.F, picked.Id); };
                list.Children.Add(row);
            }
        }
        Section(L.T("タスクバーのアプリ"), onBar);
        Section(L.T("すべてのアプリ"), others);
        if (list.Children.Count == 0)
        {
            var none = UiKit.Label(L.T("追加できるアプリがありません"), 13, t.TextSecondary);
            none.Margin = new Thickness(8, 12, 8, 12);
            list.Children.Add(none);
        }

        var scroll = new ScrollViewer
        {
            Content = list, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(scroll);
        return root;
    }

    private static void LoadAppIcon(Image img, AppEntry app, int size)
    {
        bool light = !Theme.Current.IsDark;
        int px = (int)Math.Ceiling(size * Math.Max(1.0, Win32.GetDpiForSystem() / 96.0));
        IconThread.Run(() => IconHelper.ForApp(app, px, light)).ContinueWith(r =>
        {
            if (r.Result != null) img.Dispatcher.BeginInvoke(() => img.Source = r.Result);
        }, TaskScheduler.Default);
    }
}

/// <summary>フォルダーの名前を入力する小さなウィンドウ（バーのポップアップは文字入力を受けられないので、通常のウィンドウで入力する）。Enter で確定、Esc・ほかを押すと取り消し。</summary>
internal sealed class NameDialog : Window
{
    private readonly TextBox _box;
    private readonly Action<string?> _done;
    private bool _finished, _armed;

    public NameDialog(string title, string initial, Action<string?> done)
    {
        var t = Theme.Current;
        _done = done;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = t.SolidBackground;
        FontFamily = t.UiFont;

        _box = new TextBox
        {
            Text = initial, MaxLength = 40, FontSize = 14, Foreground = t.Text, CaretBrush = t.Text, Background = t.Surface,
            BorderBrush = t.AccentBrush, BorderThickness = new Thickness(0, 0, 0, 2), Padding = new Thickness(8, 7, 8, 7),
            Margin = new Thickness(0, 12, 0, 16), VerticalContentAlignment = VerticalAlignment.Center,
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(_box.Text); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(null); e.Handled = true; }
        };

        var cancel = new Tile { CornerRadius = new CornerRadius(6), Height = 32, Padding = new Thickness(18, 0, 18, 0), Child = UiKit.Label(L.T("キャンセル"), 13), BorderBrush = t.Stroke, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += _ => Finish(null);
        var ok = new Tile { CornerRadius = new CornerRadius(6), Height = 32, Padding = new Thickness(22, 0, 22, 0), Child = UiKit.Label("OK", 13, t.OnAccent, FontWeights.SemiBold) };
        ok.Click += _ => Finish(_box.Text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = t.AccentBrush, Child = ok });

        var body = new StackPanel { Width = 320 };
        body.Children.Add(UiKit.Label(title, 15, t.Text, FontWeights.SemiBold));
        body.Children.Add(_box);
        body.Children.Add(buttons);
        Content = new Border { BorderBrush = t.Stroke, BorderThickness = new Thickness(1), Padding = new Thickness(20), Child = body };

        Loaded += (_, _) =>
        {
            Foregrounder.Force(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            _box.Focus();
            _box.SelectAll();
        };
        // ほかをクリックしたら取り消し（最初に前面になる前の「非アクティブ」には反応しない）
        Activated += (_, _) => _armed = true;
        Deactivated += (_, _) => { if (_armed) Finish(null); };
        Closed += (_, _) => Finish(null);
    }

    private void Finish(string? text)
    {
        if (_finished) return;
        _finished = true;
        try { _done(text); } finally { try { Close(); } catch { } }
    }
}
