using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ModernShell.Core;

/// <summary>
/// 通信速度（受信・送信）の直近の推移。1 秒に 1 回、アダプターの送受信バイト数の差から求める（Mbps）。
/// バーのウィジェットとポップアップが同じ値を見る。アダプターの一覧を読むのは遅いことがあるので、計測は別スレッドで行い、
/// 結果は UI スレッドで <see cref="Updated"/> として知らせる。
/// </summary>
public static class NetSpeed
{
    public const int Capacity = 30;
    /// <summary>古い順。単位は Mbps。UI スレッドからだけ読む。</summary>
    public static readonly List<double> Down = new();
    public static readonly List<double> Up = new();

    /// <summary>計測の結果が増えた（UI スレッドで呼ばれる）。</summary>
    public static event Action? Updated;

    private static readonly SystemStats Stats = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static long _lastMs = -10_000;
    private static bool _busy, _primed;

    public static double CurrentDown => Down.Count > 0 ? Down[^1] : 0;
    public static double CurrentUp => Up.Count > 0 ? Up[^1] : 0;

    /// <summary>計測する（1 秒に 1 回まで。何度呼んでもよい）。UI スレッドから呼ぶ。</summary>
    public static void Sample(Dispatcher ui)
    {
        if (_busy || Clock.ElapsedMilliseconds - _lastMs < 800) return;
        _busy = true;
        _lastMs = Clock.ElapsedMilliseconds;
        Task.Run(() => Stats.Network()).ContinueWith(r => ui.BeginInvoke(() =>
        {
            _busy = false;
            if (r.Exception != null) return;
            if (!_primed) { _primed = true; return; } // 最初の 1 回は、差を取る基準になるだけ
            var (_, down, up) = r.Result;
            Push(Down, down * 8 / 1_000_000);
            Push(Up, up * 8 / 1_000_000);
            Updated?.Invoke();
        }), TaskScheduler.Default);
    }

    private static void Push(List<double> l, double v)
    {
        l.Add(Math.Max(0, v));
        if (l.Count > Capacity) l.RemoveAt(0);
    }

    /// <summary>速さ（Mbps）を、数字と単位に分ける。1 Mbps 未満は Kbps、1000 以上は Gbps。</summary>
    public static (string value, string unit) Format(double mbps)
    {
        if (mbps >= 1000) return ((mbps / 1000).ToString("0.00"), "Gbps");
        if (mbps >= 100) return (mbps.ToString("0"), "Mbps");
        if (mbps >= 1) return (mbps.ToString("0.0"), "Mbps");
        return ((mbps * 1000).ToString("0"), "Kbps");
    }
}
