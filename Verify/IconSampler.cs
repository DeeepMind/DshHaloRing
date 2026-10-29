using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DshHaloRing.Models;
using DshHaloRing.Services;

namespace DshHaloRing.Verify;

/// <summary>
/// 生成图标样图，供人工确认外观。
/// 同时导出「深色（夜间）/ 浅色（日间）」两套主题：
///   深色底板 #202020、浅色底板 #F2F2F2，其余参数完全一致。
/// 用法：DshHaloRing.exe --sample &lt;输出目录&gt;
/// </summary>
public static class IconSampler
{
    /// <summary>深色（夜间）主题底板色。</summary>
    public const string DarkBackground = "#202020";

    /// <summary>浅色（日间）主题底板色。</summary>
    public const string LightBackground = "#F2F2F2";

    /// <summary>深色底板上的轨道色：比底板略亮，形成低调的凹槽轮廓。</summary>
    public const string DarkTrack = "#3F3F3F";

    /// <summary>浅色底板上的轨道色：比底板略暗，避免在亮底上过于扎眼。</summary>
    public const string LightTrack = "#C8C8C8";

    private static readonly (AgentState state, double progress, string label)[] States =
    {
        (AgentState.Running, 0.25, "运行25%"),
        (AgentState.Running, 0.65, "运行65%"),
        (AgentState.Completed, 1.00, "完成100%"),
        (AgentState.WaitingForUser, 0.35, "等待35%"),
        (AgentState.Degraded, 0.60, "降级60%"),
        (AgentState.Fault, 0.45, "故障45%"),
        (AgentState.Idle, 1.00, "空闲100%"),
        (AgentState.Offline, 0.00, "离线0%"),
    };

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);

        var baseSettings = new AppSettings();
        baseSettings.EnsureDefaults();

        // 夜间：深色底板 + 略亮的轨道
        var dark = CloneSettings(baseSettings);
        dark.Canvas.BackgroundColor = DarkBackground;
        dark.Ring.TrackColor = DarkTrack;
        ExportTheme(dark, Path.Combine(outDir, "dark"), "夜间（深色底板 " + DarkBackground + "）");

        // 日间：浅色底板 + 略暗的轨道，其余参数保持一致
        var light = CloneSettings(baseSettings);
        light.Canvas.BackgroundColor = LightBackground;
        light.Ring.TrackColor = LightTrack;
        ExportTheme(light, Path.Combine(outDir, "light"), "日间（浅色底板 " + LightBackground + "）");

        Console.WriteLine("两套主题样图已生成：" + outDir);
        return 0;
    }

    /// <summary>按给定配置导出一整套样图到 dir。</summary>
    private static void ExportTheme(AppSettings settings,
        string dir, string themeName)
    {
        Directory.CreateDirectory(dir);

        // 1) 每个状态导出多尺寸
        foreach (var (state, progress, _) in States)
        {
            foreach (int size in new[] { 256, 48, 32, 24, 16 })
            {
                var bmp = RingIconRenderer.Render(settings, state, progress, 1.0, size);
                SavePng(bmp, Path.Combine(dir, $"{state.ToKey()}_{(int)(progress * 100)}_{size}.png"));
            }
        }

        // 2) 对比拼图（背景按主题取反，保证底板与背景有对比）
        var sheet = BuildContactSheet(settings, out string sheetNote);
        SavePng(sheet, Path.Combine(dir, "预览拼图.png"));

        var report = new StringBuilder();
        report.AppendLine($"主题：{themeName}");
        report.AppendLine("输出目录：" + dir);
        report.AppendLine(sheetNote);
        report.AppendLine();
        report.AppendLine("外观参数：");
        report.AppendLine($"  底板色     = {settings.Canvas.BackgroundColor}（圆形，{(settings.Canvas.TransparentBackground ? "透明" : "不透明")}）");
        report.AppendLine($"  轨道色     = {settings.Ring.TrackColor}（显示 = {settings.Ring.ShowTrack}）");
        report.AppendLine($"  线宽比例   = {settings.Ring.ThicknessRatio:P0}");
        report.AppendLine($"  环半径比例 = {settings.Ring.RadiusRatio:0.00}");
        report.AppendLine($"  辉光强度   = {settings.Ring.GlowStrength:0.00}（已取消）");
        report.AppendLine($"  管状渐变   = {(settings.Ring.Tube.Enabled ? "开启" : "关闭")}（峰值 {settings.Ring.Tube.PeakOpacity:0.00} / 边缘 {settings.Ring.Tube.EdgeOpacity:0.00} / 提亮 {settings.Ring.Tube.EdgeLighten:0.00}）");
        report.AppendLine($"  起始角度   = {settings.Ring.StartAngleDegrees:0}°");
        report.AppendLine($"  中心亮点   = {settings.Dot.Enabled}");
        var text = report.ToString();
        File.WriteAllText(Path.Combine(dir, "外观参数.txt"), text, Encoding.UTF8);
        Console.WriteLine(text);
    }

    /// <summary>深拷贝配置，避免两套主题互相影响。</summary>
    private static AppSettings CloneSettings(AppSettings src)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(src);
        var clone = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        clone.EnsureDefaults();
        return clone;
    }

    /// <summary>把各状态渲染成一张横向排列的拼图。</summary>
    private static BitmapSource BuildContactSheet(AppSettings settings, out string note)
    {
        const int cell = 160;
        const int iconSize = 128;
        const int gap = 8;
        int cols = States.Length;
        int width = cols * (cell + gap) + gap;
        int height = cell + gap * 2;

        // 背景与底板形成对比：深底板用深灰背景，浅底板用浅灰背景
        bool lightPlate = IsLight(settings.Canvas.BackgroundColor);
        var bgColor = lightPlate ? Color.FromRgb(0xF8, 0xF8, 0xF8) : Color.FromRgb(0x18, 0x18, 0x18);
        var textColor = lightPlate ? Color.FromRgb(0x40, 0x40, 0x40) : Color.FromRgb(0xC8, 0xC8, 0xC8);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(bgColor), null, new Rect(0, 0, width, height));

            for (int i = 0; i < States.Length; i++)
            {
                var (state, progress, label) = States[i];
                var icon = RingIconRenderer.Render(settings, state, progress, 1.0, iconSize);
                double x = gap + i * (cell + gap);
                double y = gap;
                dc.DrawImage(icon, new Rect(x, y, iconSize, iconSize));

                var ft = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13,
                    new SolidColorBrush(textColor), 96);
                dc.DrawText(ft, new Point(x, y + iconSize + 4));
            }
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        note = $"预览拼图：{width}x{height}，每格 {iconSize}px 图标（背景 {DescribeBg(bgColor)}，底板 {settings.Canvas.BackgroundColor}）";
        return rtb;
    }

    private static string DescribeBg(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>判断颜色是否为浅色（用于选择对比背景）。</summary>
    private static bool IsLight(string hex)
    {
        var c = RingIconRenderer.ParseColor(hex, Colors.Black);
        double luma = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return luma > 160;
    }

    private static void SavePng(BitmapSource bmp, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
