using System;
using System.IO;
using System.Text;

namespace DshHaloRing.Verify;

/// <summary>
/// 启动路径埋点：确认 App 构造、OnStartup 是否执行、参数是否到达。
///
/// 写两份，互为兜底：
///   1. %APPDATA%\DshHaloRing\startup-trace.log（与配置同目录）
///   2. 程序目录下 startup-trace.log（APPDATA 被重定向/不可写时仍能留痕）
/// 写入失败不再静默忽略：把失败原因也尝试写到另一个位置。
/// </summary>
public static class StartupTrace
{
    private static readonly object Gate = new();

    private static string PrimaryPath =>
        Path.Combine(Services.SettingsStore.DirectoryPath, "startup-trace.log");

    private static string FallbackPath =>
        Path.Combine(AppContext.BaseDirectory, "startup-trace.log");

    public static void Mark(string message)
    {
        string line = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] [pid {Environment.ProcessId}] {message}{Environment.NewLine}";

        lock (Gate)
        {
            TryAppend(PrimaryPath, line);
            TryAppend(FallbackPath, line);
        }
    }

    private static void TryAppend(string path, string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch
        {
            // 该位置不可写：由另一处兜底
        }
    }
}
