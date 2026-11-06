using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModernShell.Core;
using ModernShell.Native;

namespace ModernShell.UI;

/// <summary>
/// タスクバー・スタートメニュー・ポップアップの背景を描く（3 択: 透過なし / 透過 / ぼかし）。
/// Windows の背景効果（Acrylic・Mica）は GPU が無いと描かれないので使わず、どれも透過ウィンドウの中に自分で描く。
/// 角丸・枠線も自分で描くので、GPU の有無で見た目が変わらない。
/// </summary>
public static class SurfacePaint
{
    /// <summary>描いた背景。ぼかしのときは、画面上の位置に合わせて壁紙の切り出し位置を更新する必要がある。</summary>
    public sealed class Painted
    {
        public required FrameworkElement Root { get; init; }
        public ImageBrush? Blur { get; init; }
        /// <summary>影のためにウィンドウの内側に取った余白 (DIP)。背景はこの内側に描かれる。</summary>
        public double Inset { get; init; }

        /// <summary>ぼかしの画像を、ウィンドウ（物理 px の矩形）の位置に合わせる。</summary>
        public void Place(IntPtr hwnd)
        {
            if (Blur == null || !Win32.GetWindowRect(hwnd, out var r) || r.Width <= 0) return;
            uint dpi = Win32.GetDpiForWindow(hwnd);
            int inset = (int)Math.Round(Inset * (dpi > 0 ? dpi / 96.0 : 1));
            var rect = new System.Drawing.Rectangle(r.Left + inset, r.Top + inset, Math.Max(1, r.Width - inset * 2), Math.Max(1, r.Height - inset * 2));
            MicaLike.Place(Blur, rect, System.Windows.Forms.Screen.FromRectangle(rect).Bounds);
        }
    }

    /// <summary>影の幅 (DIP)。影を付けるウィンドウは、この分だけ周りを広げて透明な余白にする。</summary>
    public const double ShadowPad = 12;

    /// <summary>
    /// content を背景で包む。opacity は重ねるテーマ色の濃さ (0–100)。
    /// 透過・ぼかしでも、クリックが裏へ抜けないようにごく薄く色を残す。
    /// </summary>
    public static Painted Wrap(FrameworkElement content, SurfaceStyle style, int opacity, CornerRadius corner, bool border = true, bool shadow = false)
    {
        var t = Theme.Current;
        var c = t.SolidColor;
        byte alpha = (byte)Math.Max(1, Math.Round(Math.Clamp(opacity, 0, 100) / 100.0 * 255));
        var tint = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        tint.Freeze();

        ImageBrush? blur = null;
        FrameworkElement inner = content;
        Brush background;
        switch (style)
        {
            case SurfaceStyle.Solid:
                background = t.SolidBackground;
                break;
            case SurfaceStyle.Transparent:
                background = tint;
                break;
            default:
                // 下: 壁紙を強くぼかした画像（自分の位置の部分）/ 上: テーマ色。画像ができるまでは単色
                blur = new ImageBrush { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, ImageSource = MicaLike.Image };
                inner = new Border { Background = blur, CornerRadius = corner, Child = new Border { Background = tint, CornerRadius = corner, Child = content } };
                background = t.SolidBackground;
                MicaLike.Refresh();
                break;
        }

        var root = new Border
        {
            CornerRadius = corner, Background = background, Child = inner,
            BorderBrush = border ? t.Stroke : null, BorderThickness = new Thickness(border ? 1 : 0),
        };
        // 中身（背景の帯など）も角丸からはみ出さないように切り抜く
        if (corner.TopLeft > 0 || corner.BottomRight > 0)
            root.SizeChanged += (_, e) => root.Clip = new RectangleGeometry(new Rect(e.NewSize), corner.TopLeft, corner.TopLeft);
        if (!shadow) return new Painted { Root = root, Blur = blur };

        // 影: Windows のメニューと同じく、周りに薄く広がる影で手前に浮いて見せる。
        // 透過・ぼかしの背景の下に塗りつぶしを置くと透けて見えてしまうので、外側に細い輪を何重にも重ねて描く（中は空）。
        // ぼかし効果（DropShadowEffect）は CPU で描くと重いので使わない
        var host = new Grid { IsHitTestVisible = false, RenderTransform = new TranslateTransform(0, 2) };
        const int rings = 10;
        double strength = t.IsDark ? 0.30 : 0.16;
        for (int i = 1; i <= rings; i++)
        {
            double a = strength * Math.Pow(1 - (i - 1) / (double)rings, 2.2);
            var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(a * 255), 0, 0, 0));
            brush.Freeze();
            host.Children.Add(new Border
            {
                Margin = new Thickness(ShadowPad - i), BorderThickness = new Thickness(1), BorderBrush = brush,
                CornerRadius = new CornerRadius(corner.TopLeft + i, corner.TopRight + i, corner.BottomRight + i, corner.BottomLeft + i),
            });
        }
        root.Margin = new Thickness(ShadowPad);
        var outer = new Grid();
        outer.Children.Add(host);
        outer.Children.Add(root);
        return new Painted { Root = outer, Blur = blur, Inset = ShadowPad };
    }
}
