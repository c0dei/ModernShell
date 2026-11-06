using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ModernShell.Core;

/// <summary>
/// パッケージ版アプリ（ストアアプリ、Windows Terminal など）のインストール先と、
/// その中の画像（"ms-appx:///ProfileIcons/x.png" 形式で指定されるアイコン）の読み込み。
/// </summary>
public static class PackageInfo
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string familyName, ref uint count, IntPtr[]? fullNames, ref uint bufferLength, char[]? buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string fullName, ref uint length, StringBuilder? path);

    /// <summary>AUMID（"Family!App"）またはパッケージファミリー名から、インストール先フォルダを返す。</summary>
    public static string? InstallPath(string aumidOrFamily)
    {
        try
        {
            var family = aumidOrFamily.Split('!')[0];
            uint count = 0, len = 0;
            GetPackagesByPackageFamily(family, ref count, null, ref len, null);
            if (count == 0 || len == 0) return null;
            var names = new IntPtr[count];
            var buf = new char[len];
            if (GetPackagesByPackageFamily(family, ref count, names, ref len, buf) != 0) return null;
            var fullName = Marshal.PtrToStringUni(names[0]);
            if (string.IsNullOrEmpty(fullName)) return null;
            uint plen = 0;
            GetPackagePathByFullName(fullName, ref plen, null);
            var sb = new StringBuilder((int)plen);
            return GetPackagePathByFullName(fullName, ref plen, sb) == 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    /// <summary>exe のパスが "...\WindowsApps\<パッケージ>\..." なら、そのパッケージのフォルダを返す。</summary>
    public static string? RootFromExe(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        var parts = exe.Split('\\');
        int i = Array.FindIndex(parts, p => p.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length - 1 ? string.Join('\\', parts.Take(i + 2)) : null;
    }

    /// <summary>
    /// "ms-appx:///ProfileIcons/x.png" をパッケージ内の実ファイルにする。
    /// 実際のファイルは "x.scale-200.png" "x.targetsize-32.png" のように拡大率付きの名前で入っていることが多いので、一番大きいものを選ぶ。
    /// </summary>
    public static string? ResolveAsset(string uri, string? packageRoot)
    {
        if (packageRoot == null) return null;
        var rel = uri.Substring("ms-appx:///".Length).Replace('/', '\\').TrimStart('\\');
        var exact = Path.Combine(packageRoot, rel);
        if (File.Exists(exact)) return exact;
        var dir = Path.GetDirectoryName(exact);
        if (dir == null || !Directory.Exists(dir)) return null;
        var stem = Path.GetFileNameWithoutExtension(exact);
        var ext = Path.GetExtension(exact);
        return Directory.EnumerateFiles(dir, stem + ".*" + ext)
            .OrderByDescending(f => Qualifier(Path.GetFileName(f)))
            .FirstOrDefault();
    }

    private static int Qualifier(string name)
    {
        // "x.scale-200.png" → 200, "x.targetsize-48.png" → 48
        foreach (var key in new[] { "scale-", "targetsize-" })
        {
            int i = name.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && int.TryParse(new string(name.Skip(i + key.Length).TakeWhile(char.IsDigit).ToArray()), out var v)) return v;
        }
        return 0;
    }

    /// <summary>png / jpg / ico などの画像ファイルを読み込む。</summary>
    public static ImageSource? LoadImage(string file, int decodeWidth = 64)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file);
            bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
