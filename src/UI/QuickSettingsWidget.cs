using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>
/// クイック設定のウィジェット: ネットワーク（Wi-Fi / 有線 / 機内モード）・Bluetooth（つながっているとき）・音量を 1 つのカードにまとめる。
/// オンにすると、バー標準のクイック設定のボタンの代わりになる。クリックで Windows のクイック設定（Win+A）、ホイールで音量。
/// </summary>
public sealed class QuickSettingsBarWidget : BarWidget
{
    public override string Id => "quicksettings";

    // Segoe Fluent Icons
    private const string GlyphWifi = "", GlyphWired = "", GlyphAirplane = "", GlyphBluetooth = "", GlyphMuted = "";
    private static readonly Brush Warn = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x5E)));

    private TextBlock? _bt, _net, _vol;
    private Line? _slash;
    private Border? _fill;
    private bool _busy;
    private string _netName = "";
    private const double BarLen = 22, BarThick = 6;

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    protected override FrameworkElement BuildContent()
    {
        double size = Vertical ? 15 : 17;
        _bt = UiKit.Glyph(GlyphBluetooth, size, T.Text);
        _bt.Visibility = Visibility.Collapsed;
        _net = UiKit.Glyph(GlyphWifi, size, T.Text);
        // つながっていないときは、アイコンに赤い斜線を重ねる
        _slash = new Line { X1 = 1, Y1 = 1, X2 = size - 1, Y2 = size - 1, Stroke = Warn, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = Visibility.Collapsed, Width = size, Height = size, IsHitTestVisible = false };
        var netGrid = new Grid { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        netGrid.Children.Add(_net);
        netGrid.Children.Add(_slash);
        _vol = UiKit.Glyph(VolumeGlyph.For(50, false), size, T.Text);

        // 音量バー: 細い丸みのあるトラックを、下から（縦のバーでは左から）アクセント色で満たす
        _fill = new Border
        {
            CornerRadius = new CornerRadius(BarThick / 2), Background = T.AccentBrush,
            VerticalAlignment = Vertical ? VerticalAlignment.Stretch : VerticalAlignment.Bottom,
            HorizontalAlignment = Vertical ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
        };
        var bar = new Border
        {
            CornerRadius = new CornerRadius(BarThick / 2), Background = Track, Child = _fill,
            Width = Vertical ? 20 : BarThick, Height = Vertical ? BarThick : BarLen,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };

        var row = new StackPanel { Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        Thickness gap = Vertical ? new Thickness(0, 5, 0, 0) : new Thickness(12, 0, 0, 0);
        netGrid.Margin = gap; _vol.Margin = gap;
        bar.Margin = Vertical ? new Thickness(0, 5, 0, 0) : new Thickness(8, 0, 0, 0);
        _bt.Margin = new Thickness(0);
        row.Children.Add(_bt);
        row.Children.Add(netGrid);
        row.Children.Add(_vol);
        row.Children.Add(bar);

        row.Loaded += (_, _) =>
        {
            NetworkControl.Hook(); NetworkControl.Changed += OnOsEvent;
            if (Card != null) { Card.MouseWheel -= OnWheel; Card.MouseWheel += OnWheel; }
            _ = RefreshAsync();
        };
        row.Unloaded += (_, _) => NetworkControl.Changed -= OnOsEvent;

        ApplyVolume();
        if (Vertical) return row;
        row.Margin = new Thickness(12, 0, 12, 0);
        PadOverride = new Thickness(0);
        return Frame(row);
    }

    private void OnOsEvent()
    {
        try { Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync())); } catch { }
    }

    public override void Tick(int count)
    {
        ApplyVolume();
        if (count % 4 == 0) _ = RefreshAsync();
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_busy || _net == null || _slash == null || _bt == null) return;
        _busy = true;
        try
        {
            var s = await NetworkControl.GetStatusAsync();
            bool airplane = await RadioControl.AirplaneAsync();
            bool bt = !airplane && await RadioControl.BluetoothConnectedAsync();
            ApplyNetwork(s, airplane, bt);
        }
        catch (Exception ex) { Log.Error("quick settings widget", ex); }
        finally { _busy = false; }
    }

    private void ApplyNetwork(NetStatus s, bool airplane, bool bluetooth)
    {
        if (_net == null || _slash == null || _bt == null) return;
        _bt.Visibility = bluetooth ? Visibility.Visible : Visibility.Collapsed;
        bool slash = false;
        double opacity = 1;
        string glyph;
        if (airplane) { glyph = GlyphAirplane; _netName = L.T("機内モード"); }
        else if (s.Connected && s.Wired) { glyph = GlyphWired; _netName = s.Name; }
        else if (s.Connected) { glyph = GlyphWifi; _netName = s.Name; opacity = s.Bars <= 1 ? 0.45 : 1; } // 電波が弱いときは薄く
        else
        {
            // つながっていない: Wi-Fi の無い PC（有線のみ）は有線のアイコン、あるときは Wi-Fi のアイコンに斜線
            glyph = s.HasWifi ? GlyphWifi : GlyphWired;
            slash = true;
            opacity = 0.6;
            _netName = s.HasWifi && !s.RadioOn ? L.T("Wi-Fi オフ") : L.T("未接続");
        }
        _net.Text = glyph;
        _net.Opacity = opacity;
        _slash.Visibility = slash ? Visibility.Visible : Visibility.Collapsed;
        UpdateTip();
    }

    private void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        int step = Math.Sign(e.Delta) * 2;
        if (step == 0) return;
        if (AudioControl.GetMuted()) AudioControl.SetMuted(false);
        AudioControl.SetVolume(AudioControl.GetVolume() + step);
        ApplyVolume();
        e.Handled = true;
    }

    private void ApplyVolume()
    {
        if (_vol == null || _fill == null) return;
        bool avail = AudioControl.Available;
        int v = avail ? AudioControl.GetVolume() : 0;
        bool m = avail && AudioControl.GetMuted();
        _vol.Text = avail ? VolumeGlyph.For(v, m) : GlyphMuted;
        double len = (Vertical ? 20 : BarLen) * (m || !avail ? 0 : v) / 100.0;
        if (Vertical) _fill.Width = len; else _fill.Height = len;
        _fill.Opacity = len <= 0 ? 0 : 1;
        UpdateTip();
    }

    private void UpdateTip()
    {
        if (Card == null) return;
        bool m = AudioControl.Available && AudioControl.GetMuted();
        string vol = m ? L.T("ミュート中") : L.F("音量: {0}%", AudioControl.Available ? AudioControl.GetVolume() : 0);
        Card.ToolTip = L.T("クイック設定 (Win+A)") + (_netName.Length > 0 ? "\n" + _netName : "") + "\n" + vol;
    }

    public override bool OpensSystemPanel => true;

    public override bool OnClick()
    {
        // タスクバーを先に前面にしてから Win+A を送る（管理者として動くアプリが前面だと、キー操作が届かないため）
        if (Outer != null) TaskbarController.TakeForeground(Outer);
        SystemActions.QuickSettings();
        return true;
    }

    public override IEnumerable<MenuItemSpec> ExtraMenu() => new[]
    {
        new MenuItemSpec("", L.T("クイック設定を開く"), () => OnClick()),
        new MenuItemSpec(AudioControl.GetMuted() ? "" : "", AudioControl.GetMuted() ? L.T("ミュートを解除") : L.T("ミュート"), () => { AudioControl.SetMuted(!AudioControl.GetMuted()); ApplyVolume(); }),
        new MenuItemSpec("", L.T("ネットワークとインターネットの設定"), () => Launcher.Open("ms-settings:network")),
        new MenuItemSpec("", L.T("サウンドの設定"), () => Launcher.Open("ms-settings:sound")),
        MenuItemSpec.Separator,
    };
}
