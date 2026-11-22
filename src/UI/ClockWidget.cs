using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>時計の書式（バー標準の時計と、時計ウィジェットで共有する）。</summary>
internal static class ClockFormat
{
    /// <summary>日付だけ表示する設定（時刻を隠す）。日付も隠すときは、空にならないよう時刻を出す。</summary>
    public static bool TimeHidden(Settings s) => s.ClockTimeFormat == ClockTimeFormat.Hidden && s.ClockDateFormat != ClockDateFormat.Hidden;

    /// <summary>設定の書式で時刻を文字にする。「システム」は Windows の地域設定（短い時刻。秒を表示するときは長い時刻）。</summary>
    public static string Time(Settings s, DateTime now)
    {
        bool sec = s.ShowSeconds;
        var sys = System.Globalization.CultureInfo.CurrentCulture;
        return s.ClockTimeFormat switch
        {
            ClockTimeFormat.H24 => now.ToString(sec ? "H:mm:ss" : "H:mm", L.Culture),
            ClockTimeFormat.H24Pad => now.ToString(sec ? "HH:mm:ss" : "HH:mm", L.Culture),
            ClockTimeFormat.H12 => now.ToString(sec ? "h:mm:ss tt" : "h:mm tt", L.Culture),
            ClockTimeFormat.Hidden when TimeHidden(s) => "",
            _ => now.ToString(sec ? "T" : "t", sys), // 標準の書式（t = 短い時刻 / T = 長い時刻）。地域設定の形になる
        };
    }

    /// <summary>設定の書式で日付を文字にする。「システム」は Windows の地域設定（短い日付）。</summary>
    public static string Date(Settings s, DateTime now) => s.ClockDateFormat switch
    {
        ClockDateFormat.YearMonthDay => now.ToString("yyyy/MM/dd", L.Culture),
        ClockDateFormat.MonthDay => now.ToString("M/d", L.Culture),
        ClockDateFormat.WithWeekday => L.IsEnglish ? now.ToString("ddd, MMM d", L.Culture) : now.ToString("M月d日 (ddd)", L.Culture),
        ClockDateFormat.Hidden => "",
        _ => now.ToString("d", System.Globalization.CultureInfo.CurrentCulture),
    };
}

/// <summary>
/// 時計のウィジェット（時刻と日付）。オンにすると、バー標準の時計の代わりになる。
/// 書式・揃え・表示スタイルは、右クリックのメニューで選ぶ。クリックで通知センター。
/// </summary>
public sealed class ClockBarWidget : BarWidget
{
    public override string Id => "clock";
    private TextBlock? _time, _date, _weekday, _dayNum;
    private ColumnDefinition[]? _progressCols;
    private bool _stacked, _dateFits = true;
    private string _sig = "";

    protected override FrameworkElement BuildContent()
    {
        var s = Settings.Current ?? new Settings();
        MeasureVertical();
        var align = Vertical ? HorizontalAlignment.Center : s.ClockAlign switch { ClockAlign.Left => HorizontalAlignment.Left, ClockAlign.Center => HorizontalAlignment.Center, _ => HorizontalAlignment.Right };
        var textAlign = align switch { HorizontalAlignment.Left => TextAlignment.Left, HorizontalAlignment.Center => TextAlignment.Center, _ => TextAlignment.Right };

        // 文字色は、バーの色（テーマ・アクセント・指定色・壁紙の色）に合わせて、白か黒の見やすいほうにする
        var ink = InkOnBar(s);
        _time = UiKit.Label("", Vertical ? 12 : 15, ink, FontWeights.SemiBold);
        _date = UiKit.Label("", Vertical ? 11 : 12, ink);
        foreach (var tb in new[] { _time, _date })
        {
            tb.HorizontalAlignment = align;
            tb.TextAlignment = textAlign;
            tb.TextTrimming = TextTrimming.None;
        }
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _weekday = null; _dayNum = null; _progressCols = null;
        var style = Vertical ? ClockStyle.TwoLines : s.ClockStyle;
        FrameworkElement content = stack;
        switch (style)
        {
            case ClockStyle.OneLine:
            {
                // 時刻と日付を 1 行に（低いタスクバー向き）
                _date.FontSize = 12;
                _date.Opacity = 0.75;
                _date.Margin = new Thickness(8, 0, 0, 0);
                _date.VerticalAlignment = _time.VerticalAlignment = VerticalAlignment.Center;
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                row.Children.Add(_time);
                row.Children.Add(_date);
                content = row;
                break;
            }
            case ClockStyle.DateTile:
            {
                // 曜日（アクセント色の帯）と日にちのタイル + 時刻
                _weekday = UiKit.Label("", 9, ImeLook.OnAccent(T), FontWeights.SemiBold);
                _weekday.TextTrimming = TextTrimming.None; _weekday.HorizontalAlignment = HorizontalAlignment.Center;
                _dayNum = UiKit.Label("", 14, T.Text, FontWeights.SemiBold);
                _dayNum.TextTrimming = TextTrimming.None; _dayNum.HorizontalAlignment = HorizontalAlignment.Center;
                var tile = new Border { Width = 30, Height = 32, CornerRadius = T.Corner(6), ClipToBounds = true, Background = T.Surface, VerticalAlignment = VerticalAlignment.Center };
                var g = new Grid();
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                g.RowDefinitions.Add(new RowDefinition());
                var top = new Border { Background = T.AccentBrush, Child = _weekday };
                Grid.SetRow(_dayNum, 1);
                _dayNum.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(top);
                g.Children.Add(_dayNum);
                tile.Child = g;
                _time.Margin = new Thickness(8, 0, 0, 0);
                _time.VerticalAlignment = VerticalAlignment.Center;
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                row.Children.Add(tile);
                row.Children.Add(_time);
                content = row;
                _date.Visibility = Visibility.Collapsed;
                break;
            }
            case ClockStyle.Progress:
            {
                // 時刻 + 日付の下に、1 日の進み具合の細い線
                _date.FontSize = 12;
                _date.Opacity = 0.75;
                _date.Margin = new Thickness(8, 0, 0, 0);
                _date.VerticalAlignment = _time.VerticalAlignment = VerticalAlignment.Center;
                var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                line.Children.Add(_time);
                line.Children.Add(_date);
                var bar = new Grid { Height = 4, Margin = new Thickness(0, 3, 0, 0) };
                _progressCols = new[] { new ColumnDefinition(), new ColumnDefinition() };
                foreach (var c in _progressCols) bar.ColumnDefinitions.Add(c);
                var fill = new Border { Background = T.AccentBrush, CornerRadius = new CornerRadius(2) };
                var rest = new Border { Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), CornerRadius = new CornerRadius(2) };
                Grid.SetColumn(rest, 1);
                bar.Children.Add(fill);
                bar.Children.Add(rest);
                var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                col.Children.Add(line);
                col.Children.Add(bar);
                content = col;
                break;
            }
            default:
                stack.Children.Add(_time);
                stack.Children.Add(_date);
                break;
        }
        if (!Vertical && ClockFormat.TimeHidden(s)) _time.Visibility = Visibility.Collapsed;
        if (style != ClockStyle.DateTile && (_stacked || (Vertical && !_dateFits) || (!Vertical && s.ClockDateFormat == ClockDateFormat.Hidden))) _date.Visibility = Visibility.Collapsed;

        Tick(0);
        if (Vertical) return stack;
        // 音楽・天気ウィジェットと同じ、薄い地と縁のある角丸のカード
        stack = new StackPanel { Children = { content }, VerticalAlignment = VerticalAlignment.Center };
        stack.Margin = new Thickness(style == ClockStyle.DateTile ? 6 : 12, 0, 12, 0);
        PadOverride = new Thickness(0);
        return new Border { Background = T.Hover, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6), Child = stack };
    }

    /// <summary>バーの地の色に対して、コントラストが高いほうの文字色（白 / 黒）。</summary>
    private Brush InkOnBar(Settings s)
    {
        var bg = s.TaskbarTint switch
        {
            BarTintMode.Accent => T.Accent,
            BarTintMode.Custom => Theme.ParseColor(s.TaskbarCustomColor) ?? T.SolidColor,
            BarTintMode.Wallpaper => MicaLike.WallpaperTint(T.SolidColor) ?? T.SolidColor,
            _ => T.SolidColor,
        };
        // 相対輝度（WCAG）。白との比と黒との比が等しくなる境目は約 0.179
        static double Lin(byte c) { double v = c / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        double lum = 0.2126 * Lin(bg.R) + 0.7152 * Lin(bg.G) + 0.0722 * Lin(bg.B);
        return lum > 0.179 ? Brushes.Black : Brushes.White;
    }

    /// <summary>縦のバーが細くて「22:31」が収まらないとき、時刻を「時」「分」の 2 段にする（実際のフォントで幅を測る）。</summary>
    private void MeasureVertical()
    {
        _stacked = false;
        _dateFits = true;
        if (!Vertical) return;
        try
        {
            var typeface = new Typeface(Theme.Current.UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            double Width(string s, double size) => new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
            double room = TileH - 4;
            _stacked = Width("00:00", 12) > room;
            _dateFits = Width("00/00", 11) <= room;
        }
        catch { _stacked = TileH < 36; _dateFits = TileH >= 38; }
    }

    public override void Tick(int count)
    {
        if (_time == null || _date == null) return;
        var s = Settings.Current ?? new Settings();
        var now = DateTime.Now;
        string time = _stacked ? now.Hour + "\n" + now.ToString("mm") // 1 文字だけの書式（"H"）は標準の書式と見なされて例外になるので使わない
            : Vertical ? now.ToString("H:mm") : ClockFormat.Time(s, now);
        string date = Vertical ? now.ToString("M/d", L.Culture) : ClockFormat.Date(s, now);
        string sig = time + "|" + date;
        // 1 日の進み具合（0:00 → 24:00）。時刻が変わるたびに合わせる
        if (_progressCols != null && sig != _sig)
        {
            double p = Math.Clamp(now.TimeOfDay.TotalMinutes / 1440.0, 0.001, 0.999);
            _progressCols[0].Width = new GridLength(p, GridUnitType.Star);
            _progressCols[1].Width = new GridLength(1 - p, GridUnitType.Star);
        }
        if (_weekday != null) _weekday.Text = now.ToString(L.IsEnglish ? "ddd" : "ddd", L.Culture);
        if (_dayNum != null) _dayNum.Text = now.Day.ToString();
        if (sig == _sig) return;
        _sig = sig;
        _time.Text = time;
        _date.Text = date;
        if (Card != null) Card.ToolTip = now.ToString(L.IsEnglish ? "dddd, MMMM d, yyyy" : "yyyy年M月d日 dddd", L.Culture) + "\n" + L.T("通知とカレンダー (Win+N)");
    }

    private static ClickOpens Opens => (Settings.Current ?? new Settings()).ClockWidgetOpens;

    public override bool OnClick() => ClickTarget.OpenWindowsIfChosen(Opens, Outer);

    public override bool OpensSystemPanel => Opens == ClickOpens.Windows;

    public override FrameworkElement? BuildDetailsView() => Opens == ClickOpens.App ? new CalendarDetailsView() : null;

    public override System.Collections.Generic.IEnumerable<MenuItemSpec> ExtraMenu()
    {
        var s = Settings.Current ?? new Settings();
        var items = ClickTarget.Menu(Opens, v => { if (Settings.Current is { } c) { c.ClockWidgetOpens = v; c.Save(); } }, Outer).ToList();
        items.RemoveAt(items.Count - 1); // 末尾の区切り線は、表示の設定のあとに入れ直す
        // 縦のバーでは、時刻と日付の形は幅に合わせて自動で決まるので、設定は出さない
        if (!Vertical)
        {
            // 見た目によって効かない設定は、選べないようにする（日付タイルは自前の日付を出す。揃えは 2 行のときだけ効く）
            bool dateOk = s.ClockStyle != ClockStyle.DateTile, alignOk = s.ClockStyle == ClockStyle.TwoLines;
            items.Add(WidgetSettingsMenu.Choice("", L.T("表示スタイル"), s.ClockStyle, new[]
            {
                (ClockStyle.TwoLines, L.T("2 行（時刻 + 日付）")), (ClockStyle.OneLine, L.T("1 行（低いタスクバー向き）")),
                (ClockStyle.DateTile, L.T("日付タイル + 時刻")), (ClockStyle.Progress, L.T("時刻 + 日付 + 1 日の進み具合")),
            }, (c, v) => c.ClockStyle = v));
            items.Add(WidgetSettingsMenu.Choice("", L.T("時刻の形式"), s.ClockTimeFormat, new[]
            {
                (ClockTimeFormat.System, L.T("システムに合わせる")), (ClockTimeFormat.H24, L.T("24 時間（9:05）")), (ClockTimeFormat.H24Pad, L.T("24 時間（09:05）")),
                (ClockTimeFormat.H12, L.T("12 時間（午前/午後）")), (ClockTimeFormat.Hidden, L.T("表示しない")),
            }, (c, v) => c.ClockTimeFormat = v));
            items.Add(WidgetSettingsMenu.Choice("", L.T("日付の形式"), s.ClockDateFormat, new[]
            {
                (ClockDateFormat.System, L.T("システムに合わせる")), (ClockDateFormat.YearMonthDay, "2026/11/16"), (ClockDateFormat.MonthDay, "11/16"),
                (ClockDateFormat.WithWeekday, L.T("11月16日 (月)")), (ClockDateFormat.Hidden, L.T("表示しない")),
            }, (c, v) => c.ClockDateFormat = v, dateOk));
            items.Add(WidgetSettingsMenu.Choice("", L.T("文字の揃え"), s.ClockAlign, new[]
            {
                (ClockAlign.Left, L.T("左揃え")), (ClockAlign.Center, L.T("中央揃え")), (ClockAlign.Right, L.T("右揃え")),
            }, (c, v) => c.ClockAlign = v, alignOk));
            items.Add(WidgetSettingsMenu.Toggle(L.T("時計に秒を表示"), s.ShowSeconds, (c, v) => c.ShowSeconds = v));
        }
        items.Add(MenuItemSpec.Separator);
        return items;
    }
}

/// <summary>時計・カレンダーのウィジェットに共通の、クリックで開くもの（Windows の通知センター / アプリのカレンダー）。</summary>
internal static class ClickTarget
{
    /// <summary>Windows の通知センターを開く。タスクバーを先に前面にしてから Win+N を送る（管理者として動くアプリが前面だと、キー操作が届かないため）。</summary>
    public static void OpenWindows(FrameworkElement? from)
    {
        if (from != null) TaskbarController.TakeForeground(from);
        SystemActions.NotificationCenter();
    }

    /// <summary>「Windows」を選んでいるなら通知センターを開いて true（アプリのカレンダーのときは false で、詳細の表示に任せる）。</summary>
    public static bool OpenWindowsIfChosen(ClickOpens opens, FrameworkElement? from)
    {
        if (opens != ClickOpens.Windows) return false;
        OpenWindows(from);
        return true;
    }

    /// <summary>右クリックのメニュー: 通知センターを開く / クリックで開くものの選択。</summary>
    public static System.Collections.Generic.IEnumerable<MenuItemSpec> Menu(ClickOpens current, Action<ClickOpens> set, FrameworkElement? from) => new[]
    {
        new MenuItemSpec("", L.T("通知センターを開く"), () => OpenWindows(from)),
        new MenuItemSpec("", L.T("クリックで開く"), () => { })
        {
            Children = () => new[]
            {
                new MenuItemSpec(current == ClickOpens.Windows ? "" : "", L.T("Windows の通知センター"), () => set(ClickOpens.Windows)),
                new MenuItemSpec(current == ClickOpens.App ? "" : "", L.T("アプリのカレンダー"), () => set(ClickOpens.App)),
            },
        },
        MenuItemSpec.Separator,
    };
}
