// Linux 上のコンパイル検証専用: 本物のビルド（ModernShell.csproj / net8.0-windows10.0.19041.0）では Windows SDK の
// WinRT 射影が使われるので、このファイルは含まれない。使っている API の形だけを再現する。
using System.Threading.Tasks;

namespace Windows.Media.Control
{
    public enum GlobalSystemMediaTransportControlsSessionPlaybackStatus { Closed, Opened, Changing, Stopped, Playing, Paused }

    public sealed class GlobalSystemMediaTransportControlsSessionMediaProperties { public string Title { get; } = ""; public string Artist { get; } = ""; }
    public sealed class GlobalSystemMediaTransportControlsSessionPlaybackInfo { public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; } }

    public sealed class GlobalSystemMediaTransportControlsSession
    {
        public Task<GlobalSystemMediaTransportControlsSessionMediaProperties> TryGetMediaPropertiesAsync() => Task.FromResult(new GlobalSystemMediaTransportControlsSessionMediaProperties());
        public GlobalSystemMediaTransportControlsSessionPlaybackInfo GetPlaybackInfo() => new();
        public Task<bool> TryTogglePlayPauseAsync() => Task.FromResult(true);
        public Task<bool> TrySkipNextAsync() => Task.FromResult(true);
        public Task<bool> TrySkipPreviousAsync() => Task.FromResult(true);
    }

    public sealed class GlobalSystemMediaTransportControlsSessionManager
    {
        public static Task<GlobalSystemMediaTransportControlsSessionManager> RequestAsync() => Task.FromResult(new GlobalSystemMediaTransportControlsSessionManager());
        public GlobalSystemMediaTransportControlsSession? GetCurrentSession() => null;
    }
}
