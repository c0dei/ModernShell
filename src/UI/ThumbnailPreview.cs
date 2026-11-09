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
/// 複数ウィンドウは横に並べ、クリックで切り替え、「×」で閉じる。
/// </summary>
public sealed class ThumbnailPreview
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

    private const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

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
    public void Hover(FrameworkElement anchor, Func<IReadOnlyList<ApplicationWindow>> windows, TaskbarEdge edge)
    {
        if (!_settings.ShowThumbnailPreview) return;
        if (IsOpen)
        {
            _gate.KeepOpen();
            if (!ReferenceEquals(_anchor, anchor)) Show(anchor, windows(), edge);
            return;
        }
        _gate.ShowAfter(_settings.PreviewDelayMs, () => Show(anchor, windows(), edge));
    }

    /// <summary>ボタンからカーソルが出た。プレビューの上に移らなければ少し後に閉じる。</summary>
    public void Leave() => _gate.Leave(IsOpen);

    /// <summary>クリックやドラッグを始めたときなど、すぐ閉じる。</summary>
    public void Cancel()
    {
        _gate.Stop();
        Close();
    }

    private void Show(FrameworkElement anchor, IReadOnlyList<ApplicationWindow> windows, TaskbarEdge edge)
    {
        StallDetector.Phase = "preview: show";
        Close();
        if (windows.Count == 0 || !anchor.IsLoaded) return;
        _anchor = anchor;
        var t = Theme.Current;
        var (maxW, maxH) = MaxSize;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        foreach (var w in windows.Take(8))
        {
            // 見出し（アイコン・ウィンドウ名・閉じる）
            var title = UiKit.Label(w.Title ?? "", 12);
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
            var card = new Tile { Padding = new Thickness(6), Margin = new Thickness(2), Child = body, Tag = slot };
            var win = w;
            card.Click += _ => { Cancel(); win.BringToFront(); };
            close.Click += _ =>
            {
                win.Close();
                // 閉じたウィンドウのカードを消す（最後の 1 枚なら全体を閉じる）
                var rest = windows.Where(x => !ReferenceEquals(x, win)).ToList();
                if (rest.Count == 0) Cancel(); else Show(anchor, rest, edge);
            };
            slot.Tag = win.Handle;
            row.Children.Add(card);
        }

        _popup = FluentSurface.Create(row, anchor, PlacementMode.Custom);
        _popup.StaysOpen = true; // ホバーで開閉するので、マウスを捕まえない
        FluentSurface.PlaceCentered(_popup, anchor, edge);
        _gate.Attach(row);
        var session = new Session();
        if (_settings.AnimatePreviews) FluentSurface.SlideIn(_popup, edge);
        _popup.Opened += (_, _) => RegisterThumbnails(row, session);
        _popup.Closed += (_, _) => Unregister(session);
        _popup.IsOpen = true;
    }

    /// <summary>ポップアップが開いたら、各カードの位置に DWM サムネイルを登録する。</summary>
    private void RegisterThumbnails(StackPanel row, Session session)
    {
        StallDetector.Phase = "preview: register thumbnails";
        if (session.Closed || PresentationSource.FromVisual(row) is not HwndSource src) return;
        session.Source = src;
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

        // 描画位置の設定: 枠の大きさが決まって配置し直されるたびに更新する。
        // 準備が整う前に 1 回きりで終わると描かれないままになるので、念のため少しの間は繰り返し設定する
        session.LayoutOwner = row;
        session.LayoutHandler = (_, _) => UpdateRects(session);
        row.LayoutUpdated += session.LayoutHandler;
        int tries = 0;
        session.Retry = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            UpdateRects(session);
            if (++tries >= 10) session.Retry?.Stop();
        }, row.Dispatcher);
        session.Retry.Start();
        row.Dispatcher.BeginInvoke(() => UpdateRects(session), DispatcherPriority.Loaded);
    }

    private static void UpdateRects(Session session)
    {
        var src = session.Source;
        if (session.Closed || src == null || src.IsDisposed || src.RootVisual == null || src.CompositionTarget == null) return;
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
    }

    private void Close()
    {
        _gate.Stop();
        if (_popup != null) { _popup.IsOpen = false; _popup = null; } // サムネイルの解除はそのプレビューの Closed で行う
        _anchor = null;
    }
}
