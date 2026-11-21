using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using WMC = global::Windows.Media.Control;

namespace ModernShell.Core;

/// <summary>
/// Windows の「メディア セッション」（再生中の曲の情報と、再生・停止・前後の曲の操作）の入り口。
/// セッション一覧は Windows が管理していて、Chrome・Spotify・音楽アプリなどが自分で登録する。最初に使うときに 1 度だけ取りに行く。
/// </summary>
internal static class MediaControl
{
    private static WMC.GlobalSystemMediaTransportControlsSessionManager? _manager;
    private static Task? _init;

    /// <summary>セッション一覧が使える状態か（まだなら <see cref="EnsureAsync"/> を呼ぶ）。</summary>
    public static bool Ready => _manager != null;

    public static Task EnsureAsync() => _init ??= InitAsync();

    private static async Task InitAsync()
    {
        try { _manager = await WMC.GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (Exception ex) { Log.Error("media manager", ex); _init = null; }
    }

    /// <summary>いま Windows が「メイン」としているセッション。</summary>
    public static WMC.GlobalSystemMediaTransportControlsSession? Current
    {
        get { try { return _manager?.GetCurrentSession(); } catch { return null; } }
    }

    /// <summary>
    /// このアプリ（ウィンドウの AUMID・実行ファイル）のセッション。ブラウザーのようにアプリ名しか持たないものも、実行ファイルの名前で突き合わせる。
    /// 見つからなければ null。
    /// </summary>
    public static WMC.GlobalSystemMediaTransportControlsSession? FindFor(string? aumid, string? exePath)
    {
        if (_manager == null) return null;
        string a = (aumid ?? "").ToLowerInvariant();
        string e = Norm(exePath);
        try
        {
            foreach (var s in _manager.GetSessions())
            {
                string id = s.SourceAppUserModelId ?? "";
                if (a.Length > 0 && id.Equals(aumid, StringComparison.OrdinalIgnoreCase)) return s;
                if (e.Length >= 3 && Norm(id) == e) return s;
            }
            if (Log.Enabled) LogMiss(aumid, exePath);
        }
        catch (Exception ex) { Log.Error("media find", ex); }
        return null;
    }

    private static readonly System.Collections.Generic.HashSet<string> MissLogged = new();

    /// <summary>
    /// セッションはあるのに、このアプリのものが見つからないとき、アプリの AUMID・実行ファイルと、セッションの ID を 1 度だけ記録する
    /// （ブラウザーのように、ウィンドウの AUMID とセッションの ID が一致しないアプリの調査用）。
    /// </summary>
    private static void LogMiss(string? aumid, string? exePath)
    {
        if (_manager == null) return;
        var ids = string.Join(", ", System.Linq.Enumerable.Select(_manager.GetSessions(), x => x.SourceAppUserModelId));
        if (ids.Length == 0) return;
        string key = (aumid ?? "") + "|" + exePath;
        if (!MissLogged.Add(key)) return;
        Log.Info($"media: no session for app aumid=\"{aumid}\" exe=\"{Path.GetFileName(exePath ?? "")}\" (sessions: {ids})");
    }

    /// <summary>"C:\...\Spotify.exe" / "Spotify.exe" / "Chrome" → "spotify" / "spotify" / "chrome"。</summary>
    private static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int slash = s.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) s = s[(slash + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.ToLowerInvariant();
    }

    /// <summary>曲のジャケット・動画の画像を、小さく縮めて読む（decodeWidth は表示の 2 倍ほど）。無い・読めないときは null。</summary>
    public static async Task<BitmapSource?> LoadThumbnailAsync(WMC.GlobalSystemMediaTransportControlsSessionMediaProperties props, int decodeWidth)
    {
        if (props.Thumbnail == null) return null;
        try
        {
            using var ras = await props.Thumbnail.OpenReadAsync();
            using var src = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await src.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = decodeWidth; // 大きな画像をそのまま持たない
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>長さを持たない（ライブ配信など）か。再生位置の報告が無い、または長さが 24 時間を超える（終わりの無い配信）ものを、ライブとみなす。</summary>
    public static bool HasNoDuration(TimeSpan start, TimeSpan end) => end <= start || end - start > TimeSpan.FromHours(24);

    // ブラウザーは、複数のタブで再生していると、タブの情報を 1 つのセッションにまとめて渡す。
    // 例: 曲名は YouTube の動画のものなのに、再生位置の情報は、長さを報告しない Twitch のものになる。
    // その混ざった状態を LIVE と誤認しないよう、「同じ曲名で、一度でも長さが報告された」ものは、ライブとはみなさない
    private static readonly System.Collections.Generic.HashSet<string> SeenWithDuration = new();

    /// <summary>
    /// ライブ配信か。長さが無い再生中のもののうち、同じ曲名で長さが報告されたことが無いもの。
    /// 呼ぶたびに「長さがあった曲名」を覚える（200 件を超えたら忘れる）。
    /// </summary>
    public static bool IsLive(string trackKey, TimeSpan start, TimeSpan end, bool playing)
    {
        bool none = HasNoDuration(start, end);
        lock (SeenWithDuration)
        {
            if (!none && trackKey.Length > 1) // "|" = 曲名もアーティストも空
            {
                if (SeenWithDuration.Count > 200) SeenWithDuration.Clear();
                SeenWithDuration.Add(trackKey);
            }
            return playing && none && !SeenWithDuration.Contains(trackKey);
        }
    }

    public static bool IsPlaying(WMC.GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus == WMC.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }
}
