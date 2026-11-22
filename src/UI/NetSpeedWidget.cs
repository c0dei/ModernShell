using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>通信速度の色（受信はアクセント色、送信は緑）。</summary>
internal static class NetSpeedColors
{
    public static readonly Color Up = Color.FromRgb(0x4F, 0xD1, 0xA5);
    public static Color Down => (Theme.Current.AccentBrush as SolidColorBrush)?.Color ?? Color.FromRgb(0x8C, 0x9E, 0xFF);
    public const string DownGlyph = "";
    public const string UpGlyph = "";
}

/// <summary>バーの通信速度のカード: 受信・送信それぞれの円（直近の最大に対する割合）と、いまの速さ。クリックで直近 30 秒の推移。</summary>
public sealed class NetSpeedBarWidget : BarWidget
{
    public override string Id => "netspeed";
    private Ring? _downRing, _upRing;
    private TextBlock? _downVal, _downUnit, _upVal, _upUnit;

    protected override FrameworkElement BuildContent()
    {
        var down = new SolidColorBrush(NetSpeedColors.Down);
        var up = new SolidColorBrush(NetSpeedColors.Up);
        double size = Vertical ? 24 : 30;
        _downRing = MakeRing(size, down, NetSpeedColors.DownGlyph);
        _upRing = MakeRing(size, up, NetSpeedColors.UpGlyph);

        var row = new StackPanel { Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (Vertical)
        {
            _upRing.Margin = new Thickness(0, 4, 0, 0);
            row.Children.Add(_downRing);
            row.Children.Add(_upRing);
        }
        else
        {
            row.Children.Add(Group(_downRing, out _downVal, out _downUnit, 0));
            row.Children.Add(Group(_upRing, out _upVal, out _upUnit, 8));
        }

        // OS の通信量は 1 秒ごとの Tick で測る。結果が出るたびに表示を更新する
        row.Loaded += (_, _) => { NetSpeed.Updated += Apply; Apply(); };
        row.Unloaded += (_, _) => NetSpeed.Updated -= Apply;
        if (Vertical) return row;
        row.Margin = new Thickness(10, 0, 10, 0);
        PadOverride = new Thickness(0);
        return new Border { Background = T.Hover, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6), Child = row };
    }

    private Ring MakeRing(double size, Brush color, string glyph)
    {
        var ring = new Ring(size, 3, color, Track, T.Text, 9);
        ring.Children.Add(UiKit.Glyph(glyph, size * 0.4, color));
        return ring;
    }

    private FrameworkElement Group(Ring ring, out TextBlock value, out TextBlock unit, double left)
    {
        value = UiKit.Label("0", 13, T.Text, FontWeights.SemiBold);
        value.TextTrimming = TextTrimming.None;
        unit = UiKit.Label("Mbps", 10, T.TextSecondary);
        unit.TextTrimming = TextTrimming.None;
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 30, Margin = new Thickness(6, 0, 0, 0) };
        col.Children.Add(value);
        col.Children.Add(unit);
        var g = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(left, 0, 0, 0) };
        g.Children.Add(ring);
        g.Children.Add(col);
        return g;
    }

    public override void Tick(int count)
    {
        if (Card != null) NetSpeed.Sample(Card.Dispatcher);
    }

    private void Apply()
    {
        if (_downRing == null || _upRing == null) return;
        double d = NetSpeed.CurrentDown, u = NetSpeed.CurrentUp;
        // 円は、直近の最大（下限 10 Mbps）に対する割合。受信と送信は同じ物差しで比べられるようにする
        double scale = Math.Max(10, Math.Max(NetSpeed.Down.DefaultIfEmpty(0).Max(), NetSpeed.Up.DefaultIfEmpty(0).Max()));
        _downRing.Set(d / scale * 100, "");
        _upRing.Set(u / scale * 100, "");
        var (dv, du) = NetSpeed.Format(d);
        var (uv, uu) = NetSpeed.Format(u);
        if (_downVal != null && _downUnit != null && _upVal != null && _upUnit != null)
        {
            _downVal.Text = dv; _downUnit.Text = du;
            _upVal.Text = uv; _upUnit.Text = uu;
        }
        if (Card != null) Card.ToolTip = $"↓ {dv} {du}   ↑ {uv} {uu}";
    }

    public override FrameworkElement? BuildDetailsView() => new NetSpeedDetailsView();

    public override IEnumerable<MenuItemSpec> ExtraMenu() => new[]
    {
        new MenuItemSpec("", L.T("ネットワークとインターネットの設定"), () => Launcher.Open("ms-settings:network")),
    };
}

// ---------------------------------------------------------------------------

/// <summary>通信速度のポップアップ: 受信・送信それぞれの、いまの速さと直近 30 秒の棒グラフ。つないでいるネットワークの名前とリンク速度。</summary>
internal sealed class NetSpeedDetailsView : Border
{
    private readonly Theme _t = Theme.Current;
    private readonly SpeedCard _down, _up;
    private readonly TextBlock _link;
    private readonly TextBlock _linkIcon;
    private bool _busy;
    private int _n;

    public NetSpeedDetailsView()
    {
        Width = 360;
        Padding = new Thickness(14, 12, 14, 10);
        System.Windows.Documents.TextElement.SetForeground(this, _t.Text);

        var body = new StackPanel();
        Child = body;

        var head = new Grid { Margin = new Thickness(4, 2, 4, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(UiKit.Label(L.T("通信速度"), 13, _t.TextSecondary, FontWeights.SemiBold));
        var range = UiKit.Label(L.T("直近30秒"), 12, _t.TextSecondary);
        Grid.SetColumn(range, 1);
        head.Children.Add(range);
        body.Children.Add(head);

        // 「ダウンロード」は別の場所で「Downloads（フォルダー）」と訳しているので、ここだけ直接切り替える
        _down = new SpeedCard(_t, NetSpeedColors.DownGlyph, L.IsEnglish ? "Download" : "ダウンロード", L.T("受信"), NetSpeedColors.Down);
        _up = new SpeedCard(_t, NetSpeedColors.UpGlyph, L.T("アップロード"), L.T("送信"), NetSpeedColors.Up);
        body.Children.Add(_down.Root);
        _up.Root.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(_up.Root);

        var linkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 12, 6, 0) };
        _linkIcon = UiKit.Glyph("", 14, _t.TextSecondary);
        _link = UiKit.Label("", 13, _t.TextSecondary);
        _link.Margin = new Thickness(10, 0, 0, 0);
        linkRow.Children.Add(_linkIcon);
        linkRow.Children.Add(_link);
        body.Children.Add(linkRow);

        var open = new Tile { Padding = new Thickness(10, 10, 10, 10), Margin = new Thickness(0, 8, 0, 0) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(UiKit.Glyph("", 15, _t.TextSecondary));
        var txt = UiKit.Label(L.T("ネットワークの詳細を開く"), 13);
        txt.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(txt, 1);
        row.Children.Add(txt);
        var chev = UiKit.Glyph("", 11, _t.TextSecondary);
        Grid.SetColumn(chev, 2);
        row.Children.Add(chev);
        open.Child = row;
        open.Click += _ => Launcher.Open("ms-settings:network-status");
        body.Children.Add(open);

        Loaded += (_, _) => { NetSpeed.Updated += Refresh; Refresh(); _ = RefreshLinkAsync(); };
        Unloaded += (_, _) => NetSpeed.Updated -= Refresh;
        Refresh();
    }

    private void Refresh()
    {
        _down.Set(NetSpeed.Down);
        _up.Set(NetSpeed.Up);
        if (++_n % 5 == 0) _ = RefreshLinkAsync(); // つなぎ先の名前は、5 秒ごとに読み直す
    }

    private async System.Threading.Tasks.Task RefreshLinkAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var s = await NetworkControl.GetStatusAsync();
            _linkIcon.Text = s.Connected && s.Wired ? "" : "";
            _link.Text = !s.Connected ? L.T("未接続")
                : s.Mbps > 0 ? s.Name + " ・ " + L.F("リンク速度 {0} Mbps", s.Mbps)
                : s.Name;
        }
        catch (Exception ex) { Log.Error("netspeed details", ex); }
        finally { _busy = false; }
    }

    /// <summary>受信・送信 1 つ分のカード（アイコン・名前・いまの速さ・直近の棒グラフ）。</summary>
    private sealed class SpeedCard
    {
        private const double ChartHeight = 44, Gap = 3;
        public readonly Border Root;
        private readonly TextBlock _value, _unit;
        private readonly Canvas _chart = new() { Height = ChartHeight, ClipToBounds = true, Margin = new Thickness(0, 12, 0, 0) };
        private readonly Rectangle[] _bars = new Rectangle[NetSpeed.Capacity];
        private readonly Brush _dim, _bright;
        private IReadOnlyList<double> _last = Array.Empty<double>();

        public SpeedCard(Theme t, string glyph, string title, string sub, Color color)
        {
            _bright = new SolidColorBrush(color);
            _dim = new SolidColorBrush(Color.FromArgb(0x88, color.R, color.G, color.B));
            var iconTile = new Border
            {
                Width = 44, Height = 44, CornerRadius = t.Corner(12),
                Background = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B)),
                Child = UiKit.Glyph(glyph, 20, _bright),
            };
            _value = UiKit.Label("0", 34, _bright, FontWeights.SemiBold);
            _value.TextTrimming = TextTrimming.None;
            _unit = UiKit.Label("Mbps", 16, t.TextSecondary);
            _unit.TextTrimming = TextTrimming.None;
            _unit.Margin = new Thickness(5, 8, 0, 0);

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(iconTile);
            var names = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(UiKit.Label(title, 15, t.Text, FontWeights.SemiBold));
            names.Children.Add(UiKit.Label(sub, 12, t.TextSecondary));
            Grid.SetColumn(names, 1);
            head.Children.Add(names);
            var big = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            big.Children.Add(_value);
            big.Children.Add(_unit);
            Grid.SetColumn(big, 2);
            head.Children.Add(big);

            for (int i = 0; i < _bars.Length; i++)
            {
                _bars[i] = new Rectangle { RadiusX = 2, RadiusY = 2, Visibility = Visibility.Collapsed };
                _chart.Children.Add(_bars[i]);
            }
            _chart.SizeChanged += (_, _) => Draw();

            var body = new StackPanel();
            body.Children.Add(head);
            body.Children.Add(_chart);
            Root = new Border { Background = t.Surface, CornerRadius = t.Corner(14), Padding = new Thickness(14), Child = body };
        }

        public void Set(IReadOnlyList<double> history)
        {
            _last = history;
            var (v, u) = NetSpeed.Format(history.Count > 0 ? history[^1] : 0);
            _value.Text = v;
            _unit.Text = u;
            Draw();
        }

        /// <summary>棒グラフ。新しい値が右端。いちばん新しい 1 本だけ濃い色にする。縦は、この 30 秒の最大に合わせる。</summary>
        private void Draw()
        {
            double w = _chart.ActualWidth > 0 ? _chart.ActualWidth : 304;
            double bw = (w - Gap * (_bars.Length - 1)) / _bars.Length;
            double max = Math.Max(1, _last.Count > 0 ? _last.Max() : 1);
            int missing = _bars.Length - _last.Count; // 足りない分は、左を空ける
            for (int i = 0; i < _bars.Length; i++)
            {
                var r = _bars[i];
                int k = i - missing;
                if (k < 0) { r.Visibility = Visibility.Collapsed; continue; }
                double h = 4 + (ChartHeight - 4) * Math.Clamp(_last[k] / max, 0, 1);
                r.Visibility = Visibility.Visible;
                r.Width = bw;
                r.Height = h;
                r.Fill = i == _bars.Length - 1 ? _bright : _dim;
                Canvas.SetLeft(r, i * (bw + Gap));
                Canvas.SetTop(r, ChartHeight - h);
            }
        }
    }
}
