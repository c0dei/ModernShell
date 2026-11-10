using System;

namespace ModernShell.Core;

/// <summary>開くときのアニメーションの長さ。設定の「アニメーションの速さ」を掛ける。</summary>
public static class Anim
{
    /// <summary>長さにかける倍率（速い = 短く、ゆっくり = 長く）。</summary>
    public static double Factor => Settings.Current?.AnimationSpeed switch
    {
        AnimationSpeed.Fast => 0.6,
        AnimationSpeed.Slow => 1.7,
        _ => 1.0,
    };

    /// <summary>標準の長さ（ミリ秒）から、設定に合わせた長さを作る。</summary>
    public static TimeSpan Ms(double baseMs) => TimeSpan.FromMilliseconds(baseMs * Factor);
}
