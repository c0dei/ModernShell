using System;
using System.Windows.Media.Animation;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>
/// スタートメニュー右側のウィジェット列（時計とカレンダー / 天気 / システム / フォルダー / 最近使ったファイル / メモ）。
/// 表示中だけタイマーを動かす。
/// </summary>
public sealed class WidgetPanel : ScrollViewer
{
    /// <summary>文字の大きさ（スタートメニューと同じ設定に従う）。</summary>
    private static double Fs(double size) => StartMenuWindow.Fs(size);

    private readonly Settings _settings;
    private readonly SystemStats _stats = new();
    private readonly DispatcherTimer _tick;
    private TextBlock? _clockTime, _clockDate;
    private (ProgressBar bar, TextBlock text)? _cpu, _mem, _disk;
    private int _ticks;

    public event Action? LaunchedSomething;

    public static bool AnyEnabled(Settings s) =>
        s.ShowWidgets && (s.WidgetClock || s.WidgetWeather || s.WidgetSystem || s.WidgetFolders || s.WidgetRecent || s.WidgetNotes);

    public WidgetPanel(Settings settings)
    {
        _settings = settings;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _tick.Stop();
    }

    /// <summary>スタートメニューを開くたびに呼ぶ。</summary>
    public void Open()
    {
        Build();
        _stats.CpuPercent(); // 初回の基準値
        _ticks = 0;
        Tick();
        _tick.Start();
    }

    public void Close() => _tick.Stop();

    private void Build()
    {
        var t = Theme.Current;
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        _stack = stack;
        _cards.Clear();
        _cpu = _mem = _disk = null;
        _clockTime = _clockDate = null;
        foreach (var id in _settings.OrderedWidgets().Where(_settings.IsWidgetEnabled))
        {
            Border card = id switch
            {
                "clock" => ClockCard(t), "weather" => WeatherCard(t), "system" => SystemCard(t),
                "folders" => FoldersCard(t), "recent" => RecentCard(t), _ => NotesCard(t),
            };
            EnableReorder(card, id);
            stack.Children.Add(card);
        }
        Content = stack;
    }

    private void Tick()
    {
        var now = DateTime.Now;
        if (_clockTime != null) _clockTime.Text = now.ToString("H:mm");
        if (_clockDate != null) _clockDate.Text = (L.IsEnglish ? now.ToString("ddd, MMM d", L.Culture) : now.ToString("M月d日 (ddd)", L.Culture));

        if (_cpu != null && _ticks++ % 2 == 0)
        {
            double cpu = _stats.CpuPercent();
            Set(_cpu.Value, cpu, $"{cpu:0}%");
            var (mp, used, total) = _stats.Memory();
            Set(_mem!.Value, mp, $"{used:0.0} / {total:0} GB");
            var (dp, free) = _stats.SystemDrive();
            Set(_disk!.Value, dp, L.F("空き {0:0} GB", free));
        }

        static void Set((ProgressBar bar, TextBlock text) x, double v, string s) { x.bar.Value = v; x.text.Text = s; }
    }

    // ------------------------------------------------------------------

    // ---- ドラッグで並べ替え ----
    // 見出しをつかむとカードがカーソルに付いてきて、他のカードの半分を越えた時点で
    // そのカードがぬるっと移動して場所を空ける（OS のドラッグ＆ドロップは使わず自前で動かす）。
    private StackPanel? _stack;
    private readonly List<(Border card, string id)> _cards = new();
    private Border? _dragCard;
    private int _dragFrom, _dragTo;
    private double _dragStartY;
    private double[] _tops = Array.Empty<double>(), _heights = Array.Empty<double>();
    private const double Gap = 8; // カード間の余白（Card の Margin.Bottom と同じ）

    private void EnableReorder(Border card, string id)
    {
        _cards.Add((card, id));
        card.RenderTransform = new TranslateTransform();
        if (card.Child is not StackPanel { Children.Count: > 0 } s || s.Children[0] is not FrameworkElement header) return;
        header.Cursor = Cursors.SizeAll;
        header.ToolTip = L.T("ドラッグで並べ替え");
        header.MouseLeftButtonDown += (_, e) => { BeginDrag(card, e); header.CaptureMouse(); e.Handled = true; };
        header.MouseMove += (_, e) => { if (_dragCard == card) DragMove(e); };
        header.MouseLeftButtonUp += (_, _) => { header.ReleaseMouseCapture(); EndDrag(); };
        header.LostMouseCapture += (_, _) => EndDrag();
    }

    private void BeginDrag(Border card, MouseEventArgs e)
    {
        if (_stack == null) return;
        _dragCard = card;
        _dragFrom = _dragTo = _cards.FindIndex(c => c.card == card);
        _dragStartY = e.GetPosition(_stack).Y;
        _tops = _cards.Select(c => c.card.TranslatePoint(new Point(0, 0), _stack).Y).ToArray();
        _heights = _cards.Select(c => c.card.ActualHeight).ToArray();
        // つかんだカードを手前に出して少し浮かせる
        Panel.SetZIndex(card, 10);
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.35 };
        card.Opacity = 0.96;
    }

    private void DragMove(MouseEventArgs e)
    {
        if (_dragCard == null || _stack == null) return;

        // 端に近づいたら自動スクロール
        var inView = e.GetPosition(this).Y;
        if (inView < 30) ScrollToVerticalOffset(VerticalOffset - 8);
        else if (inView > ActualHeight - 30) ScrollToVerticalOffset(VerticalOffset + 8);

        double dy = e.GetPosition(_stack).Y - _dragStartY;
        ((TranslateTransform)_dragCard.RenderTransform).Y = dy;

        // つかんだカードの中心が、どのカードの半分を越えたかで差し込み先を決める
        double center = _tops[_dragFrom] + _heights[_dragFrom] / 2 + dy;
        int to = _dragFrom;
        for (int k = _dragFrom + 1; k < _cards.Count; k++)
            if (center > _tops[k] + _heights[k] / 2) to = k;
        for (int k = _dragFrom - 1; k >= 0; k--)
            if (center < _tops[k] + _heights[k] / 2) to = k;
        if (to == _dragTo) return;
        _dragTo = to;

        // 間にあるカードを、つかんだカードの高さ分だけずらす
        double shift = _heights[_dragFrom] + Gap;
        for (int k = 0; k < _cards.Count; k++)
        {
            if (k == _dragFrom) continue;
            double target = _dragFrom < to && k > _dragFrom && k <= to ? -shift
                          : to < _dragFrom && k >= to && k < _dragFrom ? shift
                          : 0;
            Slide(_cards[k].card, target);
        }
    }

    private static void Slide(Border card, double y)
    {
        var tt = (TranslateTransform)card.RenderTransform;
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(y, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void EndDrag()
    {
        var card = _dragCard;
        if (card == null) return;
        _dragCard = null;
        int from = _dragFrom, to = _dragTo;

        // つかんだカードを新しい位置へ吸い付かせてから、順番を保存して作り直す
        double finalY = to == from ? 0
            : to > from ? _tops[to] + _heights[to] - (_tops[from] + _heights[from])
            : _tops[to] - _tops[from];
        var tt = (TranslateTransform)card.RenderTransform;
        var snap = new DoubleAnimation(tt.Y, finalY, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        snap.Completed += (_, _) =>
        {
            card.Effect = null;
            card.Opacity = 1;
            Panel.SetZIndex(card, 0);
            if (to == from) { tt.BeginAnimation(TranslateTransform.YProperty, null); tt.Y = 0; return; }
            ApplyOrder(from, to);
        };
        tt.BeginAnimation(TranslateTransform.YProperty, snap);
    }

    /// <summary>表示中のウィジェットの並びを変え、非表示のウィジェットの位置は保ったまま全体の順序に反映する。</summary>
    private void ApplyOrder(int from, int to)
    {
        var visible = _cards.Select(c => c.id).ToList();
        var moved = visible[from];
        visible.RemoveAt(from);
        visible.Insert(to, moved);
        var full = _settings.OrderedWidgets();
        int v = 0;
        for (int i = 0; i < full.Count; i++)
            if (visible.Contains(full[i])) full[i] = visible[v++];
        _settings.WidgetOrder = full;
        _settings.SaveQuiet(); // スタートメニュー全体は作り直さず、ウィジェット列だけ更新
        double scroll = VerticalOffset;
        Open();
        ScrollToVerticalOffset(scroll);
    }

    private static Border Card(Theme t, string title, UIElement body)
    {
        var stack = new StackPanel();
        var h = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent };
        var grip = UiKit.Glyph("\uE76F", 12, t.TextSecondary);
        DockPanel.SetDock(grip, Dock.Right);
        h.Children.Add(grip);
        h.Children.Add(UiKit.Label(title, Fs(12), t.TextSecondary, FontWeights.SemiBold));
        stack.Children.Add(h);
        stack.Children.Add(body);
        return new Border
        {
            Background = t.Surface, BorderBrush = t.Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(t.Radius), Padding = new Thickness(14, 12, 14, 14),
            Margin = new Thickness(0, 0, 0, 8), Child = stack,
        };
    }

    private Border ClockCard(Theme t)
    {
        _clockTime = UiKit.Label("", Fs(34), t.Text, FontWeights.SemiBold);
        _clockDate = UiKit.Label("", Fs(13), t.TextSecondary);
        var body = new StackPanel();
        body.Children.Add(_clockTime);
        body.Children.Add(_clockDate);
        body.Children.Add(Calendar(t));
        return Card(t, L.T("時計とカレンダー"), body);
    }

    /// <summary>今月のカレンダー（今日を強調）。</summary>
    private static UIElement Calendar(Theme t)
    {
        var today = DateTime.Today;
        var first = new DateTime(today.Year, today.Month, 1);
        var grid = new UniformGrid { Columns = 7, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var d in new[] { L.T("日"), L.T("月"), L.T("火"), L.T("水"), L.T("木"), L.T("金"), L.T("土") })
        {
            var l = UiKit.Label(d, Fs(11), t.TextSecondary);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Children.Add(l);
        }
        for (int i = 0; i < (int)first.DayOfWeek; i++) grid.Children.Add(new Border());
        for (int day = 1; day <= DateTime.DaysInMonth(today.Year, today.Month); day++)
        {
            bool isToday = day == today.Day;
            var l = UiKit.Label(day.ToString(), Fs(12), isToday ? t.OnAccent : t.Text, isToday ? FontWeights.SemiBold : FontWeights.Normal);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Children.Add(new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(1),
                Background = isToday ? t.AccentBrush : Brushes.Transparent, Child = l,
            });
        }
        return grid;
    }

    private Border WeatherCard(Theme t)
    {
        var temp = UiKit.Label("—", Fs(30), t.Text, FontWeights.SemiBold);
        var desc = UiKit.Label(L.T("読み込み中…"), Fs(13), t.TextSecondary);
        var range = UiKit.Label("", Fs(12), t.TextSecondary);
        var glyph = UiKit.Glyph("", 34, t.AccentBrush);
        glyph.Margin = new Thickness(0, 0, 16, 0);
        var text = new StackPanel();
        text.Children.Add(temp);
        text.Children.Add(desc);
        text.Children.Add(range);
        var row = UiKit.Row(glyph, text);
        var card = Card(t, L.T("天気 · ") + _settings.WeatherCity, row);

        Task.Run(() => Weather.GetAsync(_settings.WeatherCity)).ContinueWith(r =>
        {
            var w = r.Result;
            Dispatcher.BeginInvoke(() =>
            {
                if (w == null) { desc.Text = L.T("取得できませんでした（設定で地点を確認）"); return; }
                temp.Text = $"{w.Temperature:0}°";
                desc.Text = $"{w.Place}  {w.Description}";
                range.Text = L.F("最高 {0:0}° / 最低 {1:0}°", w.Max, w.Min);
                glyph.Text = w.IsClear ? "" : "";
            });
        }, TaskScheduler.Default);
        return card;
    }

    private Border SystemCard(Theme t)
    {
        var body = new StackPanel();
        (ProgressBar, TextBlock) Row(string label)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.Children.Add(UiKit.Label(label, Fs(12)));
            var bar = new ProgressBar
            {
                Height = 4, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0),
                Foreground = t.AccentBrush, Background = t.Stroke,
            };
            Grid.SetColumn(bar, 1);
            g.Children.Add(bar);
            var val = UiKit.Label("", Fs(12), t.TextSecondary);
            val.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(val, 2);
            g.Children.Add(val);
            body.Children.Add(g);
            return (bar, val);
        }
        _cpu = Row("CPU");
        _mem = Row(L.T("メモリ"));
        _disk = Row(L.T("ディスク"));
        return Card(t, L.T("システム"), body);
    }

    private Border FoldersCard(Theme t)
    {
        var grid = new UniformGrid { Columns = 3 };
        foreach (var (name, glyph, path) in UserFolders.Common())
        {
            var stack = new StackPanel();
            stack.Children.Add(UiKit.Glyph(glyph, 20, t.AccentBrush));
            var l = UiKit.Label(name, Fs(11));
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(l);
            var tile = new Tile { Height = 64, Margin = new Thickness(1), Child = stack, ToolTip = path };
            stack.VerticalAlignment = VerticalAlignment.Center;
            tile.Click += _ => Open(path);
            grid.Children.Add(tile);
        }
        return Card(t, L.T("フォルダー"), grid);
    }

    private Border RecentCard(Theme t)
    {
        var list = new StackPanel();
        var items = UserFolders.Recent(6);
        if (items.Count == 0) list.Children.Add(UiKit.Label(L.T("最近使ったファイルはありません"), Fs(12), t.TextSecondary));
        foreach (var (name, lnk) in items)
        {
            var img = UiKit.Icon(null, 20);
            IconThread.Get(lnk, 40).ContinueWith(r =>
            {
                if (r.Result != null) Dispatcher.BeginInvoke(() => img.Source = r.Result);
            }, TaskScheduler.Default);
            var label = UiKit.Label(name, Fs(12));
            label.Margin = new Thickness(10, 0, 0, 0);
            label.MaxWidth = 230;
            var row = UiKit.Row(img, label);
            row.VerticalAlignment = VerticalAlignment.Center;
            var tile = new Tile { Height = 34, Padding = new Thickness(6, 0, 6, 0), Child = row, ToolTip = name };
            tile.Click += _ => Open(lnk);
            list.Children.Add(tile);
        }
        return Card(t, L.T("最近使ったファイル"), list);
    }

    private Border NotesCard(Theme t)
    {
        var box = new TextBox
        {
            Text = Notes.Load(), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent, Foreground = t.Text,
            CaretBrush = t.Text, BorderThickness = new Thickness(0), FontSize = Fs(13),
        };
        var save = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (s, _) => { ((DispatcherTimer)s!).Stop(); Notes.Save(box.Text); }, Dispatcher);
        save.Stop();
        box.TextChanged += (_, _) => { save.Stop(); save.Start(); };
        box.LostFocus += (_, _) => Notes.Save(box.Text);
        return Card(t, L.T("メモ"), box);
    }

    private void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            LaunchedSomething?.Invoke();
        }
        catch (Exception ex) { Log.Error("widget open", ex); }
    }
}
