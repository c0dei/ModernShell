using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ManagedShell.WindowsTasks;
using ModernShell.Core;

namespace ModernShell.UI;

public sealed partial class ThumbnailPreview
{
    /// <summary>
    /// 「スタック」表示の 1 回分: 1 つのウィンドウを手前に出し、前後のウィンドウを後ろの左右に覗かせる。
    /// 矢印・点・マウスホイール・後ろのカードのクリックで切り替える。
    /// DWM サムネイルはポップアップの描画より常に手前に描かれて、WPF の要素では隠せないので、
    /// 後ろのカードの映像は、手前のカードからはみ出している部分だけを切り抜いて描く。
    /// </summary>
    private sealed class StackView
    {
        private const double BackScale = 0.85, HiddenScale = 0.8;
        private const double CardPadding = 6;
        private const int MaxDots = 20;

        /// <summary>
        /// カードの置き方。X は中央からのずれ、Align は映像を枠のどちらに寄せるか（-1 左 / 0 中央 / 1 右）、
        /// Light は手前らしさ（1 手前、0 後ろ）。後ろのカードは、見えている外側に映像を寄せる（縦長のウィンドウでも覗くように）。
        /// </summary>
        private readonly record struct Pose(double X, double Scale, double Align, double Light)
        {
            public static Pose Lerp(Pose a, Pose b, double t) => new(
                a.X + (b.X - a.X) * t, a.Scale + (b.Scale - a.Scale) * t,
                a.Align + (b.Align - a.Align) * t, a.Light + (b.Light - a.Light) * t);
        }

        private sealed class Card
        {
            public required IntPtr Window;
            public required Border Frame;
            public required FrameworkElement Head;
            public required Tile Close;
            public required Border Slot;
            public readonly ScaleTransform Scale = new();
            public readonly TranslateTransform Move = new();
            public IntPtr Thumb;
            public Pose From, To, Now;
            /// <summary>重なりの順（大きいほど手前）。切り替えの途中で入れ替える。</summary>
            public int Rank, NextRank;
        }

        private readonly List<Card> _cards = new();
        private readonly List<Border> _dots = new();
        private readonly TextBlock _counter;
        private readonly Stopwatch _clock = new();
        private readonly bool _animate;
        private readonly double _sideShift;
        private EventHandler? _frame;
        private int _index, _wheel;

        public FrameworkElement Content { get; }

        public StackView(IReadOnlyList<ApplicationWindow> windows, int start, string appName, double maxW, double maxH,
            bool animate, Action<ApplicationWindow> activate, Action<ApplicationWindow, int> close, FrameworkElement? media)
        {
            var t = Theme.Current;
            _animate = animate;
            _index = Math.Clamp(start, 0, windows.Count - 1);
            double cardW = maxW + (CardPadding + 2) * 2; // 余白と、台・ボタンの枠線（1 ずつ）
            double peek = Math.Round(maxW * 0.16);       // 後ろのカードが手前のカードからはみ出す幅
            _sideShift = cardW * (1 - BackScale) / 2 + peek;

            var stage = new Grid { Width = cardW + peek * 2, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < windows.Count; i++)
            {
                var win = windows[i];
                int at = i;
                var title = UiKit.Label(win.Title ?? "", 12 * UiKit.PopupScale);
                title.MaxWidth = maxW - 56;
                var icon = UiKit.Icon(win.Icon, 16);
                icon.Margin = new Thickness(0, 0, 8, 0);
                var closeButton = new Tile { Width = 24, Height = 24, CornerRadius = t.Corner(4), Child = UiKit.Glyph("", 10), ToolTip = L.T("ウィンドウを閉じる") };
                var head = new DockPanel { Margin = new Thickness(4, 2, 0, 6), LastChildFill = true };
                DockPanel.SetDock(closeButton, Dock.Right);
                DockPanel.SetDock(icon, Dock.Left);
                head.Children.Add(closeButton);
                head.Children.Add(icon);
                head.Children.Add(title);

                // 枠の大きさは固定（ウィンドウの縦横比が違っても、切り替えでポップアップの大きさが変わらないように）
                var slot = new Border { Width = maxW, Height = maxH, Child = UiKit.Icon(win.Icon, 32) };
                var body = new StackPanel();
                body.Children.Add(head);
                body.Children.Add(slot);
                var tile = new Tile { Padding = new Thickness(CardPadding), Child = body };
                // 重ねたときに後ろが透けないよう、不透明な台に載せる（ホバーの色はその上の Tile が付ける）
                var frame = new Border
                {
                    Width = cardW, Child = tile, CornerRadius = t.Corner(8), Background = t.SolidBackground,
                    BorderBrush = t.Stroke, BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                };
                var card = new Card { Window = win.Handle, Frame = frame, Head = head, Close = closeButton, Slot = slot };
                frame.RenderTransform = new TransformGroup { Children = { card.Scale, card.Move } };
                tile.Click += _ => { if (at == _index) activate(win); else Select(at); };
                closeButton.Click += _ => close(win, at);
                _cards.Add(card);
                stage.Children.Add(frame);
            }

            // 見出し（アプリ名・何番目か・前後の矢印）
            var appIcon = UiKit.Icon(windows[0].Icon, 16);
            appIcon.Margin = new Thickness(0, 0, 8, 0);
            var name = UiKit.Label(appName, 12 * UiKit.PopupScale);
            name.MaxWidth = Math.Max(60, stage.Width - 140);
            name.HorizontalAlignment = HorizontalAlignment.Left;
            _counter = UiKit.Label("", 12 * UiKit.PopupScale, t.TextSecondary);
            _counter.Margin = new Thickness(8, 0, 4, 0);
            Tile Arrow(string glyph, string tip, int step)
            {
                var a = new Tile { Width = 24, Height = 24, CornerRadius = t.Corner(4), Child = UiKit.Glyph(glyph, 10), ToolTip = tip };
                a.Click += _ => Step(step);
                return a;
            }
            var top = new DockPanel { Margin = new Thickness(6, 0, 0, 6), LastChildFill = true };
            var next = Arrow("", L.T("次のウィンドウ"), 1);
            var prev = Arrow("", L.T("前のウィンドウ"), -1);
            foreach (var right in new FrameworkElement[] { next, prev, _counter })
            {
                DockPanel.SetDock(right, Dock.Right);
                top.Children.Add(right);
            }
            DockPanel.SetDock(appIcon, Dock.Left);
            top.Children.Add(appIcon);
            top.Children.Add(name);

            var column = new StackPanel();
            column.Children.Add(top);
            column.Children.Add(stage);

            if (windows.Count <= MaxDots)
            {
                var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
                for (int i = 0; i < windows.Count; i++)
                {
                    int at = i;
                    var dot = new Border { Height = 6, CornerRadius = new CornerRadius(3) };
                    var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(3, 4, 3, 4), Child = dot };
                    hit.MouseLeftButtonUp += (_, e) => { Select(at); e.Handled = true; };
                    _dots.Add(dot);
                    dots.Children.Add(hit);
                }
                column.Children.Add(dots);
            }
            if (media != null) column.Children.Add(media);

            // 背景を透明にしておくと、すき間の上でもホイールが効く
            var root = new Border { Background = Brushes.Transparent, Padding = new Thickness(8), Child = column };
            root.MouseWheel += OnWheel;
            Content = root;

            for (int i = 0; i < _cards.Count; i++)
            {
                var (pose, rank) = Place(i);
                var c = _cards[i];
                c.From = c.To = c.Now = pose;
                c.Rank = c.NextRank = rank;
                Apply(c);
            }
            ShowPosition();
        }

        /// <summary>手前にするウィンドウの番号に対する、i 番目のカードの置き方と重なりの順。</summary>
        private (Pose pose, int rank) Place(int i)
        {
            int n = _cards.Count, d = (i - _index + n) % n;
            if (d == 0) return (new Pose(0, 1, 0, 1), 3);
            // 2 つのときは並び順の側に、3 つ以上のときは前後を左右に（端は反対側へつながる）
            int side = n == 2 ? (i > _index ? 1 : -1) : d == 1 ? 1 : d == n - 1 ? -1 : 0;
            return side == 0
                ? (new Pose(0, HiddenScale, 0, 0), 1) // 手前のカードの真後ろに隠れる
                : (new Pose(side * _sideShift, BackScale, side, 0), 2);
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            // タッチパッドは細かい値が続けて来るので、1 目盛り分（120）たまるごとに 1 つ送る
            if (Math.Sign(e.Delta) != Math.Sign(_wheel)) _wheel = 0;
            _wheel += e.Delta;
            int notches = _wheel / Mouse.MouseWheelDeltaForOneLine;
            if (notches == 0) return;
            _wheel -= notches * Mouse.MouseWheelDeltaForOneLine;
            Step(-Math.Sign(notches)); // 下に回すと次へ
        }

        private void Step(int step) => Select((_index + step + _cards.Count) % _cards.Count);

        private void Select(int index)
        {
            if (index == _index) return;
            _index = index;
            for (int i = 0; i < _cards.Count; i++)
            {
                var c = _cards[i];
                var (pose, rank) = Place(i);
                // 切り替えの途中でさらに切り替えても、今の位置から動き出す
                c.From = c.Now;
                c.To = pose;
                c.NextRank = rank;
                // 左右の反対側へ回り込むカードは、ほかのカードの後ろを通す
                if (c.From.X * c.To.X < 0) c.Rank = 1;
            }
            ShowPosition();
            if (_animate)
            {
                _clock.Restart();
                if (_frame == null)
                {
                    _frame = (_, _) => Advance(_clock.Elapsed.TotalMilliseconds / (220 * Anim.Factor));
                    CompositionTarget.Rendering += _frame;
                }
            }
            else Advance(1);
        }

        private void Advance(double progress)
        {
            double p = Math.Clamp(progress, 0, 1), eased = 1 - Math.Pow(1 - p, 3);
            foreach (var c in _cards)
            {
                c.Now = Pose.Lerp(c.From, c.To, eased);
                // 動きの中ほどで前後を入れ替える（回り込むカードは、動き終わるまで後ろのまま）
                if (p >= 1 || (eased >= 0.5 && c.From.X * c.To.X >= 0)) c.Rank = c.NextRank;
                Apply(c);
            }
            UpdateThumbnails();
            if (p < 1) return;
            StopAnimation();
            Mouse.Synchronize(); // 動いたカードに合わせて、ホバーの表示を付け直す
        }

        private void Apply(Card c)
        {
            c.Scale.ScaleX = c.Scale.ScaleY = c.Now.Scale;
            c.Move.X = c.Now.X;
            c.Head.Opacity = 0.55 + 0.45 * c.Now.Light;
            // 閉じる「×」は手前のカードだけ（後ろのカードは、クリックすると手前に出る）
            c.Close.Visibility = c.To.Light >= 1 ? Visibility.Visible : Visibility.Hidden;
            Panel.SetZIndex(c.Frame, c.Rank);
        }

        private void ShowPosition()
        {
            var t = Theme.Current;
            _counter.Text = $"{_index + 1}/{_cards.Count}";
            for (int i = 0; i < _dots.Count; i++)
            {
                _dots[i].Width = i == _index ? 14 : 6;
                _dots[i].Background = i == _index ? t.AccentBrush : t.TextSecondary;
                _dots[i].Opacity = i == _index ? 1 : 0.5;
            }
        }

        public void Register(IntPtr destination)
        {
            foreach (var c in _cards)
            {
                if (DwmRegisterThumbnail(destination, c.Window, out var thumb) == 0 && thumb != IntPtr.Zero) c.Thumb = thumb;
                else Log.Info($"thumbnail register failed: {c.Window}");
            }
        }

        private HwndSource? _source;

        /// <summary>今のカードの位置に合わせて、各映像の描画先と切り抜く範囲を設定する。</summary>
        public void UpdateThumbnails(HwndSource source)
        {
            _source = source;
            UpdateThumbnails();
        }

        private void UpdateThumbnails()
        {
            var src = _source;
            if (src == null || src.IsDisposed || src.RootVisual is not Visual root || src.CompositionTarget == null) return;
            var m = src.CompositionTarget.TransformToDevice;
            // 手前から順に。重なりの順が同じなら、後に追加したカードが手前（WPF の描き方と同じ）
            var covers = new List<Rect>();
            foreach (var (c, _) in _cards.Select((c, i) => (c, i)).OrderByDescending(x => x.c.Rank).ThenByDescending(x => x.i))
            {
                if (BoundsIn(c.Frame, root) is not { } frame) continue;
                if (c.Thumb != IntPtr.Zero && BoundsIn(c.Slot, root) is { } slot) Draw(c, slot, covers, m);
                covers.Add(frame);
            }
        }

        private static Rect? BoundsIn(FrameworkElement e, Visual root)
        {
            if (e.ActualWidth <= 0 || e.ActualHeight <= 0) return null;
            try { return e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight)); }
            catch { return null; }
        }

        private static void Draw(Card c, Rect slot, List<Rect> covers, Matrix toDevice)
        {
            var props = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TNP_VISIBLE };
            if (DwmQueryThumbnailSourceSize(c.Thumb, out var size) == 0 && size.cx > 0 && size.cy > 0)
            {
                // 元のウィンドウの縦横比のまま、枠に収める
                double scale = Math.Min(slot.Width / size.cx, slot.Height / size.cy);
                double w = size.cx * scale, h = size.cy * scale;
                double x = slot.X + (slot.Width - w) * (c.Now.Align + 1) / 2, y = slot.Y + (slot.Height - h) / 2;
                double left = x, right = x + w;
                // 手前のカードに隠れる分を左右から削る（両側にはみ出すときは、広いほうを残す）
                foreach (var cover in covers)
                {
                    if (cover.Right <= left || cover.Left >= right || cover.Bottom <= y || cover.Top >= y + h) continue;
                    if (cover.Left - left >= right - cover.Right) right = Math.Max(left, cover.Left);
                    else left = Math.Min(right, cover.Right);
                }
                var tl = toDevice.Transform(new Point(left, y));
                var br = toDevice.Transform(new Point(right, y + h));
                var destination = new RECT { Left = (int)Math.Round(tl.X), Top = (int)Math.Round(tl.Y), Right = (int)Math.Round(br.X), Bottom = (int)Math.Round(br.Y) };
                var source = new RECT
                {
                    Left = (int)Math.Round((left - x) / w * size.cx), Top = 0,
                    Right = (int)Math.Round((right - x) / w * size.cx), Bottom = size.cy,
                };
                if (destination.Right > destination.Left && source.Right > source.Left)
                {
                    props.dwFlags |= DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY;
                    props.rcDestination = destination;
                    props.rcSource = source;
                    props.opacity = (byte)Math.Round(255 * (0.6 + 0.4 * c.Now.Light));
                    props.fVisible = 1;
                }
            }
            DwmUpdateThumbnailProperties(c.Thumb, ref props);
        }

        private void StopAnimation()
        {
            if (_frame != null) CompositionTarget.Rendering -= _frame;
            _frame = null;
        }

        public void Dispose()
        {
            StopAnimation();
            foreach (var c in _cards)
            {
                if (c.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(c.Thumb);
                c.Thumb = IntPtr.Zero;
            }
            _source = null;
        }
    }
}
