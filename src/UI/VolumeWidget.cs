using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ModernShell.Core;

namespace ModernShell.UI;

internal static class VolumeGlyph
{
    /// <summary>音量に合わせたスピーカーのアイコン（Segoe Fluent Icons）。</summary>
    public static string For(int volume, bool muted) =>
        muted ? "" : volume <= 0 ? "" : volume < 34 ? "" : volume < 67 ? "" : "";
}

/// <summary>バーの音量のカード: スピーカーのアイコンと音量。ホイールで音量を変え、クリックで詳細（出力デバイス・アプリごとの音量）。</summary>
public sealed class VolumeBarWidget : BarWidget
{
    public override string Id => "volume";
    private TextBlock? _icon, _value;

    protected override FrameworkElement BuildContent()
    {
        _icon = UiKit.Glyph("", Vertical ? 14 : 16, T.Text);
        _value = UiKit.Label("0", Vertical ? 10 : 13, T.Text, FontWeights.SemiBold);
        _value.TextTrimming = TextTrimming.None;
        _value.HorizontalAlignment = HorizontalAlignment.Center;
        var row = new StackPanel
        {
            Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        _value.Margin = Vertical ? new Thickness(0, 2, 0, 0) : new Thickness(6, 0, 0, 0);
        row.Children.Add(_icon);
        row.Children.Add(_value);
        // 外側のカード全体でホイールを受ける（Card は Build のあとに作られるので、表示されてから付ける）
        row.Loaded += (_, _) => { if (Card != null) { Card.MouseWheel -= OnWheel; Card.MouseWheel += OnWheel; } Apply(); };
        return row;
    }

    private void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        int step = Math.Sign(e.Delta) * 2;
        if (step == 0) return;
        if (AudioControl.GetMuted()) AudioControl.SetMuted(false);
        AudioControl.SetVolume(AudioControl.GetVolume() + step);
        Apply();
        e.Handled = true;
    }

    public override void Tick(int count) => Apply();

    private void Apply()
    {
        if (_icon == null || _value == null) return;
        bool avail = AudioControl.Available;
        SetVisible(true);
        int v = avail ? AudioControl.GetVolume() : 0;
        bool m = avail && AudioControl.GetMuted();
        _icon.Text = avail ? VolumeGlyph.For(v, m) : "";
        _value.Text = m || !avail ? "–" : v.ToString();
        if (Card != null) Card.ToolTip = m ? L.T("ミュート中") : L.F("音量: {0}%", v);
    }

    public override FrameworkElement? BuildDetailsView() => new VolumeDetailsView();

    public override IEnumerable<MenuItemSpec> ExtraMenu() => new[]
    {
        new MenuItemSpec(AudioControl.GetMuted() ? "" : "", AudioControl.GetMuted() ? L.T("ミュートを解除") : L.T("ミュート"), () => { AudioControl.SetMuted(!AudioControl.GetMuted()); Apply(); }),
        new MenuItemSpec("", L.T("サウンドの設定"), () => Launcher.Open("ms-settings:sound")),
        new MenuItemSpec("", L.T("音量ミキサー"), () => Launcher.Open("ms-settings:apps-volume")),
    };
}

// ---------------------------------------------------------------------------

/// <summary>音量のポップアップ: 出力デバイス（切り替え可）・全体の音量・アプリごとの音量。</summary>
internal sealed class VolumeDetailsView : Border
{
    private readonly Theme _t = Theme.Current;
    private readonly TextBlock _devName, _devSub, _chevron, _masterGlyph, _masterValue;
    private readonly StackPanel _devList = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    private readonly FluentSlider _master;
    private readonly StackPanel _apps = new();
    private readonly TextBlock _appsHeader;
    private readonly List<AppRow> _rows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _appsSig = "\0";

    private sealed record AppRow(uint Pid, FluentSlider Slider, TextBlock Value);

    public VolumeDetailsView()
    {
        Width = 380;
        Padding = new Thickness(14, 12, 14, 10);
        System.Windows.Documents.TextElement.SetForeground(this, _t.Text);
        var body = new StackPanel();
        Child = body;

        body.Children.Add(Pad(UiKit.Label(L.T("音量"), 13, _t.TextSecondary, FontWeights.SemiBold), new Thickness(4, 2, 4, 8)));

        // 出力デバイス（押すと一覧が開く）
        var devTile = new Tile { Padding = new Thickness(12), CornerRadius = _t.Corner(14), Background = _t.Surface };
        var dg = new Grid();
        dg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dg.Children.Add(new Border { Width = 44, Height = 44, CornerRadius = _t.Corner(12), Background = _t.Hover, Child = UiKit.Glyph("", 18, _t.Text) });
        _devName = UiKit.Label("", 16, _t.Text, FontWeights.SemiBold);
        _devSub = UiKit.Label("", 12, _t.TextSecondary);
        var names = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(_devName);
        names.Children.Add(_devSub);
        Grid.SetColumn(names, 1);
        dg.Children.Add(names);
        _chevron = UiKit.Glyph("", 12, _t.TextSecondary);
        Grid.SetColumn(_chevron, 2);
        dg.Children.Add(_chevron);
        devTile.Child = dg;
        devTile.Click += _ => ToggleDevices();
        body.Children.Add(devTile);
        body.Children.Add(_devList);

        // 全体の音量
        var mg = new Grid();
        mg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var muteTile = new Tile { Padding = new Thickness(8), Width = 40, Height = 40 };
        _masterGlyph = UiKit.Glyph("", 20, _t.Text);
        muteTile.Child = _masterGlyph;
        muteTile.Click += _ => { AudioControl.SetMuted(!AudioControl.GetMuted()); Refresh(); };
        mg.Children.Add(muteTile);
        _master = new FluentSlider(0, 100, 0) { Width = double.NaN, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(8, 0, 8, 0) };
        _master.ValueChanged += v =>
        {
            if (AudioControl.GetMuted()) AudioControl.SetMuted(false);
            AudioControl.SetVolume((int)v);
            ShowMaster((int)v, false);
        };
        Grid.SetColumn(_master, 1);
        mg.Children.Add(_master);
        _masterValue = UiKit.Label("0", 22, _t.Text, FontWeights.SemiBold);
        _masterValue.TextTrimming = TextTrimming.None;
        _masterValue.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_masterValue, 2);
        mg.Children.Add(_masterValue);
        body.Children.Add(new Border { Background = _t.Surface, CornerRadius = _t.Corner(14), Padding = new Thickness(10, 8, 12, 8), Margin = new Thickness(0, 8, 0, 0), Child = mg });

        // アプリごと
        _appsHeader = UiKit.Label(L.T("アプリごとの音量"), 13, _t.TextSecondary, FontWeights.SemiBold);
        body.Children.Add(Pad(_appsHeader, new Thickness(4, 16, 4, 6)));
        body.Children.Add(_apps);

        body.Children.Add(new Border { Height = 1, Background = _t.Stroke, Margin = new Thickness(0, 10, 0, 6) });
        var open = new Tile { Padding = new Thickness(10) };
        var og = new Grid();
        og.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        og.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        og.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        og.Children.Add(UiKit.Glyph("", 15, _t.TextSecondary));
        var txt = UiKit.Label(L.T("サウンドの設定を開く"), 13);
        txt.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(txt, 1);
        og.Children.Add(txt);
        var chev = UiKit.Glyph("", 11, _t.TextSecondary);
        Grid.SetColumn(chev, 2);
        og.Children.Add(chev);
        open.Child = og;
        open.Click += _ => Launcher.Open("ms-settings:sound");
        body.Children.Add(open);

        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        Refresh();
    }

    private static FrameworkElement Pad(FrameworkElement el, Thickness m) { el.Margin = m; return el; }

    private void ShowMaster(int v, bool muted)
    {
        _masterGlyph.Text = VolumeGlyph.For(v, muted);
        _masterValue.Text = v.ToString();
        _masterValue.Opacity = muted ? 0.5 : 1;
    }

    private void Refresh()
    {
        int v = AudioControl.GetVolume();
        bool m = AudioControl.GetMuted();
        if (!_master.IsMouseCaptured) _master.SetValue(v);
        ShowMaster(v, m);
        string name = AudioControl.DefaultName(out var sub);
        _devName.Text = name == "" ? L.T("出力デバイスがありません") : name;
        _devSub.Text = (sub == "" ? "" : sub + " ・ ") + L.T("出力デバイス");
        RefreshApps();
    }

    private void RefreshApps()
    {
        var apps = AudioControl.Apps();
        string sig = string.Join(",", apps.Select(a => a.Pid));
        if (sig != _appsSig)
        {
            _appsSig = sig;
            _rows.Clear();
            _apps.Children.Clear();
            foreach (var a in apps) _apps.Children.Add(MakeRow(a));
            _appsHeader.Visibility = apps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        foreach (var a in apps)
        {
            var r = _rows.FirstOrDefault(x => x.Pid == a.Pid);
            if (r == null) continue;
            if (!r.Slider.IsMouseCaptured) r.Slider.SetValue(a.Volume);
            r.Value.Text = a.Volume.ToString();
        }
    }

    private FrameworkElement MakeRow(AudioApp a)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        FrameworkElement icon;
        ImageSource? src = null;
        try { if (a.ExePath != null) src = IconHelper.Get(a.ExePath, 48); } catch { }
        icon = src != null
            ? UiKit.Icon(src, 32)
            : new Border { Width = 32, Height = 32, CornerRadius = _t.Corner(8), Background = _t.Hover, Child = UiKit.Glyph("", 15, _t.TextSecondary) };
        icon.Margin = new Thickness(4, 0, 10, 0);
        g.Children.Add(icon);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(UiKit.Label(a.Name, 14, _t.Text, FontWeights.SemiBold));
        names.Children.Add(UiKit.Label(a.Active ? L.T("再生中") : L.T("停止中"), 12, _t.TextSecondary));
        Grid.SetColumn(names, 1);
        g.Children.Add(names);

        var slider = new FluentSlider(0, 100, a.Volume) { Width = double.NaN, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(6, 0, 6, 0) };
        var val = UiKit.Label(a.Volume.ToString(), 14, _t.TextSecondary);
        val.TextTrimming = TextTrimming.None;
        val.HorizontalAlignment = HorizontalAlignment.Right;
        uint pid = a.Pid;
        slider.ValueChanged += v =>
        {
            AudioControl.SetAppVolume(pid, (int)v);
            val.Text = ((int)v).ToString();
        };
        Grid.SetColumn(slider, 2);
        g.Children.Add(slider);
        Grid.SetColumn(val, 3);
        g.Children.Add(val);
        _rows.Add(new AppRow(pid, slider, val));
        return g;
    }

    private void ToggleDevices()
    {
        if (_devList.Visibility == Visibility.Visible)
        {
            _devList.Visibility = Visibility.Collapsed;
            _chevron.Text = "";
            return;
        }
        _devList.Children.Clear();
        foreach (var d in AudioControl.Devices())
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (d.IsDefault) row.Children.Add(UiKit.Glyph("", 13, _t.AccentBrush));
            var label = UiKit.Label(d.Name + (d.Sub == "" ? "" : " (" + d.Sub + ")"), 13);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var tile = new Tile { Padding = new Thickness(10, 8, 10, 8), Child = row, Margin = new Thickness(0, 1, 0, 1) };
            var id = d.Id;
            tile.Click += _ =>
            {
                AudioControl.SetDefault(id);
                _devList.Visibility = Visibility.Collapsed;
                _chevron.Text = "";
                _appsSig = "\0";
                Refresh();
            };
            _devList.Children.Add(tile);
        }
        _devList.Visibility = Visibility.Visible;
        _chevron.Text = "";
    }
}
