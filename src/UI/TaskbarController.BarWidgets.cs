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

    private string ZoneOf(string id) => _settings.BarWidgetZones.TryGetValue(id, out var z) && z is "Left" or "Apps" ? z : "Tray";

    /// <summary>設定の並び順で、位置（Left / Apps / Tray）ごとのウィジェットの入れ物を作る。</summary>
    private void BuildBarWidgetHosts()
    {
        _barWidgets.Clear();
        _barHosts.Clear();
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
                    var details = w.Details().ToList();
                    if (details.Count > 0) ShowIconMenu(x, details);
                };
                tile.RightClick += x => ShowIconMenu(x, w.ExtraMenu().Concat(BarWidgetMenu(id)).ToList());
                string zone = ZoneOf(id);
                if (!_barHosts.TryGetValue(zone, out var host))
                    _barHosts[zone] = host = new System.Windows.Controls.StackPanel { Orientation = Flow, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                ((System.Windows.Controls.StackPanel)host).Children.Add(tile);
                _barWidgets.Add(w);
            }
            catch (Exception ex) { Log.Error("bar widget " + id, ex); }
        }
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
