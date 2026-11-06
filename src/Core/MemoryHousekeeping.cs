using System;
using System.Diagnostics;
using System.Linq;
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
    private static System.Threading.Timer? _monitor;

    /// <summary>常駐中の使用量を 30 分ごとにログに残す（少しずつ増えていないかを確かめるため。GC は強制しない）。</summary>
    public static void StartMonitoring()
    {
        _monitor ??= new System.Threading.Timer(_ =>
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                Log.Info($"memory (periodic): task manager {TaskManagerMB(p)} MB, private {p.PrivateMemorySize64 / 1048576} MB, working set {p.WorkingSet64 / 1048576} MB, " +
                         $"managed heap {GC.GetTotalMemory(false) / 1048576} MB, handles {p.HandleCount}, " +
                         $"GDI {GetGuiResources(p.Handle, 0)}, USER {GetGuiResources(p.Handle, 1)}");
            }
            catch { }
        }, null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

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
            LogBreakdown();
        }
        catch (Exception ex) { Log.Error("memory compact", ex); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage,
            PrivateWorkingSetSize, SharedCommitUsage;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX2 counters, uint size);

    /// <summary>タスク マネージャーの「メモリ」列と同じ値（プライベート ワーキング セット、MB）。取れなければ -1。</summary>
    private static long TaskManagerMB(Process p)
    {
        var c = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
        return GetProcessMemoryInfo(p.Handle, ref c, c.cb) ? (long)(c.PrivateWorkingSetSize.ToUInt64() / 1048576) : -1;
    }

    /// <summary>
    /// 内訳をログに残す（どこを減らせばよいかの手がかり）。読み込んだ DLL の大きさ（上位）、GC が確保している量、
    /// ワーキングセット、スレッド数、GDI/USER オブジェクト数。DLL 名だけで、内容は書かない。
    /// </summary>
    private static void LogBreakdown()
    {
        using var p = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        long modules = 0;
        var top = new System.Collections.Generic.List<(string name, long size)>();
        try
        {
            foreach (ProcessModule m in p.Modules)
            {
                modules += m.ModuleMemorySize;
                top.Add((m.ModuleName, m.ModuleMemorySize));
            }
        }
        catch { }
        var biggest = string.Join(", ", top.OrderByDescending(x => x.size).Take(12).Select(x => $"{x.name} {x.size / 1048576.0:0.0}"));
        Log.Info($"memory detail: task manager {TaskManagerMB(p)} MB, working set {p.WorkingSet64 / 1048576} MB, GC committed {info.TotalCommittedBytes / 1048576} MB, " +
                 $"modules {top.Count} = {modules / 1048576} MB, threads {p.Threads.Count}, GDI {GetGuiResources(p.Handle, 0)}, USER {GetGuiResources(p.Handle, 1)}");
        Log.Info("memory detail: largest modules (MB): " + biggest);
    }

    private static long Private()
    {
        using var p = Process.GetCurrentProcess();
        return p.PrivateMemorySize64 / 1048576;
    }
}
