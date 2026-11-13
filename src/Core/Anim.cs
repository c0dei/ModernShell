using System;

namespace ModernShell.Core;

/// <summary>開くときのアニメーションの長さ。設定の「アニメーションの速さ」を掛ける。</summary>
public static class Anim
{
    /// <summary>長さにかける倍率（速い = 短く、ゆっくり = 長く）。</summary>
    public static double Factor => (Settings.Current?.AnimationSpeed switch
    {
        AnimationSpeed.Fast => 0.6,
        AnimationSpeed.Slow => 1.7,
        _ => 1.0,
    }) * (LightMode ? 0.7 : 1.0);

    private static bool? _noGpu;

    /// <summary>GPU（ハードウェア描画）が使えない環境か。WPF の描画ティアが 0 のとき（ソフトウェア描画だけ）。最初に調べた結果を使う。</summary>
    public static bool NoGpu
    {
        get
        {
            if (_noGpu == null)
            {
                try { _noGpu = (System.Windows.Media.RenderCapability.Tier >> 16) == 0; }
                catch { _noGpu = false; }
                Log.Info($"render tier: {(System.Windows.Media.RenderCapability.Tier >> 16)} (software only: {_noGpu})");
            }
            return _noGpu.Value;
        }
    }

    /// <summary>軽量モード: GPU が無く、設定で自動の軽量化がオンのとき。アニメーションを短くし、ホバーの拡大などを省く。</summary>
    public static bool LightMode => (Settings.Current?.AutoLightMode ?? false) && NoGpu;

    /// <summary>標準の長さ（ミリ秒）から、設定に合わせた長さを作る。</summary>
    public static TimeSpan Ms(double baseMs) => TimeSpan.FromMilliseconds(baseMs * Factor);
}
