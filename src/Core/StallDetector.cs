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

    private static System.Reflection.PropertyInfo? _nameProp;
    private static System.Reflection.FieldInfo? _methodField;

    /// <summary>ディスパッチャーの仕事の名前（呼び出すメソッド名。WPF の内部情報なので取れなければ優先度だけ）。</summary>
    private static string OperationName(DispatcherOperation op)
    {
        try
        {
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            _nameProp ??= typeof(DispatcherOperation).GetProperty("Name", F);
            if (_nameProp?.GetValue(op) is string name && name.Length > 0) return $"{name} [{op.Priority}]";
            _methodField ??= typeof(DispatcherOperation).GetField("_method", F);
            if (_methodField?.GetValue(op) is Delegate d) return $"{d.Method.DeclaringType?.Name}.{d.Method.Name} [{op.Priority}]";
        }
        catch { }
        return op.Priority.ToString();
    }

    public static void Start(Dispatcher dispatcher)
    {
        // 手が空いたら「待機中」に戻す（どこで固まったかを取り違えないように）
        dispatcher.Hooks.DispatcherInactive += (_, _) => Phase = "idle";
        // 起動から 60 秒間は、UI スレッドで実行する処理（ディスパッチャーの仕事）の名前も記録する。
        // ModernShell のコード以外（WPF の描画や ManagedShell）で固まっているときに、どれかを見分けるため
        var until = DateTime.Now.AddSeconds(60);
        dispatcher.Hooks.OperationStarted += (_, e) =>
        {
            if (DateTime.Now > until) return;
            Phase = "ui job: " + OperationName(e.Operation);
        };
        dispatcher.Hooks.OperationCompleted += (_, e) =>
        {
            if (DateTime.Now <= until) Phase = "idle (after " + OperationName(e.Operation) + ")";
        };
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    var op = dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => { }));
                    string? atDetection = null;
                    string last = "";
                    long nextReport = 1000;
                    while (op.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing)
                    {
                        Thread.Sleep(100);
                        if (sw.ElapsedMilliseconds < nextReport) continue;
                        // 止まっている間も 2 秒ごとに、今何の処理中かを記録する（変わったときだけ）
                        nextReport += 2000;
                        var now = Phase;
                        if (atDetection == null)
                        {
                            atDetection = now;
                            Log.Info($"ui stall: not responding (phase: {now})");
                        }
                        else if (now != last) Log.Info($"ui stall: still not responding after {sw.ElapsedMilliseconds} ms (phase: {now})");
                        last = now;
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
