using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading.Tasks;

namespace ModernShell.Core;

/// <summary>
/// 常駐中のメモリの後片付け。起動時（アプリ一覧の読み込みなど）や設定画面を閉じたあとは、
/// 一時的に使ったメモリが GC の空き領域として残りやすいので、大きなオブジェクト領域も含めて詰めて OS に返す。
/// 使用量をログに残すので、改善の効果を確かめられる。
/// </summary>
public static class MemoryHousekeeping
{
    private static bool _scheduled;

    /// <summary>少し待ってから（起動直後の処理が落ち着いてから）詰める。</summary>
    public static void CompactLater(string reason, int delayMs = 8000)
    {
        if (_scheduled) return;
        _scheduled = true;
        Task.Delay(delayMs).ContinueWith(_ => { _scheduled = false; Compact(reason); }, TaskScheduler.Default);
    }

    public static void Compact(string reason)
    {
        try
        {
            long before = Private();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Log.Info($"memory ({reason}): private {before} MB -> {Private()} MB, managed heap {GC.GetTotalMemory(false) / 1048576} MB");
        }
        catch (Exception ex) { Log.Error("memory compact", ex); }
    }

    private static long Private()
    {
        using var p = Process.GetCurrentProcess();
        return p.PrivateMemorySize64 / 1048576;
    }
}
