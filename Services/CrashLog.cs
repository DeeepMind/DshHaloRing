using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DshHaloRing.Services;

/// <summary>
/// 极简崩溃日志：写 %APPDATA%\DshHaloRing\crash.log。
/// 托盘程序没有控制台，出错时必须留痕，否则用户只能看到“双击后什么都没发生”。
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(SettingsStore.DirectoryPath, "crash.log");

    public static void Write(string where, Exception ex, bool fatal = true)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.DirectoryPath);
                var sb = new StringBuilder();
                sb.AppendLine("──────────────────────────────────────────────");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}{2}",
                    DateTimeOffset.Now, where, fatal ? "（未处理）" : "（已忽略）"));
                sb.AppendLine(ex.ToString());
                sb.AppendLine();
                File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 日志本身失败不能再制造异常
        }
    }
}
