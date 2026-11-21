using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ModernShell.Core;
using WMC = global::Windows.Media.Control;

namespace ModernShell.UI;

/// <summary>「前の曲・再生/一時停止・次の曲」の 3 つのボタン（再生中の曲のポップアップと、ウィンドウのプレビューで共通）。</summary>
internal static class MediaButtons
{
    public static FrameworkElement Build(WMC.GlobalSystemMediaTransportControlsSession session, Theme t, double width = 38, double height = 30)
    {
        bool playing = MediaControl.IsPlaying(session);
        WMC.GlobalSystemMediaTransportControlsSessionPlaybackControls? c = null;
        try { c = session.GetPlaybackInfo()?.Controls; } catch { }

        Tile Make(string glyph, string tip, bool enabled, Action act)
        {
            var g = UiKit.Glyph(glyph, 13, t.Text);
            g.HorizontalAlignment = HorizontalAlignment.Center;
            g.VerticalAlignment = VerticalAlignment.Center;
            var tile = new Tile { Width = width, Height = height, Margin = new Thickness(2, 0, 2, 0), CornerRadius = t.Corner(6), Child = g, ToolTip = tip, Opacity = enabled ? 1 : 0.4, IsEnabled = enabled };
            tile.Click += _ => { try { act(); } catch (Exception ex) { Log.Error("media control", ex); } };
            return tile;
        }

        var play = Make(playing ? "" : "", playing ? L.T("一時停止") : L.T("再生"), c?.IsPlayPauseToggleEnabled ?? true, () => { });
        var glyph = (TextBlock)play.Child;
        play.Click += tile =>
        {
            _ = session.TryTogglePlayPauseAsync();
            playing = !playing;
            glyph.Text = playing ? "" : "";
            play.ToolTip = playing ? L.T("一時停止") : L.T("再生");
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(Make("", L.T("前の曲"), c?.IsPreviousEnabled ?? true, () => _ = session.TrySkipPreviousAsync()));
        row.Children.Add(play);
        row.Children.Add(Make("", L.T("次の曲"), c?.IsNextEnabled ?? true, () => _ = session.TrySkipNextAsync()));
        return row;
    }
}

/// <summary>再生中の曲のポップアップ。再生中のものが複数あれば、全部を縦に並べる（無ければ、バーに出しているもの 1 つ）。</summary>
public sealed class NowPlayingDetailsView : Border
{
    internal NowPlayingDetailsView(IReadOnlyList<WMC.GlobalSystemMediaTransportControlsSession> sessions)
    {
        var t = Theme.Current;
        Width = 320;
        Padding = new Thickness(16, 14, 16, 12);
        TextElement.SetForeground(this, t.Text);
        var col = new StackPanel();
        for (int i = 0; i < sessions.Count; i++)
        {
            if (i > 0) col.Children.Add(new Border { Height = 1, Background = t.Hover, Margin = new Thickness(0, 12, 0, 12) });
            col.Children.Add(new NowPlayingCard(sessions[i]));
        }
        Child = col;
    }
}

/// <summary>
/// 再生中の 1 つぶん: ジャケット・曲名・アーティスト・シークバー・操作ボタン。
/// 開いている間だけ、曲や再生状態の変化を受け取り、1 秒ごとに再生位置を進める。
/// </summary>
internal sealed class NowPlayingCard : Border
{
    private readonly WMC.GlobalSystemMediaTransportControlsSession _s;
    private readonly Theme _t = Theme.Current;
    private readonly TextBlock _title, _artist, _pos, _dur;
    private readonly Border _liveChip;
    private readonly Border _cover, _fill, _track;
    private readonly FrameworkElement _seekRow;
    private readonly DispatcherTimer _timer;
    private TimeSpan _start, _end;
    private bool _playing;

    public NowPlayingCard(WMC.GlobalSystemMediaTransportControlsSession session)
    {
        _s = session;

        var note = UiKit.Glyph("", 22, _t.TextSecondary);
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.VerticalAlignment = VerticalAlignment.Center;
        _cover = new Border { Width = 72, Height = 72, CornerRadius = _t.Corner(8), Background = _t.Hover, Child = note, ClipToBounds = true };

        _title = UiKit.Label("", 15, _t.Text, FontWeights.SemiBold);
        _artist = UiKit.Label("", 12, _t.TextSecondary);
        _title.TextTrimming = _artist.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.MaxWidth = _artist.MaxWidth = 210;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(_title);
        text.Children.Add(_artist);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(_cover);
        head.Children.Add(text);

        _fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = _t.AccentBrush, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = _t.Hover, Child = _fill, ClipToBounds = true };
        // 細いバーはつかみにくいので、見えない当たり判定を上下に広げる
        var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 7, 0, 7), Child = _track, Cursor = Cursors.Hand };
        hit.MouseLeftButtonUp += OnSeek;
        _pos = UiKit.Label("0:00", 11, _t.TextSecondary);
        _dur = UiKit.Label("0:00", 11, _t.TextSecondary);
        var times = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(_dur, Dock.Right);
        times.Children.Add(_dur);
        times.Children.Add(_pos);
        var seek = new StackPanel();
        seek.Children.Add(hit);
        seek.Children.Add(times);
        _seekRow = seek;
        _seekRow.Margin = new Thickness(0, 10, 0, 4);

        _liveChip = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 1), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 4), Visibility = Visibility.Collapsed, Child = UiKit.Label("LIVE", 11, Brushes.White, FontWeights.Bold),
        };
        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(_liveChip);
        body.Children.Add(_seekRow);
        body.Children.Add(MediaButtons.Build(_s, _t));
        Child = body;

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdatePosition(), Dispatcher);
        Loaded += (_, _) =>
        {
            Subscribe(true);
            _ = LoadAsync();
            UpdatePosition();
            _timer.Start();
        };
        Unloaded += (_, _) => { _timer.Stop(); Subscribe(false); };
    }

    private void Subscribe(bool on)
    {
        try
        {
            if (on) { _s.MediaPropertiesChanged += OnProps; _s.TimelinePropertiesChanged += OnTimeline; }
            else { _s.MediaPropertiesChanged -= OnProps; _s.TimelinePropertiesChanged -= OnTimeline; }
        }
        catch (Exception ex) { Log.Error("now playing subscribe", ex); }
    }

    private void OnProps(WMC.GlobalSystemMediaTransportControlsSession s, WMC.MediaPropertiesChangedEventArgs e) => Dispatcher.BeginInvoke(() => _ = LoadAsync());
    private void OnTimeline(WMC.GlobalSystemMediaTransportControlsSession s, WMC.TimelinePropertiesChangedEventArgs e) => Dispatcher.BeginInvoke(UpdatePosition);

    /// <summary>曲名・アーティスト・ジャケットを読み込む（曲が変わったときも）。</summary>
    private async System.Threading.Tasks.Task LoadAsync()
    {
        try
        {
            var props = await _s.TryGetMediaPropertiesAsync();
            if (props == null) return;
            _title.Text = props.Title ?? "";
            _artist.Text = props.Artist ?? "";
            _artist.Visibility = _artist.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            await LoadCoverAsync(props);
        }
        catch (Exception ex) { Log.Error("now playing load", ex); }
    }

    private async System.Threading.Tasks.Task LoadCoverAsync(WMC.GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        var bmp = await MediaControl.LoadThumbnailAsync(props, 160); // 表示は 72px
        if (bmp == null) return; // ジャケットが読めないときは、音符のまま
        _cover.Child = null;
        _cover.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
    }

    private void UpdatePosition()
    {
        try
        {
            _playing = MediaControl.IsPlaying(_s);
            var tl = _s.GetTimelineProperties();
            _start = tl.StartTime; _end = tl.EndTime;
            var total = _end - _start;
            // ライブ配信など長さの無いものは、シークバーの代わりに LIVE を出す
            bool noDuration = MediaControl.HasNoDuration(_start, _end);
            _seekRow.Visibility = noDuration ? Visibility.Collapsed : Visibility.Visible;
            _liveChip.Visibility = MediaControl.IsLive(_title.Text + "|" + _artist.Text, _start, _end, _playing, tl.Position) ? Visibility.Visible : Visibility.Collapsed;
            if (noDuration) return;
            var pos = tl.Position;
            if (_playing) pos += DateTimeOffset.Now - tl.LastUpdatedTime; // 最後に報告された位置から、経過した分だけ進める
            if (pos < _start) pos = _start;
            if (pos > _end) pos = _end;
            double f = (pos - _start).TotalSeconds / total.TotalSeconds;
            _fill.Width = Math.Max(0, _track.ActualWidth * f);
            _pos.Text = Fmt(pos - _start);
            _dur.Text = Fmt(total);
        }
        catch { }
    }

    private void OnSeek(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_s.GetPlaybackInfo()?.Controls?.IsPlaybackPositionEnabled != true || _track.ActualWidth <= 0) return;
            double f = Math.Clamp(e.GetPosition(_track).X / _track.ActualWidth, 0, 1);
            _ = _s.TryChangePlaybackPositionAsync((_start + (_end - _start) * f).Ticks);
            _fill.Width = _track.ActualWidth * f;
        }
        catch (Exception ex) { Log.Error("now playing seek", ex); }
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
