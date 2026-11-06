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

/// <summary>タスクバー右側: トレイアイコン（常に表示 / 「^」の一覧 / ドラッグでの出し入れ）、クイック設定、時計。</summary>
public sealed partial class TaskbarController
{
    // ------------------------------------------------------------------
    // 右側（トレイアイコン / クイック設定 / 時計）

    private StackPanel TrayGroup(Theme t)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _overflowButton = new Tile
        {
            Width = 28, Height = TileH, Margin = new Thickness(2, 0, 0, 0),
            Child = UiKit.Glyph(IsTop ? "" : "", 12), ToolTip = "隠れているインジケーターを表示",
        };
        _overflowButton.Click += ShowOverflow;
        MakeOverflowDropTarget(_overflowButton); // 「^」の上に落としてもしまえる
        p.Children.Add(_overflowButton);

        _trayIcons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(_trayIcons);
        MakeTrayDropTarget(p);

        _network = UiKit.Glyph("", 15);
        _battery = UiKit.Glyph("", 15);
        _battery.Margin = new Thickness(10, 0, 0, 0);
        var volume = UiKit.Glyph("", 15);
        volume.Margin = new Thickness(10, 0, 0, 0);
        var quick = new Tile
        {
            Padding = new Thickness(10, 0, 10, 0), Height = TileH, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = UiKit.Row(_network, volume, _battery), ToolTip = "クイック設定 (Win+A)",
        };
        quick.Click += _ => SystemActions.QuickSettings();
        p.Children.Add(quick);

        _time = UiKit.Label("", 12);
        _date = UiKit.Label("", 12);
        _time.HorizontalAlignment = _date.HorizontalAlignment = HorizontalAlignment.Right;
        var clockStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        clockStack.Children.Add(_time);
        clockStack.Children.Add(_date);
        if (Compact) _date.Visibility = Visibility.Collapsed; // 低いバーでは日付を省略（ツールチップで確認）
        var clock = new Tile
        {
            Padding = new Thickness(10, 0, 10, 0), Height = TileH, Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = clockStack, ToolTip = "通知とカレンダー (Win+N)",
        };
        clock.Click += _ => SystemActions.NotificationCenter();
        p.Children.Add(clock);
        return p;
    }

    private Tile ShowDesktopSliver()
    {
        var desk = new Tile { Width = 10, Height = TileH, CornerRadius = new CornerRadius(0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "デスクトップの表示" };
        desk.Click += _ => SystemActions.ShowDesktop();
        return desk;
    }

    private static bool Visible(ManagedShell.WindowsTray.NotifyIcon i) =>
        !i.IsHidden && !ShellHost.HiddenSystemIcons.Contains(i.GUID.ToString());

    private IEnumerable<ManagedShell.WindowsTray.NotifyIcon> Icons(ICollectionView? view) =>
        view?.Cast<ManagedShell.WindowsTray.NotifyIcon>().Where(Visible) ?? Enumerable.Empty<ManagedShell.WindowsTray.NotifyIcon>();

    private void RebuildTray()
    {
        if (_trayIcons == null || _overflowButton == null) return;
        _trayIcons.Children.Clear();
        var pinned = Icons(_shell.Tray?.PinnedIcons).OrderBy(i => i.PinOrder).ToList();
        var reorder = new LiveReorder(_trayIcons, Orientation.Horizontal, (from, to) => MoveTrayIcon(pinned, from, to))
        {
            // 上へ引き出すと「^」の一覧が開き、そこへ落とせばしまえる（Windows と同じ）
            DragOut = el => { if (el.Tag is ManagedShell.WindowsTray.NotifyIcon ni) StartTrayDrag(ni, el); },
            // 横に動かして「^」の上に来たときも、しまう操作へ切り替える
            LeaveWhen = e => _overflowButton is { IsVisible: true } b && IsOver(b, e),
        };
        foreach (var icon in pinned)
        {
            Watch(icon);
            _trayIcons.Children.Add(TrayIconTile(icon, editable: false, reorder));
        }
        _overflowButton.Visibility = Icons(_shell.Tray?.UnpinnedIcons).Any() || _trayEditMode ? Visibility.Visible : Visibility.Collapsed;
        if (_overflowPopup?.IsOpen == true) FillOverflow();
    }

    /// <summary>トレイアイコン 1 個。マウス操作はそのままアプリへ転送する（右クリックメニューもアプリ自身が出す）。</summary>
    private Tile TrayIconTile(ManagedShell.WindowsTray.NotifyIcon icon, bool editable, LiveReorder? reorder = null, bool dragToBar = false)
    {
        var img = UiKit.Icon(icon.Icon, 16);
        var grid = new Grid();
        grid.Children.Add(img);
        if (editable && _trayEditMode)
        {
            grid.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = icon.IsPinned ? Theme.Current.AccentBrush : Theme.Current.TextSecondary, Margin = new Thickness(0, -4, -6, 0),
            });
        }
        var tile = new Tile { Width = Compact ? 26 : 32, Height = Math.Min(36, TileH), Margin = new Thickness(1, 0, 1, 0), Child = grid, ToolTip = icon.Title, Tag = icon };

        if (editable && _trayEditMode)
        {
            tile.Click += _ =>
            {
                if (icon.IsPinned) icon.Unpin(); else icon.Pin();
                _shell.SavePinnedTray(_settings);
                RebuildTray();
            };
            if (icon.IsPinned) reorder?.Add(tile); // 常に表示するアイコン同士はドラッグで並べ替え
            return tile;
        }

        int dbl = Forms.SystemInformation.DoubleClickTime;
        tile.MouseEnter += (_, _) => { SetPlacement(icon, tile); icon.IconMouseEnter(MouseHelper.GetCursorPositionParam()); };
        tile.MouseLeave += (_, _) => icon.IconMouseLeave(MouseHelper.GetCursorPositionParam());
        tile.MouseMove += (_, _) => icon.IconMouseMove(MouseHelper.GetCursorPositionParam());
        // 並べ替え可能なアイコンは、左ボタンの押下をすぐにはアプリへ送らず、離したときに「ドラッグでなかった」場合だけ
        // 押下 → 解放の順に送る（ドラッグ中にアプリのメニューが開かないように）
        reorder?.Add(tile);
        bool deferLeft = reorder != null || dragToBar;
        Point? downAt = null;
        bool draggedOut = false;
        tile.PreviewMouseDown += (_, e) =>
        {
            SetPlacement(icon, tile);
            if (deferLeft && e.ChangedButton == MouseButton.Left)
            {
                downAt = e.GetPosition(tile);
                draggedOut = false;
                if (dragToBar) e.Handled = true;
                return;
            }
            icon.IconMouseDown(e.ChangedButton, MouseHelper.GetCursorPositionParam(), dbl);
            e.Handled = true;
        };
        if (dragToBar)
        {
            // 「^」の中のアイコンをタスクバーへドラッグ → 常に表示（Windows と同じ）
            tile.PreviewMouseMove += (_, e) =>
            {
                if (downAt is not { } p0 || e.LeftButton != MouseButtonState.Pressed) return;
                var d = e.GetPosition(tile) - p0;
                if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
                downAt = null;
                draggedOut = true;
                StartTrayDrag(icon, tile);
            };
        }
        tile.PreviewMouseUp += (_, e) =>
        {
            if (deferLeft && e.ChangedButton == MouseButton.Left)
            {
                downAt = null;
                if (draggedOut || reorder?.IsDragging == true) return; // ドラッグだった
                if (reorder?.ConsumeDragged() == true) return;
                SetPlacement(icon, tile);
                icon.IconMouseDown(MouseButton.Left, MouseHelper.GetCursorPositionParam(), dbl);
            }
            SetPlacement(icon, tile);
            icon.IconMouseUp(e.ChangedButton, MouseHelper.GetCursorPositionParam(), dbl);
            if (_overflowPopup != null) _overflowPopup.IsOpen = false;
            e.Handled = true;
        };
        return tile;
    }

    // ---- トレイアイコンのドラッグで「常に表示」⇔「^ の中にしまう」 ----

    /// <summary>トレイアイコンのドラッグを始める。「^」の一覧を開いておき、そこへ落とせばしまう、タスクバーへ落とせば常に表示。</summary>
    private void StartTrayDrag(ManagedShell.WindowsTray.NotifyIcon icon, FrameworkElement source)
    {
        if (_overflowButton == null) return;
        _overflowButton.Visibility = Visibility.Visible;
        if (_overflowPopup?.IsOpen != true) ShowOverflow(_overflowButton);
        if (_overflowPopup != null) _overflowPopup.StaysOpen = true; // ドラッグ中に閉じないように
        _overflowButton.IsHitTestVisible = true; // 「^」自体にも落とせるように（開閉トグルのための無効化を解除）
        try { DragDrop.DoDragDrop(source, new DataObject("ModernShell.TrayIcon", icon), DragDropEffects.Move); }
        catch (Exception ex) { Log.Error("tray drag", ex); }
        if (_overflowPopup?.IsOpen == true) _overflowPopup.StaysOpen = _trayEditMode;
        ClearTrayDropLine();
    }

    private static bool IsOver(FrameworkElement el, MouseEventArgs e)
    {
        var p = e.GetPosition(el);
        return p.X >= 0 && p.Y >= 0 && p.X < el.ActualWidth && p.Y < el.ActualHeight;
    }

    private static ManagedShell.WindowsTray.NotifyIcon? DraggedIcon(DragEventArgs e) =>
        e.Data.GetData("ModernShell.TrayIcon") as ManagedShell.WindowsTray.NotifyIcon;

    /// <summary>タスクバー側（トレイ部分）をドロップ先にする。落とした位置に「常に表示」で入れる。</summary>
    private void MakeTrayDropTarget(Panel trayGroup)
    {
        trayGroup.AllowDrop = true;
        if (trayGroup.Background == null) trayGroup.Background = Brushes.Transparent;
        trayGroup.DragOver += (_, e) =>
        {
            if (DraggedIcon(e) is not { } icon || _trayIcons == null) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            e.Effects = DragDropEffects.Move;
            ShowTrayDropLine(TrayInsertIndex(e));
            e.Handled = true;
        };
        // 子要素の間を移るときにも DragLeave が来るので、本当にトレイ部分の外に出たときだけ隙間を閉じる
        trayGroup.DragLeave += (_, e) =>
        {
            var p = e.GetPosition(trayGroup);
            if (p.X >= 0 && p.Y >= 0 && p.X < trayGroup.ActualWidth && p.Y < trayGroup.ActualHeight) return;
            ClearTrayDropLine();
        };
        trayGroup.Drop += (_, e) =>
        {
            ClearTrayDropLine();
            if (DraggedIcon(e) is not { } icon || _trayIcons == null) return;
            var pinned = _trayIcons.Children.OfType<FrameworkElement>().Select(t => t.Tag).OfType<ManagedShell.WindowsTray.NotifyIcon>().ToList();
            int index = TrayInsertIndex(e);
            int position = index < pinned.Count ? pinned[index].PinOrder : (_shell.Tray?.PinnedNotifyIcons.Length ?? 0);
            icon.Pin(position); // しまってあったアイコンは常に表示に、常に表示のアイコンはその位置へ移動
            _shell.SavePinnedTray(_settings);
            RebuildTray();
            if (_overflowPopup?.IsOpen == true) FillOverflow();
            e.Handled = true;
        };
    }

    /// <summary>カーソル位置が、常に表示しているアイコンの何番目の前にあたるか（隙間は数えない）。</summary>
    private int TrayInsertIndex(DragEventArgs e)
    {
        if (_trayIcons == null) return 0;
        int i = 0;
        foreach (var el in _trayIcons.Children.OfType<FrameworkElement>().Where(x => x.Tag is ManagedShell.WindowsTray.NotifyIcon))
        {
            if (e.GetPosition(el).X < el.ActualWidth / 2) return i;
            i++;
        }
        return i;
    }

    // ドロップ位置に開く隙間。位置が変わったら古い隙間は縮めて消し、新しい隙間を広げる（隣のアイコンがぬるっとよける）
    private Border? _dropGap;
    private int _dropGapIndex = -1;

    private void ShowTrayDropLine(int index)
    {
        if (_trayIcons == null || index == _dropGapIndex) return;
        _dropGapIndex = index;
        CollapseGap(_dropGap);
        double pitch = (Compact ? 26 : 32) + 2;
        var gap = new Border
        {
            Width = 0, Height = Math.Min(36, TileH) - 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(1, 0, 1, 0),
            BorderBrush = Theme.Current.AccentBrush, BorderThickness = new Thickness(1), Background = Theme.Current.Hover, IsHitTestVisible = false,
        };
        // 隙間を除いたアイコンの index 番目の直前に入れる
        int child = 0, seen = 0;
        for (; child < _trayIcons.Children.Count; child++)
        {
            if (_trayIcons.Children[child] is FrameworkElement { Tag: ManagedShell.WindowsTray.NotifyIcon })
            {
                if (seen == index) break;
                seen++;
            }
        }
        _trayIcons.Children.Insert(child, gap);
        gap.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(0, pitch - 2, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _dropGap = gap;
    }

    private void CollapseGap(Border? gap)
    {
        if (gap == null || _trayIcons == null) return;
        var a = new DoubleAnimation(gap.ActualWidth, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        a.Completed += (_, _) => _trayIcons?.Children.Remove(gap);
        gap.BeginAnimation(FrameworkElement.WidthProperty, a);
    }

    private void ClearTrayDropLine()
    {
        CollapseGap(_dropGap);
        _dropGap = null;
        _dropGapIndex = -1;
    }

    private static void SetBg(FrameworkElement el, Brush b)
    {
        if (el is Panel p) p.Background = b;
        else if (el is Border bd) bd.Background = b;
    }

    /// <summary>「^」の一覧をドロップ先にする。落としたアイコンは「^」の中にしまう。</summary>
    private void MakeOverflowDropTarget(FrameworkElement area)
    {
        area.AllowDrop = true;
        var normal = Brushes.Transparent;
        area.DragOver += (_, e) =>
        {
            bool ok = DraggedIcon(e) is { IsPinned: true };
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            SetBg(area, ok ? Theme.Current.Hover : normal);
            e.Handled = true;
        };
        area.DragLeave += (_, _) => SetBg(area, normal);
        area.Drop += (_, e) =>
        {
            SetBg(area, normal);
            if (DraggedIcon(e) is not { IsPinned: true } icon) return;
            icon.Unpin();
            _shell.SavePinnedTray(_settings);
            RebuildTray();
            FillOverflow();
            e.Handled = true;
        };
    }

    /// <summary>常に表示するトレイアイコンの並べ替えを確定する。</summary>
    private void MoveTrayIcon(List<ManagedShell.WindowsTray.NotifyIcon> visible, int from, int to)
    {
        var dragged = visible[from];
        var target = visible[to];
        dragged.Pin(target.PinOrder + (to > from ? 1 : 0));
        _shell.SavePinnedTray(_settings);
        RebuildTray();
    }

    /// <summary>アプリがメニューやポップアップを正しい位置に出せるよう、アイコンの画面上の矩形を渡す。</summary>
    private static void SetPlacement(ManagedShell.WindowsTray.NotifyIcon icon, FrameworkElement el)
    {
        try
        {
            var tl = el.PointToScreen(new Point(0, 0));
            var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
            icon.Placement = new NativeMethods.Rect((int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y);
        }
        catch { }
    }

    private void UpdateTrayHostData()
    {
        if (_shell.Tray == null || _trayHost == null || !_trayHost.IsLoaded) return;
        try
        {
            var tl = _trayHost.PointToScreen(new Point(0, 0));
            var br = _trayHost.PointToScreen(new Point(_trayHost.ActualWidth, _trayHost.ActualHeight));
            _shell.Tray.SetTrayHostSizeData(new TrayHostSizeData
            {
                edge = IsTop ? NativeMethods.ABEdge.ABE_TOP : NativeMethods.ABEdge.ABE_BOTTOM,
                rc = new NativeMethods.Rect((int)tl.X, (int)tl.Y, (int)br.X, (int)br.Y),
            });
        }
        catch { }
    }

    // 「^」で開く隠れたアイコン一覧
    private StackPanel? _overflowPanel;
    private TextBlock? _overflowHint;

    private void ShowOverflow(Tile anchor)
    {
        if (_overflowPopup?.IsOpen == true) { _overflowPopup.IsOpen = false; return; } // 編集中（固定表示）に「^」で閉じる
        var t = Theme.Current;
        _overflowPanel = new StackPanel { Margin = new Thickness(6) };
        _overflowHint = UiKit.Label("", 11, t.TextSecondary);
        _overflowHint.TextWrapping = TextWrapping.Wrap;
        _overflowHint.MaxWidth = 6 * 34;
        _overflowHint.Margin = new Thickness(8, 0, 8, 4);

        var edit = new Tile { Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Right };
        void UpdateEdit()
        {
            edit.Child = UiKit.Row(UiKit.Glyph(_trayEditMode ? "" : "", 12), Spaced(UiKit.Label(_trayEditMode ? "完了" : "表示するアイコンを選ぶ", 12)));
            _overflowHint!.Text = _trayEditMode ? "クリックで表示/非表示を切り替え、「常に表示」の中はドラッグで並べ替えできます" : "";
            _overflowHint.Visibility = _trayEditMode ? Visibility.Visible : Visibility.Collapsed;
        }
        edit.Click += _ =>
        {
            _trayEditMode = !_trayEditMode;
            // 編集中はドラッグでマウスを奪っても閉じないよう固定表示（「完了」か「^」で閉じる）
            if (_overflowPopup != null) _overflowPopup.StaysOpen = _trayEditMode;
            if (_trayEditMode) anchor.IsHitTestVisible = true; // 固定表示中は「^」を押して閉じられるように
            UpdateEdit(); FillOverflow(); RebuildTray();
        };
        UpdateEdit();

        var stack = new StackPanel { Background = Brushes.Transparent };
        MakeOverflowDropTarget(stack);
        stack.Children.Add(_overflowPanel);
        stack.Children.Add(_overflowHint);
        stack.Children.Add(edit);

        _overflowPopup = FluentSurface.Create(stack, anchor, FlyoutPlacement);
        // Windows と同じく「^」ボタンを基準に左右中央へ出す（画面端では自動で内側にずれる）
        FluentSurface.PlaceCentered(_overflowPopup, anchor, IsTop);
        _overflowPopup.Closed += (_, _) => { if (_trayEditMode) { _trayEditMode = false; RebuildTray(); } };
        FillOverflow();
        FluentSurface.SlideIn(_overflowPopup, fromBelow: !IsTop);
        _overflowPopup.IsOpen = true;
        Flyout.ToggleGuard(_overflowPopup, anchor);
    }

    private void FillOverflow()
    {
        if (_overflowPanel == null) return;
        _overflowPanel.Children.Clear();
        var t = Theme.Current;

        WrapPanel Section(string? title)
        {
            if (title != null)
            {
                var h = UiKit.Label(title, 11, t.TextSecondary, FontWeights.SemiBold);
                h.Margin = new Thickness(4, _overflowPanel!.Children.Count == 0 ? 0 : 8, 0, 4);
                _overflowPanel!.Children.Add(h);
            }
            var w = new WrapPanel { MaxWidth = 6 * 34 }; // Windows と同じく数個ごとに折り返す
            _overflowPanel!.Children.Add(w);
            return w;
        }

        if (_trayEditMode)
        {
            Section("常に表示（ドラッグで並べ替え）");
            _overflowPanel.Children.RemoveAt(_overflowPanel.Children.Count - 1); // 折り返しパネルの代わりに 1 行のパネルを使う
            var pinnedRow = new StackPanel { Orientation = Orientation.Horizontal };
            _overflowPanel.Children.Add(pinnedRow);
            var pinnedIcons = Icons(_shell.Tray?.PinnedIcons).OrderBy(i => i.PinOrder).ToList();
            var reorder = new LiveReorder(pinnedRow, Orientation.Horizontal, (from, to) => { MoveTrayIcon(pinnedIcons, from, to); FillOverflow(); });
            foreach (var icon in pinnedIcons)
            {
                Watch(icon);
                pinnedRow.Children.Add(TrayIconTile(icon, editable: true, reorder));
            }
            if (pinnedRow.Children.Count == 0) pinnedRow.Children.Add(UiKit.Label("なし", 12, t.TextSecondary));
            var others = Section("「^」の中に表示");
            foreach (var icon in Icons(_shell.Tray?.UnpinnedIcons))
            {
                Watch(icon);
                others.Children.Add(TrayIconTile(icon, editable: true));
            }
            if (others.Children.Count == 0) others.Children.Add(UiKit.Label("なし", 12, t.TextSecondary));
            return;
        }

        var all = Section(null);
        foreach (var icon in Icons(_shell.Tray?.UnpinnedIcons))
        {
            Watch(icon);
            all.Children.Add(TrayIconTile(icon, editable: true, dragToBar: true));
        }
        if (all.Children.Count == 0)
            all.Children.Add(new TextBlock { Text = "アイコンはありません", Margin = new Thickness(8), Foreground = t.TextSecondary });
    }

    private static FrameworkElement Spaced(FrameworkElement e) { e.Margin = new Thickness(8, 0, 0, 0); return e; }

    private void UpdateClock()
    {
        if (_time == null || _date == null) return;
        var now = DateTime.Now;
        _time.Text = now.ToString(_settings.ShowSeconds ? "H:mm:ss" : "H:mm");
        _date.Text = now.ToString("yyyy/MM/dd");
    }

    /// <summary>ネットワーク接続とバッテリー残量をアイコンに反映。</summary>
    private void UpdateStatus()
    {
        if (_network == null || _battery == null) return;
        var t = Theme.Current;
        bool online = NetworkInterface.GetIsNetworkAvailable();
        _network.Foreground = online ? t.Text : t.TextSecondary;
        _network.Opacity = online ? 1 : 0.5;

        try
        {
            var ps = Forms.SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery))
            {
                _battery.Visibility = Visibility.Collapsed; // デスクトップ PC
                return;
            }
            _battery.Visibility = Visibility.Visible;
            int level = (int)Math.Round(Math.Clamp(ps.BatteryLifePercent, 0f, 1f) * 10);
            bool charging = ps.PowerLineStatus == Forms.PowerLineStatus.Online;
            // Segoe Fluent Icons: Battery0–9 = E850–E859, Battery10 = E83F, 充電中 0–10 = EBAB–EBB5
            _battery.Text = charging ? ((char)(0xEBAB + level)).ToString()
                          : level >= 10 ? "" : ((char)(0xE850 + level)).ToString();
            _battery.ToolTip = $"バッテリー {ps.BatteryLifePercent:P0}{(charging ? "（充電中）" : "")}";
        }
        catch { _battery.Visibility = Visibility.Collapsed; }
    }
}
