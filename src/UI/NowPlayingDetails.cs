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
        Width = 380;
        Padding = new Thickness(20, 18, 20, 14);
        TextElement.SetForeground(this, t.Text);
        var col = new StackPanel();
        for (int i = 0; i < sessions.Count; i++)
        {
            if (i > 0) col.Children.Add(new Border { Height = 1, Background = t.Hover, Margin = new Thickness(0, 14, 0, 14) });
            col.Children.Add(new NowPlayingCard(sessions[i]));
        }
        Child = col;
    }
}

/// <summary>
/// 再生中の 1 つぶん: ジャケット・曲名・アーティスト・波形シーク・シークバー・操作ボタン（シャッフル/前/再生/次/リピート）。
/// 開いている間だけ、曲や再生状態の変化を受け取り、1 秒ごとに再生位置を進める。
/// </summary>
internal sealed class NowPlayingCard : Border
{
    private const int WaveBars = 48;
    private const string RepeatAll = "", RepeatOne = "";
    private static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
    private readonly WMC.GlobalSystemMediaTransportControlsSession _s;
    private readonly Theme _t = Theme.Current;
    private readonly TextBlock _title, _artist, _pos, _dur, _playGlyph, _shuffleGlyph, _repeatGlyph;
    private readonly Border _liveChip, _cover, _fill, _track, _playhead, _playBtn, _shuffleBtn, _repeatBtn;
    private readonly Border[] _bars = new Border[WaveBars];
    private readonly Grid _waveHost;
    private readonly StackPanel _middle;
    private readonly DispatcherTimer _timer;
    private TimeSpan _start, _end;
    private bool _playing;
    private int _barsPlayed = -1;
    private string _waveKey = "";

    public NowPlayingCard(WMC.GlobalSystemMediaTransportControlsSession session)
    {
        _s = session;

        var note = UiKit.Glyph("", 22, _t.TextSecondary);
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.VerticalAlignment = VerticalAlignment.Center;
        _cover = new Border { Width = 72, Height = 72, CornerRadius = _t.Corner(10), Background = _t.Hover, Child = note, ClipToBounds = true };

        var label = UiKit.Label(L.T("再生中"), 11, _t.TextSecondary);
        _title = UiKit.Label("", 18, _t.Text, FontWeights.Bold);
        _artist = UiKit.Label("", 13, _t.TextSecondary);
        _title.TextTrimming = _artist.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.MaxWidth = _artist.MaxWidth = 252;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        text.Children.Add(label);
        text.Children.Add(_title);
        text.Children.Add(_artist);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(_cover);
        head.Children.Add(text);

        // 波形（曲の中身ではなく、曲ごとに決まる飾りの形）。再生済みはアクセント色、白い縦線が現在位置。クリックでその位置へ移動
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = WaveBars };
        for (int i = 0; i < WaveBars; i++)
        {
            _bars[i] = new Border { Margin = new Thickness(1, 0, 1, 0), CornerRadius = new CornerRadius(2), Background = Dim, VerticalAlignment = VerticalAlignment.Center, Height = 6 };
            grid.Children.Add(_bars[i]);
        }
        _playhead = new Border { Width = 2, CornerRadius = new CornerRadius(1), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        _waveHost = new Grid { Height = 56, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        _waveHost.Children.Add(grid);
        _waveHost.Children.Add(_playhead);
        _waveHost.MouseLeftButtonUp += (_, e) => SeekTo(e.GetPosition(_waveHost).X, _waveHost.ActualWidth);
        _waveHost.SizeChanged += (_, _) => UpdatePosition();

        _fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = _t.AccentBrush, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = _t.Hover, Child = _fill, ClipToBounds = true };
        // 細いバーはつかみにくいので、見えない当たり判定を上下に広げる
        var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 7, 0, 7), Child = _track, Cursor = Cursors.Hand };
        hit.MouseLeftButtonUp += (_, e) => SeekTo(e.GetPosition(_track).X, _track.ActualWidth);
        _pos = UiKit.Label("0:00", 12, _t.TextSecondary);
        _dur = UiKit.Label("0:00", 12, _t.TextSecondary);
        var times = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(_dur, Dock.Right);
        times.Children.Add(_dur);
        times.Children.Add(_pos);

        _liveChip = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 1), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 14, 0, 4), Visibility = Visibility.Collapsed, Child = UiKit.Label("LIVE", 11, Brushes.White, FontWeights.Bold),
        };
        _middle = new StackPanel { Margin = new Thickness(0, 14, 0, 6) }; // ライブ配信のときは、波形・シークバーごと隠す
        _middle.Children.Add(_waveHost);
        _middle.Children.Add(hit);
        _middle.Children.Add(times);

        // 操作ボタン: シャッフル / 前 / 再生（アクセント色の大きな丸）/ 次 / リピート
        _shuffleBtn = MakeButton("", out _shuffleGlyph, 44, 15, _t.TextSecondary, null, ToggleShuffle);
        var prev = MakeButton("", out _, 44, 15, _t.Text, null, () => _ = _s.TrySkipPreviousAsync());
        _playBtn = MakeButton("", out _playGlyph, 56, 20, _t.OnAccent, _t.AccentBrush, () =>
        {
            _ = _s.TryTogglePlayPauseAsync();
            _playing = !_playing; // 実際の状態が届くまでの間、先に見た目を変える
            ShowPlaying();
        });
        var next = MakeButton("", out _, 44, 15, _t.Text, null, () => _ = _s.TrySkipNextAsync());
        _repeatBtn = MakeButton(RepeatAll, out _repeatGlyph, 44, 15, _t.TextSecondary, null, CycleRepeat);
        _shuffleBtn.ToolTip = L.T("シャッフル");
        prev.ToolTip = L.T("前の曲");
        next.ToolTip = L.T("次の曲");
        _repeatBtn.ToolTip = L.T("リピート");
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var b in new[] { _shuffleBtn, prev, _playBtn, next, _repeatBtn }) { b.Margin = new Thickness(5, 0, 5, 0); controls.Children.Add(b); }

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(_liveChip);
        body.Children.Add(_middle);
        body.Children.Add(controls);
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

    /// <summary>丸いボタン。bg が null なら、ふだんは透明（マウスを載せると薄く色が付く）。</summary>
    private Border MakeButton(string glyph, out TextBlock g, double size, double glyphSize, Brush fg, Brush? bg, Action act)
    {
        g = UiKit.Glyph(glyph, glyphSize, fg);
        g.HorizontalAlignment = HorizontalAlignment.Center;
        g.VerticalAlignment = VerticalAlignment.Center;
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = bg ?? Brushes.Transparent, Child = g, Cursor = Cursors.Hand };
        if (bg == null)
        {
            b.MouseEnter += (_, _) => b.Background = _t.Hover;
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        }
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try { act(); } catch (Exception ex) { Log.Error("media control", ex); }
        };
        return b;
    }

    private void Subscribe(bool on)
    {
        try
        {
            if (on) { _s.MediaPropertiesChanged += OnProps; _s.TimelinePropertiesChanged += OnTimeline; _s.PlaybackInfoChanged += OnPlayback; }
            else { _s.MediaPropertiesChanged -= OnProps; _s.TimelinePropertiesChanged -= OnTimeline; _s.PlaybackInfoChanged -= OnPlayback; }
        }
        catch (Exception ex) { Log.Error("now playing subscribe", ex); }
    }

    private void OnProps(WMC.GlobalSystemMediaTransportControlsSession s, WMC.MediaPropertiesChangedEventArgs e) => Dispatcher.BeginInvoke(() => _ = LoadAsync());
    private void OnTimeline(WMC.GlobalSystemMediaTransportControlsSession s, WMC.TimelinePropertiesChangedEventArgs e) => Dispatcher.BeginInvoke(UpdatePosition);
    private void OnPlayback(WMC.GlobalSystemMediaTransportControlsSession s, WMC.PlaybackInfoChangedEventArgs e) => Dispatcher.BeginInvoke(UpdatePosition);

    /// <summary>曲名・アーティスト・ジャケットを読み込む（曲が変わったときも）。</summary>
    private async System.Threading.Tasks.Task LoadAsync()
    {
        try
        {
            var props = await _s.TryGetMediaPropertiesAsync();
            if (props == null) return;
            _title.Text = props.Title ?? "";
            string album = props.AlbumTitle ?? "", artist = props.Artist ?? "";
            _artist.Text = artist.Length > 0 && album.Length > 0 ? artist + " ・ " + album : artist + album;
            _artist.Visibility = _artist.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            BuildWave(_title.Text + "|" + artist);
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

    /// <summary>波形の棒の高さを、曲ごとに決める（同じ曲なら、いつも同じ形）。</summary>
    private void BuildWave(string key)
    {
        if (key == _waveKey) return;
        _waveKey = key;
        int seed = 17;
        foreach (char c in key) seed = unchecked(seed * 31 + c);
        double ph = (seed & 0xFF) / 40.0;
        for (int i = 0; i < WaveBars; i++)
        {
            double t = i / (double)(WaveBars - 1);
            double env = 0.35 + 0.65 * Math.Sin(Math.PI * Math.Min(1, t * 1.15 + 0.04));
            double wob = 0.55 + 0.45 * Math.Abs(Math.Sin(i * 0.9 + ph) * Math.Cos(i * 0.31 + 1 + ph));
            _bars[i].Height = Math.Max(6, Math.Round(52 * env * wob));
        }
    }

    private void ShowPlaying()
    {
        _playGlyph.Text = _playing ? "" : "";
        _playBtn.ToolTip = _playing ? L.T("一時停止") : L.T("再生");
    }

    private void UpdatePosition()
    {
        try
        {
            _playing = MediaControl.IsPlaying(_s);
            ShowPlaying();
            UpdateModes();
            var tl = _s.GetTimelineProperties();
            _start = tl.StartTime; _end = tl.EndTime;
            var total = _end - _start;
            // ライブ配信など長さの無いものは、波形・シークバーの代わりに LIVE を出す
            bool noDuration = MediaControl.HasNoDuration(_start, _end);
            _middle.Visibility = noDuration ? Visibility.Collapsed : Visibility.Visible;
            _liveChip.Visibility = MediaControl.IsLive(_title.Text + "|" + _artist.Text, _start, _end, _playing, tl.Position) ? Visibility.Visible : Visibility.Collapsed;
            if (noDuration) return;
            var pos = tl.Position;
            if (_playing) pos += DateTimeOffset.Now - tl.LastUpdatedTime; // 最後に報告された位置から、経過した分だけ進める
            if (pos < _start) pos = _start;
            if (pos > _end) pos = _end;
            double f = (pos - _start).TotalSeconds / total.TotalSeconds;
            _pos.Text = Fmt(pos - _start);
            _dur.Text = Fmt(total);
            ShowFraction(f);
        }
        catch { }
    }

    /// <summary>再生位置（0〜1）を、シークバー・波形・白い縦線に反映する。</summary>
    private void ShowFraction(double f)
    {
        _fill.Width = Math.Max(0, _track.ActualWidth * f);
        _playhead.Margin = new Thickness(Math.Max(0, _waveHost.ActualWidth * f - 1), 0, 0, 0);
        int played = (int)Math.Round(f * WaveBars);
        if (played == _barsPlayed) return; // 色が変わる棒がなければ、塗り直さない
        for (int i = 0; i < WaveBars; i++) _bars[i].Background = i < played ? _t.AccentBrush : Dim;
        _barsPlayed = played;
    }

    /// <summary>シャッフル・リピートの状態（入れているときはアクセント色。使えない配信元では薄く）。</summary>
    private void UpdateModes()
    {
        var info = _s.GetPlaybackInfo();
        var c = info?.Controls;
        var mode = info?.AutoRepeatMode ?? global::Windows.Media.MediaPlaybackAutoRepeatMode.None;
        _shuffleGlyph.Foreground = info?.IsShuffleActive == true ? _t.AccentBrush : _t.TextSecondary;
        _shuffleBtn.Opacity = c?.IsShuffleEnabled == false ? 0.4 : 1;
        _repeatGlyph.Text = mode == global::Windows.Media.MediaPlaybackAutoRepeatMode.Track ? RepeatOne : RepeatAll;
        _repeatGlyph.Foreground = mode == global::Windows.Media.MediaPlaybackAutoRepeatMode.None ? _t.TextSecondary : _t.AccentBrush;
        _repeatBtn.Opacity = c?.IsRepeatEnabled == false ? 0.4 : 1;
    }

    private void ToggleShuffle()
    {
        var info = _s.GetPlaybackInfo();
        if (info?.Controls?.IsShuffleEnabled == false) return;
        _ = _s.TryChangeShuffleActiveAsync(info?.IsShuffleActive != true);
    }

    /// <summary>リピート: なし → 全曲 → 1 曲 → なし。</summary>
    private void CycleRepeat()
    {
        var info = _s.GetPlaybackInfo();
        if (info?.Controls?.IsRepeatEnabled == false) return;
        var next = (info?.AutoRepeatMode ?? global::Windows.Media.MediaPlaybackAutoRepeatMode.None) switch
        {
            global::Windows.Media.MediaPlaybackAutoRepeatMode.None => global::Windows.Media.MediaPlaybackAutoRepeatMode.List,
            global::Windows.Media.MediaPlaybackAutoRepeatMode.List => global::Windows.Media.MediaPlaybackAutoRepeatMode.Track,
            _ => global::Windows.Media.MediaPlaybackAutoRepeatMode.None,
        };
        _ = _s.TryChangeAutoRepeatModeAsync(next);
    }

    private void SeekTo(double x, double width)
    {
        try
        {
            if (_s.GetPlaybackInfo()?.Controls?.IsPlaybackPositionEnabled != true || width <= 0) return;
            double f = Math.Clamp(x / width, 0, 1);
            _ = _s.TryChangePlaybackPositionAsync((_start + (_end - _start) * f).Ticks);
            ShowFraction(f);
        }
        catch (Exception ex) { Log.Error("now playing seek", ex); }
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
