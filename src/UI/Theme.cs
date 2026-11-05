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

    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI, Yu Gothic UI");

    public static Theme Current { get; private set; } = Build(new Settings());
    public static event Action? Changed;

    public static void Apply(Settings s)
    {
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

    public static void Apply(Window w, BackdropKind kind, Theme t, bool alwaysInactive)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        var src = HwndSource.FromHwnd(hwnd);

        int dark = t.IsDark ? 1 : 0;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        if (IsWin11)
        {
            int corner = Win32.DWMWCP_ROUND;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }

        if (kind == BackdropKind.Solid)
        {
            SetAccent(hwnd, AccentState.Disabled, 0);
            if (Is22H2) { int none = Win32.DWMSBT_NONE; Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int)); }
            w.Background = t.SolidBackground;
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = t.SolidColor;
            return;
        }

        // DWM に描かせるため WPF 側の背景は完全透過にする
        w.Background = Brushes.Transparent;
        if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        var m = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(hwnd, ref m);

        if (Is22H2 && !alwaysInactive)
        {
            int type = kind == BackdropKind.Mica ? Win32.DWMSBT_MAINWINDOW : Win32.DWMSBT_TRANSIENTWINDOW;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
        }
        else
        {
            // Mica 指定時は濃いめのティントで近い見た目にする
            byte alpha = kind == BackdropKind.Mica ? (byte)0xE0 : (byte)0xB0;
            var c = t.SolidColor;
            uint abgr = ((uint)alpha << 24) | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R;
            SetAccent(hwnd, AccentState.AcrylicBlurBehind, abgr);
        }
    }

    private enum AccentState { Disabled = 0, BlurBehind = 3, AcrylicBlurBehind = 4 }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public AccentState State; public int Flags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private static void SetAccent(IntPtr hwnd, AccentState state, uint abgr)
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
