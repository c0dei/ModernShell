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

    private System.Windows.Controls.Image Sym(int code, double size, bool isDay = true)
    {
        var img = WeatherSymbols.Create(code, isDay, size);
        img.HorizontalAlignment = HorizontalAlignment.Center;
        img.VerticalAlignment = VerticalAlignment.Center;
        return img;
    }

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

    // ---- 強弱（4 段階）----
    // 1 主役: 今の気温・最高気温・「今日」の行 … 大きく太く、いちばん濃い色
    // 2 本文: 場所・天気の説明・各行の値        … ふつうの濃さ
    // 3 補助: 項目名・最低気温・見出し           … 薄い色（TextSecondary）
    // 4 控えめ: 降水 0%・更新時刻・日の出入り    … さらに薄く（透明度を下げる）
    private static readonly Brush RainBrush = Frozen(Color.FromRgb(0x3B, 0x8E, 0xEA));
    private static readonly Brush SatBrush = Frozen(Color.FromRgb(0x4C, 0x8D, 0xE8));
    private static readonly Brush SunBrush = Frozen(Color.FromRgb(0xE5, 0x5B, 0x5B));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>気温 → 色（寒い=青、涼しい=水色、快適=緑、暖かい=黄、暑い=オレンジ〜赤）。</summary>
    private static Color TempColor(double t)
    {
        (double t, Color c)[] stops =
        {
            (-5, Color.FromRgb(0x5B, 0x8D, 0xEF)), (5, Color.FromRgb(0x4B, 0xB3, 0xD4)), (15, Color.FromRgb(0x5C, 0xC9, 0x8A)),
            (22, Color.FromRgb(0xF2, 0xB8, 0x4B)), (30, Color.FromRgb(0xF2, 0x78, 0x4B)), (38, Color.FromRgb(0xE0, 0x45, 0x3A)),
        };
        if (t <= stops[0].t) return stops[0].c;
        for (int i = 1; i < stops.Length; i++)
            if (t <= stops[i].t)
            {
                double k = (t - stops[i - 1].t) / (stops[i].t - stops[i - 1].t);
                Color a = stops[i - 1].c, b = stops[i].c;
                return Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
            }
        return stops[^1].c;
    }

    /// <summary>降水確率の表示: 高いほど濃く太く、0〜19% は薄く。</summary>
    private TextBlock RainLabel(int? pct, double size)
    {
        int v = pct ?? 0;
        var l = Label(pct is null ? "" : v + "%", size, v >= 20 ? RainBrush : _t.TextSecondary, v >= 50 ? FontWeights.SemiBold : null);
        if (v < 20) l.Opacity = 0.5;
        return l;
    }

    /// <summary>「項目名 値」の一行（項目名は薄く、値はふつうの濃さ）。</summary>
    private TextBlock Stat(string label, string value)
    {
        var t = new TextBlock { FontSize = 12, Margin = new Thickness(0, 0, 12, 0) };
        t.Inlines.Add(new System.Windows.Documents.Run(label + " ") { Foreground = _t.TextSecondary });
        t.Inlines.Add(new System.Windows.Documents.Run(value) { Foreground = _t.Text });
        return t;
    }

    private void Fill(Weather.Report? w)
    {
        _body.Children.Clear();
        if (w == null) { _body.Children.Add(Label(L.T("読み込み中…"), 13, _t.TextSecondary)); return; }

        // ---- 見出し: 今の天気（1 主役）----
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var big = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        big.Children.Add(Sym(w.Code, 52, w.IsDay));
        var temp = Label(Num(w.Temperature), 46, _t.Text, FontWeights.SemiBold);
        temp.Margin = new Thickness(10, -4, 0, 0);
        big.Children.Add(temp);
        head.Children.Add(big);
        var info = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var place = Label(w.Place, 15, _t.Text, FontWeights.SemiBold);
        info.Children.Add(place);
        var desc = new TextBlock { FontSize = 13, Margin = new Thickness(0, 1, 0, 4) };
        desc.Inlines.Add(new System.Windows.Documents.Run(w.Description) { Foreground = _t.Text });
        desc.Inlines.Add(new System.Windows.Documents.Run("　" + Num(w.Max)) { Foreground = _t.Text, FontWeight = FontWeights.SemiBold });
        desc.Inlines.Add(new System.Windows.Documents.Run(" / " + Num(w.Min)) { Foreground = _t.TextSecondary });
        info.Children.Add(desc);
        if (w.More is { } m)
        {
            var stats = new WrapPanel();
            if (m.Feels is { } f) stats.Children.Add(Stat(L.T("体感"), Num(f)));
            if (m.Humidity is { } hu) stats.Children.Add(Stat(L.T("湿度"), hu + "%"));
            if (m.Wind is { } wd) stats.Children.Add(Stat(L.T("風"), $"{wd:0} km/h"));
            info.Children.Add(stats);
        }
        Grid.SetColumn(info, 1);
        head.Children.Add(info);
        var refresh = new Tile { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Top, CornerRadius = _t.Corner(4), Child = UiKit.Glyph("\uE72C", 12, _t.TextSecondary), ToolTip = L.T("今すぐ更新") };
        refresh.Click += _ => { refresh.Opacity = 0.4; Refresh(); };
        Grid.SetColumn(refresh, 2);
        head.Children.Add(refresh);
        _body.Children.Add(head);

        if (w.More == null) return;
        var more = w.More;

        // ---- 時間ごと（3 時間おき 8 コマ。いちばん左が「今」）----
        if (more.Hours.Count > 0)
        {
            _body.Children.Add(Heading(L.T("時間ごと")));
            var row = new UniformGrid { Columns = 8 };
            int idx = 0;
            foreach (var h in more.Hours.Where((_, i) => i % 3 == 0).Take(8))
            {
                bool now = idx++ == 0;
                var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                var time = Label(now ? L.T("今") : L.IsEnglish ? h.Time.ToString("h tt", L.Culture) : h.Time.Hour + "時", 11, now ? _t.Text : _t.TextSecondary, now ? FontWeights.SemiBold : null);
                time.HorizontalAlignment = HorizontalAlignment.Center;
                col.Children.Add(time);
                var sym = Sym(h.Code, 30, h.IsDay); sym.Margin = new Thickness(0, 2, 0, 0);
                col.Children.Add(sym);
                var t = Label(Num(h.Temp), 14, _t.Text, FontWeights.SemiBold); t.HorizontalAlignment = HorizontalAlignment.Center;
                col.Children.Add(t);
                var rain = RainLabel(h.Rain, 10); rain.HorizontalAlignment = HorizontalAlignment.Center;
                col.Children.Add(rain);
                // 「今」の列は、薄い背景で目立たせる
                row.Children.Add(new Border { Child = col, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(1, 0, 1, 0), CornerRadius = _t.Corner(6), Background = now ? _t.Surface : Brushes.Transparent });
            }
            _body.Children.Add(row);
        }

        // ---- 週間（今日の行は薄い背景。土曜は青、日曜は赤の曜日名。最低は薄く・最高は濃く。棒は気温の色）----
        if (more.Days.Count > 0)
        {
            _body.Children.Add(Heading(L.T("週間予報")));
            double gmin = more.Days.Min(d => d.Min), gmax = more.Days.Max(d => d.Max), range = Math.Max(1, gmax - gmin);
            const double barW = 110;
            for (int i = 0; i < more.Days.Count; i++)
            {
                var d = more.Days[i];
                var g = new Grid { Margin = new Thickness(0, 0, 0, 0) };
                foreach (var wd in new[] { 70.0, 28.0, 44.0, 36.0, barW, 36.0 })
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(wd) });
                string day = i == 0 ? L.T("今日") : i == 1 ? L.T("明日") : d.Date.ToString(L.IsEnglish ? "ddd" : "dddd", L.Culture);
                Brush dayBrush = i == 0 ? _t.Text : !L.IsEnglish && d.Date.DayOfWeek == DayOfWeek.Saturday ? SatBrush : !L.IsEnglish && d.Date.DayOfWeek == DayOfWeek.Sunday ? SunBrush : _t.Text;
                var dayLabel = Label(day, 13, dayBrush, i == 0 ? FontWeights.SemiBold : null);
                dayLabel.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(dayLabel);
                var s = Sym(d.Code, 26); Grid.SetColumn(s, 1); g.Children.Add(s);
                var rain = RainLabel(d.Rain, 11);
                rain.HorizontalAlignment = HorizontalAlignment.Right; rain.VerticalAlignment = VerticalAlignment.Center; rain.Margin = new Thickness(0, 0, 8, 0);
                Grid.SetColumn(rain, 2); g.Children.Add(rain);
                var min = Label(Num(d.Min), 13, _t.TextSecondary); min.HorizontalAlignment = HorizontalAlignment.Right; min.VerticalAlignment = VerticalAlignment.Center; min.Margin = new Thickness(0, 0, 6, 0);
                Grid.SetColumn(min, 3); g.Children.Add(min);
                // 気温の幅の棒（全体の最低〜最高の中での位置。色は気温に合わせて、低い側は青〜、高い側は黄〜オレンジ）
                var track = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center };
                track.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), CornerRadius = new CornerRadius(2.5) });
                track.Children.Add(new Border
                {
                    Background = new LinearGradientBrush(TempColor(d.Min), TempColor(d.Max), 0), CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness((d.Min - gmin) / range * barW, 0, 0, 0), Width = Math.Max(5, (d.Max - d.Min) / range * barW),
                });
                Grid.SetColumn(track, 4); g.Children.Add(track);
                var max = Label(Num(d.Max), 14, _t.Text, FontWeights.SemiBold); max.VerticalAlignment = VerticalAlignment.Center; max.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(max, 5); g.Children.Add(max);
                _body.Children.Add(new Border
                {
                    Child = g, Padding = new Thickness(8, 5, 4, 5), CornerRadius = _t.Corner(6), Margin = new Thickness(-8, 0, -4, 0),
                    Background = i == 0 ? _t.Surface : Brushes.Transparent,
                });
            }
        }

        // ---- 足元（4 控えめ）----
        var foot = new System.Collections.Generic.List<string>();
        if (more.Sunrise != null) foot.Add(L.F("日の出 {0}", more.Sunrise));
        if (more.Sunset != null) foot.Add(L.F("日の入 {0}", more.Sunset));
        foot.Add(L.F("{0} に更新", w.FetchedAt.ToString("H:mm")));
        var footLabel = Label(string.Join("　", foot), 11, _t.TextSecondary);
        footLabel.Opacity = 0.65;
        footLabel.Margin = new Thickness(0, 12, 0, 0);
        _body.Children.Add(footLabel);
    }

    private TextBlock Heading(string text)
    {
        var h = Label(text, 11, _t.TextSecondary, FontWeights.SemiBold);
        h.Margin = new Thickness(0, 16, 0, 6);
        h.Opacity = 0.85;
        return h;
    }
}

/// <summary>天気のアイコン（Meteocons の多色 PNG。実行ファイルに埋め込み）。昼は太陽、夜は月で切り替える。</summary>
public static class WeatherSymbols
{
    private static readonly System.Collections.Generic.Dictionary<string, System.Windows.Media.Imaging.BitmapSource?> Cache = new();

    /// <summary>アイコンの画像。見つからないとき（埋め込みの欠け）は null。</summary>
    public static System.Windows.Media.Imaging.BitmapSource? Source(int code, bool isDay)
    {
        var name = Weather.IconName(code, isDay);
        if (Cache.TryGetValue(name, out var cached)) return cached;
        System.Windows.Media.Imaging.BitmapSource? img = null;
        try
        {
            using var s = typeof(WeatherSymbols).Assembly.GetManifestResourceStream("weather." + name + ".png");
            if (s != null)
            {
                var b = new System.Windows.Media.Imaging.BitmapImage();
                b.BeginInit();
                b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                b.StreamSource = s;
                b.EndInit();
                b.Freeze();
                img = b;
            }
        }
        catch (Exception ex) { Log.Error("weather icon " + name, ex); }
        Cache[name] = img;
        return img;
    }

    /// <summary>アイコンの Image 要素（size は表示の大きさ）。</summary>
    public static System.Windows.Controls.Image Create(int code, bool isDay, double size)
    {
        var img = new System.Windows.Controls.Image { Width = size, Height = size, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        img.Source = Source(code, isDay);
        return img;
    }

    public static void Apply(System.Windows.Controls.Image img, int code, bool isDay) => img.Source = Source(code, isDay);
}
