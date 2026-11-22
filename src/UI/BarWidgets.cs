using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ModernShell.Core;
using Forms = System.Windows.Forms;

namespace ModernShell.UI;

/// <summary>バーのウィジェットの一覧（設定画面の選択と、バーへの追加に使う）。</summary>
public sealed record BarWidgetInfo(string Id, string Category, string Title, string Description, Func<BarWidget> Create);

public static class BarWidgetCatalog
{
    public static readonly IReadOnlyList<BarWidgetInfo> All = new[]
    {
        new BarWidgetInfo("system", "システム", "CPU・メモリ", "CPU とメモリの使用率を円グラフで表示します。クリックで詳細", () => new SystemBarWidget()),
        new BarWidgetInfo("network", "システム", "ネットワーク", "つないでいる Wi-Fi・有線の名前と電波の強さ。クリックで Wi-Fi のオン/オフ・近くのネットワークへの接続・切断", () => new NetworkBarWidget()),
        new BarWidgetInfo("netspeed", "システム", "通信速度", "ダウンロード・アップロードの今の速さを円グラフで表示します。クリックで直近 30 秒の推移", () => new NetSpeedBarWidget()),
        new BarWidgetInfo("volume", "メディア", "音量", "今の音量。ホイールで音量を変え、クリックで出力デバイスの切り替え・アプリごとの音量", () => new VolumeBarWidget()),
        new BarWidgetInfo("tray", "システム", "トレイアイコン", "アプリが置くアイコン（「^」の一覧と、常に表示するアイコン）。オフにするとトレイアイコンは表示されません。ドラッグで常に表示/しまうを切り替え、「^」の右クリックで表示するアイコンを選べます", () => new TrayBarWidget()),
        new BarWidgetInfo("quicksettings", "システム", "クイック設定", "ネットワーク（Wi-Fi・有線・機内モード）・Bluetooth・音量をひとつにまとめた表示。クリックで Windows のクイック設定、ホイールで音量", () => new QuickSettingsBarWidget()),
        new BarWidgetInfo("ime", "システム", "IME（日本語入力）", "日本語入力の状態（あ / A など）を表示します。オンにすると、バー標準の IME 表示の代わりになります。クリックで入力モード（ひらがな・カタカナ・英数）", () => new ImeBarWidget()),
        new BarWidgetInfo("notification", "システム", "通知", "ベルのアイコンと通知の数。クリックでアプリの通知ポップアップ（通知の一覧・応答不可の切り替え・Windows の通知設定）か Windows の通知センターを開きます", () => new NotificationBarWidget()),
        new BarWidgetInfo("battery","システム", "バッテリー", "バッテリー残量と充電中の表示（バッテリーの無い PC では表示されません）", () => new BatteryBarWidget()),
        new BarWidgetInfo("nowplaying", "メディア", "再生中の曲", "再生中の曲名とアーティスト。クリックでジャケット・シークバー・操作ボタン、右クリックで前後の曲へ", () => new NowPlayingBarWidget()),
        new BarWidgetInfo("weather", "天気", "天気", "今の天気（温度・天気）。クリックで時間ごと・週間の予報を表示。設定の天気の地点と更新間隔を使います", () => new WeatherBarWidget()),
        new BarWidgetInfo("calendar", "生産性", "カレンダー", "今日の日付と次の予定。クリックで月のカレンダーと、選んだ日の予定（追加・削除できます）", () => new CalendarBarWidget()),
        new BarWidgetInfo("clock", "生産性", "時計", "時刻と日付をカードで表示します。オンにすると、バー標準の時計の代わりになります。クリックで通知センター", () => new ClockBarWidget()),
        new BarWidgetInfo("pomodoro", "生産性", "ポモドーロ", "25 分の集中と 5 分の休憩を繰り返すタイマー。クリックで開始/一時停止", () => new PomodoroBarWidget()),
    };

    /// <summary>並べ方を整えるパーツ（何個でも置ける。ID は "divider:xxxx" / "spacer:xxxx"）。</summary>
    public static readonly IReadOnlyList<BarWidgetInfo> Parts = new[]
    {
        new BarWidgetInfo("divider", "パーツ", "区切り線", "グループのあいだの細い線", () => new DividerBarPart()),
        new BarWidgetInfo("spacer", "パーツ", "スペース", "ウィジェットのあいだの空き", () => new SpacerBarPart()),
    };

    public static bool IsPart(string id) => id.StartsWith("divider:", StringComparison.Ordinal) || id.StartsWith("spacer:", StringComparison.Ordinal);

    /// <summary>新しいパーツの ID（kind は "divider" か "spacer"）。</summary>
    public static string NewPartId(string kind) => kind + ":" + Guid.NewGuid().ToString("N")[..6];

    public static BarWidgetInfo? Find(string id)
    {
        int i = id.IndexOf(':');
        var key = i < 0 ? id : id[..i];
        return All.Concat(Parts).FirstOrDefault(w => w.Id == key);
    }
}

/// <summary>タスクバーに並ぶウィジェットのカード 1 枚。</summary>
public abstract class BarWidget
{
    public abstract string Id { get; }
    /// <summary>区切り線・スペースなどのパーツか（余白を付けない）。</summary>
    protected virtual bool IsPart => false;
    protected Theme T => Theme.Current;
    protected bool Vertical { get; private set; }
    protected double TileH { get; private set; }
    /// <summary>カードの余白を、ウィジェット側で決めたいとき（<see cref="BuildContent"/> の中で入れる）。</summary>
    protected Thickness? PadOverride { get; set; }

    /// <summary>カードを作る。縦のバーでは幅が狭いので、小さな表示にする。</summary>
    public FrameworkElement Build(bool vertical, double tileH)
    {
        Vertical = vertical;
        TileH = tileH;
        var inner = BuildContent();
        var card = new Border
        {
            Background = Brushes.Transparent, CornerRadius = T.Corner(6), Child = inner,
            Padding = IsPart ? new Thickness(0) : PadOverride ?? (vertical ? new Thickness(0, 4, 0, 4) : new Thickness(8, 0, 8, 0)),
        };
        if (vertical) card.Width = tileH; else card.Height = tileH;
        Card = card;
        return card;
    }

    public FrameworkElement? Card { get; private set; }
    /// <summary>バーに置いたときの外側のボタン（隠すときは、余白ごと隠す）。</summary>
    public FrameworkElement? Outer { get; set; }

    protected abstract FrameworkElement BuildContent();

    /// <summary>1 秒ごとに呼ばれる。</summary>
    public virtual void Tick(int count) { }

    /// <summary>クリックしたときの詳細（メニュー形式）。</summary>
    public virtual IEnumerable<MenuItemSpec> Details() => Enumerable.Empty<MenuItemSpec>();

    /// <summary>クリックで開く、凝った詳細（メニューでは足りないとき）。null ならメニュー形式の <see cref="Details"/> を使う。</summary>
    public virtual FrameworkElement? BuildDetailsView() => null;

    /// <summary>左クリック。既定は詳細を出す。true を返したら処理済み（詳細は出さない）。</summary>
    public virtual bool OnClick() => false;

    /// <summary>左クリックで Windows のパネル（通知センター・クイック設定）を開くか。開く間は、バーのウィジェットの枠を強調する。</summary>
    public virtual bool OpensSystemPanel => false;

    /// <summary>右クリックのメニューに足す項目。</summary>
    public virtual IEnumerable<MenuItemSpec> ExtraMenu() => Enumerable.Empty<MenuItemSpec>();

    protected void SetVisible(bool visible)
    {
        var el = Outer ?? Card;
        if (el != null) el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 部品 ----

    /// <summary>円グラフ（0–100）。</summary>
    protected sealed class Ring : Grid
    {
        private readonly Path _arc = new();
        private readonly TextBlock _label;
        private readonly double _size, _thickness;

        public Ring(double size, double thickness, Brush fg, Brush track, Brush textBrush, double fontSize)
        {
            _size = size; _thickness = thickness;
            Width = Height = size;
            Children.Add(new Ellipse { Stroke = track, StrokeThickness = thickness, Width = size - thickness, Height = size - thickness });
            _arc.Stroke = fg;
            _arc.StrokeThickness = thickness;
            _arc.StrokeStartLineCap = _arc.StrokeEndLineCap = PenLineCap.Round;
            Children.Add(_arc);
            _label = new TextBlock
            {
                FontSize = fontSize, Foreground = textBrush, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            Children.Add(_label);
        }

        public void SetColor(Brush fg) => _arc.Stroke = fg;

        public void Set(double percent, string text)
        {
            percent = Math.Clamp(percent, 0, 100);
            _label.Text = text;
            double r = (_size - _thickness) / 2, c = _size / 2;
            if (percent <= 0.1) { _arc.Data = Geometry.Empty; return; }
            double angle = Math.Min(359.9, percent / 100.0 * 360.0) * Math.PI / 180.0;
            var start = new Point(c, c - r);
            var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true));
            _arc.Data = new PathGeometry(new[] { fig });
        }
    }

    protected Brush Track => new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
}

// ---------------------------------------------------------------------------

/// <summary>
/// トレイアイコンのウィジェット。中身（「^」と常に表示のアイコン）は、アイコンの操作の処理を持つ TaskbarController が作る。
/// ここは、一覧（設定画面・追加/削除）に載せるための目印。
/// </summary>
public sealed class TrayBarWidget : BarWidget
{
    public override string Id => "tray";
    protected override FrameworkElement BuildContent() => new Border();
}

public sealed class SystemBarWidget : BarWidget
{
    public override string Id => "system";
    private readonly SystemStats _stats = new();
    private static readonly Brush MemBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xB8, 0x72));
    private Ring? _cpu, _mem;
    private double _cpuPct, _memPct, _memUsed, _memTotal;

    protected override FrameworkElement BuildContent()
    {
        double size = Vertical ? 22 : 26;
        _cpu = new Ring(size, 3, T.AccentBrush, Track, T.Text, 9);
        _mem = new Ring(size, 3, MemBrush, Track, T.Text, 9);
        _stats.CpuPercent();
        var row = new StackPanel { Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _mem.Margin = Vertical ? new Thickness(0, 4, 0, 0) : new Thickness(6, 0, 0, 0);
        row.Children.Add(_cpu);
        row.Children.Add(_mem);
        Tick(0);
        if (Vertical) return row;
        // 音楽・天気ウィジェットと同じ、薄い地と縁のある角丸のカード
        row.Margin = new Thickness(8, 0, 8, 0);
        PadOverride = new Thickness(0);
        return new Border { Background = T.Hover, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6), Child = row };
    }

    private static readonly Brush HighBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x75));

    public override void Tick(int count)
    {
        if (_cpu == null || _mem == null) return;
        _cpuPct = _stats.CpuPercent();
        (_memPct, _memUsed, _memTotal) = _stats.Memory();
        _cpu.Set(_cpuPct, $"{_cpuPct:0}");
        _mem.Set(_memPct, $"{_memPct:0}");
        // 使用率が高いときは、リングの色を変える（ポップアップと同じ警告色）
        _cpu.SetColor(_cpuPct >= SystemDetailsView.HighLoad ? HighBrush : T.AccentBrush);
        _mem.SetColor(_memPct >= SystemDetailsView.HighLoad ? HighBrush : MemBrush);
        SystemHistory.Push(_cpuPct, _memPct);
    }

    public override FrameworkElement? BuildDetailsView() => new SystemDetailsView();

    public override IEnumerable<MenuItemSpec> Details() => new[]
    {
        MenuItemSpec.Header(L.T("システム")),
        new MenuItemSpec("", $"CPU  {_cpuPct:0}%", () => { }),
        new MenuItemSpec("", L.F("メモリ  {0:0}%（{1:0.0} / {2:0.0} GB）", _memPct, _memUsed, _memTotal), () => { }),
        MenuItemSpec.Separator,
        new MenuItemSpec("", L.T("タスク マネージャー"), SystemActions.OpenTaskManager),
    };
}

// ---------------------------------------------------------------------------

public sealed class BatteryBarWidget : BarWidget
{
    public override string Id => "battery";
    private Ring? _ring;
    private TextBlock? _bolt;
    private bool _hasBattery;

    protected override FrameworkElement BuildContent()
    {
        _ring = new Ring(Vertical ? 24 : 28, 3, new SolidColorBrush(Color.FromRgb(0x2E, 0xB8, 0x72)), Track, T.Text, 9);
        _bolt = UiKit.Glyph("", 10, T.Text);
        _bolt.Margin = new Thickness(4, 0, 0, 0);
        var row = new StackPanel { Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(_ring);
        row.Children.Add(_bolt);
        Tick(0);
        return row;
    }

    public override void Tick(int count)
    {
        if (_ring == null) return;
        try
        {
            var ps = Forms.SystemInformation.PowerStatus;
            _hasBattery = !ps.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery);
            SetVisible(_hasBattery);
            if (!_hasBattery) return;
            double pct = Math.Clamp(ps.BatteryLifePercent * 100, 0, 100);
            _ring.Set(pct, $"{pct:0}");
            if (_bolt != null) _bolt.Visibility = ps.PowerLineStatus == Forms.PowerLineStatus.Online ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { SetVisible(false); }
    }

    public override IEnumerable<MenuItemSpec> Details()
    {
        var ps = Forms.SystemInformation.PowerStatus;
        var items = new List<MenuItemSpec>
        {
            MenuItemSpec.Header(L.T("バッテリー")),
            new("", L.F("残量  {0:0}%", ps.BatteryLifePercent * 100), () => { }),
            new("", ps.PowerLineStatus == Forms.PowerLineStatus.Online ? L.T("電源に接続中") : L.T("バッテリーで動作中"), () => { }),
        };
        if (ps.BatteryLifeRemaining > 0)
        {
            var t = TimeSpan.FromSeconds(ps.BatteryLifeRemaining);
            items.Add(new("", L.F("残り  {0} 時間 {1} 分", (int)t.TotalHours, t.Minutes), () => { }));
        }
        items.Add(MenuItemSpec.Separator);
        items.Add(new("", L.T("電源とバッテリーの設定"), () => Launcher.Open("ms-settings:powersleep")));
        return items;
    }
}

// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------

/// <summary>ポモドーロ: 25 分の集中 → 5 分の休憩（4 回ごとに 15 分）。終わると音で知らせる。</summary>
public sealed class PomodoroBarWidget : BarWidget
{
    public override string Id => "pomodoro";
    private enum Phase { Focus, Break, LongBreak }
    private Phase _phase = Phase.Focus;
    private int _round = 1;
    private int _remaining = Seconds(Phase.Focus);
    private bool _running;
    private TextBlock? _label, _time;
    private Border? _frame;

    private static int Seconds(Phase p) => p switch { Phase.Focus => 25 * 60, Phase.Break => 5 * 60, _ => 15 * 60 };

    protected override FrameworkElement BuildContent()
    {
        _label = UiKit.Label("", 10, T.TextSecondary);
        _time = UiKit.Label("", Vertical ? 11 : 15, T.Text, FontWeights.SemiBold);
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (!Vertical) col.Children.Add(_label);
        col.Children.Add(_time);
        _frame = new Border { CornerRadius = T.Corner(6), BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent, Child = col, Padding = new Thickness(0, 0, 0, 0) };
        Refresh();
        return _frame;
    }

    private void Refresh()
    {
        if (_label == null || _time == null) return;
        _time.Text = $"{_remaining / 60:00}:{_remaining % 60:00}";
        _label.Text = _phase switch { Phase.Focus => $"Focus {_round}/4", Phase.Break => L.T("休憩"), _ => L.T("長い休憩") };
        if (_frame != null) _frame.BorderBrush = _running ? new SolidColorBrush(_phase == Phase.Focus ? Color.FromRgb(0xE8, 0x3A, 0x30) : Color.FromRgb(0x2E, 0xB8, 0x72)) : Brushes.Transparent;
        if (Card != null) Card.ToolTip = _running ? L.T("クリックで一時停止") : L.T("クリックで開始");
    }

    public override void Tick(int count)
    {
        if (!_running) return;
        if (--_remaining <= 0) NextPhase();
        Refresh();
    }

    private void NextPhase()
    {
        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        if (_phase == Phase.Focus) { _phase = _round >= 4 ? Phase.LongBreak : Phase.Break; }
        else { if (_phase == Phase.LongBreak) _round = 1; else _round++; _phase = Phase.Focus; }
        _remaining = Seconds(_phase);
        _running = false; // 次は、自分で始めるまで待つ
    }

    public override bool OnClick() { _running = !_running; Refresh(); return true; }

    public override IEnumerable<MenuItemSpec> ExtraMenu() => new[]
    {
        MenuItemSpec.Header(L.T("ポモドーロ")),
        new MenuItemSpec(_running ? "" : "", _running ? L.T("一時停止") : L.T("開始"), () => OnClick()),
        new MenuItemSpec("", L.T("リセット"), () => { _phase = Phase.Focus; _round = 1; _remaining = Seconds(_phase); _running = false; Refresh(); }),
        new MenuItemSpec("", L.T("次へ進む"), () => { NextPhase(); Refresh(); }),
        MenuItemSpec.Separator,
    };
}

// ---------------------------------------------------------------------------

/// <summary>今の天気（アイコン・温度・天気）。取得は天気のウィジェットと同じ（地点・更新間隔は設定のもの）。</summary>
public sealed class WeatherBarWidget : BarWidget
{
    public override string Id => "weather";
    private System.Windows.Controls.Image? _icon;
    private TextBlock? _temp, _desc;
    private string _city = "";
    private bool _busy;

    protected override FrameworkElement BuildContent()
    {
        _icon = WeatherSymbols.Create(2, true, Vertical ? 26 : 30);
        _temp = UiKit.Label("—", 13, T.Text, FontWeights.SemiBold);
        _desc = UiKit.Label("", 11, T.TextSecondary);
        _desc.MaxWidth = 160;
        _desc.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        text.Children.Add(_temp);
        text.Children.Add(_desc);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(_icon);
        if (!Vertical) row.Children.Add(text);
        Tick(0);
        if (Vertical) return row;
        // 音楽ウィジェットと同じ、薄い地と縁のある角丸のカード
        row.Margin = new Thickness(8, 0, 8, 0);
        PadOverride = new Thickness(0);
        return new Border { Background = T.Hover, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6), Child = row };
    }

    private void Apply(Weather.Report w)
    {
        if (_icon != null) WeatherSymbols.Apply(_icon, w.Code, w.IsDay);
        if (_temp != null) _temp.Text = $"{w.Temperature:0}°";
        if (_desc != null) _desc.Text = $"{w.Place}  {w.Description}";
        if (Card != null) Card.ToolTip = $"{w.Place}  {w.Description}  {w.Temperature:0}°（{L.F("最高 {0:0}° / 最低 {1:0}°", w.Max, w.Min)}）";
    }

    private void Fetch(bool force)
    {
        if (_busy) return;
        var city = Settings.Current?.WeatherCity ?? "";
        if (string.IsNullOrWhiteSpace(city)) return;
        _busy = true;
        System.Threading.Tasks.Task.Run(() => force ? Weather.RefreshAsync(city) : Weather.GetAsync(city)).ContinueWith(r =>
        {
            (Card?.Dispatcher ?? System.Windows.Application.Current.Dispatcher).BeginInvoke(() =>
            {
                _busy = false;
                if (r.Exception == null && r.Result is { } w) Apply(w);
            });
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    public override void Tick(int count)
    {
        var city = Settings.Current?.WeatherCity ?? "";
        if (city != _city) { _city = city; if (Weather.Cached(city) is { } c) Apply(c); }
        else if (count == 0 && Weather.Cached(city) is { } c0) Apply(c0);
        // 30 秒ごとに、新しさを確かめる（更新間隔を過ぎていれば取りに行く。それまでは通信しない）
        if (count % 30 == 0 && !Weather.IsFresh(city)) Fetch(false);
    }

    public override FrameworkElement? BuildDetailsView()
    {
        var city = Settings.Current?.WeatherCity ?? "";
        return string.IsNullOrWhiteSpace(city) ? null : new WeatherDetailsView(city, Weather.Cached(city));
    }

    public override IEnumerable<MenuItemSpec> ExtraMenu() => new[]
    {
        new MenuItemSpec("\uE72C", L.T("今すぐ更新"), () => Fetch(true)),
        MenuItemSpec.Separator,
    };
}

// ---------------------------------------------------------------------------

/// <summary>区切り線（細い縦線。縦のバーでは横線）。</summary>
public sealed class DividerBarPart : BarWidget
{
    public override string Id => "divider";
    protected override bool IsPart => true;
    protected override FrameworkElement BuildContent() => Vertical
        ? new Border { Height = 1, Width = TileH * 0.6, Background = T.Stroke, Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        : new Border { Width = 1, Height = TileH * 0.55, Background = T.Stroke, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
}

/// <summary>スペース（ただの空き）。</summary>
public sealed class SpacerBarPart : BarWidget
{
    public override string Id => "spacer";
    protected override bool IsPart => true;
    protected override FrameworkElement BuildContent() => Vertical
        ? new Border { Height = 20, Width = 1, Background = Brushes.Transparent }
        : new Border { Width = 20, Height = 1, Background = Brushes.Transparent };
}
