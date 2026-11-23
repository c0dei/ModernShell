using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ModernShell.UI;

/// <summary>セルに渡す 1 回分の値。Pct は 0–100（バーや塗りの量）、Hist は推移（古い順）、HistMax は推移グラフの縦の最大。</summary>
internal sealed class MetricSample
{
    public double Pct;
    public IReadOnlyList<double> Hist = Array.Empty<double>();
    public double HistMax = 100;
    public string Value = "";
    public string Unit = "";
}

/// <summary>セルの見た目の指定（名前・アイコン・色）。</summary>
internal sealed record MetricSpec(string Label, string? Glyph, Color Base, bool Warn);

/// <summary>
/// バーのウィジェットの指標 1 つ分（CPU、メモリ、受信、送信）の見た目。ウィジェットは、これを 1〜2 個並べる。
/// 見た目の違い（横バー・縦バー・スパークライン…）は、このクラスの派生で表す。
/// </summary>
internal abstract class MetricCell
{
    /// <summary>使用率がこれ以上なら注意色にする（CPU・メモリのみ）。</summary>
    public const double WarnPct = 70;
    protected static readonly Color WarnColor = Color.FromRgb(0xFF, 0x8A, 0x75);

    protected readonly Theme T = Theme.Current;
    protected readonly MetricSpec Spec;
    private readonly Brush _base, _warn;

    public FrameworkElement Root { get; protected set; } = null!;
    /// <summary>true なら、複数のセルを縦（行）に積む。</summary>
    public virtual bool Stacked => false;

    protected MetricCell(MetricSpec spec)
    {
        Spec = spec;
        _base = Freeze(new SolidColorBrush(spec.Base));
        _warn = Freeze(new SolidColorBrush(WarnColor));
    }

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    protected Brush ColorFor(double pct) => Spec.Warn && pct >= WarnPct ? _warn : _base;
    protected static Brush Track => new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
    protected static Brush Faint(Brush b, byte alpha)
    {
        var c = (b as SolidColorBrush)?.Color ?? Colors.Gray;
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    public abstract void Set(MetricSample s);

    // ---- 部品 ----

    protected TextBlock Txt(string text, double size, Brush? fg = null, FontWeight? weight = null)
    {
        var t = UiKit.Label(text, size, fg ?? T.Text, weight);
        t.TextTrimming = TextTrimming.None;
        return t;
    }

    /// <summary>名前（アイコンがあればアイコン、なければ文字）。</summary>
    protected FrameworkElement NameLabel(double size, Brush? fg = null)
    {
        fg ??= T.TextSecondary;
        if (Spec.Glyph != null)
        {
            var g = UiKit.Glyph(Spec.Glyph, Math.Round(size * 0.85, 1), fg);
            g.VerticalAlignment = VerticalAlignment.Center;
            return g;
        }
        return Txt(Spec.Label, size, fg);
    }

    protected static Border Fill(Brush b, double radius) => new() { Background = b, CornerRadius = new CornerRadius(radius) };
}

/// <summary>横バー: 名前 ─── バー ─── 数値（2 つ縦に積む）。</summary>
internal sealed class HBarCell : MetricCell
{
    private const double BarW = 64, BarH = 4;
    private readonly Border _fill = Fill(Brushes.Transparent, 2);
    private readonly TextBlock _val;
    public override bool Stacked => true;

    public HBarCell(MetricSpec spec) : base(spec)
    {
        var name = NameLabel(10);
        var nameBox = new Grid { MinWidth = 26, Children = { name } };
        name.HorizontalAlignment = HorizontalAlignment.Left;
        var bar = new Grid { Width = BarW, Height = BarH, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(Fill(Track, 2));
        _fill.HorizontalAlignment = HorizontalAlignment.Left;
        bar.Children.Add(_fill);
        _val = Txt("0", 12, T.Text, FontWeights.SemiBold);
        _val.TextAlignment = TextAlignment.Right;
        _val.MinWidth = 24;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        row.Children.Add(nameBox);
        row.Children.Add(bar);
        row.Children.Add(_val);
        Root = row;
    }

    public override void Set(MetricSample s)
    {
        _fill.Width = BarW * Math.Clamp(s.Pct, 0, 100) / 100;
        _fill.Background = ColorFor(s.Pct);
        _val.Text = s.Value;
    }
}

/// <summary>縦バー + 名前と数値。</summary>
internal sealed class VBarCell : MetricCell
{
    private const double BarH = 24;
    private readonly Border _fill = Fill(Brushes.Transparent, 3);
    private readonly TextBlock _val, _unit;

    public VBarCell(MetricSpec spec, bool showUnit) : base(spec)
    {
        var bar = new Grid { Width = 6, Height = BarH, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(Fill(Track, 3));
        _fill.VerticalAlignment = VerticalAlignment.Bottom;
        bar.Children.Add(_fill);
        _val = Txt("0", 13, T.Text, FontWeights.SemiBold);
        _unit = Txt(showUnit ? "%" : "", 10, T.TextSecondary);
        _unit.VerticalAlignment = VerticalAlignment.Bottom;
        _unit.Margin = new Thickness(1, 0, 0, 1);
        var valRow = new StackPanel { Orientation = Orientation.Horizontal };
        valRow.Children.Add(_val);
        valRow.Children.Add(_unit);
        var col = new StackPanel { Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(NameLabel(10));
        col.Children.Add(valRow);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(bar);
        row.Children.Add(col);
        Root = row;
        _showUnit = showUnit;
    }

    private readonly bool _showUnit;

    public override void Set(MetricSample s)
    {
        _fill.Height = Math.Max(s.Pct > 0.5 ? 3 : 0, BarH * Math.Clamp(s.Pct, 0, 100) / 100);
        _fill.Background = ColorFor(s.Pct);
        _val.Text = s.Value;
        if (_showUnit) _unit.Text = s.Unit;
    }
}

/// <summary>名前 / 数値 / 下線バー。</summary>
internal sealed class UnderlineCell : MetricCell
{
    private const double BarW = 42, BarH = 4;
    private readonly Border _fill = Fill(Brushes.Transparent, 2);
    private readonly TextBlock _val, _unit;

    public UnderlineCell(MetricSpec spec) : base(spec)
    {
        // 小サイズのバー（高さ 34）に収まるよう、行間を詰める
        _val = Txt("0", 14, T.Text, FontWeights.SemiBold);
        _val.LineHeight = 16; _val.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _unit = Txt("%", 10, T.TextSecondary);
        _unit.VerticalAlignment = VerticalAlignment.Bottom;
        _unit.Margin = new Thickness(1, 0, 0, 2);
        var valRow = new StackPanel { Orientation = Orientation.Horizontal };
        valRow.Children.Add(_val);
        valRow.Children.Add(_unit);
        var name = NameLabel(9.5);
        if (name is TextBlock nt) { nt.LineHeight = 11; nt.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; }
        var bar = new Grid { Width = BarW, Height = BarH, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) };
        bar.Children.Add(Fill(Faint(T.Text, 0x38), 2)); // 背景の線は、はっきり見える濃さ
        _fill.HorizontalAlignment = HorizontalAlignment.Left;
        bar.Children.Add(_fill);
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = BarW };
        col.Children.Add(name);
        col.Children.Add(valRow);
        col.Children.Add(bar);
        Root = col;
    }

    public override void Set(MetricSample s)
    {
        _fill.Width = Math.Max(s.Pct > 0.5 ? BarH : 0, BarW * Math.Clamp(s.Pct, 0, 100) / 100);
        _fill.Background = ColorFor(s.Pct);
        _val.Text = s.Value;
        _unit.Text = s.Unit;
    }
}

/// <summary>名前と数値 + 直近の推移の折れ線（下を薄く塗る）。</summary>
internal sealed class SparkCell : MetricCell
{
    private const double W = 52, H = 20;
    private readonly TextBlock _val, _unit;
    private readonly Polyline _line = new() { StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
    private readonly Polygon _area = new() { Opacity = 0.25 };
    private readonly int _capacity;

    public SparkCell(MetricSpec spec, int capacity) : base(spec)
    {
        _capacity = capacity;
        _val = Txt("0", 13, T.Text, FontWeights.SemiBold);
        _unit = Txt("", 10, T.TextSecondary);
        _unit.VerticalAlignment = VerticalAlignment.Bottom;
        _unit.Margin = new Thickness(2, 0, 0, 1);
        var valRow = new StackPanel { Orientation = Orientation.Horizontal };
        valRow.Children.Add(_val);
        valRow.Children.Add(_unit);
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 36 };
        col.Children.Add(Spec.Glyph != null ? Txt(Spec.Label, 10, T.TextSecondary) : NameLabel(10));
        col.Children.Add(valRow);
        var chart = new Canvas { Width = W, Height = H, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        chart.Children.Add(_area);
        chart.Children.Add(_line);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (Spec.Glyph != null)
        {
            var g = UiKit.Glyph(Spec.Glyph, 10, new SolidColorBrush(Spec.Base));
            g.Margin = new Thickness(0, 0, 5, 0);
            row.Children.Add(g);
        }
        row.Children.Add(col);
        row.Children.Add(chart);
        Root = row;
    }

    public override void Set(MetricSample s)
    {
        var b = ColorFor(s.Pct);
        _line.Stroke = b;
        _area.Fill = b;
        _val.Text = s.Value;
        _unit.Text = s.Unit;
        var h = s.Hist;
        if (h.Count < 2) { _line.Points = new PointCollection(); _area.Points = new PointCollection(); return; }
        double max = Math.Max(1e-6, s.HistMax), step = W / (_capacity - 1);
        double x0 = W - step * (h.Count - 1); // 新しい値が右端
        var pts = new PointCollection();
        for (int i = 0; i < h.Count; i++)
            pts.Add(new Point(x0 + i * step, 1 + (H - 2) * (1 - Math.Clamp(h[i] / max, 0, 1))));
        _line.Points = pts;
        var area = new PointCollection(pts) { new Point(W, H), new Point(x0, H) };
        _area.Points = area;
    }
}

/// <summary>名前 ─── 10 個のセグメント ─── 数値（2 つ縦に積む）。</summary>
internal sealed class SegmentCell : MetricCell
{
    private const int Count = 10;
    private readonly Border[] _segs = new Border[Count];
    private readonly TextBlock _val;
    public override bool Stacked => true;

    public SegmentCell(MetricSpec spec) : base(spec)
    {
        var name = NameLabel(10);
        name.HorizontalAlignment = HorizontalAlignment.Left;
        var nameBox = new Grid { MinWidth = 26, Children = { name } };
        var segs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < Count; i++)
        {
            _segs[i] = new Border { Width = 4, Height = 9, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(i == 0 ? 0 : 1.5, 0, 0, 0), Background = Track };
            segs.Children.Add(_segs[i]);
        }
        _val = Txt("0", 12, T.Text, FontWeights.SemiBold);
        _val.TextAlignment = TextAlignment.Right;
        _val.MinWidth = 24;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        row.Children.Add(nameBox);
        row.Children.Add(segs);
        row.Children.Add(_val);
        Root = row;
    }

    public override void Set(MetricSample s)
    {
        int on = (int)Math.Round(Math.Clamp(s.Pct, 0, 100) / 100 * Count);
        if (s.Pct > 0.5 && on == 0) on = 1;
        var b = ColorFor(s.Pct);
        for (int i = 0; i < Count; i++) _segs[i].Background = i < on ? b : Track;
        _val.Text = s.Value;
    }
}

/// <summary>角丸のタイル。下から使用量だけ塗り、名前と数値を重ねる。</summary>
internal sealed class TileCell : MetricCell
{
    private const double W = 36, H = 30, R = 6;
    private readonly Border _fill = new();
    private readonly TextBlock _val;

    public TileCell(MetricSpec spec) : base(spec)
    {
        var grid = new Grid { Width = W, Height = H, Clip = new RectangleGeometry(new Rect(0, 0, W, H), R, R) };
        grid.Children.Add(new Border { Background = Track });
        _fill.VerticalAlignment = VerticalAlignment.Bottom;
        grid.Children.Add(_fill);
        _val = Txt("0", 13, T.Text, FontWeights.SemiBold);
        _val.HorizontalAlignment = HorizontalAlignment.Center;
        var name = NameLabel(9, T.Text);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.Opacity = 0.8;
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(name);
        col.Children.Add(_val);
        grid.Children.Add(col);
        Root = grid;
    }

    public override void Set(MetricSample s)
    {
        _fill.Height = H * Math.Clamp(s.Pct, 0, 100) / 100;
        _fill.Background = Faint(ColorFor(s.Pct), 0x99);
        _val.Text = s.Value;
    }
}

/// <summary>「受信 12.4」のように、文字のラベルと、色付きの数値だけ。</summary>
internal sealed class LabelValueCell : MetricCell
{
    private readonly TextBlock _val;

    public LabelValueCell(MetricSpec spec) : base(spec)
    {
        var name = Txt(spec.Label, 12, T.TextSecondary);
        name.Margin = new Thickness(0, 0, 4, 0);
        _val = Txt("0", 13, new SolidColorBrush(spec.Base), FontWeights.SemiBold);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(name);
        row.Children.Add(_val);
        Root = row;
    }

    public override void Set(MetricSample s) => _val.Text = s.Value;
}

/// <summary>文字だけ: ● CPU 42%（アイコンがあれば、点の代わりにアイコン）。</summary>
internal sealed class TextCell : MetricCell
{
    private readonly Ellipse? _dot;
    private readonly TextBlock _val, _unit;
    public override bool Stacked { get; }

    /// <param name="withUnit">数値のあとに単位を付ける。</param>
    /// <param name="stacked">縦に積む（通信速度の 2 段表示）。</param>
    public TextCell(MetricSpec spec, bool withUnit, bool stacked, bool showName) : base(spec)
    {
        Stacked = stacked;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = stacked ? new Thickness(0, 1, 0, 1) : new Thickness(0) };
        if (spec.Glyph != null)
        {
            var g = UiKit.Glyph(spec.Glyph, 10, new SolidColorBrush(spec.Base));
            g.Margin = new Thickness(0, 0, 5, 0);
            g.MinWidth = 10;
            row.Children.Add(g);
        }
        else
        {
            _dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(_dot);
        }
        if (showName && spec.Glyph == null)
        {
            var n = Txt(spec.Label, 12, T.TextSecondary);
            n.Margin = new Thickness(0, 0, 5, 0);
            row.Children.Add(n);
        }
        _val = Txt("0", 13, T.Text, FontWeights.SemiBold);
        if (stacked) { _val.MinWidth = 34; _val.TextAlignment = TextAlignment.Right; }
        _unit = Txt("", 10, T.TextSecondary);
        _unit.VerticalAlignment = VerticalAlignment.Bottom;
        _unit.Margin = new Thickness(2, 0, 0, 1);
        row.Children.Add(_val);
        if (withUnit) row.Children.Add(_unit);
        _withUnit = withUnit;
        Root = row;
    }

    private readonly bool _withUnit;

    public override void Set(MetricSample s)
    {
        if (_dot != null) _dot.Fill = ColorFor(s.Pct);
        _val.Text = s.Value;
        if (_withUnit) _unit.Text = s.Unit;
    }
}
