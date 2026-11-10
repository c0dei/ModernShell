using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ModernShell.Native;

using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>
/// ポップアップ（メニュー・ドロップダウン一覧）を Windows 11 と同じ見た目にする。
/// 背景・角丸・枠線は <see cref="SurfacePaint"/> で自分で描く（GPU が無い PC でも同じ見た目）。
/// </summary>
public static class FluentSurface
{
    private static readonly List<Popup> _openList = new();
    private static int _open => _openList.Count;
    /// <summary>いずれかのメニュー/一覧が開いているか（開いている間はタスクバーのボタンを作り直さない）。</summary>
    public static bool AnyOpen => _open > 0;

    /// <summary>
    /// Popup を Fluent 表面として構成する。中身（Child）は背景なしの要素を渡すこと。
    /// 背景・角丸・枠線は自分で描く（透過ウィンドウ）。初めはスタートメニューと同じ背景で、
    /// タスクバーから開くものは <see cref="UseTaskbarLook"/> でタスクバーと同じ背景に切り替える。
    /// </summary>
    public static Popup Create(UIElement content, UIElement target, PlacementMode placement)
    {
        var popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = false,
            PlacementTarget = target,
            Placement = placement,
            PopupAnimation = PopupAnimation.None,
        };
        // ポップアップは開いた場所のフォントを受け継ぐ（タスクバー・スタートからなら設定のフォント、設定画面ならシステムのフォント）
        var state = new LookState { Content = (FrameworkElement)content, Font = TextElement.GetFontFamily(target) };
        _looks.Add(popup, state);
        var s = Settings.Current ?? new Settings();
        // 設定画面から開くもの（ドロップダウンなど）は、スタートメニューの背景設定とは関係なく単色にする。
        // それ以外（スタートメニュー・ウィジェットなど）はスタートメニューと同じ背景、タスクバーからのものは UseTaskbarLook で切り替える
        if (Window.GetWindow(target) is SettingsWindow) Paint(popup, state, SurfaceStyle.Solid, 100);
        else Paint(popup, state, s.StartMenuBackground, s.StartMenuOpacity);
        // タスクバー等に重ならないよう、上下に出すときは少し離す（Windows のメニューと同じくらいの間隔）。
        // 周りに影の余白があるので、その分ずらして見た目の位置を合わせる
        const double pad = SurfacePaint.ShadowPad;
        if (placement == PlacementMode.Top) { popup.VerticalOffset = -8 + pad; popup.HorizontalOffset = -pad; }
        else if (placement == PlacementMode.Bottom) { popup.VerticalOffset = 8 - pad; popup.HorizontalOffset = -pad; }
        // 縦のバーから横へ開くとき: 上端をボタンに合わせ、ボタンとのあいだを 8 だけ空ける
        else if (placement == PlacementMode.Left) { popup.HorizontalOffset = -8 + pad; popup.VerticalOffset = -pad; }
        else if (placement == PlacementMode.Right) { popup.HorizontalOffset = 8 - pad; popup.VerticalOffset = -pad; }
        else if (placement == PlacementMode.MousePoint) { popup.VerticalOffset = -pad; popup.HorizontalOffset = -pad; }
        popup.Opened += (_, _) =>
        {
            _openList.Add(popup);
            FollowBlur(popup, state);
            if (!state.Hooked && popup.Child != null && PresentationSource.FromVisual(popup.Child) is HwndSource src)
            {
                state.Hooked = true;
                SurfacePaint.PassShadowClicks(src);
            }
        };
        popup.Closed += (_, _) =>
        {
            _openList.Remove(popup);
            if (state.Follow != null) CompositionTarget.Rendering -= state.Follow;
            state.Follow = null;
        };
        return popup;
    }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Win32.POINT pt);

    /// <summary>
    /// 画面のどこかがクリックされたとき（全体のマウス監視から呼ばれる）。ModernShell 以外のウィンドウなら開いているメニューを全部閉じる。
    /// ※ タスクバーはアクティブにならない作りなので、他のアプリをクリックしてもメニューに通知が来ないため。
    ///   ModernShell 自身のウィンドウ上のクリックは WPF 側の処理（外側クリックで閉じる / 開閉トグル）に任せる。
    /// </summary>
    public static void CloseIfOutside(Win32.POINT pt)
    {
        if (_openList.Count == 0) return;
        Win32.GetWindowThreadProcessId(WindowFromPoint(pt), out var pid);
        if (pid == (uint)Environment.ProcessId) return;
        foreach (var p in _openList.ToArray()) p.IsOpen = false;
    }

    /// <summary>Esc で一番手前（最後に開いた）のメニューを閉じる。</summary>
    public static void CloseTopmost()
    {
        if (_openList.Count > 0) _openList[^1].IsOpen = false;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Popup, LookState> _looks = new();

    private sealed class LookState
    {
        public required FrameworkElement Content;
        public FontFamily? Font;
        public bool Hooked;
        public SurfacePaint.Painted? Painted;
        public EventHandler? Follow;
        public bool Taskbar;
    }

    private static void Paint(Popup popup, LookState state, SurfaceStyle style, int opacity)
    {
        // 前の背景から中身を外して包み直す
        if (state.Content.Parent is Border old) old.Child = null;
        popup.Child = null;
        state.Painted = SurfacePaint.Wrap(state.Content, style, opacity, new CornerRadius(Math.Max(0, Theme.Current.Radius)), shadow: true);
        if (state.Font != null) TextElement.SetFontFamily(state.Painted.Root, state.Font);
        popup.Child = state.Painted.Root;
    }

    public static bool HasTaskbarLook(Popup popup) => _looks.TryGetValue(popup, out var s) && s.Taskbar;

    /// <summary>
    /// ポップアップの背景をタスクバーと同じ設定（「タスクバーの背景」と「タスクバーの不透明度」）にする。開く前に呼ぶ。
    /// プレビュー・デスクトップ一覧・「^」の一覧・タスクバーの右クリックメニューで使う。
    /// </summary>
    public static void UseTaskbarLook(Popup popup)
    {
        if (Settings.Current is not { } s || !_looks.TryGetValue(popup, out var state) || state.Taskbar) return;
        state.Taskbar = true;
        Paint(popup, state, s.TaskbarBackground, s.TaskbarOpacity);
    }

    /// <summary>ぼかしの画像を、ポップアップの位置に合わせ続ける（スライドして現れる間も）。</summary>
    private static void FollowBlur(Popup popup, LookState state)
    {
        if (state.Painted?.Blur == null || popup.Child == null || PresentationSource.FromVisual(popup.Child) is not HwndSource src) return;
        var painted = state.Painted;
        var hwnd = src.Handle;
        state.Follow = (_, _) => painted.Place(hwnd);
        state.Follow(null, EventArgs.Empty);
        CompositionTarget.Rendering += state.Follow;
    }

    /// <summary>開くときの動き（WinUI 風: 少し伸びながらフェードイン）。originY は伸び始める位置 (0–1)。</summary>
    /// <summary>
    /// ボタンの画面の内側（下のバーなら真上、上のバーなら真下、左のバーなら右隣、右のバーなら左隣）の中央に出す。
    /// 画面端では Windows が内側へずらす。「^」の一覧・ウィンドウのプレビュー・デスクトップ一覧で共通。
    /// </summary>
    /// <param name="edge">タスクバーのある画面の端。</param>
    public static void PlaceCentered(Popup popup, FrameworkElement anchor, TaskbarEdge edge, double gapDip = 8)
    {
        UseTaskbarLook(popup);
        popup.PlacementTarget = anchor;
        popup.Placement = PlacementMode.Custom;
        popup.VerticalOffset = 0;
        popup.HorizontalOffset = 0; // 中央合わせなので、影の余白のずらしは要らない
        // 影の余白の分だけ近づけて、見た目の間隔を gapDip にする
        var dpi = VisualTreeHelper.GetDpi(anchor);
        double gapY = (gapDip - SurfacePaint.ShadowPad) * dpi.DpiScaleY, gapX = (gapDip - SurfacePaint.ShadowPad) * dpi.DpiScaleX;
        popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            double x = (targetSize.Width - popupSize.Width) / 2, y = (targetSize.Height - popupSize.Height) / 2;
            var above = new CustomPopupPlacement(new Point(x, -popupSize.Height - gapY), PopupPrimaryAxis.Horizontal);
            var below = new CustomPopupPlacement(new Point(x, targetSize.Height + gapY), PopupPrimaryAxis.Horizontal);
            var left = new CustomPopupPlacement(new Point(-popupSize.Width - gapX, y), PopupPrimaryAxis.Vertical);
            var right = new CustomPopupPlacement(new Point(targetSize.Width + gapX, y), PopupPrimaryAxis.Vertical);
            return edge switch
            {
                TaskbarEdge.Top => new[] { below, above },
                TaskbarEdge.Left => new[] { right, left },
                TaskbarEdge.Right => new[] { left, right },
                _ => new[] { above, below },
            };
        };
    }

    /// <summary>タスクバーのある端から、画面の内側へ向かってスライドして現れる。</summary>
    public static void SlideIn(Popup popup, TaskbarEdge edge, double distance = 24)
    {
        popup.Opened += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            // 横のバーなら縦に、縦のバーなら横にずらしてから、本来の位置へ
            bool vertical = edge is TaskbarEdge.Left or TaskbarEdge.Right;
            var prop = vertical ? Popup.HorizontalOffsetProperty : Popup.VerticalOffsetProperty;
            double baseOffset = vertical ? popup.HorizontalOffset : popup.VerticalOffset;
            // 画面の端の側から出てくる: 下のバーなら下から（+）、上なら上から（-）、左なら左から（-）、右なら右から（+）
            double from = edge is TaskbarEdge.Bottom or TaskbarEdge.Right ? distance : -distance;
            popup.BeginAnimation(prop, new DoubleAnimation(baseOffset + from, baseOffset, Anim.Ms(220))
                { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            if (popup.Child is UIElement child)
                child.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Anim.Ms(140)));
        };
    }

    /// <summary>
    /// Windows のタスクバーのフライアウトと同じく、少し下（上に出すときは上）からスライドしながら現れる。
    /// ポップアップの位置そのものを動かすので、背景ごと動く。
    /// </summary>
    public static void SlideIn(Popup popup, bool fromBelow, double distance = 24)
    {
        popup.Opened += (_, _) =>
        {
            double baseOffset = popup.VerticalOffset;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            popup.BeginAnimation(Popup.VerticalOffsetProperty,
                new DoubleAnimation(baseOffset + (fromBelow ? distance : -distance), baseOffset, Anim.Ms(220))
                { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            if (popup.Child is UIElement child)
                child.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Anim.Ms(140)));
        };
    }

    public static void AnimateOpen(FrameworkElement content, double originY)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new ScaleTransform(1, 0.85);
        content.RenderTransformOrigin = new Point(0.5, Math.Clamp(originY, 0, 1));
        content.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, Anim.Ms(180)) { EasingFunction = ease });
        content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Anim.Ms(120)));
    }
}

/// <summary>
/// Windows 11 (WinUI 3) の ComboBox を再現したドロップダウン。
///  - ボタン: 半透明の地 + 下端だけ濃い枠、押すと山形アイコンが沈む
///  - 一覧: 選択中の項目がボタンにぴったり重なる位置で開く、選択中は左端にアクセント色の縦線
/// </summary>
public sealed class FluentComboBox : Border
{
    private const double ItemHeight = 32, ItemPitch = 36, ListPadding = 2;

    private readonly (string value, string label)[] _options;
    private readonly Action<string> _onSelect;
    private readonly TranslateTransform _chevronShift = new();
    private string _value;
    private bool _pressed;
    private Popup? _popup;

    /// <summary>一覧の各項目をそのフォントで表示する（フォントの選択用）。null ならふつうのフォント。</summary>
    public Func<string, FontFamily?>? ItemFont { get; init; }

    /// <summary>一度に見せる項目の数。これより多いと一覧をスクロールにして、ボタンの下に出す。</summary>
    private const int MaxVisible = 12;

    public FluentComboBox((string value, string label)[] options, string current, Action<string> onSelect)
    {
        _options = options;
        _value = options.Any(o => o.value == current) ? current : options[0].value;
        _onSelect = onSelect;

        var t = Theme.Current;
        MinWidth = 180;
        Height = 32;
        CornerRadius = t.Corner(4);
        BorderThickness = new Thickness(1);
        BorderBrush = t.ControlBorder;
        Background = t.ControlFill;
        Padding = new Thickness(11, 0, 11, 0);
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;

        var label = UiKit.Label(LabelOf(_value), 14);
        var chevron = UiKit.Glyph("", 12, t.TextSecondary);
        chevron.HorizontalAlignment = HorizontalAlignment.Right;
        chevron.RenderTransform = _chevronShift;
        var g = new Grid();
        g.Children.Add(label);
        g.Children.Add(chevron);
        label.Margin = new Thickness(0, 0, 24, 0);
        Child = g;
    }

    private string LabelOf(string v) => _options.FirstOrDefault(o => o.value == v).label ?? v;

    private void Refresh()
    {
        var t = Theme.Current;
        Background = _pressed ? t.ControlFillPressed : IsMouseOver ? t.ControlFillHover : t.ControlFill;
        BorderBrush = IsKeyboardFocused ? t.AccentBrush : t.ControlBorder;
        Opacity = IsEnabled ? 1 : 0.4;
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); Refresh(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _pressed = false; Press(false); Refresh(); }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); Refresh(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); Refresh(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pressed = true;
        Press(true);
        Refresh();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        bool fire = _pressed && IsMouseOver;
        _pressed = false;
        Press(false);
        Refresh();
        if (fire) Open();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space or Key.F4 || (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            Open();
            e.Handled = true;
        }
    }

    /// <summary>押下中は山形アイコンを少し下げる（WinUI と同じ演出）。</summary>
    private void Press(bool down) =>
        _chevronShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(down ? 2 : 0, TimeSpan.FromMilliseconds(80)));

    private void Open()
    {
        if (_popup?.IsOpen == true) { _popup.IsOpen = false; return; }
        var t = Theme.Current;
        int index = Math.Max(0, Array.FindIndex(_options, o => o.value == _value));

        var list = new StackPanel { Margin = new Thickness(0, ListPadding, 0, ListPadding) };
        Tile? selectedTile = null;
        foreach (var (value, label) in _options)
        {
            bool sel = value == _value;
            var row = new Grid();
            row.Children.Add(new Border
            {
                Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left,
                Background = sel ? t.AccentBrush : Brushes.Transparent,
            });
            var text = UiKit.Label(label, 14);
            text.Margin = new Thickness(12, 0, 0, 0);
            if (ItemFont?.Invoke(value) is { } font) text.FontFamily = font;
            row.Children.Add(text);
            var item = new Tile { Height = ItemHeight, Margin = new Thickness(4, 2, 4, 2), Padding = new Thickness(0, 0, 12, 0), Child = row, IsSelected = sel };
            var v = value;
            item.Click += _ =>
            {
                if (_popup != null) _popup.IsOpen = false;
                if (v == _value) return;
                _value = v;
                if (Child is Grid g && g.Children[0] is TextBlock tb) tb.Text = LabelOf(v);
                _onSelect(v);
            };
            if (sel) selectedTile = item;
            list.Children.Add(item);
        }

        // 項目が多いとき（フォントの一覧など）はスクロールにして、ボタンの下に出す
        bool scroll = _options.Length > MaxVisible;
        FrameworkElement content = list;
        if (scroll)
            content = new ScrollViewer
            {
                Content = list, MaxHeight = ListPadding * 2 + MaxVisible * ItemPitch, Focusable = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

        // 選択中の項目がボタンの上にぴったり重なる位置に出す（WinUI の ComboBox と同じ）
        _popup = FluentSurface.Create(content, this, PlacementMode.Relative);
        // 周りに影の余白があるので、その分広げてずらし、一覧がボタンにぴったり重なるようにする
        const double pad = SurfacePaint.ShadowPad;
        _popup.Width = Math.Max(ActualWidth, scroll ? 280 : 0) + pad * 2;
        _popup.HorizontalOffset = -pad;
        _popup.VerticalOffset = scroll
            ? ActualHeight + 4 - pad
            : (ActualHeight - ItemHeight) / 2 - (ListPadding + 2 + index * ItemPitch) - pad;
        _popup.KeyDown += (_, e) => { if (e.Key == Key.Escape) _popup.IsOpen = false; };
        _popup.Closed += (_, _) => Focus();
        _popup.IsOpen = true;
        Flyout.ToggleGuard(_popup, this);

        double total = ListPadding * 2 + _options.Length * ItemPitch;
        if (Settings.Current?.AnimateMenus != false) FluentSurface.AnimateOpen(list, scroll ? 0 : (ListPadding + index * ItemPitch + ItemPitch / 2) / total);
        if (scroll && content is ScrollViewer sv)
            sv.Dispatcher.BeginInvoke(() => sv.ScrollToVerticalOffset(Math.Max(0, index - MaxVisible / 2) * ItemPitch), System.Windows.Threading.DispatcherPriority.Loaded);
        selectedTile?.Focus();
    }
}

/// <summary>
/// Windows 11 (WinUI) のスライダー。細いトラック + アクセント色の塗り + 丸いつまみ（中にアクセント色の点）。
/// ドラッグ中は ValueChanged、離したときに ValueCommitted を出す。
/// </summary>
public sealed class FluentSlider : Grid
{
    private readonly double _min, _max;
    private readonly Border _fill;
    private readonly Grid _thumb;
    private readonly Ellipse _dot;
    private readonly Canvas _canvas;
    private double _value;

    public event Action<double>? ValueChanged;
    public event Action<double>? ValueCommitted;

    public double Value => _value;

    public FluentSlider(double min, double max, double value, double width = 220)
    {
        var t = Theme.Current;
        _min = min; _max = max; _value = Math.Clamp(value, min, max);
        Width = width;
        Height = 32;
        Background = Brushes.Transparent; // 全体をつかめるように
        Cursor = Cursors.Hand;

        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = t.IsDark ? new SolidColorBrush(Color.FromArgb(0x8B, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(0x72, 0, 0, 0)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        _fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = t.AccentBrush, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        _dot = new Ellipse { Width = 12, Height = 12, Fill = t.AccentBrush };
        _thumb = new Grid { Width = 20, Height = 20 };
        _thumb.Children.Add(new Ellipse { Fill = t.SolidBackground, Stroke = t.ControlBorder, StrokeThickness = 1 });
        _thumb.Children.Add(_dot);
        _canvas = new Canvas { IsHitTestVisible = false };
        _canvas.Children.Add(_thumb);
        Children.Add(track);
        Children.Add(_fill);
        Children.Add(_canvas);

        MouseLeftButtonDown += (_, e) => { CaptureMouse(); SetFromMouse(e); _dot.Width = _dot.Height = 10; e.Handled = true; };
        MouseMove += (_, e) => { if (IsMouseCaptured) SetFromMouse(e); };
        MouseLeftButtonUp += (_, _) => { if (!IsMouseCaptured) return; ReleaseMouseCapture(); _dot.Width = _dot.Height = 12; ValueCommitted?.Invoke(_value); };
        MouseEnter += (_, _) => { if (!IsMouseCaptured) _dot.Width = _dot.Height = 14; };
        MouseLeave += (_, _) => { if (!IsMouseCaptured) _dot.Width = _dot.Height = 12; };
        Focusable = true;
        KeyDown += (_, e) =>
        {
            double step = (_max - _min) / 20;
            if (e.Key is Key.Left or Key.Down) { Set(_value - step); ValueCommitted?.Invoke(_value); e.Handled = true; }
            else if (e.Key is Key.Right or Key.Up) { Set(_value + step); ValueCommitted?.Invoke(_value); e.Handled = true; }
        };
        SizeChanged += (_, _) => Layout();
        Loaded += (_, _) => Layout();
    }

    private void SetFromMouse(MouseEventArgs e)
    {
        double usable = Math.Max(1, ActualWidth - 20);
        double ratio = Math.Clamp((e.GetPosition(this).X - 10) / usable, 0, 1);
        Set(_min + ratio * (_max - _min));
    }

    private void Set(double v)
    {
        v = Math.Round(Math.Clamp(v, _min, _max));
        if (v == _value) return;
        _value = v;
        Layout();
        ValueChanged?.Invoke(v);
    }

    private void Layout()
    {
        double usable = Math.Max(1, ActualWidth - 20);
        double x = (_value - _min) / (_max - _min) * usable;
        _fill.Width = Math.Max(0, x);
        Canvas.SetLeft(_thumb, x);
        Canvas.SetTop(_thumb, (ActualHeight - 20) / 2);
    }
}
