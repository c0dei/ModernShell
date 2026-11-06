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
    private readonly DispatcherTimer _showTimer, _hideTimer;
    private Popup? _popup;
    private FrameworkElement? _anchor, _pendingAnchor;
    private Func<IReadOnlyList<ApplicationWindow>>? _pendingWindows;
    private bool _top;
    private readonly List<(IntPtr thumb, Border slot)> _thumbs = new();
    private UIElement? _layoutOwner;
    private EventHandler? _layoutHandler;

    public bool IsOpen => _popup?.IsOpen == true;

    public ThumbnailPreview(Settings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _showTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
        _showTimer.Tick += (_, _) => { _showTimer.Stop(); if (_pendingAnchor != null && _pendingWindows != null) Show(_pendingAnchor, _pendingWindows(), _top); };
        _hideTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => { _hideTimer!.Stop(); Close(); }, dispatcher);
        _hideTimer.Stop();
    }

    private (double w, double h) MaxSize => _settings.PreviewSize switch
    {
        "Small" => (160, 100),
        "Large" => (300, 190),
        _ => (220, 140),
    };

    /// <summary>ボタンにカーソルが入った。待ち時間のあと表示（すでに別のプレビューが出ていればすぐ切り替え）。</summary>
    public void Hover(FrameworkElement anchor, Func<IReadOnlyList<ApplicationWindow>> windows, bool top)
    {
        if (!_settings.ShowThumbnailPreview) return;
        _hideTimer.Stop();
        _pendingAnchor = anchor;
        _pendingWindows = windows;
        _top = top;
        if (IsOpen)
        {
            if (!ReferenceEquals(_anchor, anchor)) Show(anchor, windows(), top);
            return;
        }
        _showTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _settings.PreviewDelayMs));
        _showTimer.Stop();
        _showTimer.Start();
    }

    /// <summary>ボタンからカーソルが出た。プレビューの上に移らなければ少し後に閉じる。</summary>
    public void Leave()
    {
        _showTimer.Stop();
        _pendingAnchor = null;
        if (IsOpen) { _hideTimer.Stop(); _hideTimer.Start(); }
    }

    /// <summary>クリックやドラッグを始めたときなど、すぐ閉じる。</summary>
    public void Cancel()
    {
        _showTimer.Stop();
        _pendingAnchor = null;
        Close();
    }

    private void Show(FrameworkElement anchor, IReadOnlyList<ApplicationWindow> windows, bool top)
    {
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
            var close = new Tile { Width = 24, Height = 24, CornerRadius = t.Corner(4), Child = UiKit.Glyph("", 10), ToolTip = "ウィンドウを閉じる" };
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
                if (rest.Count == 0) Cancel(); else Show(anchor, rest, top);
            };
            slot.Tag = win.Handle;
            row.Children.Add(card);
        }

        _popup = FluentSurface.Create(row, anchor, PlacementMode.Custom);
        _popup.StaysOpen = true; // ホバーで開閉するので、マウスを捕まえない
        double gap = 8 * VisualTreeHelper.GetDpi(anchor).DpiScaleY;
        _popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            double x = (targetSize.Width - popupSize.Width) / 2;
            var above = new CustomPopupPlacement(new Point(x, -popupSize.Height - gap), PopupPrimaryAxis.Horizontal);
            var below = new CustomPopupPlacement(new Point(x, targetSize.Height + gap), PopupPrimaryAxis.Horizontal);
            return top ? new[] { below, above } : new[] { above, below };
        };
        row.MouseEnter += (_, _) => _hideTimer.Stop();
        row.MouseLeave += (_, _) => { _hideTimer.Stop(); _hideTimer.Start(); };
        _popup.Opened += (_, _) => RegisterThumbnails(row);
        _popup.Closed += (_, _) => UnregisterThumbnails();
        _popup.IsOpen = true;
    }

    /// <summary>ポップアップが開いたら、各カードの位置に DWM サムネイルを登録する。</summary>
    private void RegisterThumbnails(StackPanel row)
    {
        if (PresentationSource.FromVisual(row) is not HwndSource src) return;
        var (maxW, maxH) = MaxSize;
        foreach (var card in row.Children.OfType<Tile>())
        {
            if (card.Tag is not Border slot || slot.Tag is not IntPtr hwnd) continue;
            if (DwmRegisterThumbnail(src.Handle, hwnd, out var thumb) != 0 || thumb == IntPtr.Zero) continue;
            // 元のウィンドウの縦横比に合わせて枠の大きさを決める
            if (DwmQueryThumbnailSourceSize(thumb, out var size) == 0 && size.cx > 0 && size.cy > 0)
            {
                double scale = Math.Min(maxW / size.cx, maxH / size.cy);
                slot.Width = Math.Max(80, size.cx * scale);
                slot.Height = Math.Max(50, size.cy * scale);
            }
            _thumbs.Add((thumb, slot));
        }
        // 大きさが決まって配置し直されたあとに描画位置を設定する
        row.Dispatcher.BeginInvoke(() => UpdateRects(src), DispatcherPriority.Loaded);
        // ポップアップの位置や大きさが変わったら描画位置を追従させる（閉じるときに必ず外す）
        _layoutOwner = row;
        _layoutHandler = (_, _) => UpdateRects(src);
        row.LayoutUpdated += _layoutHandler;
    }

    private void UpdateRects(HwndSource src)
    {
        if (src.RootVisual == null || src.CompositionTarget == null) return;
        var m = src.CompositionTarget.TransformToDevice;
        foreach (var (thumb, slot) in _thumbs)
        {
            if (!slot.IsLoaded) continue;
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

    private void UnregisterThumbnails()
    {
        if (_layoutOwner != null && _layoutHandler != null) _layoutOwner.LayoutUpdated -= _layoutHandler;
        _layoutOwner = null;
        _layoutHandler = null;
        foreach (var (thumb, _) in _thumbs) DwmUnregisterThumbnail(thumb);
        _thumbs.Clear();
    }

    private void Close()
    {
        _hideTimer.Stop();
        if (_popup != null) { _popup.IsOpen = false; _popup = null; }
        UnregisterThumbnails();
        _anchor = null;
    }
}
