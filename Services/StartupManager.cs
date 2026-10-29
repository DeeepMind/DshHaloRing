using System;
using System.Diagnostics;
using Microsoft.Win32;
namespace DshHaloRing.Services;

/// <summary>开机自启（HKCU Run 项，无需管理员权限）。</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DshHaloRing";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string s && s.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null)
            {
                return;
            }
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{ExecutablePath}\" --tray");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 注册表不可写时静默失败，设置界面会回读真实状态
        }
    }

    public static string ExecutablePath
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) &&
                exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                // dotnet run 场景下退回入口程序集路径
                var asm = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(asm))
                {
                    return asm;
                }
            }
            return exe ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        }
    }
}
