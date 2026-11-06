using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ModernShell.Core;

namespace ModernShell.UI;

/// <summary>
/// タスクバーの「Mica 風」背景。本物の Mica は「壁紙を強くぼかした色」を下地にするが、常に非アクティブなウィンドウでは
/// 単色になってしまう（タスクバーは常に非アクティブ）。そこで同じものを自前で作る:
/// 今のデスクトップの壁紙を画面と同じ配置（ページに合わせる・中央など）で小さく描き、強くぼかした画像を 1 枚用意して、
/// 各タスクバーは自分の画面上の位置の部分だけを切り出して表示する（ImageBrush の Viewbox）。
/// 壁紙の変更・スライドショー・仮想デスクトップの切り替えは数秒おきに確認して描き直す。
/// </summary>
public static class MicaLike
{
    /// <summary>ぼかした画面全体の画像の幅（px）。強くぼかすので小さくて十分（拡大するとなめらかになる）。</summary>
    private const int CanvasWidth = 320;
    private const int BlurRadius = 9; // 3 回の箱型ぼかし ≒ 画面幅の 1/12 程度のガウスぼかし

    private static BitmapSource? _image;
    private static string _key = "";
    private static bool _loading;
    private static DispatcherTimer? _watch;
    private static int _users;
    private static event Action? ChangedCore;

    /// <summary>今のぼかし画像（まだ作っていない・壁紙が無いときは null）。</summary>
    public static BitmapSource? Image => _image;

    /// <summary>画像が作り直されたとき（UI スレッド）。購読している間だけ壁紙の変化を見張る。</summary>
    public static event Action? Changed
    {
        add
        {
            ChangedCore += value;
            if (++_users == 1) StartWatching();
        }
        remove
        {
            ChangedCore -= value;
            if (--_users <= 0) { _users = 0; _watch?.Stop(); }
        }
    }

    private static void StartWatching()
    {
        if (_watch == null)
        {
            _watch = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher.CurrentDispatcher);
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.Desktop) _watch.Dispatcher.BeginInvoke(() => Refresh(force: true));
            };
        }
        _watch.Start();
        Refresh();
    }

    /// <summary>壁紙（ファイル・更新日時・配置・画面の縦横比）が変わっていれば作り直す。</summary>
    public static void Refresh(bool force = false)
    {
        if (_loading) return;
        var wall = VirtualDesktops.List().FirstOrDefault(d => d.IsCurrent)?.Wallpaper;
        var (style, tile, fill) = WallpaperStyle();
        var screen = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        string stamp = wall != null && File.Exists(wall) ? File.GetLastWriteTimeUtc(wall).Ticks.ToString() : "";
        var key = $"{wall}|{stamp}|{style}|{tile}|{fill}|{screen.Width}x{screen.Height}";
        if (!force && key == _key) return;
        _key = key;
        _loading = true;
        var dispatcher = Dispatcher.CurrentDispatcher;
        int h = Math.Max(1, (int)Math.Round(CanvasWidth * (double)screen.Height / Math.Max(1, screen.Width)));
        Task.Run(() => Build(wall, style, tile, fill, CanvasWidth, h)).ContinueWith(r =>
        {
            dispatcher.BeginInvoke(() =>
            {
                _loading = false;
                if (r.Exception != null) Log.Error("mica-like", r.Exception);
                _image = r.Exception == null ? r.Result : null;
                ChangedCore?.Invoke();
            });
        }, TaskScheduler.Default);
    }

    /// <summary>壁紙の配置（Control Panel\Desktop の WallpaperStyle / TileWallpaper）と背景色。</summary>
    private static (int style, bool tile, Color fill) WallpaperStyle()
    {
        int style = 10;
        bool tile = false;
        var fill = Colors.Black;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (int.TryParse(k?.GetValue("WallpaperStyle") as string, out var s)) style = s;
            tile = (k?.GetValue("TileWallpaper") as string) == "1";
            using var c = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
            var parts = (c?.GetValue("Background") as string)?.Split(' ');
            if (parts is { Length: 3 } && byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g) && byte.TryParse(parts[2], out var b))
                fill = Color.FromRgb(r, g, b);
        }
        catch { }
        return (style, tile, fill);
    }

    /// <summary>壁紙を画面と同じ配置で w×h に描いて、強くぼかす（UI を使わない純粋な計算なので別スレッドで行う）。</summary>
    private static BitmapSource? Build(string? file, int style, bool tile, Color fill, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = fill.B; px[i + 1] = fill.G; px[i + 2] = fill.R; px[i + 3] = 255; }

        if (file != null && File.Exists(file))
        {
            var src = Decode(file, w * 2);
            if (src != null)
            {
                int iw = src.PixelWidth, ih = src.PixelHeight;
                var img = new byte[iw * ih * 4];
                src.CopyPixels(img, iw * 4, 0);
                // 画面上での壁紙の大きさ（キャンバスの px）。10=ページ幅に合わせる 6=ページ縦幅に合わせる 2=拡大して表示 0=中央/並べて表示 22=スパン
                double sx, sy;
                switch (style)
                {
                    case 6: sx = sy = Math.Min((double)w / iw, (double)h / ih); break;
                    case 2: sx = (double)w / iw; sy = (double)h / ih; break;
                    default: sx = sy = Math.Max((double)w / iw, (double)h / ih); break; // 中央・並べての元の大きさは分からないので、ページ幅に合わせるとして扱う
                }
                double ox = (w - iw * sx) / 2, oy = (h - ih * sy) / 2;
                for (int y = 0; y < h; y++)
                {
                    int syi = (int)((y - oy) / sy);
                    if (syi < 0 || syi >= ih) continue;
                    for (int x = 0; x < w; x++)
                    {
                        int sxi = (int)((x - ox) / sx);
                        if (sxi < 0 || sxi >= iw) continue;
                        int s = (syi * iw + sxi) * 4, d = (y * w + x) * 4;
                        px[d] = img[s]; px[d + 1] = img[s + 1]; px[d + 2] = img[s + 2];
                    }
                }
            }
        }

        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(px, w, h, BlurRadius, horizontal: true);
            BoxBlur(px, w, h, BlurRadius, horizontal: false);
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource? Decode(string file, int decodeWidth)
    {
        try
        {
            // 同じパスのファイルが差し替えられる（スライドショー・TranscodedWallpaper）ので、キャッシュを使わずに読む
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = fs;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            conv.Freeze();
            return conv;
        }
        catch (Exception ex) { Log.Error("mica-like decode", ex); return null; }
    }

    /// <summary>1 方向の箱型ぼかし（端は端の色を伸ばす）。3 回重ねるとガウスぼかしに近くなる。</summary>
    private static void BoxBlur(byte[] px, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        var line = new int[len * 3];
        int win = r * 2 + 1;
        for (int l = 0; l < lines; l++)
        {
            int Idx(int i) => horizontal ? (l * w + i) * 4 : (i * w + l) * 4;
            for (int i = 0; i < len; i++) { int p = Idx(i); line[i * 3] = px[p]; line[i * 3 + 1] = px[p + 1]; line[i * 3 + 2] = px[p + 2]; }
            int b = 0, g = 0, rr = 0;
            for (int k = -r; k <= r; k++) { int c = Math.Clamp(k, 0, len - 1) * 3; b += line[c]; g += line[c + 1]; rr += line[c + 2]; }
            for (int i = 0; i < len; i++)
            {
                int p = Idx(i);
                px[p] = (byte)(b / win); px[p + 1] = (byte)(g / win); px[p + 2] = (byte)(rr / win);
                int add = Math.Min(i + r + 1, len - 1) * 3, sub = Math.Max(i - r, 0) * 3;
                b += line[add] - line[sub]; g += line[add + 1] - line[sub + 1]; rr += line[add + 2] - line[sub + 2];
            }
        }
    }

    /// <summary>
    /// 画面上の矩形（物理 px）に当たる部分を表示するブラシ。矩形はタスクバーが乗っているモニター基準で切り出す
    /// （モニターごとに同じ壁紙が同じ配置で表示されている前提）。
    /// </summary>
    public static void Place(ImageBrush brush, System.Drawing.Rectangle bar, System.Drawing.Rectangle monitor)
    {
        var img = _image;
        if (img == null || monitor.Width <= 0 || monitor.Height <= 0) return;
        if (!ReferenceEquals(brush.ImageSource, img)) brush.ImageSource = img;
        double kx = img.PixelWidth / (double)monitor.Width, ky = img.PixelHeight / (double)monitor.Height;
        brush.Viewbox = new Rect((bar.X - monitor.X) * kx, (bar.Y - monitor.Y) * ky, Math.Max(1, bar.Width * kx), Math.Max(1, bar.Height * ky));
    }
}
