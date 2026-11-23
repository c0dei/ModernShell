using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>電波の強さを表す 4 本の棒（右ほど高い）。強さの分だけ濃く、残りは薄く描く。</summary>
internal sealed class SignalIcon : Canvas
{
    private readonly System.Windows.Shapes.Rectangle[] _bars = new System.Windows.Shapes.Rectangle[4];

    public SignalIcon(double size)
    {
        Width = Height = size;
        SnapsToDevicePixels = true;
        double w = Math.Max(2, Math.Round(size * 0.17)), gap = Math.Max(1, Math.Round(size * 0.08));
        double total = 4 * w + 3 * gap, left = Math.Round((size - total) / 2);
        double[] h = { 0.35, 0.55, 0.78, 1.0 };
        for (int i = 0; i < 4; i++)
        {
            double bh = Math.Round(size * h[i]);
            var r = new System.Windows.Shapes.Rectangle { Width = w, Height = bh, RadiusX = 1, RadiusY = 1 };
            SetLeft(r, left + i * (w + gap));
            SetTop(r, size - bh);
            _bars[i] = r;
            Children.Add(r);
        }
    }

    public void Set(int bars, Brush on, Brush off)
    {
        for (int i = 0; i < 4; i++) _bars[i].Fill = i < bars ? on : off;
    }
}

internal static class NetText
{
    public static string Strength(int bars) => bars >= 4 ? L.T("強") : bars == 3 ? L.T("中") : L.T("弱");
    public static string Band(double ghz) => ghz <= 0 ? "—" : ghz == 2.4 ? "2.4 GHz" : $"{ghz:0} GHz";
}

// ---------------------------------------------------------------------------

/// <summary>バーのネットワークのカード: 電波のアイコンと、つないでいる名前。クリックで一覧・接続のポップアップ。</summary>
public sealed class NetworkBarWidget : BarWidget
{
    public override string Id => "network";
    private SignalIcon? _signal;
    private TextBlock? _wired, _name;
    private bool _busy;

    protected override FrameworkElement BuildContent()
    {
        _signal = new SignalIcon(Vertical ? 18 : 20);
        _wired = UiKit.Glyph("", Vertical ? 16 : 18, T.Text);
        _wired.Visibility = Visibility.Collapsed;
        var icon = new Grid { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        icon.Children.Add(_signal);
        icon.Children.Add(_wired);
        _name = UiKit.Label("", 12, T.Text, FontWeights.SemiBold);
        _name.MaxWidth = 130;
        _name.Margin = new Thickness(8, 0, 0, 0);

        var row = new StackPanel { Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(icon);
        if (!Vertical) row.Children.Add(_name);

        // OS のイベントで、つなぎ直し・切断をすぐ反映する（電波の強さの細かい変化は、Tick で読み直す）
        row.Loaded += (_, _) => { NetworkControl.Hook(); NetworkControl.Changed += OnOsEvent; _ = RefreshAsync(); };
        row.Unloaded += (_, _) => NetworkControl.Changed -= OnOsEvent;

        if (Vertical) return row;
        row.Margin = new Thickness(10, 0, 10, 0);
        PadOverride = new Thickness(0);
        return new Border { Background = T.Hover, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6), Child = row };
    }

    private void OnOsEvent()
    {
        try { Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync())); } catch { }
    }

    public override void Tick(int count)
    {
        if (count % 4 == 0) _ = RefreshAsync();
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_busy || _signal == null || _name == null || _wired == null) return;
        _busy = true;
        try
        {
            var s = await NetworkControl.GetStatusAsync();
            var dim = T.TextSecondary;
            _wired.Visibility = s.Connected && s.Wired ? Visibility.Visible : Visibility.Collapsed;
            _signal.Visibility = _wired.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            _signal.Set(s.Connected && !s.Wired ? s.Bars : 0, T.Text, Dim());
            string text = !s.RadioOn && s.HasWifi && !s.Connected ? L.T("Wi-Fi オフ") : s.Connected ? s.Name : L.T("未接続");
            _name.Text = text;
            _name.Foreground = s.Connected ? T.Text : dim;
            if (Card != null) Card.ToolTip = s.Connected ? s.Name + (s.Mbps > 0 ? $"  {s.Mbps} Mb/s" : "") : text;
        }
        catch (Exception ex) { Log.Error("network widget", ex); }
        finally { _busy = false; }
    }

    private Brush Dim() => new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80));

    public override FrameworkElement? BuildDetailsView() => new NetworkDetailsView();

    public override IEnumerable<MenuItemSpec> ExtraMenu() => new[]
    {
        new MenuItemSpec("", L.T("ネットワークとインターネットの設定"), () => Launcher.Open("ms-settings:network")),
    };
}

// ---------------------------------------------------------------------------

/// <summary>ネットワークのポップアップ: Wi-Fi のオン/オフ・つないでいるネットワーク・近くのネットワーク。</summary>
internal sealed class NetworkDetailsView : Border
{
    private readonly Theme _t = Theme.Current;
    private readonly TextBlock _sub;
    private readonly ToggleSwitch _toggle;
    private readonly StackPanel _body = new();
    private readonly DispatcherTimer _timer;
    private NetStatus _status = new();
    private List<WifiNet> _nets = new();
    private string? _note;                 // 接続中・失敗などの、行に出す一時的な表示（ssid → 文字）
    private string? _noteSsid;
    private bool _scanning, _busy, _loaded;
    private int _tick;
    private string _sig = "";

    public NetworkDetailsView()
    {
        Width = 400;
        Padding = new Thickness(16, 14, 16, 8);
        System.Windows.Documents.TextElement.SetForeground(this, _t.Text);

        var root = new StackPanel();
        Child = root;

        var head = new Grid { Margin = new Thickness(2, 0, 2, 12) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var names = new StackPanel();
        names.Children.Add(UiKit.Label("Wi-Fi", 17, _t.Text, FontWeights.SemiBold));
        _sub = UiKit.Label("", 12, _t.TextSecondary);
        names.Children.Add(_sub);
        head.Children.Add(names);
        _toggle = new ToggleSwitch(true, showLabel: false) { VerticalAlignment = VerticalAlignment.Center };
        _toggle.Toggled += async on =>
        {
            _toggle.IsEnabled = false;
            bool ok = await NetworkControl.SetRadioAsync(on);
            if (!ok) { _toggle.IsOn = !on; Launcher.Open("ms-settings:network-wifi"); }
            _toggle.IsEnabled = true;
            await RefreshAsync(rescan: on && ok);
        };
        Grid.SetColumn(_toggle, 1);
        head.Children.Add(_toggle);
        root.Children.Add(head);
        root.Children.Add(_body);

        var settings = new Tile { Padding = new Thickness(10, 10, 10, 10), Margin = new Thickness(0, 8, 0, 0) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(UiKit.Glyph("", 15, _t.TextSecondary));
        var txt = UiKit.Label(L.T("ネットワークとインターネットの設定"), 13);
        txt.TextWrapping = TextWrapping.Wrap; txt.TextTrimming = TextTrimming.None;
        txt.Margin = new Thickness(12, 0, 8, 0);
        Grid.SetColumn(txt, 1);
        row.Children.Add(txt);
        var chev = UiKit.Glyph("", 11, _t.TextSecondary);
        Grid.SetColumn(chev, 2);
        row.Children.Add(chev);
        settings.Child = row;
        settings.Click += _ => Launcher.Open("ms-settings:network");
        root.Children.Add(new Border { Height = 1, Background = _t.Stroke, Margin = new Thickness(2, 8, 2, 0) });
        root.Children.Add(settings);

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (_, _) =>
        {
            _tick++;
            await RefreshAsync(rescan: _tick % 5 == 0); // 一覧は 15 秒ごとに探し直す
        }, Dispatcher);
        Loaded += async (_, _) =>
        {
            _timer.Start();
            NetworkControl.Hook();
            NetworkControl.Changed += OnOsEvent;
            await RefreshAsync(rescan: false); // 先に、前回までに見つけた一覧で素早く出す
            await RefreshAsync(rescan: true);
        };
        Unloaded += (_, _) => { _timer.Stop(); NetworkControl.Changed -= OnOsEvent; };
    }

    private void OnOsEvent()
    {
        try { Dispatcher.BeginInvoke(new Action(async () => await RefreshAsync(false))); } catch { }
    }

    private async System.Threading.Tasks.Task RefreshAsync(bool rescan)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (rescan) _scanning = true;
            _status = await NetworkControl.GetStatusAsync();
            if (_status.RadioOn && _status.HasWifi) _nets = await NetworkControl.NetworksAsync(rescan);
            else _nets = new();
            _scanning = false;
            _loaded = true;
            Render();
        }
        catch (Exception ex) { Log.Error("network details", ex); }
        finally { _busy = false; _scanning = false; }
    }

    private void Render()
    {
        var s = _status;
        _toggle.Visibility = s.HasWifi ? Visibility.Visible : Visibility.Collapsed;
        _toggle.IsOn = s.RadioOn;
        _sub.Text = !s.HasWifi ? (s.Connected ? L.F("{0} に接続中", s.Name) : L.T("Wi-Fi アダプターがありません"))
            : !s.RadioOn ? L.T("オフ")
            : s.Connected && !s.Wired ? L.T("オン") + " ・ " + L.F("{0} に接続中", s.Name)
            : L.T("オン") + " ・ " + L.T("未接続");

        // 変わっていなければ作り直さない（ホバー中の行が点滅しないように）
        string sig = string.Join("|", s.Connected, s.Wired, s.Name, s.Bars, s.Mbps, s.GHz, s.RadioOn, _noteSsid, _note, NetworkControl.Denied,
            string.Join(",", _nets.Select(n => $"{n.Ssid}:{n.Bars}:{n.Secure}")));
        if (sig == _sig) return;
        _sig = sig;

        _body.Children.Clear();
        if (s.Connected) _body.Children.Add(ConnectedCard(s));
        if (!s.HasWifi || !s.RadioOn) return;

        var h = UiKit.Label(L.T("利用可能なネットワーク"), 13, _t.TextSecondary, FontWeights.SemiBold);
        h.Margin = new Thickness(4, 14, 0, 4);
        _body.Children.Add(h);

        var others = _nets.Where(n => !(s.Connected && !s.Wired && n.Ssid == s.Name)).OrderByDescending(n => n.Bars).ThenBy(n => n.Ssid).ToList();
        if (others.Count == 0)
        {
            string msg = NetworkControl.Denied ? L.T("近くのネットワークを取得できません。位置情報の許可が必要です")
                : _loaded && !_scanning ? L.T("ほかのネットワークは見つかりません") : L.T("探しています…");
            var m = UiKit.Label(msg, 12, _t.TextSecondary);
            m.TextWrapping = TextWrapping.Wrap; m.TextTrimming = TextTrimming.None;
            m.Margin = new Thickness(4, 6, 4, 6);
            _body.Children.Add(m);
            if (NetworkControl.Denied)
            {
                var open = new Tile { Padding = new Thickness(10, 8, 10, 8), Child = UiKit.Label(L.T("位置情報の設定を開く"), 13) };
                open.Click += _ => Launcher.Open("ms-settings:privacy-location");
                _body.Children.Add(open);
            }
            return;
        }
        var list = new StackPanel();
        foreach (var n in others) list.Children.Add(NetRow(n));
        _body.Children.Add(new ScrollViewer { MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
    }

    private FrameworkElement ConnectedCard(NetStatus s)
    {
        var accent = (_t.AccentBrush as SolidColorBrush)?.Color ?? Color.FromRgb(0x8C, 0x9E, 0xFF);
        var card = new Border
        {
            Background = _t.Surface, CornerRadius = _t.Corner(10), Padding = new Thickness(14),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, accent.R, accent.G, accent.B)),
        };
        var col = new StackPanel();
        card.Child = col;

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FrameworkElement icon;
        if (s.Wired) icon = UiKit.Glyph("", 22, _t.Text);
        else { var sg = new SignalIcon(24); sg.Set(s.Bars, _t.Text, Dim()); icon = sg; }
        icon.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(icon);
        var names = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(UiKit.Label(s.Name, 16, _t.Text, FontWeights.SemiBold));
        names.Children.Add(UiKit.Label(L.T("接続済み") + (s.Secure ? " ・ " + L.T("セキュリティ保護あり") : ""), 12, _t.TextSecondary));
        Grid.SetColumn(names, 1);
        top.Children.Add(names);
        col.Children.Add(top);

        var stats = new UniformGrid { Margin = new Thickness(0, 12, 0, 0) };
        var cells = new List<(string, string)>();
        if (s.Mbps > 0) cells.Add((L.T("速度"), $"{s.Mbps} Mb/s"));
        if (!s.Wired && s.GHz > 0) cells.Add((L.T("周波数"), NetText.Band(s.GHz)));
        if (!s.Wired) cells.Add((L.T("電波"), NetText.Strength(s.Bars)));
        if (cells.Count > 0)
        {
            stats.Columns = cells.Count;
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = new Border { Background = _t.Hover, CornerRadius = _t.Corner(8), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(i == 0 ? 0 : 3, 0, i == cells.Count - 1 ? 0 : 3, 0) };
                var sp = new StackPanel();
                sp.Children.Add(UiKit.Label(cells[i].Item1, 11, _t.TextSecondary));
                sp.Children.Add(UiKit.Label(cells[i].Item2, 14, _t.Text, FontWeights.SemiBold));
                cell.Child = sp;
                stats.Children.Add(cell);
            }
            col.Children.Add(stats);
        }

        if (!s.Wired)
        {
            var btn = new Tile
            {
                Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 9, 0, 9), BorderBrush = _t.Stroke,
                Child = new TextBlock { Text = L.T("切断"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = _t.Text, HorizontalAlignment = HorizontalAlignment.Center },
            };
            btn.Click += async _ => { NetworkControl.Disconnect(); await System.Threading.Tasks.Task.Delay(600); await RefreshAsync(false); };
            col.Children.Add(btn);
        }
        return card;
    }

    private Brush Dim() => new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80));

    private FrameworkElement NetRow(WifiNet n)
    {
        var tile = new Tile { Padding = new Thickness(8, 8, 8, 8) };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sg = new SignalIcon(22) { Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        sg.Set(n.Bars, _t.Text, Dim());
        g.Children.Add(sg);
        var names = new StackPanel { Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(UiKit.Label(n.Ssid, 14, _t.Text, FontWeights.SemiBold));
        string sub = _noteSsid == n.Ssid && _note != null ? _note : n.Secure ? L.T("セキュリティ保護あり") : L.T("オープン");
        names.Children.Add(UiKit.Label(sub, 12, _t.TextSecondary));
        Grid.SetColumn(names, 1);
        g.Children.Add(names);
        if (n.Secure)
        {
            var lk = UiKit.Glyph("", 13, _t.TextSecondary);
            Grid.SetColumn(lk, 2);
            g.Children.Add(lk);
        }
        tile.Child = g;
        tile.Click += async _ => await ConnectAsync(n);
        return tile;
    }

    private async System.Threading.Tasks.Task ConnectAsync(WifiNet n)
    {
        _noteSsid = n.Ssid; _note = L.T("接続中…");
        _sig = ""; Render();
        bool ok = await NetworkControl.ConnectAsync(n.Ssid);
        if (ok) { _noteSsid = null; _note = null; }
        else if (n.Secure) { _noteSsid = null; _note = null; Launcher.Open("ms-settings:network-wifi"); } // 新しいネットワークは、パスワード入力のため Windows の画面へ
        else _note = L.T("接続できませんでした");
        _sig = "";
        await RefreshAsync(false);
        if (!_busy) Render();
    }
}
