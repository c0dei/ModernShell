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
    /// 影の部分ではマウスを受け取らず、下のウィンドウ（同じスレッドのタスクバーなど）へ通す。
    /// 影は薄く色が付いているので、そのままだと Windows はそこもウィンドウの一部として扱う。
    /// プレビューの影がタスクバーのボタンに少し重なると、ボタンから「出た」ことになって、
    /// アイコンの拡大・縮小とプレビューの表示・非表示が繰り返されてしまう。
    /// </summary>
    public static void PassShadowClicks(System.Windows.Interop.HwndSource src)
    {
        src.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != 0x0084 /*WM_NCHITTEST*/ || !Win32.GetWindowRect(hwnd, out var r)) return IntPtr.Zero;
            int x = unchecked((short)((long)lParam & 0xFFFF)), y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
            uint dpi = Win32.GetDpiForWindow(hwnd);
            int pad = (int)Math.Round(ShadowPad * (dpi > 0 ? dpi / 96.0 : 1));
            if (x >= r.Left + pad && x < r.Right - pad && y >= r.Top + pad && y < r.Bottom - pad) return IntPtr.Zero;
            handled = true;
            return new IntPtr(-1); // HTTRANSPARENT: 下のウィンドウへ
        });
    }

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
        // 透過・ぼかしの背景の下に塗りつぶしを置くと透けて見えてしまうので、背景の外側だけにグラデーションで描く
        // （辺は直線のグラデーション、角は円形のグラデーション）。ぼかし効果（DropShadowEffect）は CPU で描くと重いので使わない
        var host = BuildShadow(corner.TopLeft, t.IsDark ? 0.30 : 0.14);
        root.Margin = new Thickness(ShadowPad);
        var outer = new Grid();
        outer.Children.Add(host);
        outer.Children.Add(root);
        return new Painted { Root = outer, Blur = blur, Inset = ShadowPad };
    }

    /// <summary>
    /// 影の部分だけを描く 3×3 のグリッド（四隅・四辺）。中央（背景の部分）は空なので、透過の背景の下に影が透けない。
    /// 濃さは背景の縁で一番濃く、外へ向かってなめらかに薄くなる。
    /// </summary>
    private static Grid BuildShadow(double radius, double strength)
    {
        double r = Math.Max(0, radius), pad = ShadowPad, size = pad + r;
        var g = new Grid { IsHitTestVisible = false };
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(size) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(size) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(size) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(size) });

        // 縁からの距離 t (0–1) での濃さ。外へ向かって二乗で薄くなる
        GradientStopCollection Stops(double start, bool reverse)
        {
            var stops = new GradientStopCollection();
            for (int i = 0; i <= 6; i++)
            {
                double t = i / 6.0;
                byte a = (byte)Math.Round(strength * Math.Pow(1 - t, 2) * 255);
                double offset = start + (1 - start) * t;
                stops.Add(new GradientStop(Color.FromArgb(a, 0, 0, 0), reverse ? 1 - offset : offset));
            }
            if (start > 0) stops.Add(new GradientStop(Colors.Transparent, Math.Max(0, start - 0.001))); // 背景の内側は描かない
            return stops;
        }

        void Add(UIElement e, int row, int col) { Grid.SetRow(e, row); Grid.SetColumn(e, col); g.Children.Add(e); }

        // 四辺: 背景の外側（幅 pad）だけ。縁で濃く、外で透明
        Add(new Border { Height = pad, VerticalAlignment = VerticalAlignment.Top, Background = new LinearGradientBrush(Stops(0, true), 90) }, 0, 1);
        Add(new Border { Height = pad, VerticalAlignment = VerticalAlignment.Bottom, Background = new LinearGradientBrush(Stops(0, false), 90) }, 2, 1);
        Add(new Border { Width = pad, HorizontalAlignment = HorizontalAlignment.Left, Background = new LinearGradientBrush(Stops(0, true), 0) }, 1, 0);
        Add(new Border { Width = pad, HorizontalAlignment = HorizontalAlignment.Right, Background = new LinearGradientBrush(Stops(0, false), 0) }, 1, 2);

        // 四隅: 背景の角の丸みの中心から円形に。丸みの内側（背景の部分）は透明
        double inner = size > 0 ? r / size : 0;
        foreach (var (row, col, cx, cy) in new[] { (0, 0, 1.0, 1.0), (0, 2, 0.0, 1.0), (2, 0, 1.0, 0.0), (2, 2, 0.0, 0.0) })
        {
            var brush = new RadialGradientBrush(Stops(inner, false))
            {
                Center = new Point(cx, cy), GradientOrigin = new Point(cx, cy), RadiusX = 1, RadiusY = 1,
            };
            Add(new Border { Background = brush }, row, col);
        }
        return g;
    }
}
