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
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ModernShell.Native;

using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>
/// ポップアップ（メニュー・ドロップダウン一覧）を Windows 11 と同じ見た目にする。
/// 透過ウィンドウ（レイヤード）にせず、DWM に角丸・枠・影を描かせ、背景は Acrylic ぼかしにする。
/// </summary>
public static class FluentSurface
{
    private static readonly List<Popup> _openList = new();
    private static int _open => _openList.Count;
    /// <summary>いずれかのメニュー/一覧が開いているか（開いている間はタスクバーのボタンを作り直さない）。</summary>
    public static bool AnyOpen => _open > 0;

    /// <summary>Popup を Fluent 表面として構成する。中身（Child）は背景なしの要素を渡すこと。</summary>
    public static Popup Create(UIElement content, UIElement target, PlacementMode placement)
    {
        var t = Theme.Current;
        var popup = new Popup
        {
            AllowsTransparency = false, // レイヤードにすると DWM の角丸・影・Acrylic が効かない
            StaysOpen = false,
            PlacementTarget = target,
            Placement = placement,
            PopupAnimation = PopupAnimation.None,
            Child = new Border
            {
                Background = Theme.TranslucentPopups ? Brushes.Transparent : t.SolidBackground,
                Child = content,
            },
        };
        // タスクバー等に重ならないよう、上下に出すときは少し離す（Windows のメニューと同じくらいの間隔）
        if (placement == PlacementMode.Top) popup.VerticalOffset = -8;
        else if (placement == PlacementMode.Bottom) popup.VerticalOffset = 8;
        popup.Opened += (_, _) => { _openList.Add(popup); ApplyBackdrop(popup); };
        popup.Closed += (_, _) => _openList.Remove(popup);
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

    private static void ApplyBackdrop(Popup popup)
    {
        if (popup.Child == null || PresentationSource.FromVisual(popup.Child) is not HwndSource src) return;
        var t = Theme.Current;
        var hwnd = src.Handle;

        int dark = t.IsDark ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int corner = t.WindowCorner;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        if (!Theme.TranslucentPopups)
        {
            if (src.CompositionTarget != null) src.CompositionTarget.BackgroundColor = t.SolidColor;
            return;
        }
        if (src.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        var m = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(hwnd, ref m);
        var c = t.FlyoutColor;
        uint abgr = (0xD0u << 24) | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R;
        Backdrop.SetAccent(hwnd, Backdrop.AccentState.AcrylicBlurBehind, abgr);
    }

    /// <summary>開くときの動き（WinUI 風: 少し伸びながらフェードイン）。originY は伸び始める位置 (0–1)。</summary>
    /// <summary>
    /// ボタンの真上（タスクバーが上にあるときは真下）の左右中央に出す。画面端では Windows が内側へずらす。
    /// 「^」の一覧・ウィンドウのプレビュー・デスクトップ一覧で共通。
    /// </summary>
    public static void PlaceCentered(Popup popup, FrameworkElement anchor, bool barAtTop, double gapDip = 8)
    {
        popup.PlacementTarget = anchor;
        popup.Placement = PlacementMode.Custom;
        popup.VerticalOffset = 0;
        double gap = gapDip * VisualTreeHelper.GetDpi(anchor).DpiScaleY;
        popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            double x = (targetSize.Width - popupSize.Width) / 2;
            var above = new CustomPopupPlacement(new Point(x, -popupSize.Height - gap), PopupPrimaryAxis.Horizontal);
            var below = new CustomPopupPlacement(new Point(x, targetSize.Height + gap), PopupPrimaryAxis.Horizontal);
            return barAtTop ? new[] { below, above } : new[] { above, below };
        };
    }

    /// <summary>
    /// Windows のタスクバーのフライアウトと同じく、少し下（上に出すときは上）からスライドしながら現れる。
    /// ポップアップの位置そのものを動かすので、Acrylic の背景ごと動く。
    /// </summary>
    public static void SlideIn(Popup popup, bool fromBelow, double distance = 24)
    {
        popup.Opened += (_, _) =>
        {
            double baseOffset = popup.VerticalOffset;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            popup.BeginAnimation(Popup.VerticalOffsetProperty,
                new DoubleAnimation(baseOffset + (fromBelow ? distance : -distance), baseOffset, TimeSpan.FromMilliseconds(220))
                { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            if (popup.Child is UIElement child)
                child.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        };
    }

    public static void AnimateOpen(FrameworkElement content, double originY)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new ScaleTransform(1, 0.85);
        content.RenderTransformOrigin = new Point(0.5, Math.Clamp(originY, 0, 1));
        content.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }
}

/// <summary>
/// Windows 11 (WinUI 3) の ComboBox を再現したドロップダウン。
///  - ボタン: 半透明の地 + 下端だけ濃い枠、押すと山形アイコンが沈む
///  - 一覧: 選択中の項目がボタンにぴったり重なる位置で開く、選択中は左端にアクセント色の縦線、Acrylic 背景
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

        // 選択中の項目がボタンの上にぴったり重なる位置に出す（WinUI の ComboBox と同じ）
        _popup = FluentSurface.Create(list, this, PlacementMode.Relative);
        _popup.Width = ActualWidth;
        _popup.HorizontalOffset = 0;
        _popup.VerticalOffset = (ActualHeight - ItemHeight) / 2 - (ListPadding + 2 + index * ItemPitch);
        _popup.KeyDown += (_, e) => { if (e.Key == Key.Escape) _popup.IsOpen = false; };
        _popup.Closed += (_, _) => Focus();
        _popup.IsOpen = true;
        Flyout.ToggleGuard(_popup, this);

        double total = ListPadding * 2 + _options.Length * ItemPitch;
        if (Settings.Current?.AnimateMenus != false) FluentSurface.AnimateOpen(list, (ListPadding + index * ItemPitch + ItemPitch / 2) / total);
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
