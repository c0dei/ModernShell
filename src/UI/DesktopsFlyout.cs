using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
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

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination, rcSource;
        public byte opacity;
        public int fVisible, fSourceClientAreaOnly;
    }
    private const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_RECTSOURCE = 0x2, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8;
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);

    /// <summary>ウィンドウ 1 枚分: DWM の描画先の枠（カード上の位置）と、画面上の元の位置。</summary>
    private sealed record Slot(IntPtr Window, Border Box, int Left, int Top, int Right, int Bottom);

    /// <summary>開いている一覧 1 回分の DWM サムネイル（開き直したとき、古い一覧の後片付けが新しい分を消さないよう別々に持つ）。</summary>
    private sealed class Session
    {
        public readonly List<Slot> Slots = new();
        public readonly List<IntPtr> Thumbs = new();
        public readonly Dictionary<int, Slot> Handles = new();
        public HwndSource? Source;
        public UIElement? Owner;
        public EventHandler? Handler;
        public bool Closed;
    }

    public DesktopsFlyout(Settings settings, Dispatcher dispatcher)
    {
        _settings = settings;
        _gate = new HoverGate(dispatcher, Close);
    }

    public void Hover(FrameworkElement anchor, TaskbarEdge edge)
    {
        if (!_settings.ShowDesktopsOnTaskView) return;
        if (_popup?.IsOpen == true) { _gate.KeepOpen(); return; }
        _gate.ShowAfter(_settings.PreviewDelayMs, () => Show(anchor, edge));
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

    private void Show(FrameworkElement anchor, TaskbarEdge edge)
    {
        StallDetector.Phase = "desktops flyout";
        Close();
        var t = Theme.Current;
        var desktops = VirtualDesktops.List();
        var session = new Session();
        // 各デスクトップのウィンドウ（前面のものが先なので、奥から描くよう後ろから登録する）
        var (screenW, screenH) = DesktopWindows.ScreenSize();
        var currentId = desktops.FirstOrDefault(d => d.IsCurrent)?.Id ?? Guid.Empty;
        var windows = DesktopWindows.List(desktops.Count <= 1 ? Guid.Empty : currentId);
        const double FrameW = 160;
        double frameH = Math.Round(FrameW * screenH / screenW);
        double scale = FrameW / screenW;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        for (int i = 0; i < desktops.Count; i++)
        {
            var d = desktops[i];
            var img = new Image { Stretch = Stretch.UniformToFill };
            if (d.Wallpaper is { } wall)
                Task.Run(() => PackageInfo.LoadImage(wall, 320)).ContinueWith(r => { if (r.Result != null) img.Dispatcher.BeginInvoke(() => img.Source = r.Result); }, TaskScheduler.Default);
            // 壁紙の上に、そのデスクトップのウィンドウの描画先を重ねる（DWM が開いたあとここへ縮小して描く）
            var canvas = new Canvas { Width = FrameW, Height = frameH };
            foreach (var w in Enumerable.Reverse(windows.Where(x => x.DesktopId == d.Id || (desktops.Count <= 1))))
            {
                // 画面の外にはみ出した分（最大化の余白など）は切る
                int l = Math.Max(0, w.Left), tp = Math.Max(0, w.Top), r = Math.Min(screenW, w.Right), b = Math.Min(screenH, w.Bottom);
                if (r - l < 2 || b - tp < 2) continue;
                var box = new Border { Width = Math.Max(1, (r - l) * scale), Height = Math.Max(1, (b - tp) * scale) };
                Canvas.SetLeft(box, l * scale);
                Canvas.SetTop(box, tp * scale);
                canvas.Children.Add(box);
                session.Slots.Add(new Slot(w.Handle, box, l - w.Left, tp - w.Top, r - w.Left, b - w.Top));
            }
            var frame = new Border
            {
                Width = FrameW, Height = frameH, CornerRadius = t.Corner(6), ClipToBounds = true, Background = t.Surface,
                BorderThickness = new Thickness(2), BorderBrush = d.IsCurrent ? t.AccentBrush : Brushes.Transparent,
                Child = new Grid { Children = { img, canvas } },
            };
            var label = UiKit.Label(d.Name, 12, t.Text, d.IsCurrent ? FontWeights.SemiBold : FontWeights.Normal);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.MaxWidth = 130;
            label.Margin = new Thickness(0, 6, 0, 0);
            var body = new StackPanel();
            body.Children.Add(frame);
            var labelRow = new Grid();
            labelRow.Children.Add(label);
            int index = i;
            Tile? close = null;
            if (desktops.Count > 1)
            {
                // 「×」でデスクトップを削除（ウィンドウは隣のデスクトップへ移る。Windows のタスク ビューと同じ）
                close = new Tile
                {
                    Width = 22, Height = 22, CornerRadius = t.Corner(4), Child = UiKit.Glyph("\uE711", 10), ToolTip = L.T("デスクトップを閉じる"),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0,
                };
                close.Click += async _ =>
                {
                    Cancel();
                    await VirtualDesktops.Remove(index);
                };
                labelRow.Children.Add(close);
            }
            body.Children.Add(labelRow);
            var card = new Tile { Padding = new Thickness(6), Margin = new Thickness(2), Child = body, ToolTip = d.Name };
            if (close != null)
            {
                // Windows と同じく、カーソルを置いたカードだけ「×」を出す
                var c = close;
                card.MouseEnter += (_, _) => c.Opacity = 1;
                card.MouseLeave += (_, _) => c.Opacity = 0;
            }
            card.Click += _ => { Cancel(); VirtualDesktops.SwitchTo(index); };
            row.Children.Add(card);
        }

        // 新しいデスクトップ
        var plusFrame = new Border
        {
            Width = FrameW, Height = frameH, CornerRadius = t.Corner(6), Background = t.ControlFill,
            BorderBrush = t.Stroke, BorderThickness = new Thickness(1), Child = UiKit.Glyph("", 20),
        };
        var plusLabel = UiKit.Label(L.T("新しいデスクトップ"), 12);
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
        FluentSurface.PlaceAligned(_popup, anchor, edge, _settings.DesktopsAlign);
        _gate.Attach(row);
        if (_settings.AnimatePreviews) FluentSurface.SlideIn(_popup, edge);
        _popup.Opened += (_, _) => Register(row, session);
        _popup.Closed += (_, _) => Unregister(session);
        _popup.IsOpen = true;
    }

    /// <summary>ポップアップが開いたら、各ウィンドウの枠の位置に DWM サムネイルを登録する（登録順に奥から重なる）。</summary>
    private static void Register(FrameworkElement root, Session session)
    {
        if (session.Closed || PresentationSource.FromVisual(root) is not HwndSource src) return;
        session.Source = src;
        foreach (var slot in session.Slots)
        {
            if (DwmRegisterThumbnail(src.Handle, slot.Window, out var thumb) != 0 || thumb == IntPtr.Zero) continue;
            session.Thumbs.Add(thumb);
            session.Handles[session.Thumbs.Count - 1] = slot;
        }
        session.Owner = root;
        session.Handler = (_, _) => Update(session);
        root.LayoutUpdated += session.Handler;
        root.Dispatcher.BeginInvoke(() => Update(session), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void Update(Session session)
    {
        var src = session.Source;
        if (session.Closed || src == null || src.IsDisposed || src.RootVisual == null || src.CompositionTarget == null) return;
        var m = src.CompositionTarget.TransformToDevice;
        foreach (var (i, slot) in session.Handles)
        {
            if (slot.Box.ActualWidth <= 0 || slot.Box.ActualHeight <= 0) continue;
            Point tl, br;
            try
            {
                var tr = slot.Box.TransformToAncestor(src.RootVisual);
                tl = m.Transform(tr.Transform(new Point(0, 0)));
                br = m.Transform(tr.Transform(new Point(slot.Box.ActualWidth, slot.Box.ActualHeight)));
            }
            catch { continue; }
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_VISIBLE | DWM_TNP_OPACITY,
                rcDestination = new RECT { Left = (int)tl.X, Top = (int)tl.Y, Right = (int)br.X, Bottom = (int)br.Y },
                rcSource = new RECT { Left = slot.Left, Top = slot.Top, Right = slot.Right, Bottom = slot.Bottom },
                opacity = 255, fVisible = 1,
            };
            DwmUpdateThumbnailProperties(session.Thumbs[i], ref props);
        }
    }

    private static void Unregister(Session session)
    {
        if (session.Closed) return;
        session.Closed = true;
        if (session.Owner != null && session.Handler != null) session.Owner.LayoutUpdated -= session.Handler;
        foreach (var th in session.Thumbs) DwmUnregisterThumbnail(th);
        session.Thumbs.Clear();
    }
}
