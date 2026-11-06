using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>
/// タスクビューボタンにカーソルを置いたときの仮想デスクトップ一覧（Windows 11 と同じ）。
/// 壁紙の縮小画像と名前を並べ、クリックで切り替え、「＋」で新しいデスクトップを作る。
/// </summary>
public sealed class DesktopsFlyout
{
    private readonly Settings _settings;
    private readonly HoverGate _gate;
    private Popup? _popup;

    public DesktopsFlyout(Settings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _gate = new HoverGate(dispatcher, Close);
    }

    public void Hover(FrameworkElement anchor, bool top)
    {
        if (!_settings.ShowDesktopsOnTaskView) return;
        if (_popup?.IsOpen == true) { _gate.KeepOpen(); return; }
        _gate.ShowAfter(_settings.PreviewDelayMs, () => Show(anchor, top));
    }

    public void Leave() => _gate.Leave(_popup?.IsOpen == true);

    public void Cancel()
    {
        _gate.Stop();
        Close();
    }

    private void Close()
    {
        _gate.Stop();
        if (_popup != null) { _popup.IsOpen = false; _popup = null; }
    }

    private void Show(FrameworkElement anchor, bool top)
    {
        Close();
        var t = Theme.Current;
        var desktops = VirtualDesktops.List();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        for (int i = 0; i < desktops.Count; i++)
        {
            var d = desktops[i];
            var img = new Image { Stretch = Stretch.UniformToFill };
            if (d.Wallpaper is { } wall)
                Task.Run(() => PackageInfo.LoadImage(wall, 320)).ContinueWith(r => { if (r.Result != null) img.Dispatcher.BeginInvoke(() => img.Source = r.Result); }, TaskScheduler.Default);
            var frame = new Border
            {
                Width = 160, Height = 90, CornerRadius = t.Corner(6), ClipToBounds = true, Background = t.Surface,
                BorderThickness = new Thickness(2), BorderBrush = d.IsCurrent ? t.AccentBrush : Brushes.Transparent,
                Child = img,
            };
            var label = UiKit.Label(d.Name, 12, t.Text, d.IsCurrent ? FontWeights.SemiBold : FontWeights.Normal);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.MaxWidth = 160;
            label.Margin = new Thickness(0, 6, 0, 0);
            var body = new StackPanel();
            body.Children.Add(frame);
            body.Children.Add(label);
            var card = new Tile { Padding = new Thickness(6), Margin = new Thickness(2), Child = body, ToolTip = d.Name };
            int index = i;
            card.Click += _ => { Cancel(); VirtualDesktops.SwitchTo(index); };
            row.Children.Add(card);
        }

        // 新しいデスクトップ
        var plusFrame = new Border
        {
            Width = 160, Height = 90, CornerRadius = t.Corner(6), Background = t.ControlFill,
            BorderBrush = t.Stroke, BorderThickness = new Thickness(1), Child = UiKit.Glyph("", 20),
        };
        var plusLabel = UiKit.Label("新しいデスクトップ", 12);
        plusLabel.HorizontalAlignment = HorizontalAlignment.Center;
        plusLabel.Margin = new Thickness(0, 6, 0, 0);
        var plusBody = new StackPanel();
        plusBody.Children.Add(plusFrame);
        plusBody.Children.Add(plusLabel);
        var plus = new Tile { Padding = new Thickness(6), Margin = new Thickness(2), Child = plusBody };
        plus.Click += _ => { Cancel(); VirtualDesktops.Create(); };
        row.Children.Add(plus);

        _popup = FluentSurface.Create(row, anchor, PlacementMode.Custom);
        _popup.StaysOpen = true;
        FluentSurface.PlaceCentered(_popup, anchor, top);
        _gate.Attach(row);
        if (_settings.AnimatePreviews) FluentSurface.SlideIn(_popup, fromBelow: !top);
        _popup.IsOpen = true;
    }
}
