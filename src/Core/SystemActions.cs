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
            Process.Start(new ProcessStartInfo(command) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    public static void WebSearch(string query) =>
        Run("explorer.exe", "\"https://www.bing.com/search?q=" + Uri.EscapeDataString(query) + "\"");

    private static void Run(string file, string args)
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); }
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
}
