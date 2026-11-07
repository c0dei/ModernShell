using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace ModernShell.Core;

/// <summary>
/// UI スレッド（タスクバー・スタートメニューを動かしているスレッド）が固まっていないかを見張って、
/// 1 秒以上応答しなかったらログに残す。そのとき何の処理中だったか（<see cref="Phase"/>）も一緒に書くので、
/// 「砂時計が出てクリックできない」原因の場所を特定できる。
/// </summary>
public static class StallDetector
{
    /// <summary>UI スレッドで今やっている処理の名前（各処理の入口で書き換える）。</summary>
    public static volatile string Phase = "idle";

    public static void Start(Dispatcher dispatcher)
    {
        // 手が空いたら「待機中」に戻す（どこで固まったかを取り違えないように）
        dispatcher.Hooks.DispatcherInactive += (_, _) => Phase = "idle";
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    var op = dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => { }));
                    string? atDetection = null;
                    while (op.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing)
                    {
                        Thread.Sleep(100);
                        if (atDetection == null && sw.ElapsedMilliseconds >= 1000)
                        {
                            atDetection = Phase;
                            Log.Info($"ui stall: not responding (phase: {atDetection})");
                        }
                    }
                    if (atDetection != null)
                        Log.Info($"ui stall: recovered after {sw.ElapsedMilliseconds} ms (phase at start: {atDetection}, now: {Phase})");
                }
                catch { }
                Thread.Sleep(250);
            }
        }) { IsBackground = true, Name = "StallDetector" }.Start();
    }
}
