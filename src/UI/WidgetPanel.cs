using System;
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
        if (_clockDate != null) _clockDate.Text = now.ToString("M月d日 (ddd)", CultureInfo.GetCultureInfo("ja-JP"));

        if (_cpu != null && _ticks++ % 2 == 0)
        {
            double cpu = _stats.CpuPercent();
            Set(_cpu.Value, cpu, $"{cpu:0}%");
            var (mp, used, total) = _stats.Memory();
            Set(_mem!.Value, mp, $"{used:0.0} / {total:0} GB");
            var (dp, free) = _stats.SystemDrive();
            Set(_disk!.Value, dp, $"空き {free:0} GB");
        }

        static void Set((ProgressBar bar, TextBlock text) x, double v, string s) { x.bar.Value = v; x.text.Text = s; }
    }

    // ------------------------------------------------------------------

    // ---- ドラッグで並べ替え（見出しをつかんで上下に移動。差し込み位置に線を表示） ----
    private Point _dragStart;

    private void EnableReorder(Border card, string id)
    {
        var t = Theme.Current;
        card.AllowDrop = true;
        if (card.Child is StackPanel { Children.Count: > 0 } s && s.Children[0] is FrameworkElement header)
        {
            header.Cursor = Cursors.SizeAll;
            header.ToolTip = "ドラッグで並べ替え";
            header.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); e.Handled = true; header.CaptureMouse(); };
            header.PreviewMouseLeftButtonUp += (_, _) => header.ReleaseMouseCapture();
            header.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || !header.IsMouseCaptured) return;
                var d = e.GetPosition(this) - _dragStart;
                if (Math.Abs(d.Y) < 6 && Math.Abs(d.X) < 6) return;
                header.ReleaseMouseCapture();
                card.Opacity = 0.5;
                DragDrop.DoDragDrop(card, new DataObject("ModernShell.Widget", id), DragDropEffects.Move);
                card.Opacity = 1;
            };
        }

        bool Below(DragEventArgs e) => e.GetPosition(card).Y > card.ActualHeight / 2;
        void ShowLine(bool? below)
        {
            card.BorderBrush = below == null ? t.Stroke : t.AccentBrush;
            card.BorderThickness = below switch { true => new Thickness(1, 1, 1, 4), false => new Thickness(1, 4, 1, 1), _ => new Thickness(1) };
        }
        card.DragOver += (_, e) =>
        {
            bool ok = e.Data.GetData("ModernShell.Widget") is string d && d != id;
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            ShowLine(ok ? Below(e) : null);
            e.Handled = true;
        };
        card.DragLeave += (_, _) => ShowLine(null);
        card.Drop += (_, e) =>
        {
            ShowLine(null);
            if (e.Data.GetData("ModernShell.Widget") is not string dragged || dragged == id) return;
            int index = _settings.OrderedWidgets().IndexOf(id) + (Below(e) ? 1 : 0);
            _settings.MoveWidget(dragged, index);
            _settings.SaveQuiet(); // スタートメニュー全体は作り直さず、ウィジェット列だけ更新
            Open();
        };
    }

    private static Border Card(Theme t, string title, UIElement body)
    {
        var stack = new StackPanel();
        var h = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent };
        var grip = UiKit.Glyph("\uE76F", 12, t.TextSecondary);
        DockPanel.SetDock(grip, Dock.Right);
        h.Children.Add(grip);
        h.Children.Add(UiKit.Label(title, 12, t.TextSecondary, FontWeights.SemiBold));
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
        _clockTime = UiKit.Label("", 34, t.Text, FontWeights.SemiBold);
        _clockDate = UiKit.Label("", 13, t.TextSecondary);
        var body = new StackPanel();
        body.Children.Add(_clockTime);
        body.Children.Add(_clockDate);
        body.Children.Add(Calendar(t));
        return Card(t, "時計とカレンダー", body);
    }

    /// <summary>今月のカレンダー（今日を強調）。</summary>
    private static UIElement Calendar(Theme t)
    {
        var today = DateTime.Today;
        var first = new DateTime(today.Year, today.Month, 1);
        var grid = new UniformGrid { Columns = 7, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var d in new[] { "日", "月", "火", "水", "木", "金", "土" })
        {
            var l = UiKit.Label(d, 11, t.TextSecondary);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Children.Add(l);
        }
        for (int i = 0; i < (int)first.DayOfWeek; i++) grid.Children.Add(new Border());
        for (int day = 1; day <= DateTime.DaysInMonth(today.Year, today.Month); day++)
        {
            bool isToday = day == today.Day;
            var l = UiKit.Label(day.ToString(), 12, isToday ? t.OnAccent : t.Text, isToday ? FontWeights.SemiBold : FontWeights.Normal);
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
        var temp = UiKit.Label("—", 30, t.Text, FontWeights.SemiBold);
        var desc = UiKit.Label("読み込み中…", 13, t.TextSecondary);
        var range = UiKit.Label("", 12, t.TextSecondary);
        var glyph = UiKit.Glyph("", 34, t.AccentBrush);
        glyph.Margin = new Thickness(0, 0, 16, 0);
        var text = new StackPanel();
        text.Children.Add(temp);
        text.Children.Add(desc);
        text.Children.Add(range);
        var row = UiKit.Row(glyph, text);
        var card = Card(t, "天気 · " + _settings.WeatherCity, row);

        Task.Run(() => Weather.GetAsync(_settings.WeatherCity)).ContinueWith(r =>
        {
            var w = r.Result;
            Dispatcher.BeginInvoke(() =>
            {
                if (w == null) { desc.Text = "取得できませんでした（設定で地点を確認）"; return; }
                temp.Text = $"{w.Temperature:0}°";
                desc.Text = $"{w.Place}  {w.Description}";
                range.Text = $"最高 {w.Max:0}° / 最低 {w.Min:0}°";
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
            g.Children.Add(UiKit.Label(label, 12));
            var bar = new ProgressBar
            {
                Height = 4, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0),
                Foreground = t.AccentBrush, Background = t.Stroke,
            };
            Grid.SetColumn(bar, 1);
            g.Children.Add(bar);
            var val = UiKit.Label("", 12, t.TextSecondary);
            val.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(val, 2);
            g.Children.Add(val);
            body.Children.Add(g);
            return (bar, val);
        }
        _cpu = Row("CPU");
        _mem = Row("メモリ");
        _disk = Row("ディスク");
        return Card(t, "システム", body);
    }

    private Border FoldersCard(Theme t)
    {
        var grid = new UniformGrid { Columns = 3 };
        foreach (var (name, glyph, path) in UserFolders.Common())
        {
            var stack = new StackPanel();
            stack.Children.Add(UiKit.Glyph(glyph, 20, t.AccentBrush));
            var l = UiKit.Label(name, 11);
            l.HorizontalAlignment = HorizontalAlignment.Center;
            l.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(l);
            var tile = new Tile { Height = 64, Margin = new Thickness(1), Child = stack, ToolTip = path };
            stack.VerticalAlignment = VerticalAlignment.Center;
            tile.Click += _ => Open(path);
            grid.Children.Add(tile);
        }
        return Card(t, "フォルダー", grid);
    }

    private Border RecentCard(Theme t)
    {
        var list = new StackPanel();
        var items = UserFolders.Recent(6);
        if (items.Count == 0) list.Children.Add(UiKit.Label("最近使ったファイルはありません", 12, t.TextSecondary));
        foreach (var (name, lnk) in items)
        {
            var img = UiKit.Icon(null, 20);
            Task.Run(() => IconHelper.Get(lnk, 40)).ContinueWith(r =>
            {
                if (r.Result != null) Dispatcher.BeginInvoke(() => img.Source = r.Result);
            }, TaskScheduler.Default);
            var label = UiKit.Label(name, 12);
            label.Margin = new Thickness(10, 0, 0, 0);
            label.MaxWidth = 230;
            var row = UiKit.Row(img, label);
            row.VerticalAlignment = VerticalAlignment.Center;
            var tile = new Tile { Height = 34, Padding = new Thickness(6, 0, 6, 0), Child = row, ToolTip = name };
            tile.Click += _ => Open(lnk);
            list.Children.Add(tile);
        }
        return Card(t, "最近使ったファイル", list);
    }

    private Border NotesCard(Theme t)
    {
        var box = new TextBox
        {
            Text = Notes.Load(), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent, Foreground = t.Text,
            CaretBrush = t.Text, BorderThickness = new Thickness(0), FontSize = 13,
        };
        var save = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (s, _) => { ((DispatcherTimer)s!).Stop(); Notes.Save(box.Text); }, Dispatcher);
        save.Stop();
        box.TextChanged += (_, _) => { save.Stop(); save.Start(); };
        box.LostFocus += (_, _) => Notes.Save(box.Text);
        return Card(t, "メモ", box);
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
