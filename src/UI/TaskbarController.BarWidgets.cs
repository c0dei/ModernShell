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

    private FrameworkElement? BuildBarWidgetsHost()
    {
        _barWidgets.Clear();
        if (_settings.BarWidgets.Count == 0) return null;
        var host = new System.Windows.Controls.StackPanel { Orientation = Flow, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
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
                host.Children.Add(tile);
                _barWidgets.Add(w);
            }
            catch (Exception ex) { Log.Error("bar widget " + id, ex); }
        }
        return host.Children.Count == 0 ? null : host;
    }

    /// <summary>ウィジェットの右クリック: 並べ替えと、バーから外す。</summary>
    private IEnumerable<MenuItemSpec> BarWidgetMenu(string id)
    {
        var list = _settings.BarWidgets;
        int i = list.IndexOf(id);
        var items = new List<MenuItemSpec>();
        if (i > 0) items.Add(new("", L.T("左へ移動"), () => { list.RemoveAt(i); list.Insert(i - 1, id); _settings.Save(); }));
        if (i >= 0 && i < list.Count - 1) items.Add(new("", L.T("右へ移動"), () => { list.RemoveAt(i); list.Insert(i + 1, id); _settings.Save(); }));
        items.Add(new("", L.T("バーから外す"), () => { list.Remove(id); _settings.Save(); }));
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
