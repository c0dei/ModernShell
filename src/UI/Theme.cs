using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using ModernShell.Core;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>ライト/ダーク・アクセントカラーから算出したブラシ一式。</summary>
public sealed class Theme
{
    public bool IsDark { get; init; }
    public Color Accent { get; init; }
    public Brush AccentBrush { get; init; } = Brushes.DodgerBlue;
    public Brush OnAccent { get; init; } = Brushes.White;
    public Brush Text { get; init; } = Brushes.White;
    public Brush TextSecondary { get; init; } = Brushes.Gray;
    public Brush Surface { get; init; } = Brushes.Transparent;     // カード/検索欄
    public Brush Hover { get; init; } = Brushes.Transparent;
    public Brush Pressed { get; init; } = Brushes.Transparent;
    public Brush Stroke { get; init; } = Brushes.Transparent;
    public Brush FooterBand { get; init; } = Brushes.Transparent;
    /// <summary>背景効果が使えないとき（Solid / ポップアップ）の不透明背景。</summary>
    public Brush SolidBackground { get; init; } = Brushes.Black;
    public Color SolidColor { get; init; }
    public double Radius { get; init; } = 8;

    // ---- Windows 11 (WinUI) のコントロール配色 ----
    private static SolidColorBrush F(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }
    /// <summary>ボタン/ドロップダウンの地の色（通常・ホバー・押下）。</summary>
    public Brush ControlFill => IsDark ? F(0x0F, 255, 255, 255) : F(0xB3, 255, 255, 255);
    public Brush ControlFillHover => IsDark ? F(0x15, 255, 255, 255) : F(0x80, 0xF9, 0xF9, 0xF9);
    public Brush ControlFillPressed => IsDark ? F(0x08, 255, 255, 255) : F(0x4D, 0xF9, 0xF9, 0xF9);
    /// <summary>コントロールの枠。WinUI と同じく下端だけ色を変えて立体感を出す。</summary>
    public Brush ControlBorder
    {
        get
        {
            var top = IsDark ? Color.FromArgb(0x18, 255, 255, 255) : Color.FromArgb(0x0F, 0, 0, 0);
            var bottom = IsDark ? Color.FromArgb(0x12, 255, 255, 255) : Color.FromArgb(0x29, 0, 0, 0);
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), MappingMode = BrushMappingMode.RelativeToBoundingBox };
            b.GradientStops.Add(new GradientStop(top, 0));
            b.GradientStops.Add(new GradientStop(top, 0.92));
            b.GradientStops.Add(new GradientStop(bottom, 0.92));
            b.GradientStops.Add(new GradientStop(bottom, 1));
            b.Freeze();
            return b;
        }
    }
    /// <summary>標準 (8px) のときに <paramref name="standard"/> px になる角丸を、設定の「角の丸み」に比例させて返す。</summary>
    public CornerRadius Corner(double standard) => new(Radius <= 0 ? 0 : standard * Radius / 8);

    /// <summary>
    /// ウィンドウ外周の角の形（DWM に指定する値）。Windows が描けるのは「なし / 小 (4px) / 標準 (8px)」の 3 種類だけなので、
    /// 設定の「大」はウィンドウ外周では標準と同じになり、内側のカードやボタンだけが大きく丸くなる。
    /// </summary>
    public int WindowCorner => Radius <= 0 ? Win32.DWMWCP_DONOTROUND : Radius <= 4 ? Win32.DWMWCP_ROUNDSMALL : Win32.DWMWCP_ROUND;

    /// <summary>メニュー/ドロップダウン一覧の地の色。</summary>
    public Color FlyoutColor => IsDark ? Color.FromRgb(0x2C, 0x2C, 0x2C) : Color.FromRgb(0xFC, 0xFC, 0xFC);

    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI, Yu Gothic UI");

    public static Theme Current { get; private set; } = Build(new Settings());
    public static event Action? Changed;

    /// <summary>
    /// 設定と Windows の状態からテーマを作り直す。見た目（ライト/ダーク・アクセント色・角の丸み）が変わったときだけ
    /// Changed を出す。Windows の「設定が変わった」通知は、作業領域の変化や他のアプリの通知などでも頻繁に来るので、
    /// 毎回出すとそのたびにタスクバーやスタートメニューが丸ごと作り直されてしまう。
    /// </summary>
    public static void Apply(Settings s)
    {
        var next = Build(s);
        static string Key(Theme t) => $"{t.IsDark}|{t.Accent}|{t.Radius}";
        bool changed = Key(next) != Key(Current);
        Current = next;
        if (changed) Changed?.Invoke();
    }

    private static Theme Build(Settings s)
    {
        bool dark = s.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemIsDark(),
        };
        var accent = ParseColor(s.AccentColor) ?? SystemAccent();

        static SolidColorBrush B(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }
        static SolidColorBrush C(Color c) { var x = new SolidColorBrush(c); x.Freeze(); return x; }

        var solid = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        double lum = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;

        return dark
            ? new Theme
            {
                IsDark = true, Accent = accent, AccentBrush = C(accent), OnAccent = lum > 0.6 ? Brushes.Black : Brushes.White,
                Text = B(255, 255, 255, 255), TextSecondary = B(255, 0xC5, 0xC5, 0xC5),
                Surface = B(0x14, 255, 255, 255), Hover = B(0x15, 255, 255, 255), Pressed = B(0x0B, 255, 255, 255),
                Stroke = B(0x18, 255, 255, 255), FooterBand = B(0x30, 0, 0, 0),
                SolidBackground = C(solid), SolidColor = solid, Radius = s.CornerRadius,
            }
            : new Theme
            {
                IsDark = false, Accent = accent, AccentBrush = C(accent), OnAccent = lum > 0.6 ? Brushes.Black : Brushes.White,
                Text = B(255, 0x1A, 0x1A, 0x1A), TextSecondary = B(255, 0x5D, 0x5D, 0x5D),
                Surface = B(0xB0, 255, 255, 255), Hover = B(0x0F, 0, 0, 0), Pressed = B(0x08, 0, 0, 0),
                Stroke = B(0x14, 0, 0, 0), FooterBand = B(0x0A, 0, 0, 0),
                SolidBackground = C(solid), SolidColor = solid, Radius = s.CornerRadius,
            };
    }

    /// <summary>スタート/タスクバーは「アプリモード」ではなく「Windows モード」の配色に従う。</summary>
    public static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v ? v == 0 : true;
        }
        catch { return true; }
    }

    public static Color SystemAccent()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentColorMenu") is int abgr)
                return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
            if (Win32.DwmGetColorizationColor(out uint argb, out _) == 0)
                return Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }
        catch { }
        return Color.FromRgb(0x00, 0x78, 0xD4);
    }

    public static Color? ParseColor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return (Color)ColorConverter.ConvertFromString(s.Trim()); } catch { return null; }
    }
}

/// <summary>
/// 設定画面だけが使う Windows の背景効果（Mica）。設定画面は普通のウィンドウ（アクティブになる）なので公式 API で付けられる。
/// GPU が無い PC では Windows が単色で描く。タスクバー・スタートメニュー・メニューは <see cref="SurfacePaint"/> で自分で描く。
/// </summary>
public static class Backdrop
{
    private static readonly bool Is22H2 = Environment.OSVersion.Version.Build >= 22621;
    private static readonly bool IsWin11 = Environment.OSVersion.Version.Build >= 22000;

    public static void ApplyMica(Window w, Theme t)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        var src = HwndSource.FromHwnd(hwnd);

        int dark = t.IsDark ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        if (IsWin11)
        {
            int corner = t.WindowCorner;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        if (!Is22H2)
        {
            w.Background = t.SolidBackground;
            return;
        }
        // DWM に描かせるため WPF 側の背景は完全透過にし、クライアント領域全体にフレームを広げる
        w.Background = Brushes.Transparent;
        if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        var m = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(hwnd, ref m);
        int type = Win32.DWMSBT_MAINWINDOW;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
    }
}
