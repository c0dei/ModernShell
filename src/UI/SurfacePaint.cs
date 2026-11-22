using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        /// <param name="offsetY">実際の位置からずらして合わせる量（物理 px）。スライド中に、開き終わった位置に合わせておくため。</param>
        public void Place(IntPtr hwnd, int offsetY = 0)
        {
            if (Blur == null || !Win32.GetWindowRect(hwnd, out var r) || r.Width <= 0) return;
            r.Top += offsetY;
            r.Bottom += offsetY;
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
    /// <summary>設定の「タスクバーの背景に重ねる色」。テーマ色のとき（既定）は null。バーとそこから開くポップアップで共通。</summary>
    public static Color? TaskbarTintColor(Settings s) => s.TaskbarTint switch
    {
        BarTintMode.Accent => Theme.Current.Accent,
        BarTintMode.Custom => Theme.ParseColor(s.TaskbarCustomColor),
        BarTintMode.Wallpaper => MicaLike.WallpaperTint(Theme.Current.SolidColor),
        _ => null,
    };

    private static Geometry RoundedRect(Rect r, double tl, double tr, double br, double bl)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            void Arc(Point to, double rad) { if (rad > 0) ctx.ArcTo(to, new Size(rad, rad), 0, false, SweepDirection.Clockwise, true, false); }
            ctx.BeginFigure(new Point(r.Left + tl, r.Top), true, true);
            ctx.LineTo(new Point(r.Right - tr, r.Top), true, false);
            Arc(new Point(r.Right, r.Top + tr), tr);
            ctx.LineTo(new Point(r.Right, r.Bottom - br), true, false);
            Arc(new Point(r.Right - br, r.Bottom), br);
            ctx.LineTo(new Point(r.Left + bl, r.Bottom), true, false);
            Arc(new Point(r.Left, r.Bottom - bl), bl);
            ctx.LineTo(new Point(r.Left, r.Top + tl), true, false);
            Arc(new Point(r.Left + tl, r.Top), tl);
        }
        g.Freeze();
        return g;
    }

    /// <summary>
    /// 角丸の四角形のマスク画像（形の内側が不透明、外側が透明）。ring &gt; 0 のときは縁の輪（太さ ring DIP）だけ。
    /// 4 倍の大きさで描いてから縮めて、縁をなめらかにする。実際のピクセル数（dpi 倍）で作るので、拡大縮小のにじみもない。
    /// </summary>
    private static ImageSource? RoundedMask(Size size, CornerRadius c, double dpi, double ring)
    {
        try
        {
            int w = Math.Max(1, (int)Math.Round(size.Width * dpi)), h = Math.Max(1, (int)Math.Round(size.Height * dpi));
            int ss = Math.Max(1, Math.Min(4, 12000 / Math.Max(w, h))); // 大きな画面では倍率を下げる（画像の大きさの上限）
            double k = dpi * ss, W = w * ss, H = h * ss;
            Geometry geo = RoundedRect(new Rect(0, 0, W, H), c.TopLeft * k, c.TopRight * k, c.BottomRight * k, c.BottomLeft * k);
            if (ring > 0)
            {
                double e = ring * k;
                var inner = RoundedRect(new Rect(e, e, Math.Max(1, W - 2 * e), Math.Max(1, H - 2 * e)),
                    Math.Max(0, c.TopLeft - ring) * k, Math.Max(0, c.TopRight - ring) * k, Math.Max(0, c.BottomRight - ring) * k, Math.Max(0, c.BottomLeft - ring) * k);
                geo = Geometry.Combine(geo, inner, GeometryCombineMode.Exclude, null);
            }
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawGeometry(Brushes.Black, null, geo);
            var big = new RenderTargetBitmap((int)W, (int)H, 96, 96, PixelFormats.Pbgra32);
            big.Render(dv);
            big.Freeze();
            if (ss == 1) return big;
            var dv2 = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv2, BitmapScalingMode.Fant);
            using (var dc = dv2.RenderOpen()) dc.DrawImage(big, new Rect(0, 0, w, h));
            var small = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            small.Render(dv2);
            small.Freeze();
            return small;
        }
        catch (Exception ex) { Log.Error("rounded mask", ex); return null; }
    }

    public static Painted Wrap(FrameworkElement content, SurfaceStyle style, int opacity, CornerRadius corner, bool border = true, bool shadow = false, Color? tintColor = null)
    {
        var t = Theme.Current;
        var c = tintColor ?? t.SolidColor;
        byte alpha = (byte)Math.Max(1, Math.Round(Math.Clamp(opacity, 0, 100) / 100.0 * 255));
        var tint = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        tint.Freeze();

        ImageBrush? blur = null;
        Brush background;
        switch (style)
        {
            case SurfaceStyle.Solid:
                if (tintColor is { } sc) { var sb = new SolidColorBrush(sc); sb.Freeze(); background = sb; }
                else background = t.SolidBackground;
                break;
            case SurfaceStyle.Transparent:
                background = tint;
                break;
            default:
                // 下: 壁紙を強くぼかした画像（自分の位置の部分）/ 上: テーマ色。画像ができるまでは単色
                blur = new ImageBrush { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, ImageSource = MicaLike.Image };
                background = t.SolidBackground;
                MicaLike.Refresh();
                break;
        }

        // 飾り（背景・ぼかし・縁取り）と中身を別の層にする（中身だけを角丸のマスクで切り抜くため）。
        double edge = border ? 1 : 0;
        bool rounded = corner.TopLeft > 0 || corner.TopRight > 0 || corner.BottomRight > 0 || corner.BottomLeft > 0;
        var innerCorner = new CornerRadius(Math.Max(0, corner.TopLeft - edge), Math.Max(0, corner.TopRight - edge), Math.Max(0, corner.BottomRight - edge), Math.Max(0, corner.BottomLeft - edge));
        var root = new Grid();
        var host = new Border { Child = content, Margin = new Thickness(edge) };

        if (!rounded)
        {
            // 角が丸くない（画面端に密着した帯など）: 切り抜きは要らない
            var plain = new Border { Background = background, BorderBrush = border ? t.Stroke : null, BorderThickness = new Thickness(edge) };
            if (blur != null) plain.Child = new Border { Background = blur, Child = new Border { Background = tint } };
            root.Children.Add(plain);
            root.Children.Add(host);
        }
        else
        {
            // 角丸の縁は、WPF の標準だと 1 倍の解像度でしか描かれず、角が荒く見える。
            // 角丸の形（塗り・縁取りの輪）を 4 倍の大きさで描いて縮めたマスク画像で、背景と縁取りを切り抜く（縁の濃淡の段階が増えて、なめらかになる）
            var fill = new Border { Background = background };
            if (blur != null) fill.Child = new Border { Background = blur, Child = new Border { Background = tint } };
            var fillMask = new ImageBrush { Stretch = Stretch.Fill };
            fill.OpacityMask = fillMask;
            root.Children.Add(fill);

            ImageBrush? ringMask = null;
            if (border)
            {
                ringMask = new ImageBrush { Stretch = Stretch.Fill };
                root.Children.Add(new Border { Background = t.Stroke, OpacityMask = ringMask, IsHitTestVisible = false });
            }
            root.Children.Add(host);

            // 中身（背景の帯など）も角丸からはみ出さないように切り抜く（Clip は縁にアンチエイリアスがかからないので使わない）
            var hostMask = new ImageBrush { Stretch = Stretch.Fill };
            host.OpacityMask = hostMask;
            root.SizeChanged += (_, e) =>
            {
                double dpi = VisualTreeHelper.GetDpi(root).DpiScaleX;
                fillMask.ImageSource = RoundedMask(e.NewSize, corner, dpi, 0);
                if (ringMask != null) ringMask.ImageSource = RoundedMask(e.NewSize, corner, dpi, edge);
                hostMask.ImageSource = RoundedMask(new Size(Math.Max(1, e.NewSize.Width - 2 * edge), Math.Max(1, e.NewSize.Height - 2 * edge)), innerCorner, dpi, 0);
            };
        }
        if (!shadow) return new Painted { Root = root, Blur = blur };

        // 影: Windows のメニューと同じく、周りに薄く広がる影で手前に浮いて見せる。
        // 透過・ぼかしの背景の下に塗りつぶしを置くと透けて見えてしまうので、背景の外側だけにグラデーションで描く
        // （辺は直線のグラデーション、角は円形のグラデーション）。ぼかし効果（DropShadowEffect）は CPU で描くと重いので使わない
        var shadowHost = BuildShadow(corner.TopLeft, t.IsDark ? 0.30 : 0.14);
        root.Margin = new Thickness(ShadowPad);
        var outer = new Grid();
        outer.Children.Add(shadowHost);
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
            // 円形（四隅）は、丸みの縁で「透明 → 最大の濃さ」へ一気に変えると、段差がギザギザになり、
            // 背景の縁の下に黒い輪が透けて見える。縁をまたいで数 px かけてなだらかに立ち上げる
            double ramp = start > 0 ? Math.Min(start, 1.5 / Math.Max(1, size)) : 0;
            double from = start + ramp;
            for (int i = 0; i <= 6; i++)
            {
                double t = i / 6.0;
                byte a = (byte)Math.Round(strength * Math.Pow(1 - t, 2) * 255);
                double offset = from + (1 - from) * t;
                stops.Add(new GradientStop(Color.FromArgb(a, 0, 0, 0), reverse ? 1 - offset : offset));
            }
            if (start > 0) stops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), Math.Max(0, start - ramp))); // 背景の内側は描かない（Colors.Transparent は「透明な白」で、黒との補間で縁に明るい線が出る）
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
