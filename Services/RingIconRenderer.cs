using System;
using DshHaloRing.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
namespace DshHaloRing.Services;

/// <summary>
/// 环形指示灯的矢量渲染器。
///
/// 角度约定：0° 指向 12 点方向，角度沿顺时针增大（屏幕坐标系 y 轴向下）。
/// 因此任意角度 a 在屏幕上对应方向 (sin a, -cos a)，起始角 60° 即指向上右方。
///
/// 分层顺序（与参考图标 HaloBattery.exe 的观感一致）：
///   1. 底板（默认 #202020 不透明、近似 Win11 圆角）
///   2. 轨道环（未完成部分）
///   3. 外辉光（blur + 提亮，营造 halo）
///   4. 主环（圆头端点）
///   5. 中心进度点
/// </summary>
public static class RingIconRenderer
{
    /// <summary>渲染一个尺寸为 size×size 的 RGBA 位图。</summary>
    public static BitmapSource Render(AppSettings settings, AgentState state, double progress,
        double intensity = 1.0, int size = 32)
    {
        settings.EnsureDefaults();

        var visual = new DrawingVisual();
        var dc = visual.RenderOpen();
        try
        {
            Draw(dc, settings, state, progress, intensity, size);
        }
        finally
        {
            dc.Close();
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>把渲染结果转成可写位图（用于 ico 编码，因为编码器需要 BGRA 排列的像素）。</summary>
    public static BitmapSource RenderBgraPremultipliedAsStraight(AppSettings settings, AgentState state,
        double progress, double intensity = 1.0, int size = 32)
    {
        var src = Render(settings, state, progress, intensity, size);
        var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>
    /// 计算环的几何：线宽与中心线半径。
    /// 渲染与自检都必须走这里，避免两处公式不一致导致采样点错位。
    /// </summary>
    public static (double thickness, double radius) ComputeRingGeometry(AppSettings settings, int size)
    {
        var ring = settings.Ring;
        var outerRadius = size / 2.0;
        var thickness = Math.Max(size * Math.Clamp(ring.ThicknessRatio, 0.01, 0.6), 1.0);
        // 环与圆形底板边缘之间留出一圈底板，避免环贴着边显得局促；
        // 间距随图标尺寸缩放，小图标按比例给更少。
        var margin = Math.Max(size * 0.075, 1.0);
        var usableRadius = Math.Max(outerRadius - thickness / 2.0 - margin, thickness);
        var radius = usableRadius * Math.Clamp(ring.RadiusRatio, 0.3, 1.0);
        return (thickness, radius);
    }

    private static void Draw(DrawingContext dc, AppSettings settings, AgentState state,
        double progress, double intensity, int size)
    {
        var ring = settings.Ring;
        var canvas = settings.Canvas;
        var style = settings.StyleFor(state);

        intensity = Math.Clamp(intensity, 0.0, 1.0);
        progress = Math.Clamp(progress, 0.0, 1.0);

        var baseColor = style.ToMediaColor();
        var ringColor = WithOpacity(baseColor, style.Opacity * intensity);

        // ── 1. 底板 ──────────────────────────────────────────────
        // 圆形底板必须“画满整个画布”：半径直接取 size/2，
        // 这样圆形直径等于图标边长，与系统其它圆形图标视觉大小一致。
        // 不能先用方形铺底再裁圆，那样圆一定比方形小一圈。
        if (!canvas.TransparentBackground)
        {
            var bg = ParseColor(canvas.BackgroundColor, Color.FromRgb(0x20, 0x20, 0x20));
            var bgBrush = new SolidColorBrush(bg);
            bgBrush.Freeze();
            if (canvas.CornerRadiusRatio >= 0.499)
            {
                // 真正的圆形底板：直径 = size，铺满画布
                dc.DrawEllipse(bgBrush, null, new Point(size / 2.0, size / 2.0),
                    size / 2.0, size / 2.0);
            }
            else if (canvas.CornerRadiusRatio <= 0.001)
            {
                dc.DrawRectangle(bgBrush, null, new Rect(0, 0, size, size));
            }
            else
            {
                var r = size * canvas.CornerRadiusRatio;
                dc.DrawRoundedRectangle(bgBrush, null,
                    new Rect(0, 0, size, size), r, r);
            }
        }

        // ── 2. 几何参数 ──────────────────────────────────────────
        var center = new Point(size / 2.0, size / 2.0);
        var outerRadius = size / 2.0;
        var thickness = Math.Max(size * Math.Clamp(ring.ThicknessRatio, 0.01, 0.6), 1.0);

        // 安全边距：环外缘与画布边缘至少留 1px（小图标按比例留更少，避免环太小）。
        var margin = Math.Max(size * 0.02, 1.0);
        var usableRadius = Math.Max(outerRadius - thickness / 2.0 - margin, thickness);
        var radius = usableRadius * Math.Clamp(ring.RadiusRatio, 0.3, 1.0);

        var startAngle = ring.StartAngleDegrees;
        var sweepAngle = 360.0 * progress;

        // ── 3. 轨道环 ────────────────────────────────────────────
        // 灰色轨道画满整圈：底板 #202020 之上用略亮的灰形成圆环轮廓，
        // 进度弧随后叠加在轨道上，视觉上像“在凹槽里推进”。
        if (ring.ShowTrack && !string.IsNullOrWhiteSpace(ring.TrackColor))
        {
            var track = ParseColor(ring.TrackColor, Color.FromRgb(0x3F, 0x3F, 0x3F));
            var trackBrush = new SolidColorBrush(WithOpacity(track, Math.Max(intensity, 0.45)));
            trackBrush.Freeze();
            dc.DrawGeometry(null, MakePen(trackBrush, thickness, ring.RoundCaps),
                BuildArc(center, radius, startAngle, 359.9, ring.Clockwise));
        }

        if (ring.ShowProgress && progress > 0.0005)
        {
            // ── 4. 主环：管状截面渐变 ─────────────────────────────
            // 用若干同心圆弧叠加模拟截面明暗：内缘暗 → 中间亮 → 外缘暗，
            // 视觉上像一根有高光的圆管，而不是一块平涂的色条。
            // 未启用渐变时退化为单条纯色环。
            var arc = BuildArc(center, radius, startAngle, sweepAngle, ring.Clockwise);
            var tube = ring.Tube;

            if (tube is { Enabled: true })
            {
                // 分层数随图标尺寸变化：小图标少画几层，避免糊成一片
                int bands = size >= 48 ? 14 : size >= 32 ? 10 : 6;
                double halfThickness = thickness / 2.0;
                // 每一层用一个更细的环，半径略微偏移，拼出截面明暗
                double bandThickness = thickness / bands * 2.2;

                for (int i = 0; i < bands; i++)
                {
                    // t: 0 = 内缘, 1 = 外缘
                    double t = bands == 1 ? 0.5 : i / (double)(bands - 1);
                    // 高光位置可由 HighlightBias 在截面内左右移动
                    double peak = Math.Clamp(0.5 + tube.HighlightBias * 0.4, 0.05, 0.95);
                    // 到高光中心的归一化距离（0=高光处，1=两端）
                    double dist = Math.Abs(t - peak) / Math.Max(peak, 1 - peak);

                    // 圆管截面亮度：中心最亮，两侧按余弦衰减
                    double shade = Math.Cos(dist * Math.PI / 2.0);
                    double alpha = tube.EdgeOpacity + (tube.PeakOpacity - tube.EdgeOpacity) * shade;

                    // 边缘掺一点白，模拟管壁反光
                    var c = ringColor;
                    if (tube.EdgeLighten > 0)
                    {
                        double w = tube.EdgeLighten * (1 - shade);
                        c = Color.FromRgb(
                            (byte)Math.Round(c.R + (255 - c.R) * w),
                            (byte)Math.Round(c.G + (255 - c.G) * w),
                            (byte)Math.Round(c.B + (255 - c.B) * w));
                    }

                    // 动画强度同时作用于「颜色亮度」与「不透明度」：
                    // 底板不透明时只降 alpha 会让绿色与深灰底板混合、亮度几乎不变，
                    // 呼吸效果被稀释。这里把基础色向底板色压暗，保证明暗变化肉眼可辨。
                    var dimmed = Color.FromRgb(
                        (byte)Math.Round(c.R * intensity),
                        (byte)Math.Round(c.G * intensity),
                        (byte)Math.Round(c.B * intensity));

                    var brush = new SolidColorBrush(WithOpacity(dimmed, Math.Clamp(alpha, 0, 1) * style.Opacity * intensity));
                    brush.Freeze();

                    // 该层对应的半径偏移
                    double bandRadius = radius - halfThickness + thickness * t;
                    dc.DrawGeometry(null, MakePen(brush, bandThickness, ring.RoundCaps),
                        BuildArc(center, bandRadius, startAngle, sweepAngle, ring.Clockwise));
                }
            }
            else
            {
                var mainBrush = new SolidColorBrush(ringColor);
                mainBrush.Freeze();
                dc.DrawGeometry(null, MakePen(mainBrush, thickness, ring.RoundCaps), arc);
            }
        }

        // ── 5. 中心进度点（默认关闭：只保留周围圆环作指示）────────
        if (settings.Dot.Enabled)
        {
            var dotRadius = radius * Math.Clamp(settings.Dot.RadiusRatio, 0.0, 0.85);
            if (dotRadius > 0.35)
            {
                // 中心点的亮度跟随进度：0% 时隐约可见，100% 时饱满
                double dotAlpha = Math.Clamp(0.28 + 0.72 * progress, 0, 1) * intensity * Math.Max(style.Opacity, 0.55);
                var dotBrush = new SolidColorBrush(WithOpacity(baseColor, dotAlpha));
                dotBrush.Freeze();
                dc.DrawEllipse(dotBrush, null, center, dotRadius, dotRadius);
            }
        }
    }

    private static Pen MakePen(Brush brush, double thickness, bool roundCaps)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = roundCaps ? PenLineCap.Round : PenLineCap.Flat,
            EndLineCap = roundCaps ? PenLineCap.Round : PenLineCap.Flat,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// 构造一段圆弧几何。sweepDegrees 为正值；方向由 clockwise 决定。
    /// 角度 → 屏幕点：x = cx + r·sin(θ), y = cy − r·cos(θ)。
    /// </summary>
    public static StreamGeometry BuildArc(Point center, double radius, double startDegrees,
        double sweepDegrees, bool clockwise)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geometry.Open())
        {
            double effectiveSweep = Math.Clamp(sweepDegrees, 0.0, 359.999);
            if (effectiveSweep <= 0.01)
            {
                geometry.Clear();
                geometry.Freeze();
                return geometry;
            }

            var dir = clockwise ? 1.0 : -1.0;
            var begin = PointOnCircle(center, radius, startDegrees);
            var end = PointOnCircle(center, radius, startDegrees + dir * effectiveSweep);

            ctx.BeginFigure(begin, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0,
                isLargeArc: effectiveSweep > 180.0,
                sweepDirection: clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                isStroked: true,
                isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>角度（0°=12点，顺时针为正）到屏幕坐标。</summary>
    public static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Sin(rad), center.Y - radius * Math.Cos(rad));
    }

    public static Color WithOpacity(Color color, double opacity)
    {
        var a = Math.Clamp(opacity, 0.0, 1.0);
        return Color.FromArgb((byte)Math.Round(a * 255), color.R, color.G, color.B);
    }

    public static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c)
            {
                return c;
            }
        }
        catch (FormatException)
        {
            // 非法输入，用回退色
        }
        return fallback;
    }

    public static string ToHex(Color color) =>
        string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
}
