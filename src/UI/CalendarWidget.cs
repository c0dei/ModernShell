using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ModernShell.Core;

namespace ModernShell.UI;

internal static class CalendarFormat
{
    public static string Weekday(DateTime d, bool full = false) =>
        full ? d.ToString("dddd", L.Culture) : L.Culture.DateTimeFormat.AbbreviatedDayNames[(int)d.DayOfWeek];

    /// <summary>11月21日(土) / Nov 21 (Sat)</summary>
    public static string MonthDayWeekday(DateTime d) =>
        d.ToString(L.IsEnglish ? "MMM d" : "M月d日", L.Culture) + (L.IsEnglish ? " (" : "(") + Weekday(d) + ")";

    public static string? HolidayName(DateTime d) => L.IsEnglish ? (JpHoliday.Name(d) != null ? "Holiday" : null) : JpHoliday.Name(d);
}

/// <summary>バーのカレンダーのカード: 今日の日付と、次の予定。クリックで月のカレンダーと今日の予定。</summary>
public sealed class CalendarBarWidget : BarWidget
{
    public override string Id => "calendar";
    private TextBlock? _weekday, _day, _title, _sub;
    private string _sig = "";

    protected override FrameworkElement BuildContent()
    {
        _weekday = UiKit.Label("", 8.5, T.AccentBrush, FontWeights.SemiBold);
        _weekday.TextTrimming = TextTrimming.None;
        _weekday.HorizontalAlignment = HorizontalAlignment.Center;
        _day = UiKit.Label("", 14, T.Text, FontWeights.SemiBold);
        _day.TextTrimming = TextTrimming.None;
        _day.HorizontalAlignment = HorizontalAlignment.Center;
        var page = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        page.Children.Add(_weekday);
        page.Children.Add(_day);
        _weekday.Margin = new Thickness(0, 0, 0, -2);
        var icon = new Border
        {
            Width = Vertical ? 30 : 30, Height = 32, CornerRadius = T.Corner(6), Background = T.Surface,
            BorderBrush = T.Stroke, BorderThickness = new Thickness(1), Child = page,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };

        _title = UiKit.Label("", 13, T.Text, FontWeights.SemiBold);
        _sub = UiKit.Label("", 11, T.TextSecondary);
        _title.MaxWidth = _sub.MaxWidth = 150;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        text.Children.Add(_title);
        text.Children.Add(_sub);

        Tick(0);
        if (Vertical) return icon;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 10, 0) };
        row.Children.Add(icon);
        row.Children.Add(text);
        PadOverride = new Thickness(0);
        return new Border { Background = T.Hover, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6), Child = row };
    }

    public override void Tick(int count)
    {
        if (_weekday == null || _day == null || _title == null || _sub == null) return;
        var now = DateTime.Now;
        var today = CalendarStore.On(now.Date);
        // 次の予定: これから始まる時刻付きの予定 → 終日の予定 → なし
        CalendarEvent? next = today.Where(e => CalendarStore.TimeOf(e) is { } t && t >= now.TimeOfDay).FirstOrDefault()
                              ?? today.FirstOrDefault(e => e.Time == "");
        string? holiday = CalendarFormat.HolidayName(now);
        string line2 = next != null ? (next.Time == "" ? "" : next.Time + "  ") + next.Title
            : holiday ?? (today.Count > 0 ? L.F("本日 {0} 件（終了）", today.Count) : L.T("予定なし"));
        string sig = now.ToString("yyyyMMdd") + "|" + line2 + "|" + CalendarStore.Version + "|" + JpHoliday.Version;
        if (sig == _sig) return;
        _sig = sig;

        _weekday.Text = CalendarFormat.Weekday(now);
        _day.Text = now.Day.ToString();
        _title.Text = CalendarFormat.MonthDayWeekday(now);
        _sub.Text = line2;
        if (Card != null) Card.ToolTip = _title.Text + "\n" + line2;
    }

    public override FrameworkElement? BuildDetailsView() => new CalendarDetailsView();
}

// ---------------------------------------------------------------------------

/// <summary>カレンダーのポップアップ: 時刻・月のカレンダー・選んだ日の予定（追加・削除できる）。</summary>
internal sealed class CalendarDetailsView : Border
{
    private readonly Theme _t = Theme.Current;
    private readonly TextBlock _time, _date, _month, _eventsTitle;
    private readonly UniformGrid _grid = new() { Columns = 7 };
    private readonly StackPanel _events = new();
    private readonly TextBox _timeBox, _titleBox;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _month1 = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selected = DateTime.Today;
    private DateTime _today = DateTime.Today;

    private static readonly Brush Sunday = new SolidColorBrush(Color.FromRgb(0xFF, 0x8F, 0x7E));
    private static readonly Brush Saturday = new SolidColorBrush(Color.FromRgb(0x8F, 0xB4, 0xFF));

    public CalendarDetailsView()
    {
        Width = 360;
        Padding = new Thickness(18, 14, 18, 14);
        System.Windows.Documents.TextElement.SetForeground(this, _t.Text);
        var body = new StackPanel();
        Child = body;

        // 時刻と日付
        _time = UiKit.Label("", 40, _t.Text, FontWeights.SemiBold);
        _time.TextTrimming = TextTrimming.None;
        _date = UiKit.Label("", 13, _t.TextSecondary);
        body.Children.Add(_time);
        body.Children.Add(_date);

        // 月の見出しと、今日・前へ・次へ
        var head = new Grid { Margin = new Thickness(0, 14, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _month = UiKit.Label("", 17, _t.Text, FontWeights.SemiBold);
        _month.TextTrimming = TextTrimming.None;
        head.Children.Add(_month);
        var todayBtn = new Tile
        {
            Padding = new Thickness(14, 6, 14, 6), CornerRadius = _t.Corner(8), BorderBrush = _t.Stroke, Margin = new Thickness(0, 0, 8, 0),
            Child = UiKit.Label(L.T("今日"), 13, _t.Text),
        };
        todayBtn.Click += _ => { _selected = _today = DateTime.Today; _month1 = new DateTime(_today.Year, _today.Month, 1); Rebuild(); };
        Grid.SetColumn(todayBtn, 1);
        head.Children.Add(todayBtn);
        var prev = NavButton("", -1, 2);
        var next = NavButton("", 1, 3);
        head.Children.Add(prev);
        head.Children.Add(next);
        body.Children.Add(head);

        // 曜日の見出しと日付
        var names = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
        for (int i = 0; i < 7; i++)
        {
            var l = UiKit.Label(CalendarFormat.Weekday(new DateTime(2023, 1, 1).AddDays(i)), 12, WeekdayBrush(i, true));
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.Margin = new Thickness(0, 4, 0, 4);
            names.Children.Add(l);
        }
        body.Children.Add(names);
        body.Children.Add(_grid);

        // 選んだ日の予定
        body.Children.Add(new Border { Height = 1, Background = _t.Stroke, Margin = new Thickness(0, 8, 0, 10) });
        _eventsTitle = UiKit.Label("", 13, _t.TextSecondary, FontWeights.SemiBold);
        _eventsTitle.Margin = new Thickness(2, 0, 0, 8);
        body.Children.Add(_eventsTitle);
        body.Children.Add(_events);

        // 予定の追加: 時刻（省略すると終日）・内容・追加ボタン
        _timeBox = MakeBox(64, L.T("時刻"), out var timeWrap);
        _titleBox = MakeBox(double.NaN, L.T("予定を追加"), out var titleWrap);
        var addBtn = new Tile { Width = 34, Height = 34, CornerRadius = _t.Corner(8), Background = _t.Hover, Child = UiKit.Glyph("", 14, _t.Text), ToolTip = L.T("追加") };
        addBtn.Click += _ => AddEvent();
        var add = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(timeWrap, 0);
        titleWrap.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(titleWrap, 1);
        Grid.SetColumn(addBtn, 2);
        add.Children.Add(timeWrap);
        add.Children.Add(titleWrap);
        add.Children.Add(addBtn);
        body.Children.Add(add);
        KeyEventHandler enter = (_, e) => { if (e.Key == Key.Enter) { AddEvent(); e.Handled = true; } };
        _titleBox.KeyDown += enter;
        _timeBox.KeyDown += enter;

        _timer.Tick += (_, _) => TickClock();
        Loaded += (_, _) => { TickClock(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        TickClock();
        Rebuild();
    }

    private Tile NavButton(string glyph, int delta, int col)
    {
        var b = new Tile { Width = 36, Height = 34, CornerRadius = _t.Corner(8), Child = UiKit.Glyph(glyph, 13, _t.Text) };
        b.Click += _ => { _month1 = _month1.AddMonths(delta); Rebuild(); };
        Grid.SetColumn(b, col);
        return b;
    }

    /// <summary>入力欄（地は薄く、何も入っていないときはヒントを出す）。frame は置くための枠。</summary>
    private TextBox MakeBox(double width, string hint, out FrameworkElement frame)
    {
        var box = new TextBox
        {
            Background = Brushes.Transparent, Foreground = _t.Text, CaretBrush = _t.Text, BorderThickness = new Thickness(0),
            FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0),
        };
        var hintText = UiKit.Label(hint, 13, _t.TextSecondary);
        hintText.IsHitTestVisible = false;
        hintText.Margin = new Thickness(1, 0, 0, 0);
        var inner = new Grid();
        inner.Children.Add(hintText);
        inner.Children.Add(box);
        box.TextChanged += (_, _) => hintText.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var border = new Border
        {
            Background = _t.Hover, BorderBrush = _t.Stroke, BorderThickness = new Thickness(1), CornerRadius = _t.Corner(8),
            Padding = new Thickness(10, 0, 10, 0), Height = 34, Child = inner,
        };
        if (!double.IsNaN(width)) border.Width = width;
        frame = border;
        return box;
    }

    private void AddEvent()
    {
        string title = _titleBox.Text.Trim();
        if (title.Length == 0) { _titleBox.Focus(); return; }
        string? time = CalendarStore.NormalizeTime(_timeBox.Text);
        if (time == null) { _timeBox.Focus(); _timeBox.SelectAll(); return; }
        CalendarStore.Add(_selected, time, title);
        _titleBox.Clear();
        _timeBox.Clear();
        Rebuild();
        _titleBox.Focus();
    }

    private Brush WeekdayBrush(int dow, bool header = false) => dow == 0 ? Sunday : dow == 6 ? Saturday : header ? _t.TextSecondary : _t.Text;

    private void TickClock()
    {
        var now = DateTime.Now;
        _time.Text = now.ToString("H:mm");
        _date.Text = now.ToString(L.IsEnglish ? "dddd, MMMM d, yyyy" : "yyyy年M月d日 dddd", L.Culture);
        // 日付をまたいだら、今日の位置を直す
        if (now.Date != _today)
        {
            if (_selected == _today) _selected = now.Date;
            _today = now.Date;
            Rebuild();
        }
    }

    private void Rebuild()
    {
        _month.Text = _month1.ToString(L.IsEnglish ? "MMMM yyyy" : "yyyy年M月", L.Culture);
        _grid.Children.Clear();
        var start = _month1.AddDays(-(int)_month1.DayOfWeek);
        for (int i = 0; i < 42; i++) _grid.Children.Add(DayCell(start.AddDays(i)));
        RebuildEvents();
    }

    private FrameworkElement DayCell(DateTime d)
    {
        bool inMonth = d.Month == _month1.Month;
        bool isToday = d.Date == _today;
        bool isSel = d.Date == _selected;
        string? holiday = CalendarFormat.HolidayName(d);
        int dow = (int)d.DayOfWeek;
        Brush fg = isToday ? _t.OnAccent : holiday != null && !L.IsEnglish || dow == 0 ? Sunday : WeekdayBrush(dow);
        if (holiday != null && !isToday) fg = Sunday;
        var label = UiKit.Label(d.Day.ToString(), 15, fg, isToday ? FontWeights.SemiBold : FontWeights.Normal);
        label.TextTrimming = TextTrimming.None;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var circle = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Child = label,
            Background = isToday ? _t.AccentBrush : Brushes.Transparent,
            BorderBrush = isSel && !isToday ? _t.AccentBrush : Brushes.Transparent, BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        };
        var cell = new Tile { Height = 44, Padding = new Thickness(0), Tag = d, CornerRadius = _t.Corner(8) };
        var g = new Grid { Opacity = inMonth ? 1 : 0.4 };
        g.Children.Add(circle);
        if (CalendarStore.Has(d))
            g.Children.Add(new Ellipse
            {
                Width = 5, Height = 5, Fill = isToday ? _t.AccentBrush : (holiday != null ? Sunday : dow == 6 ? Saturday : _t.AccentBrush),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1),
            });
        cell.Child = g;
        if (holiday != null) cell.ToolTip = holiday;
        cell.Click += _ =>
        {
            _selected = d.Date;
            if (d.Month != _month1.Month) _month1 = new DateTime(d.Year, d.Month, 1);
            Rebuild();
        };
        return cell;
    }

    private void RebuildEvents()
    {
        string label = _selected == _today ? L.T("今日") + "(" + CalendarFormat.Weekday(_selected) + ")" : CalendarFormat.MonthDayWeekday(_selected);
        string? holiday = CalendarFormat.HolidayName(_selected);
        _eventsTitle.Text = L.F("{0}の予定", label) + (holiday != null ? "  ·  " + holiday : "");
        _events.Children.Clear();
        var list = CalendarStore.On(_selected);
        if (list.Count == 0)
        {
            var none = UiKit.Label(L.T("予定はありません"), 13, _t.TextSecondary);
            none.Margin = new Thickness(2, 0, 0, 6);
            _events.Children.Add(none);
            return;
        }
        foreach (var e in list)
        {
            var id = e.Id;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = _t.AccentBrush, Margin = new Thickness(0, 2, 12, 2) });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(UiKit.Label(e.Title, 15, _t.Text, FontWeights.SemiBold));
            text.Children.Add(UiKit.Label(e.Time == "" ? L.T("終日") : e.Time, 12, _t.TextSecondary));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var del = new Tile { Width = 30, Height = 30, CornerRadius = _t.Corner(8), Child = UiKit.Glyph("", 11, _t.TextSecondary), ToolTip = L.T("削除"), VerticalAlignment = VerticalAlignment.Center };
            del.Click += _ => { CalendarStore.Remove(id); Rebuild(); };
            Grid.SetColumn(del, 2);
            row.Children.Add(del);
            _events.Children.Add(new Border
            {
                Background = _t.Surface, CornerRadius = _t.Corner(12), Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(0, 0, 0, 6), Child = row,
            });
        }
    }
}
