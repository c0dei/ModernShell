using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ModernShell.Core;
using ModernShell.Native;
using WMC = global::Windows.Media.Control;

namespace ModernShell.UI;

/// <summary>
/// 再生中の曲（Windows のメディア セッション。ブラウザー・Spotify・音楽アプリなど、システムの音量パネルに出るもの）。
/// 見た目は設定で選ぶ:
///  標準 … サムネイル + 曲名・アーティスト + 動く波形 + 下線の再生位置
///  リング … 再生位置の輪で囲んだ丸いサムネイル + 波形（文字なし）。縦のバーは幅が狭いので、常にこの形（波形は輪の下）
/// ライブ配信（Twitch など、長さの無いもの）は、再生位置の代わりに LIVE を出す。
/// </summary>
public sealed class NowPlayingBarWidget : BarWidget
{
    public override string Id => "nowplaying";

    private Border? _cover, _fill, _underline, _liveChip, _playBtn, _film;
    private TextBlock? _playGlyph;
    private Ring? _ring;
    private Border[]? _bars;
    // 波線の再生位置（Line のときは使わない）
    private Canvas? _waveHost, _waveClip, _accHost;
    private System.Windows.Shapes.Ellipse? _dot;
    private double _waveMid;
    private const double WaveAmp = 1.8, DotSize = 6;
    private System.Windows.Shapes.Path? _waveDim, _waveAcc;
    private TranslateTransform? _tWave;
    private const double WavePeriod = 20; // 波 1 つの長さ（px）。長いほどゆるやか
    private double _waveBuiltFor;
    private TextBlock? _title, _artist;
    private WMC.GlobalSystemMediaTransportControlsSession? _session;
    private string _titleText = "", _artistText = "", _trackKey = "", _lastLogged = "";
    private bool _playing, _busy, _live, _again;
    private bool _showThumb = true, _showProgress = true, _singleLine, _coverLoaded, _coverLoading;
    private double _ringPct = -1;
    private TimeSpan _start, _end, _pos0;
    private DateTimeOffset _posAt;
    private string _goodKey = ""; // 長さが取れていた曲（title|artist）。シーク中の一時的な「長さなし」を無視するのに使う
    private DispatcherTimer? _anim;
    private double _phase;
    // 棒 3 本の動き。ゆっくり（約 0.4〜0.6 回/秒）・位相をずらして、落ち着いた見た目にする
    private static readonly double[] Freq = { 0.55, 0.85, 0.65 }, Offs = { 0, 2.1, 4.0 };

    private TextBlock Note()
    {
        var note = UiKit.Glyph("", 14, T.TextSecondary);
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.VerticalAlignment = VerticalAlignment.Center;
        return note;
    }

    protected override FrameworkElement BuildContent()
    {
        var s = Settings.Current ?? new Settings();
        bool ringStyle = Vertical || s.NowPlayingStyle == NowPlayingStyle.Ring;
        bool overlay = s.NowPlayingWavePlacement == NowPlayingWavePlacement.Overlay;
        bool tiny = !Vertical && TileH < 28;               // 低いバー（24px 相当）: サムネイルと 1 行の曲名と再生位置だけ。操作はホバーで出す
        bool wave = s.NowPlayingWave && !(tiny && !ringStyle);
        _showThumb = s.NowPlayingThumbnail;
        _showProgress = s.NowPlayingProgress;
        var tc = T.Text is SolidColorBrush sb ? sb.Color : Colors.White;
        var track = new SolidColorBrush(Color.FromArgb(60, tc.R, tc.G, tc.B));

        // バーの高さに合わせる。低いバーでは、下線の分の余白をとり、サムネイルを小さくし、曲名とアーティストを 1 行にする
        bool underline = !ringStyle && _showProgress;
        double lineH = TileH < 40 ? 2 : 3;                  // 直線の太さ
        double waveH = 6;                                   // 波線の高さ
        double reserve = underline ? waveH + 3 : 0;         // 下線のために、中身の下に空ける高さ
        double room = Vertical ? 0 : TileH - reserve;       // 中身に使える高さ
        _singleLine = !Vertical && TileH < 32;              // デザインの 32px 以上（バー高さの設定 38px 以上）は 2 行。それより低いと 1 行

        // サムネイル（読めるまで・無いときは音符）。標準の形は、バーの高さいっぱいの正方形。波形は、その中（下寄り）に重ねる
        FrameworkElement lead;
        double coverSize;       // サムネイルの大きさ（波形の棒の高さの目安）
        Grid? coverHost = null;
        if (ringStyle)
        {
            double ringSize = Vertical ? Math.Min(38, Math.Max(28, TileH - 10)) : Math.Clamp(TileH - 6, 22, 38);
            double thick = ringSize < 30 ? 2 : 3;
            _ring = new Ring(ringSize, thick, T.AccentBrush, track, T.Text, 10) { VerticalAlignment = VerticalAlignment.Center };
            double d = ringSize - thick * 2 - 4;
            coverSize = d;
            _cover = new Border { Width = d, Height = d, CornerRadius = new CornerRadius(d / 2), Background = T.Hover, Child = Note(), ClipToBounds = true };
            _ring.Children.Add(_cover);
            lead = _ring;
        }
        else
        {
            double cs = Math.Clamp(Math.Round(TileH * 0.67), 18, 32); // バーの高さの約 2/3（48px のバーで 32px）
            coverSize = cs;
            _cover = new Border { Width = cs, Height = cs, CornerRadius = T.Corner(cs < 28 ? 4 : 6), Background = T.Hover, Child = Note(), ClipToBounds = true };
            coverHost = new Grid { Width = cs, Height = cs, VerticalAlignment = VerticalAlignment.Center };
            coverHost.Children.Add(_cover);
            lead = coverHost;
        }

        // 標準の形は、左右 6px の余白で幅いっぱいに。下線の分だけ、中身を上げる
        var row = new StackPanel
        {
            Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = ringStyle ? HorizontalAlignment.Center : HorizontalAlignment.Left, Margin = ringStyle ? new Thickness(0) : new Thickness(6, 0, 6, underline ? 3 : 0),
        };
        row.Children.Add(lead);

        if (!ringStyle)
        {
            bool compact2 = !_singleLine && TileH < 40; // 2 行で高さに余裕がないときは、少し小さく
            _title = UiKit.Label("", _singleLine || compact2 ? 12 : 13, T.Text, FontWeights.Medium);
            _artist = UiKit.Label("", compact2 ? 10 : 11, T.TextSecondary);
            _title.MaxWidth = _artist.MaxWidth = Math.Clamp(s.NowPlayingTextWidth, 80, 400); // 設定の「曲名の最大幅」。超えた分は「…」
            _title.TextTrimming = _artist.TextTrimming = TextTrimming.CharacterEllipsis;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            text.Children.Add(_title);
            if (!_singleLine) text.Children.Add(_artist);
            row.Children.Add(text);
        }

        // LIVE（横のバーのみ。縦のバーは幅が足りないので、ツールチップだけ）
        if (!Vertical)
        {
            _liveChip = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Child = UiKit.Label("LIVE", 9, Brushes.White, FontWeights.Bold),
            };
            row.Children.Add(_liveChip);
        }

        // 動く波形（3 本の棒）。サムネイルの中に、暗い膜の上へ重ねる（ジャケットの明るさに関わらず見える）。一時停止中は、膜も棒も消す。
        // 高さは変えず、縦の倍率だけ変えるので、レイアウトのやり直しが起きない
        if (wave)
        {
            int n = Freq.Length;
            double h = ringStyle || overlay ? Math.Clamp(coverSize * 0.45, 7, 16) : Math.Clamp(room * 0.4, 8, 16);
            var eq = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, UseLayoutRounding = true, SnapsToDevicePixels = true };
            _bars = new Border[n];
            // 棒の幅と間隔は、物理ピクセルの整数にそろえる（端数だと、棒ごとに間隔が 1px 広い・狭いとずれて見える）
            double scale = Win32.GetDpiForSystem() is var dpiSys and > 0 ? dpiSys / 96.0 : 1.0;
            double barW = Math.Max(2, Math.Round((coverSize < 26 ? 2.5 : 3) * scale)) / scale, gapW = Math.Max(1, Math.Round(2 * scale)) / scale;
            for (int i = 0; i < n; i++)
            {
                _bars[i] = new Border
                {
                    Width = barW, Height = h, CornerRadius = new CornerRadius(1.5), UseLayoutRounding = true, SnapsToDevicePixels = true, Background = ringStyle || overlay ? Brushes.White : T.AccentBrush, Margin = new Thickness(i == 0 ? 0 : gapW, 0, 0, 0),
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 0.2),
                };
                eq.Children.Add(_bars[i]);
            }
            if (ringStyle)
            {
                var pill = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), CornerRadius = new CornerRadius(4), Padding = new Thickness(3, 2, 3, 2),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Child = eq,
                };
                _ring!.Children.Add(pill);
            }
            else if (!overlay)
            {
                eq.Margin = new Thickness(10, 0, 0, 0);
                eq.IsHitTestVisible = false;
                row.Children.Add(eq);
            }
            else
            {
                _film = new Border { Background = new SolidColorBrush(Color.FromArgb(140, 10, 12, 24)), CornerRadius = _cover!.CornerRadius, IsHitTestVisible = false, Child = eq, Visibility = Visibility.Collapsed };
                coverHost!.Children.Add(_film);
            }
        }

        // 再生/一時停止ボタン（標準の形・横のバー）。クリックはここで受け、詳細のポップアップは開かない
        if (!ringStyle)
        {
            double pb = Math.Clamp(Math.Round(TileH * 0.67), 18, 32);
            _playGlyph = UiKit.Glyph("", pb < 24 ? 9 : 12, T.Text);
            _playGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            _playGlyph.VerticalAlignment = VerticalAlignment.Center;
            _playBtn = new Border
            {
                Width = pb, Height = pb, CornerRadius = new CornerRadius(pb / 2), Background = T.Hover, Child = _playGlyph, Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), ToolTip = L.T("一時停止"),
            };
            _playBtn.MouseLeftButtonDown += (_, e) => e.Handled = true; // 外側のタイルに押下を渡さない
            _playBtn.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                try { _ = _session?.TryTogglePlayPauseAsync(); } catch (Exception ex) { Log.Error("now playing toggle", ex); }
            };
            row.Children.Add(_playBtn);
        }

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(row);
        if (tiny && _playBtn != null) // 低いバーでは、再生ボタンをふだんは隠し、マウスを載せたときだけ出す
        {
            var pbtn = _playBtn;
            pbtn.Visibility = Visibility.Collapsed;
            root.MouseEnter += (_, _) => pbtn.Visibility = Visibility.Visible;
            root.MouseLeave += (_, _) => pbtn.Visibility = Visibility.Collapsed;
        }

        // 再生位置の下線（標準の形のみ。リングは輪で出す）
        if (underline)
        {
            if (s.NowPlayingProgressStyle == NowPlayingProgressStyle.Wave)
            {
            // 波線: 薄い直線（全体）の上に、再生済みの長さだけアクセント色の波を重ねる。波は左へ流れ、再生済みの境目は動かない
            _tWave = new TranslateTransform();
            _waveDim = new System.Windows.Shapes.Path { Stroke = track, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, RenderTransform = _tWave };
            _waveAcc = new System.Windows.Shapes.Path { Stroke = T.AccentBrush, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, RenderTransform = _tWave };
            // Canvas に入れる: Canvas は中身の大きさを自分の大きさに含めない。Grid に入れると、波の図形（幅 + 1 周期）の分だけ枠が広がり、
            // 枠が広がると波を作り直して、さらに広がる…と際限なく伸びる（CPU も使い続ける）
            _accHost = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Height = waveH, ClipToBounds = true };
            _accHost.Children.Add(_waveAcc);
            // 波は、はみ出さないようクリップした入れ物に。現在位置の丸は、その外（波の山・谷で枠の少し外に出てもよい）
            _waveClip = new Canvas { Height = waveH, ClipToBounds = true };
            _waveClip.Children.Add(_waveDim);
            _waveClip.Children.Add(_accHost);
            _dot = new System.Windows.Shapes.Ellipse { Width = DotSize, Height = DotSize, Fill = T.AccentBrush };
            _waveMid = waveH / 2; // 波を作り直す前でも、丸が正しい高さに出る
            _waveHost = new Canvas { Height = waveH, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 0), Visibility = Visibility.Collapsed };
            _waveHost.Children.Add(_waveClip);
            _waveHost.Children.Add(_dot);
            _waveHost.SizeChanged += (_, e) => { if (_waveClip != null) _waveClip.Width = e.NewSize.Width; BuildWave(e.NewSize.Width, waveH); };
            root.Children.Add(_waveHost);
            }

            // 直線（再生済みの長さだけアクセント色）。波線のときは、一時停止中だけ出す
            _fill = new Border { Height = lineH, CornerRadius = new CornerRadius(lineH / 2), Background = T.AccentBrush, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            _underline = new Border
            {
                Height = lineH, CornerRadius = new CornerRadius(lineH / 2), Background = track, Child = _fill, ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2), Visibility = Visibility.Collapsed,
            };
            root.Children.Add(_underline);
        }

        // 標準の形は、薄い地と縁のある角丸のカード。下線が角からはみ出さないよう、角丸で切り抜く
        FrameworkElement result = root;
        if (!ringStyle && !Vertical)
        {
            var radius = T.Corner(6).TopLeft;
            root.SizeChanged += (_, e) => root.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
            result = new Border { Background = T.Hover, BorderBrush = T.Stroke, BorderThickness = new Thickness(1), CornerRadius = T.Corner(6), Child = root };
            PadOverride = new Thickness(0);
        }

        // バーを作り直すと古いウィジェットは捨てられる。波形のタイマーが残らないよう、画面から外れたら止める
        root.Loaded += (_, _) => ApplyPlaying();
        root.Unloaded += (_, _) => { _anim?.Stop(); Unsubscribe(true); };
        SetVisible(false);
        _ = RefreshAsync();
        return result;
    }

    // OS からのイベントで更新する（ポーリングは、イベントを出さないブラウザーへの保険）
    private WMC.GlobalSystemMediaTransportControlsSession? _subscribed;
    private bool _managerHooked;

    private void OnOsEvent()
    {
        try { Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync())); } catch { }
    }

    private void Subscribe(WMC.GlobalSystemMediaTransportControlsSession? s)
    {
        if (!_managerHooked) { _managerHooked = true; MediaControl.SessionsChanged += OnOsEvent; }
        if (ReferenceEquals(s, _subscribed)) return;
        Unsubscribe(false);
        _subscribed = s;
        if (s == null) return;
        try
        {
            s.TimelinePropertiesChanged += OnSessionEvent;
            s.PlaybackInfoChanged += OnSessionEvent;
            s.MediaPropertiesChanged += OnSessionEvent;
        }
        catch (Exception ex) { Log.Error("now playing subscribe", ex); }
    }

    private void OnSessionEvent(WMC.GlobalSystemMediaTransportControlsSession sender, object args) => OnOsEvent();

    private void Unsubscribe(bool manager)
    {
        if (manager && _managerHooked) { MediaControl.SessionsChanged -= OnOsEvent; _managerHooked = false; }
        var s = _subscribed; _subscribed = null;
        if (s == null) return;
        try
        {
            s.TimelinePropertiesChanged -= OnSessionEvent;
            s.PlaybackInfoChanged -= OnSessionEvent;
            s.MediaPropertiesChanged -= OnSessionEvent;
        }
        catch { }
    }

    public override void Tick(int count)
    {
        if (count % 5 == 0) _ = RefreshAsync();
        UpdateProgress();
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_busy) { _again = true; return; } // 処理中にイベントが来たら、終わった後にもう一度
        _busy = true;
        try
        {
            await MediaControl.EnsureAsync();
            _session = MediaControl.Pick(); // 再生中のものを優先
            Subscribe(_session);
            string title = "", artist = "";
            bool playing = false;
            if (_session != null)
            {
                var props = await _session.TryGetMediaPropertiesAsync();
                title = props?.Title ?? "";
                artist = props?.Artist ?? "";
                playing = MediaControl.IsPlaying(_session);
                var tl = _session.GetTimelineProperties();
                string key = title + "|" + artist;
                // ブラウザーで再生位置をドラッグ（シーク）している間などは、長さが一時的に 0 で報告される。
                // 同じ曲で、直前まで長さが取れていたなら、その値を保ち、位置は経過時間で進め続ける（グレーの状態にしない）
                if (MediaControl.HasNoDuration(tl.StartTime, tl.EndTime) && key == _goodKey && _goodKey.Length > 1)
                {
                    // _start/_end/_pos0/_posAt は更新しない
                }
                else
                {
                    _start = tl.StartTime; _end = tl.EndTime; _pos0 = tl.Position; _posAt = tl.LastUpdatedTime;
                    if (!MediaControl.HasNoDuration(_start, _end)) _goodKey = key; else if (key != _goodKey) _goodKey = "";
                }
                if (props != null)
                {
                    if (key != _trackKey) { _trackKey = key; _coverLoaded = false; if (_showThumb) ShowNote(); } // 曲が変わったら、前の曲のサムネイルを残さない
                    // サムネイルは、曲名より遅れて届くことがある（YouTube など）。届いていて、まだ読めていなければ読む
                    if (_showThumb && !_coverLoaded && !_coverLoading && props.Thumbnail != null) _ = LoadCoverAsync(props);
                }
            }
            _titleText = title; _artistText = artist; _playing = playing;
            bool wasLive = _live;
            _live = _session != null && MediaControl.IsLive(title + "|" + artist, _start, _end, playing, _pos0);
            if (Log.Enabled && (_live != wasLive || title + "|" + artist != _lastLogged))
            {
                _lastLogged = title + "|" + artist;
                Log.Info($"now playing: source={_session?.SourceAppUserModelId} title=\"{title}\" playing={playing} start={_start} end={_end} pos={_pos0} live={_live}");
            }
            if (_title != null) _title.Text = _singleLine && artist.Length > 0 ? title + " - " + artist : title; // 低いバーは 1 行にまとめる
            if (_artist != null) { _artist.Text = artist; _artist.Visibility = artist.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
            if (_liveChip != null) _liveChip.Visibility = _live ? Visibility.Visible : Visibility.Collapsed;
            if (Card != null)
            {
                string tip = artist.Length > 0 ? title + " - " + artist : title;
                Card.ToolTip = _live ? "LIVE  " + tip : tip;
            }
            SetVisible(_session != null && title.Length > 0);
            ApplyPlaying();
            UpdateProgress();
        }
        catch (Exception ex) { Log.Error("now playing", ex); SetVisible(false); }
        finally { _busy = false; if (_again) { _again = false; _ = RefreshAsync(); } }
    }

    /// <summary>曲が変わったとき: サムネイルを読み直す（読めなければ音符に戻す）。</summary>
    private async System.Threading.Tasks.Task LoadCoverAsync(WMC.GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        _coverLoading = true;
        string forKey = _trackKey;
        try
        {
            var bmp = await MediaControl.LoadThumbnailAsync(props, 96); // 表示は 32〜38px（高 DPI でも足りる大きさ）
            if (_cover == null || forKey != _trackKey || bmp == null) return; // 読み込み中に曲が変わっていたら、捨てる
            _cover.Child = null;
            _cover.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            _coverLoaded = true;
        }
        finally { _coverLoading = false; }
    }

    /// <summary>サムネイルを音符に戻す（曲が変わったとき・読めないとき）。</summary>
    private void ShowNote()
    {
        if (_cover == null) return;
        _cover.Background = T.Hover;
        _cover.Child = Note();
    }

    /// <summary>再生位置（0〜1）。最後に報告された位置から、経過した分を足して出す。ライブ配信など長さが無いときは null。</summary>
    private double? Fraction()
    {
        if (_session == null || MediaControl.HasNoDuration(_start, _end)) return null;
        var pos = _pos0;
        if (_playing && _posAt.Year > 2000) pos += DateTimeOffset.Now - _posAt;
        if (pos < _start) pos = _start;
        if (pos > _end) pos = _end;
        return (pos - _start).TotalSeconds / (_end - _start).TotalSeconds;
    }

    /// <summary>再生位置を進める（1 秒ごと）。</summary>
    private void UpdateProgress()
    {
        var f = _showProgress ? Fraction() : null;
        // 再生中なのに長さが取れないとき（ブラウザーが、動画の切り替わり後、一時停止・再生などの操作があるまで位置を報告しないことがある）:
        // 位置は分からないが、薄い線だけは出して、バーが消えたように見えないようにする。ライブ配信は LIVE を出すので、線は出さない
        bool unknown = _showProgress && !f.HasValue && _session != null && _playing && !_live;
        bool show = f.HasValue || unknown;
        double pct = f is { } v ? v * 100 : 0;
        if (_ring != null && Math.Abs(pct - _ringPct) >= 0.3) { _ring.Set(pct, ""); _ringPct = pct; } // 輪は、変わったときだけ描き直す
        if (_waveHost != null && _accHost != null)
        {
            _waveHost.Visibility = show && _playing ? Visibility.Visible : Visibility.Collapsed;
            _accHost.Width = f.HasValue ? Math.Max(0, _waveHost.ActualWidth * f.Value) : 0;
            if (_dot != null) _dot.Visibility = f.HasValue ? Visibility.Visible : Visibility.Collapsed;
            if (f.HasValue) PlaceDot();
        }
        if (_underline != null && _fill != null)
        {
            _underline.Visibility = show && (!_playing || _waveHost == null) ? Visibility.Visible : Visibility.Collapsed;
            _fill.Width = f.HasValue ? Math.Max(0, _underline.ActualWidth * f.Value) : 0;
        }
    }

    /// <summary>波線の形（幅 + 1 周期ぶん。横にずらしても切れ目が出ない）。幅が変わったときだけ作り直す。</summary>
    private void BuildWave(double width, double height)
    {
        if (width <= 0 || _waveDim == null || _waveAcc == null) return;
        if (Math.Abs(width - _waveBuiltFor) < 1) return; // 幅が変わっていなければ作り直さない
        _waveBuiltFor = width;
        width = Math.Min(width, 800);                    // 異常に広いときの保険（点の数を抑える）
        double amp = WaveAmp, mid = height / 2;
        _waveMid = mid;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, mid), false, false);
            for (double x = 1; x <= width + WavePeriod; x += 1)
                c.LineTo(new Point(x, mid + amp * Math.Sin(2 * Math.PI * x / WavePeriod)), true, true);
        }
        g.Freeze();
        _waveAcc.Data = g;                                  // 再生済みは波線
        _waveDim.Data = new LineGeometry(new Point(0, mid), new Point(width + WavePeriod, mid)); // これから再生するところは直線
        _waveDim.Data.Freeze();
        if (_accHost != null && _waveHost != null && Fraction() is { } f) _accHost.Width = width * f;
        PlaceDot();
    }

    /// <summary>現在位置の丸を、再生済みの境目に置く。波の中心線の高さに固定（波が流れても上下しない）。</summary>
    private void PlaceDot()
    {
        if (_dot == null || _accHost == null) return;
        Canvas.SetLeft(_dot, _accHost.Width - DotSize / 2);
        Canvas.SetTop(_dot, _waveMid - DotSize / 2);
    }

    /// <summary>再生中だけ波形・波線を動かす（1 秒に 10 回）。一時停止・停止中はタイマーを止めて、棒を低く薄くする。</summary>
    private void ApplyPlaying()
    {
        if (_playGlyph != null) { _playGlyph.Text = _playing ? "" : ""; if (_playBtn != null) _playBtn.ToolTip = _playing ? L.T("一時停止") : L.T("再生"); }
        if (_film != null) _film.Visibility = _playing ? Visibility.Visible : Visibility.Collapsed;
        if (_bars == null && _tWave == null) return;
        if (_playing && Card?.IsLoaded == true)
        {
            _anim ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Animate(), Application.Current.Dispatcher);
            if (!_anim.IsEnabled) _anim.Start();
            if (_bars != null) foreach (var b in _bars) b.Opacity = 1;
        }
        else
        {
            _anim?.Stop();
            if (_bars != null) foreach (var b in _bars) { ((ScaleTransform)b.RenderTransform).ScaleY = 0.35; b.Opacity = _film != null ? 1 : 0.55; }
        }
    }

    private void Animate()
    {
        if (Card is not { IsLoaded: true }) { _anim?.Stop(); return; } // 画面から外れた（バーが作り直された）古いウィジェットは、タイマーごと止める
        if (!Card.IsVisible) return;                                    // バーが隠れている間は動かさない
        _phase += 0.5;
        if (_bars != null)
            for (int i = 0; i < _bars.Length; i++)
                ((ScaleTransform)_bars[i].RenderTransform).ScaleY = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(_phase * Freq[i] + Offs[i]));
        if (_tWave != null) _tWave.X = -((_phase * 1.6) % WavePeriod); // 1 秒に約 8px 左へ流す。1 周期ずらすと元の形に戻る
    }

    /// <summary>クリックで、ジャケット・シークバー・操作ボタンのポップアップを開く（再生/一時停止はそこと右クリックのメニューで）。</summary>
    public override FrameworkElement? BuildDetailsView()
    {
        var list = MediaControl.ForPopup(); // 再生中のものが複数あれば、全部並べる
        return list.Count == 0 ? null : new NowPlayingDetailsView(list);
    }

    public override IEnumerable<MenuItemSpec> ExtraMenu() => PlayerMenu().Concat(SettingsMenu());

    private IEnumerable<MenuItemSpec> PlayerMenu() => new[]
    {
        MenuItemSpec.Header(_artistText.Length > 0 ? _titleText + " - " + _artistText : _titleText),
        new MenuItemSpec("", L.T("前の曲"), () => { try { _ = _session?.TrySkipPreviousAsync(); } catch { } }),
        new MenuItemSpec(_playing ? "" : "", _playing ? L.T("一時停止") : L.T("再生"), () => { try { _ = _session?.TryTogglePlayPauseAsync(); } catch { } }),
        new MenuItemSpec("", L.T("次の曲"), () => { try { _ = _session?.TrySkipNextAsync(); } catch { } }),
        MenuItemSpec.Separator,
    };

    /// <summary>右クリックから、このウィジェットの見た目の設定を変える（設定画面を開かなくてよい）。</summary>
    private static IEnumerable<MenuItemSpec> SettingsMenu()
    {
        var s = Settings.Current ?? new Settings();
        return new[]
        {
            WidgetSettingsMenu.Choice("", L.T("表示の形"), s.NowPlayingStyle, new[] { (NowPlayingStyle.Standard, L.T("標準")), (NowPlayingStyle.Ring, L.T("リング")) }, (c, v) => c.NowPlayingStyle = v),
            WidgetSettingsMenu.Toggle(L.T("サムネイルを表示"), s.NowPlayingThumbnail, (c, v) => c.NowPlayingThumbnail = v),
            WidgetSettingsMenu.Toggle(L.T("再生中の波形"), s.NowPlayingWave, (c, v) => c.NowPlayingWave = v),
            WidgetSettingsMenu.Toggle(L.T("再生位置を表示"), s.NowPlayingProgress, (c, v) => c.NowPlayingProgress = v),
            MenuItemSpec.Separator,
        };
    }
}
