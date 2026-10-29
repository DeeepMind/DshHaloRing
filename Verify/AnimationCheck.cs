using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using DshHaloRing.Models;
using DshHaloRing.Services;

namespace DshHaloRing.Verify;

/// <summary>
/// 动画实测：按真实时间序列采样「运行中」状态的强度与渲染像素，
/// 验证托盘图标确实在做呼吸式明暗变化，而不是常亮。
/// 用法：DshHaloRing.exe --animcheck
/// </summary>
public static class AnimationCheck
{
    public static int Run()
    {
        var sb = new StringBuilder();
        var settings = new AppSettings();
        settings.EnsureDefaults();

        // 1) 强度序列（对应 2.4s 呼吸周期）
        sb.AppendLine("=== 「运行中」强度采样（周期 2.4s）===");
        var values = new List<double>();
        for (double t = 0; t <= 2.4 + 1e-9; t += 0.2)
        {
            double v = AnimationEngine.Intensity(AgentState.Running, t, true);
            values.Add(v);
            string bar = new string('#', (int)Math.Round(v * 40));
            sb.AppendLine($"  t={t:0.0}s  强度={v:0.000}  {bar}");
        }

        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in values) { min = Math.Min(min, v); max = Math.Max(max, v); }
        sb.AppendLine();
        sb.AppendLine($"  强度范围：{min:0.000} ~ {max:0.000}（幅度 {max - min:0.000}）");
        sb.AppendLine($"  判定：{(min < 0.6 && max > 0.95 ? "✓ 明显呼吸" : "✗ 变化不足")}");
        sb.AppendLine();

        // 2) 实际渲染像素：不同强度下环的颜色是否随之变暗
        sb.AppendLine("=== 实际渲染像素（256px，环上 0° 位置）===");
        var (_, radius) = Services.RingIconRenderer.ComputeRingGeometry(settings, 256);

        foreach (double intensity in new[] { 1.0, 0.85, 0.7, 0.55 })
        {
            var bmp = Services.RingIconRenderer.RenderBgraPremultipliedAsStraight(
                settings, AgentState.Running, 0.75, intensity, 256);
            var stride = 256 * 4;
            var pixels = new byte[stride * 256];
            bmp.CopyPixels(pixels, stride, 0);

            // 取环上 12 个方位、每个方位取截面最亮点的平均亮度
            long sumLuma = 0;
            int samples = 0;
            for (int deg = 0; deg < 360; deg += 30)
            {
                double bestLuma = -1;
                for (double rr = radius - 6; rr <= radius + 6; rr += 0.5)
                {
                    var p2 = Services.RingIconRenderer.PointOnCircle(new System.Windows.Point(128, 128), rr, deg);
                    int x = (int)Math.Round(p2.X), y = (int)Math.Round(p2.Y);
                    if (x < 0 || y < 0 || x >= 256 || y >= 256) continue;
                    int idx = (y * 256 + x) * 4;
                    byte g2 = pixels[idx + 1];
                    double luma = 0.299 * pixels[idx + 2] + 0.587 * g2 + 0.114 * pixels[idx];
                    if (luma > bestLuma) bestLuma = luma;
                }
                if (bestLuma >= 0) { sumLuma += (long)bestLuma; samples++; }
            }
            double avg = samples > 0 ? sumLuma / (double)samples : 0;
            sb.AppendLine($"  强度={intensity:0.00}  环身平均亮度≈{avg:0}  {new string('#', (int)(avg / 5))}");
        }

        // 3) 结论
        sb.AppendLine();
        var idle = AnimationEngine.Intensity(AgentState.Idle, 1.0, true);
        var idle2 = AnimationEngine.Intensity(AgentState.Idle, 5.0, true);
        sb.AppendLine($"=== 对照：空闲态强度 {idle:0.00} / {idle2:0.00}（应为常亮 1.00）===");
        sb.AppendLine($"=== 对照：关闭动画时运行中强度 = {AnimationEngine.Intensity(AgentState.Running, 1.0, false):0.00} ===");

        var text = sb.ToString();
        Console.WriteLine(text);
        var path = Path.Combine(AppContext.BaseDirectory, "animcheck.txt");
        try { File.WriteAllText(path, text, Encoding.UTF8); } catch { /* 忽略 */ }
        return 0;
    }
}
