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

    private static long _lastCompactPrivateMB;
    private static int _periodicRuns;

    /// <summary>
    /// 常駐中の使用量を 10 分ごとにログに残し、あわせて自動で後片付けをする（超長時間の起動でもメモリが増え続けないように）。
    /// 後片付けは、操作がしばらく無く（1 分以上）、前回の後片付けから使用量が 20 MB 以上増えているか 1 時間経っているときだけ。
    /// 操作中に GC で引っかからないようにするため。GC を詰めたあと、ワーキング セットも OS に返す。
    /// </summary>
    public static void StartMonitoring()
    {
        _monitor ??= new System.Threading.Timer(_ =>
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                long priv = p.PrivateMemorySize64 / 1048576;
                _periodicRuns++;
                if (_periodicRuns % 3 == 0 && Log.Enabled) // ログは 30 分ごと
                    Log.Info($"memory (periodic): task manager {TaskManagerMB(p)} MB, private {priv} MB, working set {p.WorkingSet64 / 1048576} MB, " +
                             $"managed heap {GC.GetTotalMemory(false) / 1048576} MB, handles {p.HandleCount}, " +
                             $"GDI {GetGuiResources(p.Handle, 0)}, USER {GetGuiResources(p.Handle, 1)}");
                bool grown = priv - _lastCompactPrivateMB >= 20, hourly = _periodicRuns % 6 == 0;
                if ((grown || hourly) && IdleSeconds() >= 60)
                {
                    Compact("idle");
                    TrimWorkingSet();
                    _lastCompactPrivateMB = Private();
                }
            }
            catch { }
        }, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    /// <summary>キーボード・マウスの操作が無い時間（秒）。取れなければ 0（＝操作中扱いで後片付けしない）。</summary>
    private static uint IdleSeconds()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref li) ? ((uint)Environment.TickCount - li.dwTime) / 1000 : 0;
    }

    /// <summary>使っていないページを OS に返す（タスク マネージャーの数字が下がる。必要になれば自動で読み戻される）。</summary>
    private static void TrimWorkingSet()
    {
        using var p = Process.GetCurrentProcess();
        long before = Log.Enabled ? p.WorkingSet64 / 1048576 : 0;
        SetProcessWorkingSetSize(p.Handle, (IntPtr)(-1), (IntPtr)(-1));
        if (!Log.Enabled) return;
        p.Refresh();
        Log.Info($"memory (trim): working set {before} MB -> {p.WorkingSet64 / 1048576} MB");
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
            // Aggressive: 使っていない確保済みの領域（空きのリージョン）も OS に返す（通常の Forced では確保したまま残る）
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
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
        if (!Log.Enabled) return; // モジュール一覧の取得は重いので、ログを書くときだけ
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
