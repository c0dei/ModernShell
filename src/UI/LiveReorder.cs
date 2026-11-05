using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ModernShell.UI;

/// <summary>
/// パネル内の要素をドラッグで並べ替える（ウィジェットと同じ動き）。
///  - 少し動かすとドラッグ開始。つかんだ要素はカーソルに付いてくる
///  - 他の要素の半分を越えたら、その要素がぬるっと移動して場所を空ける
///  - 離すと新しい位置へ吸い付き、commit(from, to) が呼ばれる
/// クリックとの区別: 6px 以上動かしたときだけドラッグになり、そのときはクリックを発生させない。
/// </summary>
public sealed class LiveReorder
{
    private const double Threshold = 6;

    private readonly Panel _panel;
    private readonly bool _horizontal;
    private readonly Action<int, int> _commit;
    private readonly List<FrameworkElement> _items = new();

    private FrameworkElement? _pending, _drag;
    private Point _start;
    private int _from, _to;
    private double[] _pos = Array.Empty<double>(), _size = Array.Empty<double>();
    private bool _justDragged;

    public LiveReorder(Panel panel, Orientation orientation, Action<int, int> commit)
    {
        _panel = panel;
        _horizontal = orientation == Orientation.Horizontal;
        _commit = commit;
    }

    public bool IsDragging => _drag != null;

    /// <summary>
    /// 並びと直角の方向（横並びなら上下）に大きく引き出されたときに呼ばれる。
    /// 設定すると、その時点で並べ替えを取り消して呼び出し側に引き渡す（例: トレイアイコンを「^」の中へしまう）。
    /// </summary>
    public Action<FrameworkElement>? DragOut { get; set; }
    private const double DragOutDistance = 28;

    /// <summary>直前の操作がドラッグだったか（クリック処理側で確認し、true ならクリックとして扱わない）。読むとリセット。</summary>
    public bool ConsumeDragged()
    {
        bool v = _justDragged;
        _justDragged = false;
        return v;
    }

    public void Add(FrameworkElement item)
    {
        _items.Add(item);
        item.RenderTransform = new TranslateTransform();
        item.PreviewMouseLeftButtonDown += (_, e) => { _pending = item; _start = e.GetPosition(_panel); _justDragged = false; };
        item.PreviewMouseMove += (_, e) =>
        {
            if (_drag == item) { Move(e); return; }
            if (_pending != item || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(_panel) - _start;
            if (Math.Abs(_horizontal ? d.X : d.Y) < Threshold) return;
            Begin(item);
            Move(e);
        };
        item.PreviewMouseLeftButtonUp += (_, e) =>
        {
            _pending = null;
            if (_drag != item) return;
            e.Handled = true; // クリックにしない
            item.ReleaseMouseCapture();
            End();
        };
        item.LostMouseCapture += (_, _) => { if (_drag == item) End(); };
    }

    private double Pos(FrameworkElement el) { var p = el.TranslatePoint(new Point(0, 0), _panel); return _horizontal ? p.X : p.Y; }
    private double Size(FrameworkElement el) => _horizontal ? el.ActualWidth : el.ActualHeight;
    private double Pitch(FrameworkElement el) => Size(el) + (_horizontal ? el.Margin.Left + el.Margin.Right : el.Margin.Top + el.Margin.Bottom);

    private void Begin(FrameworkElement item)
    {
        _drag = item;
        _from = _to = _items.IndexOf(item);
        _pos = _items.Select(Pos).ToArray();
        _size = _items.Select(Size).ToArray();
        item.CaptureMouse();
        Panel.SetZIndex(item, 10);
        item.Opacity = 0.9;
        item.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.35 };
    }

    private void Move(MouseEventArgs e)
    {
        if (_drag == null) return;
        var d = e.GetPosition(_panel) - _start;
        double delta = _horizontal ? d.X : d.Y;
        double across = _horizontal ? d.Y : d.X;
        if (DragOut != null && Math.Abs(across) > DragOutDistance)
        {
            var item = _drag;
            Cancel();
            DragOut(item);
            return;
        }
        Set(_drag, delta, animate: false);

        // つかんだ要素の中心が、どの要素の半分を越えたかで差し込み先を決める
        double center = _pos[_from] + _size[_from] / 2 + delta;
        int to = _from;
        for (int k = _from + 1; k < _items.Count; k++) if (center > _pos[k] + _size[k] / 2) to = k;
        for (int k = _from - 1; k >= 0; k--) if (center < _pos[k] + _size[k] / 2) to = k;
        if (to == _to) return;
        _to = to;

        double shift = Pitch(_drag);
        for (int k = 0; k < _items.Count; k++)
        {
            if (k == _from) continue;
            double target = _from < to && k > _from && k <= to ? -shift
                          : to < _from && k >= to && k < _from ? shift
                          : 0;
            Set(_items[k], target, animate: true);
        }
    }

    private DependencyProperty Axis => _horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;

    private void Set(FrameworkElement el, double value, bool animate)
    {
        var tt = (TranslateTransform)el.RenderTransform;
        var prop = Axis;
        if (animate)
            tt.BeginAnimation(prop, new DoubleAnimation(value, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            tt.BeginAnimation(prop, null);
            tt.SetValue(prop, value);
        }
    }

    /// <summary>並べ替えを取り消して全部元の位置へ戻す。</summary>
    private void Cancel()
    {
        var item = _drag;
        if (item == null) return;
        _drag = null;
        _pending = null;
        _justDragged = true;
        item.ReleaseMouseCapture();
        item.Effect = null;
        item.Opacity = 1;
        Panel.SetZIndex(item, 0);
        foreach (var el in _items) Set(el, 0, animate: true);
    }

    private void End()
    {
        var item = _drag;
        if (item == null) return;
        _drag = null;
        _justDragged = true;
        int from = _from, to = _to;

        double final = to == from ? 0
            : to > from ? _pos[to] + _size[to] - (_pos[from] + _size[from])
            : _pos[to] - _pos[from];
        var tt = (TranslateTransform)item.RenderTransform;
        var prop = Axis;
        var snap = new DoubleAnimation(final, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        snap.Completed += (_, _) =>
        {
            item.Effect = null;
            item.Opacity = 1;
            Panel.SetZIndex(item, 0);
            if (from == to)
            {
                tt.BeginAnimation(prop, null);
                tt.SetValue(prop, 0.0);
                return;
            }
            _commit(from, to);
        };
        tt.BeginAnimation(prop, snap);
    }
}
