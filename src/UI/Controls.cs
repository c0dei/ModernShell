using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ModernShell.UI;

/// <summary>
/// ホバー/押下の背景だけを持つフラットなボタン。
/// WPF 標準 Button のテンプレートは古い見た目なので、Border ベースで自作している。
/// </summary>
public class Tile : Border
{
    public event Action<Tile>? Click;
    public event Action<Tile>? RightClick;
    public event Action<Tile>? MiddleClick;

    private bool _pressed;
    private bool _selected;
    public object? Payload { get; set; }

    public Tile()
    {
        CornerRadius = Theme.Current.Corner(6);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(1);
        BorderBrush = Brushes.Transparent;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Arrow;
        SnapsToDevicePixels = true;
    }

    public void PerformClick() => Click?.Invoke(this);

    /// <summary>キーボード選択中（検索結果の先頭など）。</summary>
    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; Refresh(); }
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); Refresh(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _pressed = false; Refresh(); }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); Refresh(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); Refresh(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pressed = true; Refresh();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        bool fire = _pressed && IsMouseOver;
        _pressed = false; Refresh();
        if (fire) Click?.Invoke(this);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (RightClick != null) { RightClick(this); e.Handled = true; }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && MiddleClick != null) { MiddleClick(this); e.Handled = true; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space) { Click?.Invoke(this); e.Handled = true; }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            RightClick?.Invoke(this); e.Handled = true;
        }
    }

    public void Refresh()
    {
        var t = Theme.Current;
        Background = _pressed ? t.Pressed : (IsMouseOver || _selected || IsKeyboardFocused) ? t.Hover : Brushes.Transparent;
        BorderBrush = IsKeyboardFocused ? t.Stroke : Brushes.Transparent;
    }
}

public static class UiKit
{
    public static TextBlock Glyph(string glyph, double size = 16, Brush? fg = null) => new()
    {
        Text = glyph,
        FontFamily = Theme.IconFont,
        FontSize = size,
        Foreground = fg ?? Theme.Current.Text,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Label(string text, double size = 13, Brush? fg = null, FontWeight? weight = null) => new()
    {
        Text = text,
        FontFamily = Theme.TextFont,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = fg ?? Theme.Current.Text,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Image Icon(ImageSource? src, double size) => new()
    {
        Source = src,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        SnapsToDevicePixels = true,
    };

    /// <summary>Windows ロゴ風の 4 枚タイル（グリフに頼らず描画）。</summary>
    public static FrameworkElement StartLogo(double size, Brush fill)
    {
        var g = new Grid { Width = size, Height = size };
        double gap = Math.Max(1, size / 14), half = (size - gap) / 2;
        for (int i = 0; i < 4; i++)
        {
            g.Children.Add(new Border
            {
                Width = half, Height = half, Background = fill, CornerRadius = new CornerRadius(size / 16),
                HorizontalAlignment = i % 2 == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = i < 2 ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            });
        }
        return g;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static void FadeSlideIn(FrameworkElement el, double fromY, int ms = 160)
    {
        var tt = new TranslateTransform(0, fromY);
        el.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
    }
}

/// <summary>メニュー項目。Glyph は Segoe Fluent Icons の文字。</summary>
public sealed record MenuItemSpec(string Glyph, string Text, Action Action, bool Danger = false)
{
    public static MenuItemSpec Separator => new("", "-", () => { });
    public bool IsSeparator => Text == "-";
}

/// <summary>標準 ContextMenu の代わりのモダンなポップアップメニュー。</summary>
public static class Flyout
{
    /// <summary>
    /// ポップアップを開いたボタンを、開いている間だけヒットテスト対象外にする。
    /// （StaysOpen=false のポップアップは外側の押下で閉じるが、その押下がボタンにも届くと閉じた直後に開き直してしまうため）
    /// </summary>
    public static void ToggleGuard(Popup popup, UIElement target)
    {
        if (!popup.IsOpen) return;
        target.IsHitTestVisible = false;
        popup.Closed += (_, _) => target.Dispatcher.BeginInvoke(() => target.IsHitTestVisible = true,
            System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// メニューを開く。開いている間は呼び出し元のボタンをクリック不可にしておくことで、
    /// ボタンをもう一度押したとき「外側クリックで閉じる」だけが起きて再オープンされない（開閉トグルになる）。
    /// </summary>
    public static Popup Show(UIElement target, IEnumerable<MenuItemSpec> items, PlacementMode placement = PlacementMode.MousePoint)
    {
        var t = Theme.Current;
        var panel = new StackPanel { Margin = new Thickness(4) };
        var popup = FluentSurface.Create(panel, target, placement);

        foreach (var spec in items)
        {
            if (spec.IsSeparator)
            {
                panel.Children.Add(new Border { Height = 1, Background = t.Stroke, Margin = new Thickness(-4, 3, -4, 3) });
                continue;
            }
            var row = new Grid { Margin = new Thickness(12, 8, 20, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var g = UiKit.Glyph(spec.Glyph, 14);
            g.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(g);
            var l = UiKit.Label(spec.Text, 14);
            Grid.SetColumn(l, 1);
            row.Children.Add(l);
            var tile = new Tile { Child = row, MinWidth = 220, Margin = new Thickness(0, 1, 0, 1) };
            var action = spec.Action;
            tile.Click += _ => { popup.IsOpen = false; action(); };
            panel.Children.Add(tile);
        }

        popup.IsOpen = true;
        ToggleGuard(popup, target);
        FluentSurface.AnimateOpen(panel, placement == PlacementMode.Top ? 1 : 0);
        if (panel.Children.Count > 0 && panel.Children[0] is Tile first) first.Focus();
        return popup;
    }
}
