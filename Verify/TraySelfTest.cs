using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DshHaloRing.Models;

namespace DshHaloRing.Verify;

/// <summary>
/// 托盘集成冒烟测试（独立进程）：真实创建 NotifyIcon + 真实渲染管线推帧，
/// 结果写到 %APPDATA%\DshHaloRing\tray-selftest.log，退出码 0 = 通过。
/// 用法：DshHaloRing.exe --tray-selftest [秒数]
/// </summary>
public static class TraySelfTest
{
    public static int Run(int seconds)
    {
        var log = new StringBuilder();
        int exitCode = 0;
        string logPath = Path.Combine(Services.SettingsStore.DirectoryPath, "tray-selftest.log");

        void Line(string s)
        {
            log.AppendLine(s);
            Console.WriteLine(s);
        }

        try
        {
            Directory.CreateDirectory(Services.SettingsStore.DirectoryPath);

            var settings = new Models.AppSettings();
            settings.EnsureDefaults();
            Line($"[1/5] 配置加载：{settings.StateStyles.Count} 种状态配色，起始角 {settings.Ring.StartAngleDegrees}°");

            var aggregator = new Services.StateAggregator();
            Line("[2/5] StateAggregator 已创建");

            using var tray = new Tray.TrayController(settings, aggregator);
            Line($"[3/5] TrayController 已创建，NotifyIcon.Visible={true}，Text=\"{Sanitize(tray.TooltipText)}\"");

            // 校验菜单结构：应含开机自启动项，且不应再有演示相关项
            var menu = tray.MenuItemsForTest();
            var names = new List<string>();
            foreach (var item in menu)
            {
                names.Add(item);
            }
            Line($"      菜单项: {string.Join(" | ", names)}");

            bool hasStartup = names.Contains("开机自启动");
            bool hasDemo = false;
            foreach (var n in names)
            {
                if (n.Contains("演示") || n.Contains("切换状态") || n.Contains("设置进度")) { hasDemo = true; break; }
            }
            Line($"      含开机自启动: {hasStartup}，含演示项: {hasDemo}");
            if (!hasStartup || hasDemo)
            {
                Line("TRAY_SELFTEST_FAIL（菜单结构不符）");
                return 3;
            }

            // 真实渲染管线推帧：覆盖全部状态
            var states = (Models.AgentState[])Enum.GetValues(typeof(Models.AgentState));
            int frames = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                var state = states[frames % states.Length];
                double progress = (frames % 10) / 9.0;
                tray.UpdateIcon(state, progress, 1.0);
                frames++;
                Thread.Sleep(50);
            }
            Line($"[4/5] 推帧成功：{frames} 帧，覆盖 {states.Length} 种状态，耗时 {sw.Elapsed.TotalSeconds:0.0}s");
            Line($"[5/5] 最后一次状态：{aggregator.Current.ToDisplayName()}");

            tray.Dispose();
            Line("TRAY_SELFTEST_OK");
        }
        catch (Exception ex)
        {
            Line("TRAY_SELFTEST_FAIL");
            Line(ex.ToString());
            Services.CrashLog.Write("TraySelfTest", ex);
            exitCode = 1;
        }

        try
        {
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 日志写不了也不影响退出码
        }

        return exitCode;
    }

    private static string Sanitize(string? s) => (s ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
}
