using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModernShell.Native;

namespace ModernShell.Core;

/// <summary>
/// シェルアイテム（exe / lnk / shell:AppsFolder\AUMID）からアルファ付きアイコンを取得する。
/// CreateBitmapSourceFromHBitmap はアルファを失うため、DIB のビットを直接コピーする。
/// </summary>
internal static class IconHelper
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    private const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    /// <summary>parsingName は "shell:AppsFolder\..." やファイルパス。STA/MTA どちらでも可。</summary>
    public static ImageSource? Get(string parsingName, int size = 48)
    {
        var key = parsingName + "|" + size;
        return Cache.GetOrAdd(key, _ => Load(parsingName, size));
    }

    private static ImageSource? Load(string parsingName, int size)
    {
        IShellItemImageFactory? factory = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factory) != 0 || factory == null)
                return null;
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            try { return FromHBitmap(hbm); }
            finally { Win32.DeleteObject(hbm); }
        }
        catch (Exception ex)
        {
            Log.Error($"icon {parsingName}", ex);
            return null;
        }
        finally
        {
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    internal static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (Win32.GetObject(hbm, Marshal.SizeOf<Win32.DIBSECTION>(), out var ds) == 0) return null;
        var bm = ds.dsBm;
        if (bm.bmBits == IntPtr.Zero || bm.bmBitsPixel != 32)
        {
            // DIB でない場合はフォールバック（アルファは失われる可能性あり）
            var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }

        int w = bm.bmWidth, h = bm.bmHeight, stride = bm.bmWidthBytes;
        var buffer = new byte[stride * h];
        bool bottomUp = ds.dsBmih.biHeight > 0;
        for (int y = 0; y < h; y++)
        {
            int srcRow = bottomUp ? h - 1 - y : y;
            Marshal.Copy(bm.bmBits + srcRow * stride, buffer, y * stride, stride);
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buffer, stride);
        bmp.Freeze();
        return bmp;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    /// <summary>"C:\…\app.dll" の index 番目のアイコン（負の値はリソース ID）。ジャンプリストのタスクなどで使う。</summary>
    public static ImageSource? FromFile(string file, int index)
    {
        if (string.IsNullOrEmpty(file) || !System.IO.File.Exists(file)) return null;
        var key = "res|" + file + "|" + index;
        return Cache.GetOrAdd(key, _ =>
        {
            var large = new IntPtr[1];
            try
            {
                if (ExtractIconEx(file, index, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { if (large[0] != IntPtr.Zero) Win32.DestroyIcon(large[0]); }
        });
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[]? ids, uint count, uint flags);

    /// <summary>
    /// exe / dll / ico から、指定サイズにいちばん合うアイコンを直接取り出す（アルファ付き）。
    /// シェルに大きいサイズを頼むと、その大きさの絵を持たないアプリでは「小さいアイコンを枠付きの台紙に載せた画像」が返り、
    /// 白い枠線のように見えることがあるため、タスクバーのボタンにはこちらを使う。
    /// </summary>
    public static ImageSource? Exact(string file, int index, int sizePx)
    {
        if (string.IsNullOrEmpty(file) || !System.IO.File.Exists(file)) return null;
        var key = "exact|" + file + "|" + index + "|" + sizePx;
        return Cache.GetOrAdd(key, _ =>
        {
            var icons = new IntPtr[1];
            try
            {
                if (PrivateExtractIcons(file, index, sizePx, sizePx, icons, null, 1, 0) == 0 || icons[0] == IntPtr.Zero) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { if (icons[0] != IntPtr.Zero) Win32.DestroyIcon(icons[0]); }
        });
    }

    /// <summary>ショートカットのアイコン（指定があればそれ、なければリンク先の exe）を表示サイズで取り出す。</summary>
    public static ImageSource? ForShortcut(string lnk, int sizePx)
    {
        var info = Shortcut.Read(lnk);
        if (info == null) return null;
        if (info.IconFile != null && Exact(info.IconFile, info.IconIndex, sizePx) is { } custom) return custom;
        return info.Target != null && info.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Exact(info.Target, 0, sizePx) : null;
    }

    /// <summary>ウィンドウ自身が持つアイコン（UWP の ApplicationFrameHost 等のフォールバック用）。</summary>
    public static ImageSource? FromWindow(IntPtr hwnd)
    {
        IntPtr hIcon = IntPtr.Zero;
        if (Win32.SendMessageTimeout(hwnd, Win32.WM_GETICON, (IntPtr)Win32.ICON_BIG, IntPtr.Zero, 0x2 /*ABORTIFHUNG*/, 50, out var r) != IntPtr.Zero)
            hIcon = r;
        if (hIcon == IntPtr.Zero &&
            Win32.SendMessageTimeout(hwnd, Win32.WM_GETICON, (IntPtr)Win32.ICON_SMALL2, IntPtr.Zero, 0x2, 50, out r) != IntPtr.Zero)
            hIcon = r;
        if (hIcon == IntPtr.Zero) hIcon = Win32.GetClassLongPtr(hwnd, Win32.GCLP_HICON);
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            // ウィンドウ所有のアイコンなので DestroyIcon はしない
            var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }
}
