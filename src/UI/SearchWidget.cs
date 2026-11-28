using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ModernShell.Core;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>
/// 検索のウィジェット。クリックで、検索を開く（もう一度押すと閉じる）。
/// 設定が ModernShell のスタートなら、スタート メニューを検索ボックスにフォーカスを当てて開く。Windows 標準のスタートなら Windows の検索（Win+S）。
/// 「検索ボックス」の形では、バーの上で直接入力して Enter を押すと、その言葉で検索する。
/// </summary>
public sealed class SearchBarWidget : BarWidget
{
    public override string Id => "search";

    /// <summary>検索を開く処理（App が設定する）。</summary>
    public static Action? OpenStartSearch { get; set; }
    /// <summary>入力した言葉で検索を開く処理（App が設定する）。</summary>
    public static Action<string>? OpenStartSearchWith { get; set; }

    private SearchLook.InputBox? _input;

    protected override FrameworkElement BuildContent() =>
        SearchLook.Card(this, T, Vertical, L.T("検索"), SearchLook.Kind.Search, (Settings.Current ?? new Settings()).SearchWidgetStyle, Submit, out _input);

    public override bool OwnsMouse(DependencyObject source) => _input?.Owns(source) == true;

    private void Submit(string text) => OpenStartSearchWith?.Invoke(text);

    public override bool OnClick()
    {
        // 開いているなら、もう一度押して閉じる（バーはクリックしても前面を奪わないので、ここで判定できる）
        if (IsWindowsSearchForeground())
        {
            Input.Tap(Win32.VK_ESCAPE);
            return true;
        }
        // キー操作で開くので、管理者として動くアプリが前面でも届くよう、先にバーを前面にする
        if (Card != null) TaskbarController.TakeForeground(Card);
        OpenStartSearch?.Invoke();
        return true;
    }

    public override IEnumerable<MenuItemSpec> ExtraMenu() => SearchLook.StyleMenu(Settings.Current?.SearchWidgetStyle ?? SearchStyle.Icon, (c, v) => c.SearchWidgetStyle = v);

    /// <summary>Windows の検索（Win+S）が前面で開いているか。</summary>
    private static bool IsWindowsSearchForeground()
    {
        try
        {
            var h = Win32.GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            Win32.GetWindowThreadProcessId(h, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName is "SearchHost" or "SearchApp" or "SearchUI";
        }
        catch { return false; }
    }
}

/// <summary>
/// Everything のウィジェット。クリックで、Everything の検索ウィンドウを開く（もう一度押すと閉じる）。Everything が入っている PC で使える。
/// 開くウィンドウは、アプリ独自の検索ウィンドウ（Everything SDK）か、Everything 本体のウィンドウを選べる。
/// 「検索ボックス」の形では、バーの上で直接入力して Enter を押すと、その言葉で検索する。
/// </summary>
public sealed class EverythingBarWidget : BarWidget
{
    public override string Id => "everything";

    private static EverythingWindow? _panel;
    private static EverythingWindow Panel => _panel ??= new EverythingWindow(Settings.Current ?? new Settings());

    private SearchLook.InputBox? _input;

    private static EverythingOpens Opens => (Settings.Current ?? new Settings()).EverythingWidgetOpens;

    protected override FrameworkElement BuildContent() =>
        SearchLook.Card(this, T, Vertical, "Everything", SearchLook.Kind.Everything, (Settings.Current ?? new Settings()).EverythingWidgetStyle, Submit, out _input);

    public override bool OwnsMouse(DependencyObject source) => _input?.Owns(source) == true;

    private void Submit(string text)
    {
        if (Opens == EverythingOpens.Panel && EverythingSdk.Available) Panel.ShowSearch(text);
        else Everything.OpenWithQuery(text);
    }

    public override bool OnClick()
    {
        if (Opens == EverythingOpens.Panel && EverythingSdk.Available)
        {
            Panel.Toggle(); // 開いていれば閉じる（バーはクリックしても前面を奪わないので、開いている間も押せる）
            return true;
        }
        if (Everything.CloseIfForeground()) return true;
        if (Card != null) TaskbarController.TakeForeground(Card);
        Everything.Open();
        return true;
    }

    public override IEnumerable<MenuItemSpec> ExtraMenu()
    {
        var s = Settings.Current ?? new Settings();
        var items = new List<MenuItemSpec>
        {
            WidgetSettingsMenu.Choice("", L.T("クリックで開く"), s.EverythingWidgetOpens, new[]
            {
                (EverythingOpens.Panel, L.T("アプリの検索ウィンドウ")),
                (EverythingOpens.Window, L.T("Everything のウィンドウ")),
            }, (c, v) => c.EverythingWidgetOpens = v),
        };
        items.AddRange(SearchLook.StyleMenu(s.EverythingWidgetStyle, (c, v) => c.EverythingWidgetStyle = v));
        return items;
    }
}

internal static class SearchLook
{
    public enum Kind { Search, Everything }

    public static IEnumerable<MenuItemSpec> StyleMenu(SearchStyle current, Action<Settings, SearchStyle> set) => new[]
    {
        WidgetSettingsMenu.Choice("", L.T("表示の形"), current, new[] { (SearchStyle.Icon, L.T("アイコンのみ")), (SearchStyle.Box, L.T("検索ボックス")) }, set),
        MenuItemSpec.Separator,
    };

    /// <summary>フォルダに虫めがねを重ねたアイコン（Everything 用）。</summary>
    public static FrameworkElement FolderSearchIcon(double size, Brush brush)
    {
        Geometry G(string d) { var g = Geometry.Parse(d); g.Freeze(); return g; }
        var canvas = new Canvas { Width = 24, Height = 24 };
        // フォルダ（右下は虫めがねのために空ける）
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = G("M10.5,19.5 H5 A2,2 0 0 1 3,17.5 V6.5 A2,2 0 0 1 5,4.5 H9 L11,6.5 H19 A2,2 0 0 1 21,8.5 V10"),
            Stroke = brush, StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        canvas.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Stroke = brush, StrokeThickness = 1.7, Margin = new Thickness(11.5, 12, 0, 0) });
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = G("M20,19.5 L22.5,22"), Stroke = brush, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        return new Viewbox { Width = size, Height = size, Child = canvas, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    public static FrameworkElement Icon(Kind kind, double size, Brush brush) =>
        kind == Kind.Everything ? FolderSearchIcon(size + 1, brush) : UiKit.Glyph("", size, brush);

    /// <summary>
    /// ウィジェットのカード。縦のバーは幅が狭いので、いつもアイコンのみ。
    /// 「検索ボックス」の形では、実際に入力できる欄を置く（<paramref name="submit"/> は Enter で呼ばれる。null なら見た目だけ）。
    /// </summary>
    public static FrameworkElement Card(BarWidget w, Theme t, bool vertical, string tip, Kind kind, SearchStyle style, Action<string>? submit, out InputBox? input)
    {
        input = null;
        w.SetPadding(new Thickness(0));
        var icon = Icon(kind, vertical ? 16 : 17, t.Text);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        FrameworkElement content = icon;
        if (style == SearchStyle.Box && !vertical)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
            icon.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(icon);
            string hint = kind == Kind.Everything ? L.T("ファイル名で検索") : L.T("検索");
            if (submit != null)
            {
                input = new InputBox(t, hint, submit) { VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(input);
            }
            else
            {
                var text = UiKit.Label(hint, 13, t.TextSecondary);
                text.VerticalAlignment = VerticalAlignment.Center;
                text.Margin = new Thickness(0, 0, 6, 0);
                row.Children.Add(text);
            }
            content = row;
        }
        else icon.Margin = vertical ? new Thickness(0, 6, 0, 6) : new Thickness(11, 0, 11, 0);
        var frame = w.Frame(content);
        frame.ToolTip = tip;
        return frame;
    }

    /// <summary>
    /// バーの上に置く入力欄。バーは、クリックしても前面にならない作りなので、欄を押したときだけ前面（アクティブ）にして入力できるようにし、
    /// 入力が終わったら元に戻す。Enter で確定、Esc でやめる。
    /// </summary>
    public sealed class InputBox : Grid
    {
        private const double BoxWidth = 150;
        private readonly TextBox _box;
        private readonly TextBlock _hint;
        private readonly Action<string> _submit;
        private IntPtr _previous;

        public InputBox(Theme t, string hint, Action<string> submit)
        {
            _submit = submit;
            Width = BoxWidth;
            _hint = UiKit.Label(hint, 13, t.TextSecondary);
            _hint.IsHitTestVisible = false;
            _box = new TextBox
            {
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = t.Text, CaretBrush = t.Text,
                FontSize = 13, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center,
            };
            Children.Add(_hint);
            Children.Add(_box);
            _box.TextChanged += (_, _) => _hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _box.PreviewMouseLeftButtonDown += (_, _) => Activate();
            _box.GotKeyboardFocus += (_, _) => { if (!IsActive()) Activate(); };
            _box.LostKeyboardFocus += (_, _) => Deactivate();
            _box.PreviewKeyDown += OnKeyDown;
        }

        /// <summary>この入力欄の中で押されたか（ドラッグ・クリックの処理を、欄に任せるため）。</summary>
        public bool Owns(DependencyObject source)
        {
            for (DependencyObject? d = source; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (ReferenceEquals(d, this)) return true;
            return false;
        }

        private IntPtr Hwnd => PresentationSource.FromVisual(this) is HwndSource s ? s.Handle : IntPtr.Zero;
        private bool IsActive() => Hwnd != IntPtr.Zero && Win32.GetForegroundWindow() == Hwnd;

        private void Activate()
        {
            var h = Hwnd;
            if (h == IntPtr.Zero) return;
            var fg = Win32.GetForegroundWindow();
            if (fg != h) _previous = fg;
            // クリックしても前面にならない（NOACTIVATE）作りを、入力している間だけ外す
            long ex = (long)Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE);
            if ((ex & Win32.WS_EX_NOACTIVATE) != 0) Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, (IntPtr)(ex & ~Win32.WS_EX_NOACTIVATE));
            Foregrounder.Force(h);
            Keyboard.Focus(_box);
        }

        private void Deactivate()
        {
            var h = Hwnd;
            if (h == IntPtr.Zero) return;
            long ex = (long)Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, (IntPtr)(ex | Win32.WS_EX_NOACTIVATE));
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string text = _box.Text.Trim();
                _box.Text = "";
                _previous = IntPtr.Zero; // 開いた検索のウィンドウが前面になるので、元には戻さない
                Keyboard.ClearFocus();
                if (text.Length > 0) _submit(text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _box.Text = "";
                Keyboard.ClearFocus();
                var back = _previous;
                _previous = IntPtr.Zero;
                if (back != IntPtr.Zero && back != Hwnd) Win32.SetForegroundWindow(back);
                e.Handled = true;
            }
        }
    }
}
