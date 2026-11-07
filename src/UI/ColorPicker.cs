using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>
/// Windows 11 の「カスタム色」と同じ構成のカラーピッカー。
/// 彩度×明度の四角 + 色相バー + 16 進入力 + プレビュー。HSV で保持する。
/// </summary>
public sealed class ColorPicker : StackPanel
{
    private const double AreaW = 300, AreaH = 180, HueH = 14;

    private double _h, _s, _v; // h: 0–360, s/v: 0–1
    private readonly Border _area, _preview;
    private readonly Ellipse _areaThumb, _hueThumb;
    private readonly Canvas _areaCanvas, _hueCanvas;
    private readonly TextBox _hex;
    private bool _updatingText;

    public event Action<Color>? Applied;
    public event Action? Cancelled;

    public Color Value => FromHsv(_h, _s, _v);

    public ColorPicker(Color initial)
    {
        var t = Theme.Current;
        (_h, _s, _v) = ToHsv(initial);

        // ---- 彩度 × 明度 ----
        _area = new Border { Width = AreaW, Height = AreaH, CornerRadius = t.Corner(6) };
        var white = new Border
        {
            CornerRadius = t.Corner(6),
            Background = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(1, 0)),
        };
        var black = new Border
        {
            CornerRadius = t.Corner(6),
            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 0), new Point(0, 1)),
        };
        _areaThumb = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        _areaCanvas = new Canvas { Width = AreaW, Height = AreaH, Background = Brushes.Transparent, Cursor = Cursors.Cross };
        _areaCanvas.Children.Add(_areaThumb);
        var area = new Grid { Width = AreaW, Height = AreaH, HorizontalAlignment = HorizontalAlignment.Left };
        area.Children.Add(_area);
        area.Children.Add(white);
        area.Children.Add(black);
        area.Children.Add(_areaCanvas);
        Drag(_areaCanvas, p =>
        {
            _s = Math.Clamp(p.X / AreaW, 0, 1);
            _v = 1 - Math.Clamp(p.Y / AreaH, 0, 1);
            Update();
        });

        // ---- 色相 ----
        var hues = new GradientStopCollection();
        for (int i = 0; i <= 6; i++) hues.Add(new GradientStop(FromHsv(i * 60 % 360 == 0 && i == 6 ? 359.9 : i * 60, 1, 1), i / 6.0));
        var hueBar = new Border
        {
            Width = AreaW, Height = HueH, CornerRadius = new CornerRadius(HueH / 2),
            Background = new LinearGradientBrush(hues, new Point(0, 0), new Point(1, 0)),
        };
        _hueThumb = new Ellipse { Width = 18, Height = 18, Stroke = Brushes.White, StrokeThickness = 3, IsHitTestVisible = false };
        _hueCanvas = new Canvas { Width = AreaW, Height = HueH, Background = Brushes.Transparent };
        _hueCanvas.Children.Add(_hueThumb);
        var hue = new Grid { Width = AreaW, Height = 20, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        hueBar.VerticalAlignment = _hueCanvas.VerticalAlignment = VerticalAlignment.Center;
        hue.Children.Add(hueBar);
        hue.Children.Add(_hueCanvas);
        Drag(_hueCanvas, p => { _h = Math.Clamp(p.X / AreaW, 0, 0.9999) * 360; Update(); });

        // ---- 16 進 + プレビュー + ボタン ----
        _preview = new Border { Width = 40, Height = 32, CornerRadius = t.Corner(4), BorderBrush = t.Stroke, BorderThickness = new Thickness(1) };
        _hex = new TextBox
        {
            Width = 110, Height = 32, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(10, 0, 0, 0),
            Background = t.ControlFill, Foreground = t.Text, CaretBrush = t.Text, BorderBrush = t.ControlBorder, BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center, FontSize = 14,
        };
        _hex.TextChanged += (_, _) =>
        {
            if (_updatingText) return;
            if (Theme.ParseColor(_hex.Text.StartsWith("#") ? _hex.Text : "#" + _hex.Text) is { } c && _hex.Text.TrimStart('#').Length == 6)
            {
                (_h, _s, _v) = ToHsv(c);
                Update(fromText: true);
            }
        };
        var apply = Button(L.T("適用"), accent: true, () => Applied?.Invoke(Value));
        var cancel = Button(L.T("キャンセル"), accent: false, () => Cancelled?.Invoke());
        apply.Margin = new Thickness(20, 0, 8, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        row.Children.Add(_preview);
        row.Children.Add(_hex);
        row.Children.Add(apply);
        row.Children.Add(cancel);

        Children.Add(area);
        Children.Add(hue);
        Children.Add(row);
        Loaded += (_, _) => Update();
    }

    private static Tile Button(string text, bool accent, Action onClick)
    {
        var t = Theme.Current;
        var b = new Tile
        {
            Height = 32, Padding = new Thickness(16, 0, 16, 0), CornerRadius = t.Corner(4),
            Background = accent ? t.AccentBrush : t.ControlFill, BorderBrush = t.ControlBorder, BorderThickness = new Thickness(1),
            Child = UiKit.Label(text, 14, accent ? t.OnAccent : t.Text),
        };
        var normal = b.Background;
        b.MouseLeave += (_, _) => { b.Background = normal; b.BorderBrush = t.ControlBorder; };
        b.Click += _ => onClick();
        return b;
    }

    /// <summary>押したまま動かすと値が変わる（キャンバス外に出ても追従）。</summary>
    private static void Drag(Canvas c, Action<Point> set)
    {
        c.MouseLeftButtonDown += (_, e) => { c.CaptureMouse(); set(e.GetPosition(c)); e.Handled = true; };
        c.MouseMove += (_, e) => { if (c.IsMouseCaptured) set(e.GetPosition(c)); };
        c.MouseLeftButtonUp += (_, _) => c.ReleaseMouseCapture();
    }

    private void Update(bool fromText = false)
    {
        _area.Background = new SolidColorBrush(FromHsv(_h, 1, 1));
        Canvas.SetLeft(_areaThumb, _s * AreaW - 8);
        Canvas.SetTop(_areaThumb, (1 - _v) * AreaH - 8);
        _areaThumb.Stroke = _v > 0.5 && _s < 0.5 ? Brushes.Black : Brushes.White;
        Canvas.SetLeft(_hueThumb, _h / 360 * AreaW - 9);
        Canvas.SetTop(_hueThumb, (HueH - 18) / 2);
        _hueThumb.Fill = new SolidColorBrush(FromHsv(_h, 1, 1));
        var c = Value;
        _preview.Background = new SolidColorBrush(c);
        if (!fromText)
        {
            _updatingText = true;
            _hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            _updatingText = false;
        }
    }

    public static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }
}
