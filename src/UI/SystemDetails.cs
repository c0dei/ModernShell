using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>CPU・メモリの直近の使用率（1 秒ごと、最大 60 件）。バーのウィジェットが記録し、ポップアップが推移グラフに使う。</summary>
public static class SystemHistory
{
    public const int Capacity = 60;
    public static readonly List<double> Cpu = new();
    public static readonly List<double> Mem = new();

    public static void Push(double cpu, double mem)
    {
        Add(Cpu, cpu); Add(Mem, mem);
    }

    private static void Add(List<double> l, double v)
    {
        l.Add(v);
        if (l.Count > Capacity) l.RemoveAt(0);
    }
}

/// <summary>CPU・メモリの詳細（使用率・推移グラフ・内訳）。バーのシステムウィジェットをクリックしたときのポップアップの中身。</summary>
public sealed class SystemDetailsView : Border
{
    /// <summary>使用率がこれ以上なら「高め」として色を変える。</summary>
    public const double HighLoad = 85;

    private static readonly Color MemColor = Color.FromRgb(0x2E, 0xB8, 0x72);
    private static readonly Color HighColor = Color.FromRgb(0xFF, 0x8A, 0x75);

    private readonly Theme _t = Theme.Current;
    private readonly SystemStats _stats = new();
    private readonly DispatcherTimer _timer;
    private readonly MetricCard _cpu, _mem;
    private readonly TextBlock _warn;
    private readonly (string name, int mhz) _cpuInfo = SystemStats.CpuInfo();

    public SystemDetailsView()
    {
        Width = 360;
        Padding = new Thickness(14, 12, 14, 10);
        TextElement.SetForeground(this, _t.Text);

        var body = new StackPanel();
        Child = body;
        body.Children.Add(UiKit.Label(L.T("システム"), 13, _t.TextSecondary, FontWeights.SemiBold));
        ((TextBlock)body.Children[0]).Margin = new Thickness(4, 2, 0, 8);

        var accent = (_t.AccentBrush as SolidColorBrush)?.Color ?? Color.FromRgb(0x8C, 0x9E, 0xFF);
        _cpu = new MetricCard(_t, "M9,7 h6 a2,2 0 0 1 2,2 v6 a2,2 0 0 1 -2,2 h-6 a2,2 0 0 1 -2,-2 v-6 a2,2 0 0 1 2,-2 z M10.5,10.5 h3 v3 h-3 z M10,3 v2.5 M14,3 v2.5 M10,18.5 v2.5 M14,18.5 v2.5 M3,10 h2.5 M3,14 h2.5 M18.5,10 h2.5 M18.5,14 h2.5",
            L.T("CPU"), accent);
        _mem = new MetricCard(_t, "M3,8 h18 v8 h-18 z M7,16 v3 M11,16 v3 M15,16 v3 M19,16 v3 M7,11 v2 M11,11 v2 M15,11 v2 M19,11 v2",
            L.T("メモリ"), MemColor);
        body.Children.Add(_cpu.Root);
        _mem.Root.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(_mem.Root);

        _warn = UiKit.Label(L.T("使用率が高めです。不要なアプリを閉じると軽くなります"), 12, new SolidColorBrush(HighColor));
        _warn.TextWrapping = TextWrapping.Wrap;
        _warn.TextTrimming = TextTrimming.None;
        _mem.Footer.Children.Add(_warn);

        var tm = new Tile { Padding = new Thickness(10, 9, 10, 9), Margin = new Thickness(0, 8, 0, 0) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(UiKit.Glyph("", 15, _t.TextSecondary));
        var txt = UiKit.Label(L.T("タスク マネージャーを開く"), 13);
        txt.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(txt, 1);
        row.Children.Add(txt);
        var chev = UiKit.Glyph("", 11, _t.TextSecondary);
        Grid.SetColumn(chev, 2);
        row.Children.Add(chev);
        tm.Child = row;
        tm.Click += _ => SystemActions.OpenTaskManager();
        body.Children.Add(tm);

        _stats.CpuPercent();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        double cpu = SystemHistory.Cpu.Count > 0 ? SystemHistory.Cpu[^1] : _stats.CpuPercent();
        var (memPct, used, total) = _stats.Memory();
        var (avail, commitUsed, commitTotal) = SystemStats.MemoryDetail();
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);

        var cpuColor = cpu >= HighLoad ? HighColor : (_t.AccentBrush as SolidColorBrush)?.Color ?? Color.FromRgb(0x8C, 0x9E, 0xFF);
        _cpu.Set(cpu, cpuColor, _cpuInfo.name.Length > 0 ? _cpuInfo.name : L.T("プロセッサ使用率"), SystemHistory.Cpu,
            new[]
            {
                (L.T("コア"), L.F("{0} スレッド", Environment.ProcessorCount)),
                (L.T("クロック"), _cpuInfo.mhz > 0 ? $"{_cpuInfo.mhz / 1000.0:0.0} GHz" : "—"),
                (L.T("稼働時間"), up.TotalDays >= 1 ? L.F("{0}日 {1}時間", (int)up.TotalDays, up.Hours) : $"{(int)up.TotalHours}:{up.Minutes:00}"),
            });

        var memColor = memPct >= HighLoad ? HighColor : MemColor;
        _mem.Set(memPct, memColor, $"{used:0.0} / {total:0.0} GB", SystemHistory.Mem,
            new[]
            {
                (L.T("空き"), $"{avail:0.0} GB"),
                (L.T("コミット済み"), $"{commitUsed:0.0} / {commitTotal:0.0} GB"),
            });
        _warn.Foreground = new SolidColorBrush(HighColor);
        _warn.Visibility = memPct >= HighLoad ? Visibility.Visible : Visibility.Collapsed;
        _warn.Margin = new Thickness(0, 10, 0, 0);
    }

    /// <summary>1 つの指標のカード（アイコン・名前と補足・大きな % ・推移グラフ・内訳）。</summary>
    private sealed class MetricCard
    {
        public readonly Border Root;
        public readonly StackPanel Footer = new();
        private readonly Theme _t;
        private readonly Border _iconTile;
        private readonly Path _icon;
        private readonly TextBlock _sub, _value, _unit;
        private readonly Canvas _graph = new() { Height = 40, ClipToBounds = true, Margin = new Thickness(0, 12, 0, 0) };
        private readonly Path _line = new() { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
        private readonly Path _fill = new();
        private readonly Border _track, _bar;
        private readonly UniformGrid _stats = new() { Margin = new Thickness(0, 12, 0, 0) };

        public MetricCard(Theme t, string iconData, string title, Color color)
        {
            _t = t;
            _icon = new Path { Data = Geometry.Parse(iconData), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Width = 22, Height = 22, Stretch = Stretch.Uniform };
            _iconTile = new Border { Width = 44, Height = 44, CornerRadius = t.Corner(12), Child = _icon };
            _sub = UiKit.Label("", 12, t.TextSecondary);
            _value = UiKit.Label("0", 34, t.Text, FontWeights.SemiBold);
            _unit = UiKit.Label("%", 16, t.TextSecondary);
            _unit.Margin = new Thickness(1, 8, 0, 0);

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(_iconTile);
            var names = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(UiKit.Label(title, 15, t.Text, FontWeights.SemiBold));
            names.Children.Add(_sub);
            Grid.SetColumn(names, 1);
            head.Children.Add(names);
            var big = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            big.Children.Add(_value);
            big.Children.Add(_unit);
            Grid.SetColumn(big, 2);
            head.Children.Add(big);

            _graph.Children.Add(_fill);
            _graph.Children.Add(_line);
            // 推移グラフの背景（薄い 4 本の目盛り線は付けず、地だけ）
            var graphBox = new Border { CornerRadius = t.Corner(6), Background = new SolidColorBrush(Color.FromArgb(0x10, 0x80, 0x80, 0x80)), Child = _graph, Margin = new Thickness(0, 12, 0, 0) };
            _graph.Margin = new Thickness(0);

            _bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
            _track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), Margin = new Thickness(0, 12, 0, 0), Child = _bar };

            var body = new StackPanel();
            body.Children.Add(head);
            body.Children.Add(_track);
            body.Children.Add(graphBox);
            body.Children.Add(_stats);
            body.Children.Add(Footer);
            Root = new Border { Background = t.Surface, CornerRadius = t.Corner(14), Padding = new Thickness(14, 14, 14, 14), Child = body };
            _track.SizeChanged += (_, _) => SetBarWidth();
            Set(0, color, "", new List<double>(), Array.Empty<(string, string)>());
        }

        private double _pct;

        private void SetBarWidth() => _bar.Width = Math.Max(0, _track.ActualWidth * _pct / 100.0);

        public void Set(double pct, Color color, string sub, IReadOnlyList<double> history, (string label, string value)[] stats)
        {
            _pct = Math.Clamp(pct, 0, 100);
            var brush = new SolidColorBrush(color);
            _value.Text = $"{_pct:0}";
            _value.Foreground = brush;
            _sub.Text = sub;
            _icon.Stroke = brush;
            _iconTile.Background = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
            _bar.Background = brush;
            SetBarWidth();
            _line.Stroke = brush;
            _fill.Fill = new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B), Color.FromArgb(0x00, color.R, color.G, color.B), 90);
            DrawGraph(history);

            _stats.Columns = Math.Max(1, stats.Length);
            if (_stats.Children.Count != stats.Length)
            {
                _stats.Children.Clear();
                foreach (var _ in stats) _stats.Children.Add(new StackPanel());
            }
            for (int i = 0; i < stats.Length; i++)
            {
                var cell = (StackPanel)_stats.Children[i];
                cell.Children.Clear();
                cell.Children.Add(UiKit.Label(stats[i].label, 11, _t.TextSecondary));
                var v = UiKit.Label(stats[i].value, 13, _t.Text, FontWeights.SemiBold);
                v.Margin = new Thickness(0, 1, 0, 0);
                cell.Children.Add(v);
            }
        }

        /// <summary>直近の推移を折れ線＋塗りで描く（縦軸は 0–100%）。</summary>
        private void DrawGraph(IReadOnlyList<double> h)
        {
            double w = _graph.ActualWidth > 0 ? _graph.ActualWidth : 304, ht = _graph.Height;
            if (h.Count < 2) { _line.Data = null; _fill.Data = null; return; }
            double step = w / (SystemHistory.Capacity - 1);
            double x0 = w - (h.Count - 1) * step;                // 新しい値が右端。足りない分は左を空ける
            Point P(int i) => new(x0 + i * step, ht - 2 - (ht - 4) * Math.Clamp(h[i], 0, 100) / 100.0);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(P(0), false, false);
                for (int i = 1; i < h.Count; i++) c.LineTo(P(i), true, true);
            }
            g.Freeze();
            _line.Data = g;
            var f = new StreamGeometry();
            using (var c = f.Open())
            {
                c.BeginFigure(new Point(x0, ht), true, true);
                for (int i = 0; i < h.Count; i++) c.LineTo(P(i), true, false);
                c.LineTo(new Point(w, ht), true, false);
            }
            f.Freeze();
            _fill.Data = f;
        }
    }
}
