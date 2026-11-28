using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>IME のウィジェットとポップアップで共有する色（アクセント色の上に載せる文字色など）。</summary>
internal static class ImeLook
{
    /// <summary>アクセント色の上で読みやすい文字色（白 / 黒）。</summary>
    public static Brush OnAccent(Theme t)
    {
        static double Lin(byte c) { double v = c / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        double lum = 0.2126 * Lin(t.Accent.R) + 0.7152 * Lin(t.Accent.G) + 0.0722 * Lin(t.Accent.B);
        return lum > 0.179 ? Brushes.Black : Brushes.White;
    }

    public static Brush AccentTint(Theme t, byte alpha) => new SolidColorBrush(Color.FromArgb(alpha, t.Accent.R, t.Accent.G, t.Accent.B));

    public static string Name(ImeMode m) => m switch
    {
        ImeMode.Hiragana => L.T("ひらがな"), ImeMode.Katakana => L.T("全角カタカナ"), ImeMode.HalfKatakana => L.T("半角カタカナ"),
        ImeMode.FullAlpha => L.T("全角英数"), ImeMode.HalfAlpha => L.T("半角英数"), _ => L.T("直接入力"),
    };
}

/// <summary>
/// IME のウィジェット: 日本語入力の状態（あ / カ / A など）を角丸の札で表示する。
/// クリックで入力モードを選ぶポップアップ。オンにすると、バー標準の IME 表示の代わりになる。
/// </summary>
public sealed class ImeBarWidget : BarWidget
{
    public override string Id => "ime";
    private Border? _badge;
    private TextBlock? _text;
    private string _sig = "";

    protected override FrameworkElement BuildContent()
    {
        _text = UiKit.Label("", Vertical ? 14 : 16, T.Text, FontWeights.SemiBold);
        _text.TextTrimming = TextTrimming.None;
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        double side = Vertical ? 24 : Math.Max(24, Math.Min(32, TileH - 12));
        _badge = new Border
        {
            Width = side, Height = side, CornerRadius = T.Corner(8), Child = _text,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        // 前面のアプリの切り替え・IME のキー操作のあとに、すぐ読み直す
        _badge.Loaded += (_, _) => { ImeControl.MayHaveChanged -= Refresh; ImeControl.MayHaveChanged += Refresh; Refresh(); };
        _badge.Unloaded += (_, _) => ImeControl.MayHaveChanged -= Refresh;
        Refresh();
        if (Vertical) return _badge;
        // 他のウィジェットと同じ、薄い地と縁のある角丸のカード
        _badge.Margin = new Thickness(8, 0, 8, 0);
        PadOverride = new Thickness(0);
        return Frame(_badge);
    }

    public override void Tick(int count) => Refresh();

    private void Refresh()
    {
        if (_badge == null || _text == null) return;
        var s = ImeControl.Read();
        // 日本語入力がオン（あ / カ）のときはアクセント色の札、英数・オフのときは薄い札
        bool on = s.Japanese && s.Mode is ImeMode.Hiragana or ImeMode.Katakana or ImeMode.HalfKatakana;
        string sig = s.Text + (on ? "1" : "0");
        if (sig == _sig) return;
        _sig = sig;
        _text.Text = s.Text;
        _text.FontSize = s.Text.Length > 1 ? 12 : Vertical ? 14 : 16;
        _badge.Background = on ? T.AccentBrush : T.Hover;
        _text.Foreground = on ? ImeLook.OnAccent(T) : T.Text;
        if (Card != null) Card.ToolTip = s.Japanese ? L.F("日本語入力: {0}", ImeLook.Name(s.Mode)) : L.T("日本語以外のキーボード");
    }

    public override FrameworkElement? BuildDetailsView() => new ImeDetailsView();

    public override IEnumerable<MenuItemSpec> ExtraMenu() => new[]
    {
        new MenuItemSpec("", L.T("言語と入力の設定"), OpenSettings),
        MenuItemSpec.Separator,
    };

    internal static void OpenSettings()
    {
        try { Launcher.Open("ms-settings:regionlanguage-jpnime"); }
        catch (Exception ex) { Log.Error("ime settings", ex); }
    }
}

// ---------------------------------------------------------------------------

/// <summary>IME のポップアップ: 入力モード（ひらがな〜直接入力）・設定へのリンク。</summary>
internal sealed class ImeDetailsView : Border
{
    private readonly Theme _t = Theme.Current;
    private readonly IntPtr _ime = ImeControl.Read().Ime; // 開いた時点の前面のアプリの IME（操作中に前面が変わっても対象を間違えない）
    private readonly TextBlock _current;
    private readonly List<(ImeMode Mode, Border Frame, Border Box, TextBlock Glyph, TextBlock Check)> _rows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _soon = new() { Interval = TimeSpan.FromMilliseconds(120) };

    private static readonly (ImeMode Mode, string Glyph, string Sample, bool Outline)[] Modes =
    {
        (ImeMode.Hiragana, "あ", "あいう", false),
        (ImeMode.Katakana, "カ", "アイウ", false),
        (ImeMode.HalfKatakana, "ｶ", "ｱｲｳ", false),
        (ImeMode.FullAlpha, "A", "ＡＢＣ", false),
        (ImeMode.HalfAlpha, "A", "ABC", false),
        (ImeMode.Direct, "A", "", true),
    };

    public ImeDetailsView()
    {
        Width = 340;
        Padding = new Thickness(14, 12, 14, 8);
        System.Windows.Documents.TextElement.SetForeground(this, _t.Text);
        var body = new StackPanel();
        Child = body;

        // 見出しと、今のモード
        var head = new Grid { Margin = new Thickness(4, 2, 4, 8) };
        head.Children.Add(UiKit.Label(L.T("日本語入力"), 13, _t.TextSecondary, FontWeights.SemiBold));
        _current = UiKit.Label("", 13, _t.TextSecondary);
        _current.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(_current);
        body.Children.Add(head);

        foreach (var (mode, glyph, sample, outline) in Modes)
        {
            var box = new Border { Width = 32, Height = 32, CornerRadius = _t.Corner(8), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            var g = UiKit.Label(glyph, 15, _t.Text, FontWeights.Bold);
            g.TextTrimming = TextTrimming.None;
            g.HorizontalAlignment = HorizontalAlignment.Center;
            box.Child = g;
            if (outline) box.BorderBrush = _t.TextSecondary; else box.Background = _t.Hover;

            string subText = mode == ImeMode.Direct ? L.T("IME オフ") : sample;
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
            texts.Children.Add(UiKit.Label(ImeLook.Name(mode), 15, _t.Text, FontWeights.SemiBold));
            texts.Children.Add(UiKit.Label(subText, 12, _t.TextSecondary));
            var check = UiKit.Glyph("", 14, _t.AccentBrush);
            check.Visibility = Visibility.Collapsed;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(box);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);

            var row = new Tile { Padding = new Thickness(8, 6, 12, 6), Margin = new Thickness(0, 1, 0, 1), CornerRadius = _t.Corner(10), Child = grid };
            var m = mode;
            row.Click += _ => { ImeControl.SetMode(_ime, m); Soon(); };
            // 選んでいるモードの背景と枠は、ホバーで書き換わる Tile ではなく外側の枠に付ける
            var frame = new Border { CornerRadius = _t.Corner(10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 1, 0, 1), Child = row, BorderBrush = Brushes.Transparent };
            row.Margin = new Thickness(0);
            row.BorderThickness = new Thickness(0);
            body.Children.Add(frame);
            _rows.Add((mode, frame, box, g, check));
        }


        // 切り替えキーの案内
        var hint = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 12, 4, 0) };
        hint.Children.Add(new Border
        {
            BorderBrush = _t.TextSecondary, BorderThickness = new Thickness(1), CornerRadius = _t.Corner(4), Padding = new Thickness(5, 1, 5, 1), VerticalAlignment = VerticalAlignment.Center,
            Child = UiKit.Label(L.T("半角/全角"), 12, _t.TextSecondary),
        });
        var hintText = UiKit.Label(L.T("キーで入力のオン・オフを切り替え"), 12, _t.TextSecondary);
        hintText.Margin = new Thickness(6, 0, 0, 0);
        hint.Children.Add(hintText);
        body.Children.Add(hint);

        // 設定へ
        body.Children.Add(new Border { Height = 1, Background = _t.Stroke, Margin = new Thickness(0, 10, 0, 6) });
        var open = new Tile { Padding = new Thickness(10) };
        var og = new Grid();
        og.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        og.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        og.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        og.Children.Add(UiKit.Glyph("", 15, _t.TextSecondary));
        var txt = UiKit.Label(L.T("言語と入力の設定"), 13);
        txt.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(txt, 1);
        og.Children.Add(txt);
        var chev = UiKit.Glyph("", 11, _t.TextSecondary);
        Grid.SetColumn(chev, 2);
        og.Children.Add(chev);
        open.Child = og;
        open.Click += _ => ImeBarWidget.OpenSettings();
        body.Children.Add(open);

        _timer.Tick += (_, _) => Refresh();
        _soon.Tick += (_, _) => { _soon.Stop(); Refresh(); };
        Loaded += (_, _) => { ImeControl.MayHaveChanged += Soon; Refresh(); _timer.Start(); };
        Unloaded += (_, _) => { ImeControl.MayHaveChanged -= Soon; _timer.Stop(); _soon.Stop(); };
        Refresh();
    }

    private static FrameworkElement Pad(FrameworkElement el, Thickness m) { el.Margin = m; return el; }


    /// <summary>IME は操作を受けてから少し遅れて状態を変えるので、少し待って読み直す。</summary>
    private void Soon() { _soon.Stop(); _soon.Start(); }

    private void Refresh()
    {
        var s = ImeControl.Read();
        _current.Text = s.Japanese ? ImeLook.Name(s.Mode) : s.Text;
        var onInk = ImeLook.OnAccent(_t);
        foreach (var (mode, row, box, glyph, check) in _rows)
        {
            bool sel = s.Japanese && s.Mode == mode;
            row.Background = sel ? ImeLook.AccentTint(_t, 0x30) : Brushes.Transparent;
            row.BorderBrush = sel ? _t.AccentBrush : Brushes.Transparent;
            check.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
            if (mode != ImeMode.Direct) box.Background = sel ? _t.AccentBrush : _t.Hover;
            glyph.Foreground = sel && mode != ImeMode.Direct ? onInk : _t.Text;
            row.IsEnabled = s.Usable;
            row.Opacity = s.Usable ? 1 : 0.5;
        }
    }
}
