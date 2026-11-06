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
        public required Border Root { get; init; }
        public ImageBrush? Blur { get; init; }

        /// <summary>ぼかしの画像を、ウィンドウ（物理 px の矩形）の位置に合わせる。</summary>
        public void Place(IntPtr hwnd)
        {
            if (Blur == null || !Win32.GetWindowRect(hwnd, out var r) || r.Width <= 0) return;
            var rect = new System.Drawing.Rectangle(r.Left, r.Top, r.Width, r.Height);
            MicaLike.Place(Blur, rect, System.Windows.Forms.Screen.FromRectangle(rect).Bounds);
        }
    }

    /// <summary>
    /// content を背景で包む。opacity は重ねるテーマ色の濃さ (0–100)。
    /// 透過・ぼかしでも、クリックが裏へ抜けないようにごく薄く色を残す。
    /// </summary>
    public static Painted Wrap(FrameworkElement content, SurfaceStyle style, int opacity, CornerRadius corner, bool border = true)
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
        return new Painted { Root = root, Blur = blur };
    }
}
