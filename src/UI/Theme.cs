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

    /// <summary>メニュー/ドロップダウン一覧の Acrylic の地の色。</summary>
    public Color FlyoutColor => IsDark ? Color.FromRgb(0x2C, 0x2C, 0x2C) : Color.FromRgb(0xFC, 0xFC, 0xFC);
    /// <summary>ポップアップに Acrylic を使うか（背景効果「なし」のときは単色）。</summary>
    public static bool TranslucentPopups { get; private set; } = true;

    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI, Yu Gothic UI");

    public static Theme Current { get; private set; } = Build(new Settings());
    public static event Action? Changed;

    public static void Apply(Settings s)
    {
        TranslucentPopups = s.Backdrop != BackdropKind.Solid;
        Current = Build(s);
        Changed?.Invoke();
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
/// ウィンドウ背景効果（Mica / Acrylic）と角丸。
/// - Win11 22H2+ かつアクティブになるウィンドウ: DWMWA_SYSTEMBACKDROP_TYPE（公式 API）
/// - 常に非アクティブなウィンドウ（タスクバー）や Win10: SetWindowCompositionAttribute の Acrylic
///   （公式 Backdrop は非アクティブ時に単色化されるため）
/// </summary>
public static class Backdrop
{
    private static readonly bool Is22H2 = Environment.OSVersion.Version.Build >= 22621;
    private static readonly bool IsWin11 = Environment.OSVersion.Version.Build >= 22000;

    /// <summary>公式の背景効果 (DWMWA_SYSTEMBACKDROP_TYPE) を使うかどうか。</summary>
    public static bool UsesSystemBackdrop(BackdropKind kind, BackdropEngine engine, bool alwaysInactive) =>
        kind != BackdropKind.Solid && Is22H2 && engine switch
        {
            BackdropEngine.SystemBackdrop => true,
            BackdropEngine.AccentBlur => false,
            _ => !alwaysInactive,
        };

    /// <summary>
    /// 背景効果を適用する。
    /// 公式 API の背景効果はタイトルバー枠を持つウィンドウでしか描かれないため、
    /// それを使うウィンドウは WindowStyle=SingleBorderWindow + WindowChrome(CaptionHeight=0) にしておくこと
    /// （WindowStyle=None だと真っ黒/不透明になる）。
    /// ※ Windows の「設定 → 個人用設定 → 色 → 透明効果」がオフだと、どの方式でも不透明になる。
    /// </summary>
    /// <param name="tint">背景の不透明度 (0–1)。null なら既定。
    /// Acrylic ぼかし方式では色の濃さそのもの、Windows 標準方式では上に重ねる単色の濃さとして使う。</param>
    public static void Apply(Window w, BackdropKind kind, BackdropEngine engine, Theme t, bool alwaysInactive, double? tint = null,
        TaskbarMaterial material = TaskbarMaterial.Acrylic)
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

        bool system = UsesSystemBackdrop(kind, engine, alwaysInactive);
        if (Is22H2 && !system)
        {
            int none = Win32.DWMSBT_NONE;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        }

        if (kind == BackdropKind.Solid)
        {
            SetAccent(hwnd, AccentState.Disabled, 0);
            w.Background = t.SolidBackground;
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = t.SolidColor;
            return;
        }

        // DWM に描かせるため WPF 側の背景は完全透過にし、クライアント領域全体にフレームを広げる
        w.Background = Brushes.Transparent;
        if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        var m = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(hwnd, ref m);

        if (system)
        {
            // 標準の背景効果は透明度を変えられないので、上に半透明の単色を重ねて濃くする
            if (tint is > 0) w.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(tint.Value, 0, 1) * 255), t.SolidColor.R, t.SolidColor.G, t.SolidColor.B));
            SetAccent(hwnd, AccentState.Disabled, 0);
            int type = kind == BackdropKind.Mica ? Win32.DWMSBT_MAINWINDOW : Win32.DWMSBT_TRANSIENTWINDOW;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
        }
        else
        {
            var c = t.SolidColor;
            byte alpha;
            if (tint is { } v)
            {
                // 濃さはスタートメニューと同じく「上に重ねる半透明の色」で付ける（アクセントポリシーの色の濃さは
                // 環境によって効かないことがあるため）。ぼかし側の色はごく薄くしておく（0 だと既定色になる環境がある）
                alpha = 0x10;
                w.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(v, 0, 1) * 255), c.R, c.G, c.B));
            }
            else alpha = kind == BackdropKind.Mica ? (byte)0xD8 : (byte)0x99; // Mica 指定時は濃いめのティントで近い見た目にする
            uint abgr = ((uint)alpha << 24) | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R;
            // 下地の種類: Acrylic（明るさの層とノイズ入り。暗い背景では灰色の板に見える）/ ぼかしのみ / 完全に透明
            var state = material switch
            {
                TaskbarMaterial.Blur => AccentState.BlurBehind,
                TaskbarMaterial.Clear => AccentState.TransparentGradient,
                _ => AccentState.AcrylicBlurBehind,
            };
            if (material == TaskbarMaterial.Clear) abgr &= 0x00FFFFFF; // 下地は完全に透明。濃さは重ねる色だけで付ける
            SetAccent(hwnd, state, abgr);
        }
    }

    internal enum AccentState { Disabled = 0, TransparentGradient = 2, BlurBehind = 3, AcrylicBlurBehind = 4 }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public AccentState State; public int Flags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    internal static void SetAccent(IntPtr hwnd, AccentState state, uint abgr)
    {
        var policy = new AccentPolicy { State = state, Flags = 2, GradientColor = abgr };
        int size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = 19 /*WCA_ACCENT_POLICY*/, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}
