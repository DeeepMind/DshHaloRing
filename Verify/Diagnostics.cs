using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DshHaloRing.Models;
using DshHaloRing.Services;

namespace DshHaloRing.Verify;

/// <summary>临时诊断工具：观察 CCW 的弧覆盖角度与 Idle 颜色的像素分布。</summary>
public static class Diagnostics
{
    public static void Run()
    {
        var sb = new StringBuilder();

        var settings = new AppSettings();
        settings.EnsureDefaults();

        // ── 1. CCW 情况下的亮角分布 ──
        var ccw = Clone(settings);
        ccw.Ring.Clockwise = false;
        sb.AppendLine("=== CCW, progress 0.25, 起点应为 60° 逆时针 90° → 330°，弧覆盖 330..60 ===" );
        var sample = RenderBgra(ccw, 0.25, 256);
        sb.AppendLine(DumpLitAngles(sample, 256, ccw));

        // ── 2. Idle 颜色的实际像素 ──
        sb.AppendLine();
        sb.AppendLine("=== Idle progress .5, 期望主环 #2F6B45（opacity 1.0） ===");
        var idle = RenderBgra(settings, 0.5, 256, AgentState.Idle);
        sb.AppendLine(DumpRingSamples(idle, 256, settings));

        // ── 3. 对照：Running（高饱和）===
        sb.AppendLine();
        sb.AppendLine("=== 对照 Running ===");
        var running = RenderBgra(settings, 0.5, 256, AgentState.Running);
        sb.AppendLine(DumpRingSamples(running, 256, settings));

        Console.WriteLine(sb.ToString());
    }

    private static AppSettings Clone(AppSettings s)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var c = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        c.EnsureDefaults();
        return c;
    }

    private static byte[] RenderBgra(AppSettings s, double progress, int size, AgentState state = AgentState.Running)
    {
        var bmp = RingIconRenderer.RenderBgraPremultipliedAsStraight(s, state, progress, 1.0, size);
        var stride = size * 4;
        var px = new byte[stride * size];
        bmp.CopyPixels(px, stride, 0);
        return px;
    }

    private static string DumpLitAngles(byte[] px, int size, AppSettings s)
    {
        var ring = s.Ring;
        double cx = size / 2.0, cy = size / 2.0;
        double outer = size / 2.0;
        double th = Math.Max(size * ring.ThicknessRatio, 1.0);
        double radius = Math.Min(outer * ring.RadiusRatio, outer - th / 2 - 0.5);

        var sb = new StringBuilder();
        var lit = new StringBuilder();
        for (int deg = 0; deg < 360; deg++)
        {
            var p = RingIconRenderer.PointOnCircle(new Point(cx, cy), radius, deg);
            int x = (int)Math.Round(p.X), y = (int)Math.Round(p.Y);
            int i = (y * size + x) * 4;
            byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            bool isLit = (max - min) >= 40 && max >= 90 && a > 128;
            lit.Append(isLit ? '#' : '.');
            if ((deg + 1) % 60 == 0) lit.Append($" {deg + 1}°\n");
        }
        sb.Append(lit);
        return sb.ToString();
    }

    private static string DumpRingSamples(byte[] px, int size, AppSettings s)
    {
        var ring = s.Ring;
        double cx = size / 2.0, cy = size / 2.0;
        double outer = size / 2.0;
        double th = Math.Max(size * ring.ThicknessRatio, 1.0);
        double radius = Math.Min(outer * ring.RadiusRatio, outer - th / 2 - 0.5);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"radius={radius:0.00} thickness={th:0.00}");
        foreach (var deg in new[] { 60, 90, 120, 150, 180, 210, 240, 300 })
        {
            var p = RingIconRenderer.PointOnCircle(new Point(cx, cy), radius, deg);
            int x = (int)Math.Round(p.X), y = (int)Math.Round(p.Y);
            int i = (y * size + x) * 4;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  deg={deg,4}  px=#{px[i + 2]:X2}{px[i + 1]:X2}{px[i]:X2}  a={px[i + 3]}");
        }
        return sb.ToString();
    }
}
