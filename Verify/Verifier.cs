using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DshHaloRing.Models;
using DshHaloRing.Services;

namespace DshHaloRing.Verify;

/// <summary>
/// 像素级验证工具：渲染各状态/各尺寸的图标，导出 PNG 供人工查看，
/// 同时把渲染结果反解成数据（起止角度、颜色、进度弧长），用阈值断言。
/// 用法：dotnet run -- --verify [输出目录]
/// </summary>
public static class Verifier
{
    private readonly record struct Check(string Name, bool Passed, string Detail);

    public static int Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var checks = new List<Check>();
        var settings = new AppSettings();
        settings.EnsureDefaults();

        // ── 1. 导出各状态的 256px 预览 ──────────────────────────
        foreach (AgentState state in Enum.GetValues<AgentState>())
        {
            double progress = state switch
            {
                AgentState.Idle => 1.0,        // 空闲满环，与离线的空环区分
                AgentState.Offline => 0.0,
                AgentState.Completed => 1.0,
                AgentState.Fault => 0.35,
                AgentState.Degraded => 0.60,
                AgentState.WaitingForUser => 0.25,
                _ => 0.65,
            };
            foreach (int size in new[] { 256, 48, 32, 16 })
            {
                var bmp = RingIconRenderer.Render(settings, state, progress, 1.0, size);
                SavePng(bmp, Path.Combine(outputDir, $"state_{state.ToKey()}_{size}.png"));
            }
        }

        // ── 2. 几何校验：起始角 60°、顺时针、进度弧长 ──────────
        checks.Add(CheckStartAngleAtZeroProgress(settings));
        double start = settings.Ring.StartAngleDegrees;
        checks.Add(CheckArcEndpoint(settings, 0.25, expectDegrees: start + 90));
        checks.Add(CheckArcEndpoint(settings, 0.50, expectDegrees: start + 180));
        checks.Add(CheckArcEndpoint(settings, 0.75, expectDegrees: start + 270));
        checks.Add(CheckClockwiseDirection(settings));
        checks.Add(CheckCounterClockwiseOption(settings));
        checks.Add(CheckDefaultStartAngle(settings));
        checks.Add(CheckStartAngleConfigurable(settings, 0.0));
        checks.Add(CheckStartAngleConfigurable(settings, 180.0));

        // ── 3. 颜色校验 ─────────────────────────────────────────
        checks.Add(CheckStateColor(settings, AgentState.Running, "#10CD52"));
        checks.Add(CheckStateColor(settings, AgentState.Fault, "#FF3B30"));
        checks.Add(CheckStateColor(settings, AgentState.WaitingForUser, "#F5A623"));
        checks.Add(CheckStateColor(settings, AgentState.Idle, "#2F6B45"));
        checks.Add(CheckStateColor(settings, AgentState.Offline, "#5A5A5A"));
        checks.Add(CheckTransparentBackground(settings));
        checks.Add(CheckOpaqueBackground(settings, "#202020"));
        checks.Add(CheckCenterIsHollow(settings));
        checks.Add(CheckTrackVisible(settings));
        checks.Add(CheckThemeSwitching(settings));
        checks.AddRange(CheckDshStatusParsing());
        checks.Add(CheckProgressSmoothing());
        checks.Add(CheckIdleFullRingVsOffline());

        // ── 4. 优先级校验 ───────────────────────────────────────
        checks.AddRange(CheckPriorities());

        // ── 5. 动画波形校验 ─────────────────────────────────────
        checks.AddRange(CheckAnimations());

        // ── 6. ico 编码校验 ─────────────────────────────────────
        checks.Add(CheckIcoEncoding(settings, outputDir));

        // ── 输出报告 ───────────────────────────────────────────
        var sb = new StringBuilder();
        int failed = 0;
        foreach (var c in checks)
        {
            if (!c.Passed) failed++;
            sb.AppendLine($"{(c.Passed ? "PASS" : "FAIL")}  {c.Name}  —  {c.Detail}");
        }
        sb.AppendLine();
        sb.AppendLine($"合计 {checks.Count} 项，通过 {checks.Count - failed}，失败 {failed}");
        var report = sb.ToString();
        File.WriteAllText(Path.Combine(outputDir, "verify-report.txt"), report, Encoding.UTF8);
        Console.WriteLine(report);
        return failed == 0 ? 0 : 1;
    }

    // ══════════════════════ 几何 ══════════════════════

    /// <summary>0% 时进度弧退化为一点，仍应在起始角方向的环上。</summary>
    private static Check CheckStartAngleAtZeroProgress(AppSettings settings)
    {
        var ring = settings.Ring;
        var center = new System.Windows.Point(64, 64);
        double radius = 64 * ring.RadiusRatio;
        var expected = RingIconRenderer.PointOnCircle(center, radius, ring.StartAngleDegrees);
        bool ok = Math.Abs(expected.X - (64 + radius * Math.Sin(settings.Ring.StartAngleDegrees * Math.PI/180))) < 0.5 &&
                  Math.Abs(expected.Y - (64 - radius * Math.Cos(settings.Ring.StartAngleDegrees * Math.PI/180))) < 0.5;
        return new Check($"起始角 {settings.Ring.StartAngleDegrees:0}° 方向正确", ok,
            $"期望点≈({expected.X:0.0},{expected.Y:0.0})");
    }

    /// <summary>
    /// 反解像素：在半径环上按角度步进采样，找出亮色的角度区间，与期望的 [start, start+sweep] 比较。
    /// 容差来源：圆头端点会沿弧两端各外扩约半个线宽对应的角度，
    /// 32px 级图标上约 4–8°，因此按图标尺寸换算角度容差。
    /// </summary>
    private static Check CheckArcEndpoint(AppSettings settings, double progress, double expectDegrees)
    {
        var sample = RenderSample(settings, AgentState.Running, progress, 256);
        var lit = FindLitAngles(sample, 256, settings);
        if (lit.Count == 0)
        {
            return new Check($"进度 {progress:P0} 弧端点角度", false, "未检测到亮环像素");
        }

        double min = lit[0];
        double max = lit[^1];
        double tolerance = CapAngleTolerance(settings, 256);
        double start = settings.Ring.StartAngleDegrees;
        double startDev = Math.Abs(NormalizeDelta(min - start));
        double endDev = Math.Abs(NormalizeDelta(max - expectDegrees));
        bool ok = startDev <= tolerance && endDev <= tolerance;

        return new Check($"进度 {progress:P0} 弧从 {start:0}° 到 {expectDegrees:0}°", ok,
            $"实测角度区间 {min:0.0}°..{max:0.0}°（起点偏差 {startDev:0.0}°，终点偏差 {endDev:0.0}°，容差 {tolerance:0.0}°）");
    }

    /// <summary>圆头端点造成的角度外扩上限（度）。</summary>
    private static double CapAngleTolerance(AppSettings settings, int size)
    {
        var (thickness, radius) = RingIconRenderer.ComputeRingGeometry(settings, size);
        double capRadius = settings.Ring.RoundCaps ? thickness / 2.0 : 0.0;
        // 端点圆心到弧端的距离 + 圆头半径 对应的张角
        double halfWidth = capRadius + 1.0;
        return Math.Asin(Math.Clamp(halfWidth / radius, -1, 1)) * 180.0 / Math.PI + 2.0;
    }

    /// <summary>顺时针：50% 的弧跨越 180°，其中点应≈150°，起点在 60°。</summary>
    private static Check CheckClockwiseDirection(AppSettings settings)
    {
        var lit = FindLitAngles(RenderSample(settings, AgentState.Running, 0.5, 256), 256, settings);
        if (lit.Count == 0)
        {
            return new Check("顺时针方向", false, "未检测到亮环像素");
        }
        double mid = ArcMidpoint(lit);
        double expectedMid = settings.Ring.StartAngleDegrees + 180 / 2.0;
        double dev = Math.Abs(NormalizeDelta(mid - expectedMid));
        bool ok = dev <= 12;
        return new Check($"顺时针：50% 时弧中点≈{expectedMid:0}°", ok, $"实测中点 {mid:0.0}°，偏差 {dev:0.0}°");
    }

    private static Check CheckCounterClockwiseOption(AppSettings settings)
    {
        var ccw = CloneWith(settings, s => s.Ring.Clockwise = false);
        var lit = FindLitAngles(RenderSample(ccw, AgentState.Running, 0.25, 256), 256, ccw);
        if (lit.Count == 0)
        {
            return new Check("逆时针开关生效", false, "未检测到亮环像素");
        }
        // 逆时针 25%（90°）：从起始角往回走 90°，中点 = 起始角 − 45°。
        // 起始角 0° 时覆盖 270°..0°，展开区间 270→360，中点 = 315°。
        double expected = settings.Ring.StartAngleDegrees - 45.0;
        double mid = ArcMidpoint(lit);
        double dev = Math.Abs(NormalizeDelta(mid - expected));
        bool ok = dev <= 15;
        var span = NormalizeDelta(lit[^1] - lit[0]) is var d && d < 0 ? d + 360 : d;
        return new Check($"逆时针开关生效：25% 时中点≈{expected:0}°", ok,
            $"实测中点 {mid:0.0}°，偏差 {dev:0.0}°；首={lit[0]:0} 末={lit[^1]:0} 数={lit.Count} 跨度={span:0}");
    }

    /// <summary>
    /// 弧的中点角：把角度展开成连续区间后取中心。
    /// 用单位向量求平均会得到“短弧”的中点，跨 0° 的弧会被算到反向，因此必须按展开后的区间算。
    /// </summary>
    private static double ArcMidpoint(List<double> litAngles)
    {
        double first = litAngles[0];
        double last = litAngles[^1];
        double span = last - first;
        if (span < 0)
        {
            span += 360.0;
        }
        return (first + span / 2.0) % 360.0;
    }

    private static Check CheckDefaultStartAngle(AppSettings settings) =>
        new("默认起始角度 = 0°（顶部起始）", Math.Abs(settings.Ring.StartAngleDegrees - 0.0) < 1e-9,
            $"实际 {settings.Ring.StartAngleDegrees}°");

    private static Check CheckStartAngleConfigurable(AppSettings settings, double angle)
    {
        var custom = CloneWith(settings, s => s.Ring.StartAngleDegrees = angle);
        // 进度极小（2%）时弧几乎退化为起点
        var lit = FindLitAngles(RenderSample(custom, AgentState.Running, 0.02, 256), 256, custom);
        if (lit.Count == 0)
        {
            return new Check($"起始角可配置为 {angle:0}°", false, "未检测到亮环像素");
        }
        double dev = Math.Abs(NormalizeDelta(lit[0] - angle));
        bool ok = dev <= CapAngleTolerance(custom, 256) + 2;
        return new Check($"起始角可配置为 {angle:0}°", ok, $"实测起点 {lit[0]:0.0}°，偏差 {dev:0.0}°");
    }

    // ══════════════════════ 颜色 ══════════════════════

    private static Check CheckStateColor(AppSettings settings, AgentState state, string expectedHex)
    {
        var sample = RenderSample(settings, state, 0.5, 256);
        var target = RingIconRenderer.ParseColor(expectedHex, Colors.Lime);
        var (r, g, b) = FindDominantRingColor(sample, 256, settings, target);

        // 管状渐变会让环身颜色有明暗过渡，实测值偏向中间色调；
        // 因此这里检查色相方向是否正确，并允许更大的亮度差。
        double maxChannelDelta = Math.Max(Math.Abs(r - target.R), Math.Max(Math.Abs(g - target.G), Math.Abs(b - target.B)));
        double hueDist = Math.Sqrt(Math.Pow(r - target.R, 2) + Math.Pow(g - target.G, 2) + Math.Pow(b - target.B, 2));
        bool ok = hueDist <= 45 || maxChannelDelta <= 60;
        return new Check($"{state.ToDisplayName()} 颜色 ≈ {expectedHex}", ok,
            $"实测 #{r:X2}{g:X2}{b:X2}，色差 {hueDist:0.0}（管状渐变允许明暗偏移）");
    }

    /// <summary>新默认：透明底板，四个角必须全透明（托盘里只见圆环，没有方块底）。</summary>
    private static Check CheckTransparentBackground(AppSettings settings)
    {
        var sample = RenderSample(settings, AgentState.Running, 0.5, 256);
        // 四个角都必须完全透明
        var corners = new[] { (4, 4), (251, 4), (4, 251), (251, 251) };
        bool allClear = true;
        var detail = new System.Text.StringBuilder();
        foreach (var (x, y) in corners)
        {
            var (_, _, _, a) = GetPixel(sample, 256, x, y);
            if (a != 0) allClear = false;
            detail.Append($"({x},{y})a={a} ");
        }
        // 环上必须有不透明内容：进度 50% 的弧覆盖起始角到起始角+180°，
        // 取弧中点 150° 处的环上像素（正上方 0° 不在弧内，不能用来采样）。
        double ringR = RingIconRenderer.ComputeRingGeometry(settings, 256).radius;
        var onArc = RingIconRenderer.PointOnCircle(new System.Windows.Point(128, 128), ringR,
            settings.Ring.StartAngleDegrees + 180.0 * 0.5);
        var (_, _, _, ringA) = GetPixel(sample, 256, (int)Math.Round(onArc.X), (int)Math.Round(onArc.Y));
        bool ok = allClear && ringA > 150;
        return new Check("默认透明底板：四角全透明、环上有内容", ok, $"{detail}环上a={ringA}");
    }

    /// <summary>不透明底板选项：开启后角落应为实心底板色，且底板为圆形（角落仍透明，中心区域实心）。</summary>
    private static Check CheckOpaqueBackground(AppSettings settings, string expectedHex)
    {
        var opaque = CloneWith(settings, s =>
        {
            s.Canvas.TransparentBackground = false;
            s.Canvas.BackgroundColor = expectedHex;
            s.Canvas.CornerRadiusRatio = 0.5;   // 圆形底板
        });
        var sample = RenderSample(opaque, AgentState.Running, 0.5, 256);
        var target = RingIconRenderer.ParseColor(expectedHex, Colors.Black);
        // 圆形底板的角落应透明，圆内靠近边缘处（圆环内侧空白区）应为底板色
        var (_, _, _, cornerA) = GetPixel(sample, 256, 3, 3);
        var (r, g, b, innerA) = GetPixel(sample, 256, 128, 128);
        double dist = Math.Sqrt(Math.Pow(r - target.R, 2) + Math.Pow(g - target.G, 2) + Math.Pow(b - target.B, 2));
        bool ok = cornerA == 0 && innerA > 240 && dist <= 12;
        return new Check($"不透明圆形底板 ≈ {expectedHex}", ok,
            $"角落a={cornerA}，圆心#{r:X2}{g:X2}{b:X2} a={innerA}，色差 {dist:0.0}");
    }

    /// <summary>中心无亮点：圆心应为纯底板色 #202020，与状态色无关。</summary>
    private static Check CheckCenterIsHollow(AppSettings settings)
    {
        var sample = RenderSample(settings, AgentState.Running, 0.5, 256);
        var (r, g, b, a) = GetPixel(sample, 256, 128, 128);
        bool ok = a == 255 && r == 0x20 && g == 0x20 && b == 0x20;
        return new Check("圆环中心无亮点（为底板色 #202020）", ok,
            $"圆心 #{r:X2}{g:X2}{b:X2} a={a}");
    }

    /// <summary>灰色轨道：未完成部分应显示为灰色轨道色，而非透明。</summary>
    private static Check CheckTrackVisible(AppSettings settings)
    {
        // 进度 50%：300° 方向属于未完成段，应呈现轨道灰
        var sample = RenderSample(settings, AgentState.Running, 0.5, 256);
        var (_, radius) = RingIconRenderer.ComputeRingGeometry(settings, 256);
        var p = RingIconRenderer.PointOnCircle(new System.Windows.Point(128, 128), radius, 300);
        var (r, g, b, a) = GetPixel(sample, 256, (int)Math.Round(p.X), (int)Math.Round(p.Y));
        var trackTarget = RingIconRenderer.ParseColor(settings.Ring.TrackColor, Colors.Gray);
        double dist = Math.Sqrt(Math.Pow(r - trackTarget.R, 2) + Math.Pow(g - trackTarget.G, 2) + Math.Pow(b - trackTarget.B, 2));
        bool ok = settings.Ring.ShowTrack && a == 255 && dist <= 45;
        return new Check("灰色轨道可见（未完成段为轨道色）", ok,
            $"ShowTrack={settings.Ring.ShowTrack}，300° 处 #{r:X2}{g:X2}{b:X2} a={a}，与轨道色差 {dist:0.0}");
    }

    /// <summary>
    /// 主题切换：切到日间后底板应变为 #F2F2F2、轨道应变为 #C8C8C8，
    /// 切回夜间应恢复 #202020 / #3F3F3F，且两者渲染结果确实不同。
    /// </summary>
    private static Check CheckThemeSwitching(AppSettings settings)
    {
        var day = CloneWith(settings, s => s.ApplyTheme(ThemeMode.Light));
        var night = CloneWith(settings, s => s.ApplyTheme(ThemeMode.Dark));

        // 圆心必须是底板色
        var daySample = RenderSample(day, AgentState.Running, 0.5, 256);
        var (dr, dg, db, _) = GetPixel(daySample, 256, 128, 128);

        var nightSample = RenderSample(night, AgentState.Running, 0.5, 256);
        var (nr, ng, nb, _) = GetPixel(nightSample, 256, 128, 128);

        bool dayOk = dr == 0xF2 && dg == 0xF2 && db == 0xF2;
        bool nightOk = nr == 0x20 && ng == 0x20 && nb == 0x20;
        bool differ = (dr != nr) || (dg != ng) || (db != nb);

        // 轨道色也要跟着变
        bool trackOk =
            string.Equals(day.Ring.TrackColor, "#C8C8C8", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(night.Ring.TrackColor, "#3F3F3F", StringComparison.OrdinalIgnoreCase);

        bool ok = dayOk && nightOk && differ && trackOk;
        return new Check("主题切换（夜间 #202020 / 日间 #F2F2F2）", ok,
            $"日间圆心 #{dr:X2}{dg:X2}{db:X2}，夜间圆心 #{nr:X2}{ng:X2}{nb:X2}，" +
            $"轨道 {day.Ring.TrackColor}/{night.Ring.TrackColor}");
    }

    /// <summary>
    /// DSH 状态桥文件解析：验证 key=value 文本能被正确转成状态与进度。
    /// 这是接真实 DSH 的推荐路径，必须保证解析稳定。
    /// </summary>
    private static IEnumerable<Check> CheckDshStatusParsing()
    {
        // 典型内容
        const string sample =
            "version=1\n" +
            "state=running\n" +
            "priority=2\n" +
            "updated=1792765694881\n" +
            "pid=4712\n" +
            "detail=Agent 运行中（turn 12）\n" +
            "progress=44\n" +
            "attention=0\n";

        var fields = DshStatusFileSource.ParseKeyValue(sample);
        var snap = DshStatusFileSource.ToSnapshot(fields);

        bool stateOk = snap.State == AgentState.Running;
        bool progressOk = Math.Abs(snap.Progress - 0.44) < 0.001;
        bool detailOk = snap.Detail == "Agent 运行中（turn 12）";
        bool timeOk = snap.TimestampUtc.HasValue;

        yield return new Check("DSH 状态桥：running + progress 44% 解析正确",
            stateOk && progressOk && detailOk && timeOk,
            $"state={snap.State} progress={snap.Progress:P0} detail='{snap.Detail}' 时间戳={snap.TimestampUtc:HH:mm:ss}");

        // 各状态映射
        var cases = new (string text, AgentState expect)[]
        {
            ("state=idle", AgentState.Idle),
            ("state=running", AgentState.Running),
            ("state=attention", AgentState.WaitingForUser),
            ("state=degraded", AgentState.Degraded),
            ("state=error", AgentState.Fault),
            ("state=completed", AgentState.Completed),
            ("state=offline", AgentState.Offline),
        };
        bool allMapped = true;
        var detail = new System.Text.StringBuilder();
        foreach (var (text, expect) in cases)
        {
            var s = DshStatusFileSource.ToSnapshot(DshStatusFileSource.ParseKeyValue(text));
            bool ok = s.State == expect;
            allMapped &= ok;
            detail.Append($"{text}→{s.State}:{(ok ? "√" : "×")} ");
        }
        yield return new Check("DSH 状态桥：7 种 state 全部映射正确", allMapped, detail.ToString());

        // progress 边界：100 与 0（离线才是空环）
        var full = DshStatusFileSource.ToSnapshot(
            DshStatusFileSource.ParseKeyValue("state=completed\nprogress=100"));
        var offline = DshStatusFileSource.ToSnapshot(
            DshStatusFileSource.ParseKeyValue("state=offline\nprogress=0"));
        yield return new Check("DSH 状态桥：progress 边界（0/100）",
            Math.Abs(full.Progress - 1.0) < 1e-9 && Math.Abs(offline.Progress - 0.0) < 1e-9,
            $"100→{full.Progress:P0}，offline 0→{offline.Progress:P0}");

        // 空闲态必须满环：即使状态源写 progress=0，也要强制成 100%，
        // 否则空闲与离线在托盘上无法区分。
        var idleZero = DshStatusFileSource.ToSnapshot(
            DshStatusFileSource.ParseKeyValue("state=idle\nprogress=0"));
        var idleNoProgress = DshStatusFileSource.ToSnapshot(
            DshStatusFileSource.ParseKeyValue("state=idle"));
        yield return new Check("DSH 状态桥：空闲态强制满环（progress=0 → 100%）",
            Math.Abs(idleZero.Progress - 1.0) < 1e-9 && Math.Abs(idleNoProgress.Progress - 1.0) < 1e-9,
            $"idle+progress=0 → {idleZero.Progress:P0}，idle 无 progress → {idleNoProgress.Progress:P0}");

        // 缺字段容错
        var partial = DshStatusFileSource.ToSnapshot(
            DshStatusFileSource.ParseKeyValue("state=running"));
        yield return new Check("DSH 状态桥：缺 progress 字段不崩溃",
            partial.State == AgentState.Running && partial.Progress >= 0,
            $"state={partial.State} progress={partial.Progress:P0}");
    }

    /// <summary>
    /// 空闲 vs 离线：空闲必须满环（100%），离线必须空环（0%），
    /// 否则两者在托盘上只靠颜色深浅区分，远看几乎一样。
    /// 这里同时校验「像素上确实一满一空」，而不只是数值。
    /// </summary>
    private static Check CheckIdleFullRingVsOffline()
    {
        var settings = new AppSettings();
        settings.EnsureDefaults();

        // 数值层：空闲/离线经状态源来的进度
        var idleSnapshot = DshStatusFileSource.ToSnapshot(
            DshStatusFileSource.ParseKeyValue("state=idle\nprogress=0"));
        var offlineSnapshot = DshStatusFileSource.ToSnapshot(
            DshStatusFileSource.ParseKeyValue("state=offline\nprogress=0"));
        bool idleFull = Math.Abs(idleSnapshot.Progress - 1.0) < 1e-9;
        bool offlineEmpty = Math.Abs(offlineSnapshot.Progress - 0.0) < 1e-9;

        // 像素层：取 180°（环正下方）。满环时必然点亮，空环时只剩灰色轨道。
        // 注意起点为 0°、顺时针，所以 320° 在弧起点之前、满环也不亮，不能用作判据。
        const double probeDegrees = 180.0;
        int covered = CountLitPixelsAtAngle(settings, AgentState.Idle, 1.0, probeDegrees);
        int uncovered = CountLitPixelsAtAngle(settings, AgentState.Offline, 0.0, probeDegrees);
        bool pixelOk = covered > 0 && uncovered == 0;

        bool ok = idleFull && offlineEmpty && pixelOk;
        return new Check("空闲满环 / 离线空环（视觉可区分）", ok,
            $"解析：idle→{idleSnapshot.Progress:P0}，offline→{offlineSnapshot.Progress:P0}；" +
            $"{probeDegrees:0}° 处点亮像素：空闲 {covered} 个，离线 {uncovered} 个");
    }

    /// <summary>
    /// 统计指定角度上确实被「状态色」点亮的采样点数。
    /// 判据要求色相饱和（非灰色轨道）+ 明度足够，仅靠色差会把灰色轨道误判成主环。
    /// </summary>
    private static int CountLitPixelsAtAngle(AppSettings settings, AgentState state, double progress, double degrees)
    {
        const int size = 256;
        var bmp = RenderSample(settings, state, progress, size);
        var style = settings.StyleFor(state);
        var target = style.ToMediaColor();

        // 状态色本身是灰色（如离线 #5A5A5A）时不能用饱和度判据，退回严格色差。
        int tMax = Math.Max(target.R, Math.Max(target.G, target.B));
        int tMin = Math.Min(target.R, Math.Min(target.G, target.B));
        bool targetSaturated = (tMax - tMin) >= 40;

        var (thickness, radius) = RingIconRenderer.ComputeRingGeometry(settings, size);
        thickness = Math.Max(thickness, 1.0);

        double rad = degrees * Math.PI / 180.0;
        int lit = 0;
        for (int i = -2; i <= 2; i++)
        {
            double r = radius + i * thickness / 5.0;
            int x = (int)Math.Round(size / 2.0 + r * Math.Cos(rad));
            int y = (int)Math.Round(size / 2.0 + r * Math.Sin(rad));
            if (x < 0 || y < 0 || x >= size || y >= size)
            {
                continue;
            }
            var (pr, pg, pb, pa) = GetPixel(bmp, size, x, y);
            if (pa < 128)
            {
                continue;
            }
            double dist = Math.Sqrt(
                Math.Pow(pr - target.R, 2) + Math.Pow(pg - target.G, 2) + Math.Pow(pb - target.B, 2));

            if (targetSaturated)
            {
                // 有色状态：既要色差小，也要有饱和度（排除灰色轨道 #3F3F3F）
                int pMax = Math.Max(pr, Math.Max(pg, pb));
                int pMin = Math.Min(pr, Math.Min(pg, pb));
                if (dist <= 60 && (pMax - pMin) >= 30 && pMax >= 70)
                {
                    lit++;
                }
            }
            else if (dist <= 20)
            {
                lit++;
            }
        }
        return lit;
    }

    /// <summary>
    /// 进度平滑：SetProgress 设目标值后，实际渲染进度应逐帧线性逼近，
    /// 而不是一步跳到目标（跳跃式）。
    /// </summary>
    private static Check CheckProgressSmoothing()
    {
        var settings = new AppSettings();
        settings.EnsureDefaults();
        var aggregator = new StateAggregator();
        aggregator.Set(AgentState.Running, true);

        using var loop = new IconAnimationLoop(aggregator, settings);
        var samples = new List<double>();
        loop.FrameChanged += (_, f) => samples.Add(f.Progress);

        loop.SnapProgress(0.0);      // 从 0 开始
        loop.SetProgress(0.8);       // 目标 80%
        loop.Start();

        // 手动驱动若干帧：DispatcherTimer 在单元测试里不会自动跑，
        // 这里用 Thread.Sleep + DoEvents 的方式推进。
        var deadline = DateTime.UtcNow.AddSeconds(6.0);
        while (DateTime.UtcNow < deadline)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            System.Threading.Thread.Sleep(20);
            if (samples.Count > 2 && samples[^1] >= 0.799)
            {
                break;
            }
        }
        loop.Stop();

        if (samples.Count < 3)
        {
            return new Check("进度线性推进（非跳跃）", false,
                $"采样不足（{samples.Count} 帧），无法判定");
        }

        // 判断是否“逐步逼近”：中间应有 0 与目标之间的采样值
        bool hasIntermediate = false;
        foreach (var v in samples) { if (v > 0.02 && v < 0.78) { hasIntermediate = true; break; } }
        // 判断是否“单调递增”
        bool monotonic = true;
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i] < samples[i - 1] - 1e-9) { monotonic = false; break; }
        }
        // 判断是否最终到达目标
        bool reached = Math.Abs(samples[^1] - 0.8) < 0.02;

        bool ok = hasIntermediate && monotonic && reached;
        return new Check("进度线性推进（非跳跃）", ok,
            $"{samples.Count} 帧，首={samples[0]:0.000} 末={samples[^1]:0.000}，" +
            $"有中间值={hasIntermediate} 单调={monotonic} 到达={reached}");
    }

    // ══════════════════════ 优先级 ══════════════════════

    private static IEnumerable<Check> CheckPriorities()
    {
        // 故障 > 降级
        var a = new StateAggregator();
        a.Set(AgentState.Degraded, true);
        a.Set(AgentState.Fault, true);
        yield return new Check("优先级：故障 > 降级", a.Current == AgentState.Fault, $"当前 {a.Current}");

        // 降级 > 等待
        var b = new StateAggregator();
        b.Set(AgentState.WaitingForUser, true);
        b.Set(AgentState.Degraded, true);
        yield return new Check("优先级：降级 > 等待输入", b.Current == AgentState.Degraded, $"当前 {b.Current}");

        // 等待 > 运行
        var c = new StateAggregator();
        c.Set(AgentState.Running, true);
        c.Set(AgentState.WaitingForUser, true);
        yield return new Check("优先级：等待输入 > 运行中", c.Current == AgentState.WaitingForUser, $"当前 {c.Current}");

        // 运行 > 完成
        var d = new StateAggregator();
        d.Set(AgentState.Running, true);
        d.Set(AgentState.Completed, true);
        yield return new Check("优先级：运行中 > 完成", d.Current == AgentState.Running, $"当前 {d.Current}");

        // 完成 > 空闲
        var e = new StateAggregator();
        e.Set(AgentState.Idle, true);
        e.Set(AgentState.Completed, true);
        yield return new Check("优先级：完成 > 空闲", e.Current == AgentState.Completed, $"当前 {e.Current}");

        // 完成态持续保持：等待远超旧超时时间后仍应为 Completed
        var f = new StateAggregator();
        f.Set(AgentState.Idle, true);
        f.EnterCompleted();
        bool wasCompleted = f.Current == AgentState.Completed;
        System.Threading.Thread.Sleep(400);
        f.Tick(DateTimeOffset.UtcNow);
        bool stillCompleted = f.Current == AgentState.Completed;
        yield return new Check("完成态持续保持（不自动回落）", wasCompleted && stillCompleted,
            $"进入后=Completed:{wasCompleted}，等待后仍=Completed:{stillCompleted}，当前 {f.Current}");

        // 下一个状态到来时，完成态应被清除
        var f2 = new StateAggregator();
        f2.EnterCompleted();
        bool entered = f2.Current == AgentState.Completed;
        f2.ClearCompleted();
        f2.Set(AgentState.Idle, true);
        bool switched = f2.Current == AgentState.Idle;
        yield return new Check("新状态到来时完成态被清除", entered && switched,
            $"进入完成={entered}，切到空闲={switched}，当前 {f2.Current}");

        // 空集合 → 保持初始空闲（离线必须由状态源显式声明，避免启动瞬间误判）
        var g = new StateAggregator();
        yield return new Check("无状态成立时保持空闲（离线需显式声明）", g.Current == AgentState.Idle, $"当前 {g.Current}");

        // 显式声明离线 → 显示离线
        var g2 = new StateAggregator();
        g2.Set(AgentState.Offline, true);
        yield return new Check("显式声明离线时显示离线", g2.Current == AgentState.Offline, $"当前 {g2.Current}");

        // 全量优先级顺序
        var pairs = new (AgentState high, AgentState low)[]
        {
            (AgentState.Fault, AgentState.Degraded),
            (AgentState.Degraded, AgentState.WaitingForUser),
            (AgentState.WaitingForUser, AgentState.Running),
            (AgentState.Running, AgentState.Completed),
            (AgentState.Completed, AgentState.Idle),
            (AgentState.Idle, AgentState.Offline),
        };
        bool allGood = true;
        var detail = new StringBuilder();
        foreach (var (high, low) in pairs)
        {
            var ag = new StateAggregator();
            ag.Set(low, true);
            ag.Set(high, true);
            bool pass = ag.Current == high;
            allGood &= pass;
            detail.Append($"{high}>{low}:{(pass ? "√" : "×")} ");
        }
        yield return new Check("优先级链完整（故障>降级>等待>运行>完成>空闲>离线）", allGood, detail.ToString());
    }

    // ══════════════════════ 动画 ══════════════════════

    private static IEnumerable<Check> CheckAnimations()
    {
        // 运行中：呼吸效果已取消，应保持常亮
        var running = SampleSeries(AgentState.Running, 2.4, 0.02);
        double rMin = double.MaxValue, rMax = double.MinValue;
        foreach (var v in running) { rMin = Math.Min(rMin, v); rMax = Math.Max(rMax, v); }
        yield return new Check("运行中：常亮（呼吸已取消）", Math.Abs(rMin - 1.0) < 1e-9 && Math.Abs(rMax - 1.0) < 1e-9,
            $"min={rMin:0.00} max={rMax:0.00}");

        // 等待：慢闪（周期 1.6s，一个周期内应同时出现接近 0.15 与 1.0）
        var waiting = SampleSeries(AgentState.WaitingForUser, 1.6, 0.01);
        yield return new Check("等待输入：慢闪（周期≈1.6s）", ContainsLowHigh(waiting, 0.2, 0.97),
            DescribeRange(waiting));

        // 降级：快闪（周期 0.42s → 1 秒内应出现 ≥2 次亮暗完整循环）
        var degraded = SampleSeries(AgentState.Degraded, 1.0, 0.005);
        int cycles = CountCycles(degraded, 0.6);
        yield return new Check("降级：快闪（0.42s 周期）", cycles >= 2, $"1 秒内亮暗循环 {cycles} 次");

        // 故障：常亮 + 双闪（2.4s 内出现 2 次压暗，其余时间接近 1.0）
        var fault = SampleSeries(AgentState.Fault, 2.4, 0.005);
        int dimCount = 0;
        int dimRuns = 0;
        bool inDim = false;
        foreach (var v in fault)
        {
            bool dim = v < 0.5;
            if (dim && !inDim) { dimCount++; }
            if (dim) { dimRuns++; }
            inDim = dim;
        }
        yield return new Check("故障：常亮 + 周期性双闪", dimCount == 2,
            $"2.4s 内压暗段 {dimCount} 段，共 {dimRuns} 帧处于暗态");

        // 完成：持续慢闪，周期 2 秒
        var completed = SampleSeries(AgentState.Completed, 4.0, 0.01);
        int flashCycles = CountCycles(completed, 0.6);
        double cMin = double.MaxValue, cMax = double.MinValue;
        foreach (var v in completed) { cMin = Math.Min(cMin, v); cMax = Math.Max(cMax, v); }
        // 4 秒应正好经历 2 个周期（2 次上升沿），且幅度明显
        bool okBlink = flashCycles >= 2 && cMin < 0.45 && cMax > 0.97;
        yield return new Check("完成：持续慢闪（周期 2s，幅度 0.30~1.0）", okBlink,
            $"4 秒内 {flashCycles} 个周期，min={cMin:0.00} max={cMax:0.00}");

        // 空闲/离线：常亮
        bool idleFlat = AllNear(SampleSeries(AgentState.Idle, 5.0, 0.05), 1.0);
        bool offlineFlat = AllNear(SampleSeries(AgentState.Offline, 5.0, 0.05), 1.0);
        yield return new Check("空闲/离线：常亮不闪", idleFlat && offlineFlat, $"idle={idleFlat} offline={offlineFlat}");

        // 关闭动画 → 恒为 1.0
        bool disabledFlat = true;
        for (double t = 0; t < 3.0; t += 0.1)
        {
            if (Math.Abs(AnimationEngine.Intensity(AgentState.Degraded, t, false) - 1.0) > 1e-9)
            {
                disabledFlat = false;
                break;
            }
        }
        yield return new Check("关闭动画后强度恒为 1.0", disabledFlat, $"结果 {disabledFlat}");
    }

    // ══════════════════════ ico 编码 ══════════════════════

    private static Check CheckIcoEncoding(AppSettings settings, string outputDir)
    {
        var images = new List<BitmapSource>();
        foreach (var size in new[] { 48, 32, 24, 20, 16 })
        {
            images.Add(RingIconRenderer.RenderBgraPremultipliedAsStraight(settings, AgentState.Running, 0.5, 1.0, size));
        }
        var bytes = IconPackager.EncodeMultiIco(images);
        var path = Path.Combine(outputDir, "tray-multisize.ico");
        File.WriteAllBytes(path, bytes);

        // 回读校验：用 System.Drawing 载入并确认尺寸
        int frameCount = BitConverter.ToUInt16(bytes, 4);
        bool loadOk;
        string detail;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var icon = new System.Drawing.Icon(ms, new System.Drawing.Size(32, 32));
            loadOk = icon.Width is > 0;
            detail = $"{frameCount} 帧，载入成功，取到 {icon.Width}x{icon.Height}，{bytes.Length} 字节";
        }
        catch (Exception ex)
        {
            loadOk = false;
            detail = $"载入失败：{ex.Message}";
        }

        return new Check("多尺寸 ico 编码可被系统载入", frameCount == 5 && loadOk, detail);
    }

    // ══════════════════════ 采样工具 ══════════════════════

    private static byte[] RenderSample(AppSettings settings, AgentState state, double progress, int size)
    {
        var bmp = RingIconRenderer.RenderBgraPremultipliedAsStraight(settings, state, progress, 1.0, size);
        var stride = size * 4;
        var pixels = new byte[stride * size];
        bmp.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static (byte r, byte g, byte b, byte a) GetPixel(byte[] pixels, int size, int x, int y)
    {
        int i = (y * size + x) * 4;
        return (pixels[i + 2], pixels[i + 1], pixels[i], pixels[i + 3]);
    }

    /// <summary>
    /// 沿环半径逐角度采样（1° 步进），返回“亮”的角度列表（按卷绕顺序，起点为 0° 之后第一个亮角）。
    /// 判亮依据：与轨道色/底板色的差异明显，且通道足够饱和。
    /// </summary>
    private static List<double> FindLitAngles(byte[] pixels, int size, AppSettings settings)
    {
        var ring = settings.Ring;
        double cx = size / 2.0, cy = size / 2.0;
        double radius = RingIconRenderer.ComputeRingGeometry(settings, size).radius;
        _ = ring;

        var lit = new bool[360];
        for (int deg = 0; deg < 360; deg++)
        {
            var p = RingIconRenderer.PointOnCircle(new System.Windows.Point(cx, cy), radius, deg);
            int x = (int)Math.Round(p.X);
            int y = (int)Math.Round(p.Y);
            if (x < 0 || y < 0 || x >= size || y >= size) continue;
            var (r, g, b, a) = GetPixel(pixels, size, x, y);
            // 亮：绿色通道明显高于底板灰(0x20)且饱和（非灰色轨道）
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            bool saturated = (max - min) >= 40 && max >= 90;
            lit[deg] = saturated && a > 128;
        }

        // 找出连续段（允许小断裂 3°）
        var result = new List<double>();
        int gap = 0;
        // 从“亮段起点”开始扫：跳过尾部的亮像素（跨 0° 的弧在 359° 也是亮的），
        // 否则起点会被钉在 0°，跨度被截断。
        int start = -1;
        for (int d = 0; d < 360; d++)
        {
            if (lit[d] && !lit[((d - 1) % 360 + 360) % 360])
            {
                start = d;
                break;
            }
        }
        if (start < 0)
        {
            return result;   // 整圈全亮或全灭
        }

        int last = start;
        for (int i = start; i < start + 364; i++)
        {
            if (lit[i % 360])
            {
                last = i;
                gap = 0;
            }
            else if (++gap > 3 || i - last > 4)
            {
                break;
            }
        }

        for (int i = start; i <= last; i++)
        {
            result.Add((i % 360 + 360) % 360);
        }
        return result;
    }

    /// <summary>
    /// 取主环上的主导色：沿环半径密集采样，只保留“与该状态应显示颜色最接近”的像素求均值。
    /// 直接按“饱和度最高”筛选会误选轨道色（灰）或底板，低饱和状态（暗绿/灰）会失败。
    /// </summary>
    private static (byte r, byte g, byte b) FindDominantRingColor(byte[] pixels, int size, AppSettings settings,
        System.Windows.Media.Color expected)
    {
        var ring = settings.Ring;
        double cx = size / 2.0, cy = size / 2.0;
        var (thickness, radius) = RingIconRenderer.ComputeRingGeometry(settings, size);
        _ = ring;

        // 只在弧线最中心处的像素值有意义：越靠边越接近抗锯齿半透明。
        long sr = 0, sg = 0, sb = 0;
        int n = 0;
        for (int deg = 0; deg < 360; deg++)
        {
            for (double rr = radius - thickness * 0.05; rr <= radius + thickness * 0.05; rr += 0.25)
            {
                var p = RingIconRenderer.PointOnCircle(new System.Windows.Point(cx, cy), rr, deg);
                int x = (int)Math.Round(p.X), y = (int)Math.Round(p.Y);
                if (x < 0 || y < 0 || x >= size || y >= size) continue;
                var (r, g, b, a) = GetPixel(pixels, size, x, y);
                if (a < 200) continue;
                double d = Math.Sqrt(Math.Pow(r - expected.R, 2) + Math.Pow(g - expected.G, 2) + Math.Pow(b - expected.B, 2));
                if (d <= 40)
                {
                    sr += r; sg += g; sb += b; n++;
                }
            }
        }

        if (n == 0) return (0, 0, 0);
        return ((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));
    }

    private static double NormalizeDelta(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg < -180) deg += 360;
        return deg;
    }

    private static AppSettings CloneWith(AppSettings source, Action<AppSettings> mutate)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(source);
        var clone = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        clone.EnsureDefaults();
        mutate(clone);
        return clone;
    }

    private static List<double> SampleSeries(AgentState state, double duration, double step)
    {
        var list = new List<double>();
        for (double t = 0; t <= duration; t += step)
        {
            list.Add(AnimationEngine.Intensity(state, t, true));
        }
        return list;
    }

    private static bool ContainsLowHigh(List<double> values, double low, double high)
    {
        bool sawLow = false, sawHigh = false;
        foreach (var v in values)
        {
            if (v <= low + 0.05) sawLow = true;
            if (v >= high) sawHigh = true;
        }
        return sawLow && sawHigh;
    }

    private static string DescribeRange(List<double> values)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in values) { min = Math.Min(min, v); max = Math.Max(max, v); }
        return $"min={min:0.00} max={max:0.00} 采样 {values.Count} 点";
    }

    private static int CountCycles(List<double> values, double threshold)
    {
        // 统计“低 → 高”的上升沿数量
        int cycles = 0;
        bool prevLow = values.Count > 0 && values[0] < threshold;
        foreach (var v in values)
        {
            bool curLow = v < threshold;
            if (prevLow && !curLow) cycles++;
            prevLow = curLow;
        }
        return cycles;
    }

    private static bool AllNear(List<double> values, double target, double tolerance = 1e-9)
    {
        foreach (var v in values)
        {
            if (Math.Abs(v - target) > tolerance) return false;
        }
        return true;
    }

    private static void SavePng(BitmapSource bmp, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
