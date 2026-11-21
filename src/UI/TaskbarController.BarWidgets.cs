using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>バーのウィジェット: タスクバーの右側（トレイの手前）に、小さなカードを並べる。</summary>
public sealed partial class TaskbarController
{
    private readonly List<BarWidget> _barWidgets = new();
    private int _barTick;

    private readonly Dictionary<string, FrameworkElement> _barHosts = new();
    private readonly Dictionary<string, LiveReorder> _widgetReorders = new();

    private string ZoneOf(string id) => _settings.BarWidgetZones.TryGetValue(id, out var z) && z is "Left" or "Edge" or "Apps" ? z : "Tray";

    /// <summary>設定の並び順で、位置（Left / Apps / Tray）ごとのウィジェットの入れ物を作る。</summary>
    private void BuildBarWidgetHosts()
    {
        _barWidgets.Clear();
        _barHosts.Clear();
        // 同じ位置（ゾーン）のウィジェットは、アプリのボタンと同じようにドラッグで並べ替えられる
        var reorders = _widgetReorders;
        reorders.Clear();
        var zoneIds = new Dictionary<string, List<string>>();
        foreach (var id in _settings.BarWidgets.ToList())
        {
            var info = BarWidgetCatalog.Find(id);
            if (info == null) continue;
            try
            {
                var w = info.Create();
                var card = w.Build(Vertical, TileH);
                var tile = new Tile { Child = card, Margin = Gap, Padding = new Thickness(0), Tag = id };
                w.Outer = tile;
                tile.Click += x =>
                {
                    if (w.OnClick()) return;
                    if (w.BuildDetailsView() is { } view) { ShowBarPopup(x, view); return; }
                    var details = w.Details().ToList();
                    if (details.Count > 0) ShowIconMenu(x, details);
                };
                tile.RightClick += x => ShowIconMenu(x, w.ExtraMenu().Concat(BarWidgetMenu(id)).ToList());
                string zone = ZoneOf(id);
                if (!_barHosts.TryGetValue(zone, out var host))
                    _barHosts[zone] = host = new System.Windows.Controls.StackPanel { Orientation = Flow, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                var panel = (System.Windows.Controls.StackPanel)host;
                panel.Children.Add(tile);
                if (!reorders.TryGetValue(zone, out var reorder))
                {
                    var ids = zoneIds[zone] = new List<string>();
                    reorders[zone] = reorder = new LiveReorder(panel, Flow, (from, to) => CommitWidgetOrder(ids, from, to))
                    {
                        DropOutside = (item, p) => DropWidgetElsewhere(item, panel.PointToScreen(p)),
                        DragMoved = (item, p) => PreviewWidgetDrop(item, panel.PointToScreen(p)),
                    };
                }
                zoneIds[zone].Add(id);
                reorder.Add(tile);
                _barWidgets.Add(w);
            }
            catch (Exception ex) { Log.Error("bar widget " + id, ex); }
        }
    }

    /// <summary>ドラッグで決まった同じ位置内の並びを、全体の並び順（設定）へ反映する。</summary>
    private void CommitWidgetOrder(List<string> zoneIds, int from, int to)
    {
        var moved = zoneIds[from];
        zoneIds.RemoveAt(from);
        zoneIds.Insert(to, moved);
        var list = _settings.BarWidgets;
        int v = 0;
        for (int i = 0; i < list.Count; i++)
            if (zoneIds.Contains(list[i])) list[i] = zoneIds[v++];
        _settings.Save();
    }

    private static Rect ScreenRect(FrameworkElement el)
    {
        if (!el.IsLoaded || el.ActualWidth <= 0 || el.ActualHeight <= 0) return Rect.Empty;
        var tl = el.PointToScreen(new Point(0, 0));
        var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
        return new Rect(tl, br);
    }

    /// <summary>
    /// 別の位置（バーの左端 / スタートボタンの隣 / アプリの右隣 / トレイの手前）にドロップされたウィジェットを、そこへ移す。
    /// ドロップ先は、カーソル（画面座標）が重なっている領域で決める。ウィジェットのない位置でも、バー上の目印（スタートボタン・アプリ・トレイ）から決まる。
    /// </summary>
    private bool DropWidgetElsewhere(FrameworkElement item, Point screen)
    {
        ClearWidgetGaps();
        if (item.Tag is not string id || !ResolveWidgetTarget(id, screen, out var target, out var index, out var others)) return false;

        var list = _settings.BarWidgets;
        int oldPos = list.IndexOf(id);
        string oldZone = ZoneOf(id);
        list.Remove(id);
        int insertAt = others.Count == 0 ? list.Count
            : index < others.Count ? list.IndexOf(others[index])
            : list.IndexOf(others[^1]) + 1;
        list.Insert(insertAt, id);
        if (target == "Tray") _settings.BarWidgetZones.Remove(id); else _settings.BarWidgetZones[id] = target;
        if (insertAt == oldPos && target == oldZone) return false; // 変わらなかった
        _settings.Save();
        return true;
    }

    /// <summary>ドラッグ中、ほかの位置のウィジェットの上に来たら、そこのウィジェットが場所を空ける（入れ替えと同じアニメーション）。</summary>
    private void PreviewWidgetDrop(FrameworkElement item, Point screen)
    {
        if (item.Tag is not string id || !ResolveWidgetTarget(id, screen, out var target, out var index, out _) || target == ZoneOf(id))
        {
            ClearWidgetGaps();
            return;
        }
        double size = Vertical ? item.ActualHeight + item.Margin.Top + item.Margin.Bottom : item.ActualWidth + item.Margin.Left + item.Margin.Right;
        foreach (var (z, r) in _widgetReorders)
        {
            if (z == target) r.ShowGap(index, size, shiftBefore: z == "Tray"); // トレイ側は右寄せ: 前を左へずらして、右のトレイにはみ出さないようにする
            else r.ClearGap();
        }
    }

    private void ClearWidgetGaps() { foreach (var r in _widgetReorders.Values) r.ClearGap(); }

    /// <summary>カーソル（画面座標）から、移動先の位置・その中での順番・先にいるウィジェットを求める。</summary>
    private bool ResolveWidgetTarget(string id, Point screen, out string target, out int index, out List<string> others)
    {
        target = ""; index = 0; others = new List<string>();
        string? found = null;
        foreach (var z in new[] { "Edge", "Left", "Apps", "Tray" })
            if (_barHosts.TryGetValue(z, out var h) && ScreenRect(h) is { IsEmpty: false } r && r.Contains(screen)) { found = z; break; }
        if (found == null)
        {
            var start = _startGroupEl != null ? ScreenRect(_startGroupEl) : Rect.Empty;
            var apps = _apps != null ? ScreenRect(_apps) : Rect.Empty;
            var tray = _trayHost != null ? ScreenRect(_trayHost) : Rect.Empty;
            double a(Point p) => Vertical ? p.Y : p.X;
            if (!start.IsEmpty && start.Contains(screen)) found = "Left";
            else if (!apps.IsEmpty && apps.Contains(screen)) found = "Apps";
            else if (!tray.IsEmpty && tray.Contains(screen)) found = "Tray";
            else if (!start.IsEmpty && a(screen) < (Vertical ? start.Top : start.Left)) found = "Edge";
            else
            {
                // どこにも重ならない: 進行方向でいちばん近い領域
                double best = double.MaxValue;
                foreach (var (z, rc) in new[] { ("Left", start), ("Apps", apps), ("Tray", tray) })
                {
                    if (rc.IsEmpty) continue;
                    double c = Vertical ? rc.Top + rc.Height / 2 : rc.Left + rc.Width / 2;
                    if (Math.Abs(a(screen) - c) < best) { best = Math.Abs(a(screen) - c); found = z; }
                }
            }
        }
        if (found == null) return false;
        target = found;

        // 移した先での並び順: カーソルより手前にある（中心が手前の）ウィジェットの後ろへ
        var list = _settings.BarWidgets;
        string zone = found;
        others = list.Where(x => x != id && ZoneOf(x) == zone).ToList();
        if (_barHosts.TryGetValue(zone, out var host) && host is System.Windows.Controls.Panel pnl)
        {
            foreach (var el in pnl.Children.OfType<FrameworkElement>())
            {
                if (el.Tag is not string tid || tid == id || ScreenRect(el) is not { IsEmpty: false } er) continue;
                // 場所を空けるアニメーション中のずれは引いて、元の位置の中心で比べる（そうしないと、空けた分だけ判定が動いて行ったり来たりする）
                var tt = el.RenderTransform as System.Windows.Media.TranslateTransform;
                double shifted = tt == null ? 0 : Vertical ? tt.Y : tt.X;
                double mid = (Vertical ? er.Top + er.Height / 2 : er.Left + er.Width / 2) - shifted;
                if ((Vertical ? screen.Y : screen.X) > mid) index = Math.Max(index, others.IndexOf(tid) + 1);
            }
        }
        return true;
    }

    /// <summary>ウィジェットのクリックで開く詳細のポップアップ（メニューでは足りない内容用）。</summary>
    private void ShowBarPopup(FrameworkElement anchor, FrameworkElement content)
    {
        var popup = FluentSurface.Create(content, anchor, System.Windows.Controls.Primitives.PlacementMode.Custom);
        popup.StaysOpen = false;
        FluentSurface.UseTaskbarLook(popup);
        FluentSurface.PlaceAligned(popup, anchor, Edge, _settings.IconMenuAlign);
        if (_settings.AnimateMenus) FluentSurface.SlideIn(popup, Edge);
        popup.IsOpen = true;
        Flyout.ToggleGuard(popup, anchor);
    }

    /// <summary>ウィジェットの右クリック: 位置・並べ替え・バーから外す。</summary>
    private IEnumerable<MenuItemSpec> BarWidgetMenu(string id)
    {
        var list = _settings.BarWidgets;
        string zone = ZoneOf(id);
        var sameZone = list.Where(x => ZoneOf(x) == zone).ToList();
        int i = sameZone.IndexOf(id);
        void Swap(string other)
        {
            int a = list.IndexOf(id), b = list.IndexOf(other);
            (list[a], list[b]) = (list[b], list[a]);
            _settings.Save();
        }
        void Move(string z) { if (z == "Tray") _settings.BarWidgetZones.Remove(id); else _settings.BarWidgetZones[id] = z; _settings.Save(); }
        var items = new List<MenuItemSpec>
        {
            new("\uE8A7", L.T("表示位置"), () => { })
            {
                Children = () => new[]
                {
                    new MenuItemSpec(zone == "Edge" ? "\uE73E" : "", L.T("バーの左端"), () => Move("Edge")),
                    new MenuItemSpec(zone == "Left" ? "\uE73E" : "", L.T("スタートボタンの隣"), () => Move("Left")),
                    new MenuItemSpec(zone == "Apps" ? "\uE73E" : "", L.T("アプリの右隣"), () => Move("Apps")),
                    new MenuItemSpec(zone == "Tray" ? "\uE73E" : "", L.T("右側（トレイの手前）"), () => Move("Tray")),
                },
            },
        };
        if (i > 0) items.Add(new("\uE76B", L.T("前へ移動"), () => Swap(sameZone[i - 1])));
        if (i >= 0 && i < sameZone.Count - 1) items.Add(new("\uE76C", L.T("後ろへ移動"), () => Swap(sameZone[i + 1])));
        items.Add(new("\uE74D", L.T("バーから外す"), () => { list.Remove(id); _settings.BarWidgetZones.Remove(id); _settings.Save(); }));
        return items;
    }

    private void UpdateBarWidgets()
    {
        if (_barWidgets.Count == 0) return;
        _barTick++;
        foreach (var w in _barWidgets)
        {
            try { w.Tick(_barTick); }
            catch (Exception ex) { Log.Error("bar widget tick " + w.Id, ex); }
        }
    }
}
