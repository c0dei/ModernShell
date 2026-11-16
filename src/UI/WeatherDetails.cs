using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>天気の詳細（今の天気・時間ごと・週間）。バーの天気ウィジェットをクリックしたときのポップアップの中身。</summary>
public sealed class WeatherDetailsView : Border
{
    private readonly string _city;
    private readonly Theme _t = Theme.Current;
    private readonly StackPanel _body = new();
    private bool _busy;

    public WeatherDetailsView(string city, Weather.Report? report)
    {
        _city = city;
        Width = 400;
        Padding = new Thickness(16, 14, 16, 14);
        Child = _body;
        TextElement.SetForeground(this, _t.Text);
        if (report?.More != null) Fill(report);
        else { Fill(null); Refresh(); }
    }

    private static string Num(double v) => $"{v:0}°";

    private TextBlock Label(string text, double size, Brush? fg = null, FontWeight? w = null) => UiKit.Label(text, size, fg ?? _t.Text, w);

    private TextBlock Sym(int code, double size, Brush? fg = null) => new()
    {
        Text = Weather.Symbol(code), FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = size, Foreground = fg ?? WeatherSymbols.Brush(code),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>最新の天気を取り直して、表示を入れ替える（今の表示は消さない）。</summary>
    private void Refresh()
    {
        if (_busy) return;
        _busy = true;
        System.Threading.Tasks.Task.Run(() => Weather.RefreshAsync(_city)).ContinueWith(r =>
            Dispatcher.BeginInvoke(() =>
            {
                _busy = false;
                if (r.Exception == null && r.Result is { } w) Fill(w);
                else if (Weather.Cached(_city) is null) { _body.Children.Clear(); _body.Children.Add(Label(L.T("通信できませんでした（↻ で再試行）"), 13, _t.TextSecondary)); }
            }), System.Threading.Tasks.TaskScheduler.Default);
    }

    private void Fill(Weather.Report? w)
    {
        _body.Children.Clear();
        if (w == null) { _body.Children.Add(Label(L.T("読み込み中…"), 13, _t.TextSecondary)); return; }

        // ---- 見出し: 今の天気 ----
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var big = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        big.Children.Add(Sym(w.Code, 40));
        var temp = Label(Num(w.Temperature), 40, _t.Text, FontWeights.SemiBold);
        temp.Margin = new Thickness(10, 0, 0, 0);
        big.Children.Add(temp);
        head.Children.Add(big);
        var info = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Label(w.Place, 14, _t.Text, FontWeights.SemiBold));
        info.Children.Add(Label($"{w.Description}　{L.F("最高 {0:0}° / 最低 {1:0}°", w.Max, w.Min)}", 12, _t.TextSecondary));
        if (w.More is { } m)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (m.Feels is { } f) parts.Add(L.F("体感 {0:0}°", f));
            if (m.Humidity is { } hu) parts.Add(L.F("湿度 {0}%", hu));
            if (m.Wind is { } wd) parts.Add(L.F("風 {0:0} km/h", wd));
            if (parts.Count > 0) info.Children.Add(Label(string.Join("　", parts), 12, _t.TextSecondary));
        }
        Grid.SetColumn(info, 1);
        head.Children.Add(info);
        var refresh = new Tile { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Top, CornerRadius = _t.Corner(4), Child = UiKit.Glyph("", 12, _t.TextSecondary), ToolTip = L.T("今すぐ更新") };
        refresh.Click += _ => { refresh.Opacity = 0.4; Refresh(); };
        Grid.SetColumn(refresh, 2);
        head.Children.Add(refresh);
        _body.Children.Add(head);

        if (w.More == null) return;
        var more = w.More;

        // ---- 時間ごと（3 時間おき 8 コマ）----
        if (more.Hours.Count > 0)
        {
            _body.Children.Add(Heading(L.T("時間ごと")));
            var row = new UniformGrid { Columns = 8 };
            foreach (var h in more.Hours.Where((_, i) => i % 3 == 0).Take(8))
            {
                var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                var time = Label(L.IsEnglish ? h.Time.ToString("h tt", L.Culture) : h.Time.Hour + "時", 11, _t.TextSecondary);
                time.HorizontalAlignment = HorizontalAlignment.Center;
                col.Children.Add(time);
                var sym = Sym(h.Code, 20); sym.Margin = new Thickness(0, 4, 0, 2);
                col.Children.Add(sym);
                var t = Label(Num(h.Temp), 13, _t.Text, FontWeights.SemiBold); t.HorizontalAlignment = HorizontalAlignment.Center;
                col.Children.Add(t);
                var rain = Label(h.Rain is { } r ? r + "%" : "", 10, new SolidColorBrush(Color.FromRgb(0x3B, 0x8E, 0xEA))); rain.HorizontalAlignment = HorizontalAlignment.Center;
                col.Children.Add(rain);
                row.Children.Add(col);
            }
            _body.Children.Add(row);
        }

        // ---- 週間 ----
        if (more.Days.Count > 0)
        {
            _body.Children.Add(Heading(L.T("週間予報")));
            double gmin = more.Days.Min(d => d.Min), gmax = more.Days.Max(d => d.Max), range = Math.Max(1, gmax - gmin);
            const double barW = 110;
            for (int i = 0; i < more.Days.Count; i++)
            {
                var d = more.Days[i];
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                foreach (var wd in new[] { 70.0, 28.0, 44.0, 36.0, barW, 36.0 })
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(wd) });
                string day = i == 0 ? L.T("今日") : i == 1 ? L.T("明日") : d.Date.ToString(L.IsEnglish ? "ddd" : "dddd", L.Culture);
                var dayLabel = Label(day, 13, _t.Text, i == 0 ? FontWeights.SemiBold : null);
                g.Children.Add(dayLabel);
                var s = Sym(d.Code, 16); Grid.SetColumn(s, 1); g.Children.Add(s);
                var rain = Label(d.Rain is { } r ? r + "%" : "", 11, new SolidColorBrush(Color.FromRgb(0x3B, 0x8E, 0xEA)));
                rain.HorizontalAlignment = HorizontalAlignment.Right; rain.VerticalAlignment = VerticalAlignment.Center; rain.Margin = new Thickness(0, 0, 8, 0);
                Grid.SetColumn(rain, 2); g.Children.Add(rain);
                var min = Label(Num(d.Min), 12, _t.TextSecondary); min.HorizontalAlignment = HorizontalAlignment.Right; min.VerticalAlignment = VerticalAlignment.Center; min.Margin = new Thickness(0, 0, 6, 0);
                Grid.SetColumn(min, 3); g.Children.Add(min);
                // 気温の幅の棒（全体の最低〜最高の中での位置）
                var track = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center };
                track.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), CornerRadius = new CornerRadius(2) });
                track.Children.Add(new Border
                {
                    Background = _t.AccentBrush, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness((d.Min - gmin) / range * barW, 0, 0, 0), Width = Math.Max(4, (d.Max - d.Min) / range * barW),
                });
                Grid.SetColumn(track, 4); g.Children.Add(track);
                var max = Label(Num(d.Max), 12, _t.Text, FontWeights.SemiBold); max.VerticalAlignment = VerticalAlignment.Center; max.Margin = new Thickness(6, 0, 0, 0);
                Grid.SetColumn(max, 5); g.Children.Add(max);
                _body.Children.Add(g);
            }
        }

        // ---- 足元: 日の出・日の入、更新時刻 ----
        var foot = new System.Collections.Generic.List<string>();
        if (more.Sunrise != null) foot.Add(L.F("日の出 {0}", more.Sunrise));
        if (more.Sunset != null) foot.Add(L.F("日の入 {0}", more.Sunset));
        foot.Add(L.F("{0} に更新", w.FetchedAt.ToString("H:mm")));
        var footLabel = Label(string.Join("　", foot), 11, _t.TextSecondary);
        footLabel.Margin = new Thickness(0, 10, 0, 0);
        _body.Children.Add(footLabel);
    }

    private TextBlock Heading(string text)
    {
        var h = Label(text, 12, _t.TextSecondary, FontWeights.SemiBold);
        h.Margin = new Thickness(0, 14, 0, 6);
        return h;
    }
}

/// <summary>天気の記号の見た目（色付きの Segoe UI Symbol）。</summary>
public static class WeatherSymbols
{
    public static readonly FontFamily Font = new("Segoe UI Symbol");

    public static Brush Brush(int code)
    {
        var (r, g, b) = Weather.SymbolRgb(code);
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    /// <summary>既存の TextBlock（アイコン用）を、天気コードの記号・色にする。</summary>
    public static void Apply(TextBlock t, int code)
    {
        t.FontFamily = Font;
        t.Text = Weather.Symbol(code);
        t.Foreground = Brush(code);
    }
}
