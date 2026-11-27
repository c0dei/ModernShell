using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.WindowsTasks;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>
/// タスクバーのボタンにカーソルを置いたときのウィンドウのプレビュー。
/// DWM サムネイル（DwmRegisterThumbnail）で、他のウィンドウの中身をライブで縮小表示する（Windows のタスクバーと同じ仕組み）。
/// 複数ウィンドウは横に並べる（設定で重ねる「スタック」にもできる）。クリックで切り替え、「×」で閉じる。
/// </summary>
public sealed partial class ThumbnailPreview
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination, rcSource;
        public byte opacity;
        public int fVisible, fSourceClientAreaOnly;
    }

    private const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_RECTSOURCE = 0x2, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);

    private readonly Settings _settings;
    private readonly HoverGate _gate;
    private Popup? _popup;
    private FrameworkElement? _anchor;
    /// <summary>
    /// 開いているプレビュー 1 回分の状態。プレビューを開き直したとき、古いプレビューの後片付け（Closed は遅れて来る）が
    /// 新しいプレビューのサムネイルまで消してしまわないよう、プレビューごとに別々に持つ。
    /// </summary>
    private sealed class Session
    {
        public readonly List<(IntPtr thumb, Border slot)> Thumbs = new();
        public HwndSource? Source;
        public UIElement? LayoutOwner;
        public EventHandler? LayoutHandler;
        public DispatcherTimer? Retry;
        public bool Closed;
        /// <summary>「スタック」表示のとき。横並びのときは null。</summary>
        public StackView? Stack;
    }

    public bool IsOpen => _popup?.IsOpen == true;

    public ThumbnailPreview(Settings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _gate = new HoverGate(dispatcher, Close);
    }

    private (double w, double h) MaxSize => _settings.PreviewSize switch
    {
        "Small" => (160, 100),
        "Large" => (300, 190),
        _ => (220, 140),
    };

    /// <summary>ボタンにカーソルが入った。待ち時間のあと表示（すでに別のプレビューが出ていればすぐ切り替え）。</summary>
    /// <param name="appName">アプリ名（「スタック」表示の見出し）。</param>
    public void Hover(FrameworkElement anchor, Func<IReadOnlyList<ApplicationWindow>> windows, TaskbarEdge edge, Func<string> appName)
    {
        if (!_settings.ShowThumbnailPreview) return;
        if (IsOpen)
        {
            _gate.KeepOpen();
            if (!ReferenceEquals(_anchor, anchor)) Show(anchor, windows(), edge, appName);
            return;
        }
        _gate.ShowAfter(_settings.PreviewDelayMs, () => Show(anchor, windows(), edge, appName));
    }

    /// <summary>クリックで、待たずにすぐ表示する（Windows 11 と同じく、複数ウィンドウのボタンをクリックしたとき）。</summary>
    public void ShowNow(FrameworkElement anchor, IReadOnlyList<ApplicationWindow> windows, TaskbarEdge edge, Func<string> appName)
    {
        _gate.Stop();
        Show(anchor, windows, edge, appName);
    }

    /// <summary>ボタンからカーソルが出た。プレビューの上に移らなければ少し後に閉じる。</summary>
    public void Leave() => _gate.Leave(IsOpen);

    /// <summary>クリックやドラッグを始めたときなど、すぐ閉じる。</summary>
    public void Cancel()
    {
        _gate.Stop();
        Close();
    }

    /// <param name="start">「スタック」表示で最初に手前にする番号。指定がなければ前面のウィンドウ（なければ先頭）。</param>
    private void Show(FrameworkElement anchor, IReadOnlyList<ApplicationWindow> windows, TaskbarEdge edge, Func<string> appName, int? start = null)
    {
        StallDetector.Phase = "preview: show";
        Close();
        if (windows.Count == 0 || !anchor.IsLoaded) return;
        _anchor = anchor;
        var session = new Session();
        FrameworkElement content;
        if (_settings.PreviewLayout == PreviewLayout.Stack && windows.Count > 1)
        {
            session.Stack = BuildStack(anchor, windows.ToList(), edge, appName, start);
            content = session.Stack.Content;
        }
        else content = BuildRow(anchor, windows, edge, appName);

        _popup = FluentSurface.Create(content, anchor, PlacementMode.Custom);
        _popup.StaysOpen = true; // ホバーで開閉するので、マウスを捕まえない
        FluentSurface.PlaceAligned(_popup, anchor, edge, _settings.PreviewAlign);
        _gate.Attach(content);
        if (_settings.AnimatePreviews) FluentSurface.SlideIn(_popup, edge);
        _popup.Opened += (_, _) => RegisterThumbnails(content, session);
        _popup.Closed += (_, _) => Unregister(session);
        _popup.IsOpen = true;
    }

    private StackView BuildStack(FrameworkElement anchor, IReadOnlyList<ApplicationWindow> windows, TaskbarEdge edge, Func<string> appName, int? start)
    {
        var t = Theme.Current;
        var (maxW, maxH) = MaxSize;
        int active = windows.ToList().FindIndex(w => w.State == ApplicationWindow.WindowState.Active);
        FrameworkElement? media = null;
        foreach (var w in windows)
            if ((media = MediaStrip(w, maxW, t)) != null) break;
        return new StackView(windows, start ?? Math.Max(0, active), appName(), maxW, maxH, _settings.AnimatePreviews,
            win => { Cancel(); win.BringToFront(); },
            (win, at) =>
            {
                win.Close();
                // 閉じたウィンドウのカードを消して、同じ番号の位置を手前にする（最後の 1 枚なら全体を閉じる）
                var rest = windows.Where(x => !ReferenceEquals(x, win)).ToList();
                if (rest.Count == 0) Cancel(); else Show(anchor, rest, edge, appName, Math.Min(at, rest.Count - 1));
            },
            media);
    }

    /// <summary>ウィンドウを横に並べた中身。</summary>
    private StackPanel BuildRow(FrameworkElement anchor, IReadOnlyList<ApplicationWindow> windows, TaskbarEdge edge, Func<string> appName)
    {
        var t = Theme.Current;
        var (maxW, maxH) = MaxSize;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        bool mediaAdded = false; // 同じアプリのウィンドウが複数あっても、メディア操作は 1 か所だけ
        foreach (var w in windows.Take(8))
        {
            // 見出し（アイコン・ウィンドウ名・閉じる）
            // プレビューの絵の大きさは「プレビューの大きさ」の設定で決まるので、文字だけを合わせる
            var title = UiKit.Label(w.Title ?? "", 12 * UiKit.PopupScale);
            title.MaxWidth = maxW - 56;
            var icon = UiKit.Icon(w.Icon, 16);
            icon.Margin = new Thickness(0, 0, 8, 0);
            var close = new Tile { Width = 24, Height = 24, CornerRadius = t.Corner(4), Child = UiKit.Glyph("", 10), ToolTip = L.T("ウィンドウを閉じる") };
            var head = new DockPanel { Margin = new Thickness(4, 2, 0, 6), LastChildFill = true };
            DockPanel.SetDock(close, Dock.Right);
            DockPanel.SetDock(icon, Dock.Left);
            head.Children.Add(close);
            head.Children.Add(icon);
            head.Children.Add(title);

            // サムネイルの描画先（DWM がこの位置に描く）。描けない場合に備えて大きなアイコンを下に置く
            var slot = new Border
            {
                Width = maxW, Height = maxH,
                Child = UiKit.Icon(w.Icon, 32),
            };
            var body = new StackPanel();
            body.Children.Add(head);
            body.Children.Add(slot);
            if (!mediaAdded && MediaStrip(w, maxW, t) is { } media) { body.Children.Add(media); mediaAdded = true; }
            var card = new Tile { Padding = new Thickness(6), Margin = new Thickness(2), Child = body, Tag = slot };
            var win = w;
            card.Click += _ => { Cancel(); win.BringToFront(); };
            close.Click += _ =>
            {
                win.Close();
                // 閉じたウィンドウのカードを消す（最後の 1 枚なら全体を閉じる）
                var rest = windows.Where(x => !ReferenceEquals(x, win)).ToList();
                if (rest.Count == 0) Cancel(); else Show(anchor, rest, edge, appName);
            };
            slot.Tag = win.Handle;
            row.Children.Add(card);
        }
        return row;
    }

    /// <summary>
    /// 音楽や動画を再生中のアプリなら、曲名と「前の曲・再生/一時停止・次の曲」。メディア セッションの一覧は最初に 1 度だけ取りに行くので、
    /// 最初の 1 回は間に合わず出ないことがある（次のプレビューから出る）。
    /// </summary>
    private FrameworkElement? MediaStrip(ApplicationWindow w, double maxW, Theme t)
    {
        if (!_settings.ShowPreviewMediaControls) return null;
        if (!MediaControl.Ready) { _ = MediaControl.EnsureAsync(); return null; }
        try
        {
            var session = MediaControl.FindFor(string.IsNullOrEmpty(w.AppUserModelID) ? ProcessAumid.Get(w.Handle) : w.AppUserModelID, w.WinFileName);
            if (session == null) return null;
            var name = UiKit.Label("", 11 * UiKit.PopupScale, t.TextSecondary);
            name.MaxWidth = maxW;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Visibility = Visibility.Collapsed;
            _ = FillTrackName(session, name);
            var col = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            col.Children.Add(name);
            col.Children.Add(MediaButtons.Build(session, t));
            return col;
        }
        catch (Exception ex) { Log.Error("preview media", ex); return null; }
    }

    private static async System.Threading.Tasks.Task FillTrackName(global::Windows.Media.Control.GlobalSystemMediaTransportControlsSession session, TextBlock label)
    {
        try
        {
            var p = await session.TryGetMediaPropertiesAsync();
            string text = string.IsNullOrEmpty(p?.Artist) ? p?.Title ?? "" : p!.Title + " - " + p.Artist;
            if (text.Length == 0) return;
            label.Dispatcher.Invoke(() => { label.Text = text; label.Visibility = Visibility.Visible; });
        }
        catch { }
    }

    /// <summary>ポップアップが開いたら、各カードの位置に DWM サムネイルを登録する。</summary>
    private void RegisterThumbnails(FrameworkElement content, Session session)
    {
        StallDetector.Phase = "preview: register thumbnails";
        if (session.Closed || PresentationSource.FromVisual(content) is not HwndSource src) return;
        session.Source = src;
        if (session.Stack is { } stack) stack.Register(src.Handle);
        else RegisterRow((StackPanel)content, src, session);

        // 描画位置の設定: 枠の大きさが決まって配置し直されるたびに更新する。
        // 準備が整う前に 1 回きりで終わると描かれないままになるので、念のため少しの間は繰り返し設定する
        session.LayoutOwner = content;
        session.LayoutHandler = (_, _) => UpdateRects(session);
        content.LayoutUpdated += session.LayoutHandler;
        int tries = 0;
        session.Retry = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            UpdateRects(session);
            if (++tries >= 10) session.Retry?.Stop();
        }, content.Dispatcher);
        session.Retry.Start();
        content.Dispatcher.BeginInvoke(() => UpdateRects(session), DispatcherPriority.Loaded);
    }

    private void RegisterRow(StackPanel row, HwndSource src, Session session)
    {
        var (maxW, maxH) = MaxSize;
        foreach (var card in row.Children.OfType<Tile>())
        {
            if (card.Tag is not Border slot || slot.Tag is not IntPtr hwnd) continue;
            if (DwmRegisterThumbnail(src.Handle, hwnd, out var thumb) != 0 || thumb == IntPtr.Zero)
            {
                Log.Info($"thumbnail register failed: {hwnd}");
                continue;
            }
            // 元のウィンドウの縦横比に合わせて枠の大きさを決める
            if (DwmQueryThumbnailSourceSize(thumb, out var size) == 0 && size.cx > 0 && size.cy > 0)
            {
                double scale = Math.Min(maxW / size.cx, maxH / size.cy);
                slot.Width = Math.Max(80, size.cx * scale);
                slot.Height = Math.Max(50, size.cy * scale);
            }
            session.Thumbs.Add((thumb, slot));
        }
    }

    private static void UpdateRects(Session session)
    {
        var src = session.Source;
        if (session.Closed || src == null || src.IsDisposed || src.RootVisual == null || src.CompositionTarget == null) return;
        if (session.Stack is { } stack) { stack.UpdateThumbnails(src); return; }
        var m = src.CompositionTarget.TransformToDevice;
        foreach (var (thumb, slot) in session.Thumbs)
        {
            // IsLoaded を待つと、最後の配置のあとに Loaded が来て設定されないままになることがあるので、実際の大きさで判断する
            if (slot.ActualWidth <= 0 || slot.ActualHeight <= 0) continue;
            Point tl, br;
            try
            {
                var tr = slot.TransformToAncestor(src.RootVisual);
                tl = m.Transform(tr.Transform(new Point(0, 0)));
                br = m.Transform(tr.Transform(new Point(slot.ActualWidth, slot.ActualHeight)));
            }
            catch { continue; }
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT { Left = (int)tl.X, Top = (int)tl.Y, Right = (int)br.X, Bottom = (int)br.Y },
                opacity = 255, fVisible = 1, fSourceClientAreaOnly = 0,
            };
            DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    private static void Unregister(Session session)
    {
        if (session.Closed) return;
        session.Closed = true;
        session.Retry?.Stop();
        if (session.LayoutOwner != null && session.LayoutHandler != null) session.LayoutOwner.LayoutUpdated -= session.LayoutHandler;
        foreach (var (thumb, _) in session.Thumbs) DwmUnregisterThumbnail(thumb);
        session.Thumbs.Clear();
        session.Stack?.Dispose();
    }

    private void Close()
    {
        _gate.Stop();
        if (_popup != null) { _popup.IsOpen = false; _popup = null; } // サムネイルの解除はそのプレビューの Closed で行う
        _anchor = null;
    }
}
