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

    private MetricCell? _downCell, _upCell;
    private TextBlock? _sharedUnit;

    /// <summary>リング以外の見た目。受信・送信のセルを並べる（ダウンロードだけのスパークラインもある）。</summary>
    private FrameworkElement BuildStyled(NetSpeedStyle style)
    {
        var downSpec = new MetricSpec(L.T("受信"), NetSpeedColors.DownGlyph, NetSpeedColors.Down, false);
        var upSpec = new MetricSpec(L.T("送信"), NetSpeedColors.UpGlyph, NetSpeedColors.Up, false);
        // 文字ラベルの形（テキスト・タイル）は、矢印を使わない。テキストは、受信だけ色を付ける
        if (style == NetSpeedStyle.Text) { downSpec = downSpec with { Glyph = null }; upSpec = upSpec with { Glyph = null, Base = (T.Text as SolidColorBrush)?.Color ?? Colors.White }; }
        if (style == NetSpeedStyle.Tile) { downSpec = downSpec with { Glyph = null }; upSpec = upSpec with { Glyph = null }; }
        MetricCell Make(MetricSpec sp) => style switch
        {
            NetSpeedStyle.HBar => new HBarCell(sp),
            NetSpeedStyle.Sparkline => new SparkCell(sp, NetSpeed.Capacity),
            NetSpeedStyle.VBar => new VBarCell(sp, false),
            NetSpeedStyle.Tile => new TileCell(sp),
            NetSpeedStyle.Text => new LabelValueCell(sp),
            NetSpeedStyle.Compact => new TextCell(sp, false, false, false),
            _ => new TextCell(sp, true, true, false), // TwoLine
        };
        _downCell = Make(downSpec);
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        panel.Orientation = _downCell.Stacked ? Orientation.Vertical : Orientation.Horizontal;
        panel.Children.Add(_downCell.Root);
        if (style != NetSpeedStyle.Sparkline) // スパークラインは受信だけ
        {
            _upCell = Make(upSpec);
            _upCell.Root.Margin = _downCell.Stacked ? new Thickness(0) : new Thickness(style == NetSpeedStyle.Tile ? 6 : style == NetSpeedStyle.Text ? 8 : 12, 0, 0, 0);
            panel.Children.Add(_upCell.Root);
        }
        if (style is NetSpeedStyle.Compact or NetSpeedStyle.Text)
        {
            _sharedUnit = UiKit.Label("Mb/s", 10, T.TextSecondary);
            _sharedUnit.TextTrimming = TextTrimming.None;
            _sharedUnit.Margin = new Thickness(8, 0, 0, 0);
            panel.Children.Add(_sharedUnit);
        }
        panel.Loaded += (_, _) => { NetSpeed.Updated += Apply; Apply(); };
        panel.Unloaded += (_, _) => NetSpeed.Updated -= Apply;
        PadOverride = new Thickness(0);
        return new Border { Background = T.Hover, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6), Child = panel };
    }

    protected override FrameworkElement BuildContent()
    {
        var style = Vertical ? NetSpeedStyle.Ring : (Settings.Current ?? new Settings()).NetSpeedWidgetStyle;
        if (style != NetSpeedStyle.Ring) return BuildStyled(style);
        // 受信・送信それぞれの円。円の中に数値（単位は、ホバーのツールチップ）
        var down = new SolidColorBrush(NetSpeedColors.Down);
        var up = new SolidColorBrush(NetSpeedColors.Up);
        double size = Vertical ? 26 : 32;
        double font = Vertical ? 8 : 9;
        _downRing = new Ring(size, 3, down, Track, T.Text, font);
        _upRing = new Ring(size, 3, up, Track, T.Text, font);

        var row = new StackPanel { Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _upRing.Margin = Vertical ? new Thickness(0, 4, 0, 0) : new Thickness(8, 0, 0, 0);
        row.Children.Add(_downRing);
        row.Children.Add(_upRing);

        // OS の通信量は 1 秒ごとの Tick で測る。結果が出るたびに表示を更新する
        row.Loaded += (_, _) => { NetSpeed.Updated += Apply; Apply(); };
        row.Unloaded += (_, _) => NetSpeed.Updated -= Apply;
        if (Vertical) return row;
        row.Margin = new Thickness(10, 0, 10, 0);
        PadOverride = new Thickness(0);
        return new Border { Background = T.Hover, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6), Child = row };
    }

    public override void Tick(int count)
    {
        if (Card != null) NetSpeed.Sample(Card.Dispatcher);
    }

    private void Apply()
    {
        double d = NetSpeed.CurrentDown, u = NetSpeed.CurrentUp;
        // 円・バー・塗りは、直近の最大（下限 10 Mbps）に対する割合。受信と送信は同じ物差しで比べられるようにする
        double scale = Math.Max(10, Math.Max(NetSpeed.Down.DefaultIfEmpty(0).Max(), NetSpeed.Up.DefaultIfEmpty(0).Max()));
        if (_downCell != null)
        {
            var (dv0, du0) = NetSpeed.Format(d);
            var (uv0, uu0) = NetSpeed.Format(u);
            string common = _sharedUnit != null ? NetSpeed.Format(Math.Max(d, u)).unit : "";
            if (_sharedUnit != null) { dv0 = NetSpeed.FormatIn(d, common); uv0 = NetSpeed.FormatIn(u, common); _sharedUnit.Text = common; }
            _downCell.Set(new MetricSample { Pct = d / scale * 100, Hist = NetSpeed.Down, HistMax = Math.Max(1, NetSpeed.Down.DefaultIfEmpty(0).Max()), Value = dv0, Unit = du0 });
            _upCell?.Set(new MetricSample { Pct = u / scale * 100, Hist = NetSpeed.Up, HistMax = Math.Max(1, NetSpeed.Up.DefaultIfEmpty(0).Max()), Value = uv0, Unit = uu0 });
            if (Card != null) Card.ToolTip = $"↓ {dv0} {_sharedUnit?.Text ?? du0}   ↑ {uv0} {_sharedUnit?.Text ?? uu0}";
            return;
        }
        if (_downRing == null || _upRing == null) return;
        // 円の中は数字だけ。2 つの単位は大きいほうに揃える（単位は、ツールチップに出す）
        string unit = NetSpeed.Format(Math.Max(d, u)).unit;
        string dv = NetSpeed.FormatIn(d, unit), uv = NetSpeed.FormatIn(u, unit);
        _downRing.Set(d / scale * 100, dv);
        _upRing.Set(u / scale * 100, uv);
        if (Card != null) Card.ToolTip = $"↓ {dv} {unit}   ↑ {uv} {unit}";
    }

    public override FrameworkElement? BuildDetailsView() => new NetSpeedDetailsView();

    public override IEnumerable<MenuItemSpec> ExtraMenu() => new[]
    {
        WidgetSettingsMenu.Choice("", L.T("表示の形"), (Settings.Current ?? new Settings()).NetSpeedWidgetStyle, new[]
        {
            (NetSpeedStyle.Ring, L.T("リング")), (NetSpeedStyle.HBar, L.T("横バー")), (NetSpeedStyle.VBar, L.T("縦バー")),
            (NetSpeedStyle.Sparkline, L.T("スパークライン（ダウンロード）")), (NetSpeedStyle.Tile, L.T("塗りつぶしタイル")), (NetSpeedStyle.Text, L.T("テキストのみ（受信・送信ラベル）")),
            (NetSpeedStyle.TwoLine, L.T("矢印と数値（2段）")), (NetSpeedStyle.Compact, L.T("1行コンパクト")),
        }, (c, v) => c.NetSpeedWidgetStyle = v),
        MenuItemSpec.Separator,
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
                : s.Mbps > 0 ? s.Name + " ・ " + L.F("リンク速度 {0} Mb/s", s.Mbps)
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
            _unit = UiKit.Label("Mb/s", 16, t.TextSecondary);
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
