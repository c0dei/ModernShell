using System;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using ModernShell.Native;

namespace ModernShell.Core;

public static class SystemActions
{
    public static void Lock() => Win32.LockWorkStation();
    public static void Sleep() => Win32.SetSuspendState(false, false, false);
    public static void SignOut() => Win32.ExitWindowsEx(0 /*EWX_LOGOFF*/, 0);
    public static void Shutdown() => Run("shutdown.exe", "/s /hybrid /t 0");
    public static void Restart() => Run("shutdown.exe", "/r /t 0");

    public static void OpenSettings() => Run("explorer.exe", "ms-settings:");
    public static void OpenFileExplorer() => Run("explorer.exe", "");
    public static void OpenTaskManager() => Run("taskmgr.exe", "");
    public static void OpenUserAccount() => Run("explorer.exe", "ms-settings:yourinfo");

    // 純正 UI はタスクバー非表示でもショートカットで開ける
    public static void QuickSettings() => Input.WinCombo(0x41); // Win+A
    public static void NotificationCenter() => Input.WinCombo(0x4E); // Win+N
    public static void ShowDesktop() => Input.WinCombo(0x44); // Win+D
    public static void TaskView() => Input.WinCombo(0x09); // Win+Tab
    public static void PowerUserMenu() => Input.WinCombo(0x58); // Win+X

    /// <summary>"ファイル名を指定して実行" 相当。検索欄にコマンドやパスを打った場合に使う。</summary>
    public static bool TryRun(string command)
    {
        try
        {
            Launcher.Open(command);
            return true;
        }
        catch { return false; }
    }

    public static void WebSearch(string query) =>
        Run("explorer.exe", "\"https://www.bing.com/search?q=" + Uri.EscapeDataString(query) + "\"");

    private static void Run(string file, string args)
    {
        try { Launcher.Open(file, args); }
        catch (Exception ex) { Log.Error($"run {file}", ex); }
    }

    public static string UserDisplayName()
    {
        try
        {
            var sb = new StringBuilder(256);
            int size = sb.Capacity;
            if (Win32.GetUserNameEx(Win32.NameDisplay, sb, ref size) && sb.Length > 0) return sb.ToString();
        }
        catch { }
        return Environment.UserName;
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetRunAtStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable) key.SetValue("ModernShell", "\"" + Environment.ProcessPath + "\"");
            else key.DeleteValue("ModernShell", false);
        }
        catch (Exception ex) { Log.Error("run at startup", ex); }
    }

    private const string TaskName = "ModernShell";

    /// <summary>
    /// サインイン時の起動方法を設定する。
    /// elevated = false: 普通の権限（スタートアップの登録 = レジストリの Run）。
    /// elevated = true: 管理者として（タスクスケジューラの「最上位の特権で実行」。毎回 UAC の確認は出ない）。
    /// どちらか片方だけを登録し、もう片方は消す（二重に起動しないように）。タスクの登録・削除には管理者の権限が要るので、
    /// ModernShell が管理者でないときは UAC の確認を 1 回出す。断られたら false。
    /// </summary>
    public static bool SetStartup(bool enable, bool elevated)
    {
        bool ok = true;
        if (enable && elevated)
        {
            ok = RegisterElevatedTask();
            if (ok) SetRunAtStartup(false);
        }
        else
        {
            if (TaskExists()) ok = Schtasks($"/Delete /TN \"{TaskName}\" /F");
            SetRunAtStartup(enable);
        }
        return ok;
    }

    /// <summary>管理者としての自動起動のタスクが登録されているか。</summary>
    public static bool TaskExists()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool RegisterElevatedTask()
    {
        try
        {
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            var exe = Environment.ProcessPath!;
            string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;
            // サインインしたらすぐ起動（待ちなし）・電源の状態に関係なく動かす・時間制限なし・二重起動しない
            var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>ModernShell (start at sign-in as administrator)</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Esc(user)}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{Esc(exe)}</Command></Exec></Actions>
</Task>";
            var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ModernShell-task.xml");
            System.IO.File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
        }
        catch (Exception ex) { Log.Error("register task", ex); return false; }
    }

    /// <summary>schtasks を実行する。ModernShell が管理者でなければ UAC の確認を出して管理者で実行する。</summary>
    private static bool Schtasks(string args)
    {
        try
        {
            var psi = Launcher.IsElevated
                ? new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true }
                : new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(15000);
            Log.Info($"schtasks {args.Split(' ')[0]} -> {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Log.Info($"schtasks cancelled or failed: {ex.Message}"); return false; }
    }
}
