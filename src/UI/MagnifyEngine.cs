using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ModernShell.UI;

/// <summary>
/// タスクバーのアイコンの拡大（Mac のドック風）。カーソルの位置からの距離で、各アイコンの拡大率を決める。
/// - ホバーのみ: カーソルの下のアイコンだけを拡大する。
/// - 両隣も: 距離に応じてなめらかに小さくなる釣り鐘型の拡大（真下が最大、隣は 7 割ほど、2 つ隣は 2 割ほど）。
/// どちらも、目標の拡大率へ毎フレーム少しずつ近づけるので、カーソルを動かしても、カクつかずになめらかにつながる。
/// レイアウトは動かさず、アイコンの描画だけを拡大する（拡大率の上限は、ボタンの大きさ・バーの太さに収まる範囲）。
/// </summary>
public sealed class MagnifyEngine : IDisposable
{
    private sealed class Item
    {
        public required FrameworkElement Tile;
        public required ScaleTransform Scale;
        public double Current = 1, Target = 1;
    }

    private readonly Panel _panel;
    private readonly bool _vertical, _neighbors;
    private readonly double _max, _unit;
    private readonly List<Item> _items = new();
    private double? _pointer;
    private bool _rendering, _disposed;

    /// <param name="max">最大の拡大率（1 より大きい）。</param>
    /// <param name="unit">ボタン 1 つ分の間隔（カーソルからの距離を「ボタン何個分」にするため）。</param>
    public MagnifyEngine(Panel panel, bool vertical, double max, bool neighbors, double unit)
    {
        _panel = panel; _vertical = vertical; _max = Math.Max(1, max); _neighbors = neighbors; _unit = Math.Max(8, unit);
        // ボタンのあいだの隙間でもカーソルを追えるよう、透明の背景でマウスを受ける
        if (_panel.Background == null) _panel.Background = Brushes.Transparent;
        _panel.MouseMove += OnMove;
        _panel.MouseLeave += OnLeave;
    }

    /// <summary>アイコンを登録する。origin: 拡大の支点（画面の端の側）。</summary>
    public void Register(FrameworkElement tile, FrameworkElement icon, Point origin)
    {
        var scale = new ScaleTransform(1, 1);
        icon.RenderTransform = scale;
        icon.RenderTransformOrigin = origin;
        _items.Add(new Item { Tile = tile, Scale = scale });
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_panel);
        _pointer = _vertical ? p.Y : p.X;
        Retarget();
    }

    private void OnLeave(object sender, MouseEventArgs e)
    {
        _pointer = null;
        Retarget();
    }

    private void Retarget()
    {
        foreach (var it in _items)
        {
            double target = 1;
            if (_pointer is { } ptr && it.Tile.IsVisible)
            {
                var c = it.Tile.TranslatePoint(new Point(it.Tile.ActualWidth / 2, it.Tile.ActualHeight / 2), _panel);
                double d = Math.Abs(ptr - (_vertical ? c.Y : c.X)) / _unit; // ボタン何個分離れているか
                double k = _neighbors
                    ? Math.Exp(-d * d / (2 * 1.15 * 1.15))   // 釣り鐘型: d=1 で約 0.69、d=2 で約 0.22、d=3 で約 0.04
                    : d < 0.5 ? 1 : 0;                       // ホバーのみ
                if (k < 0.01) k = 0;
                target = 1 + (_max - 1) * k;
            }
            it.Target = target;
        }
        if (!_rendering && !_disposed) { _rendering = true; CompositionTarget.Rendering += OnFrame; }
    }

    /// <summary>毎フレーム、現在の拡大率を目標へ近づける（動きがおさまったら止める）。</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        bool moving = false;
        foreach (var it in _items)
        {
            double diff = it.Target - it.Current;
            if (Math.Abs(diff) < 0.002) { it.Current = it.Target; }
            else { it.Current += diff * 0.32; moving = true; }
            it.Scale.ScaleX = it.Scale.ScaleY = it.Current;
        }
        if (!moving) { CompositionTarget.Rendering -= OnFrame; _rendering = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _panel.MouseMove -= OnMove;
        _panel.MouseLeave -= OnLeave;
        if (_rendering) CompositionTarget.Rendering -= OnFrame;
        _rendering = false;
        _items.Clear();
    }
}
