using System;
using DshHaloRing.Models;
namespace DshHaloRing.Services;

/// <summary>
/// 各状态的动画亮度波形。返回 0..1 的强度系数，乘到颜色不透明度上。
/// 时长与节奏按需求表：
///   运行中 —— 缓慢呼吸
///   等待输入/授权 —— 慢闪
///   降级 —— 快闪
///   故障 —— 常亮 + 周期性双闪
///   完成 —— 短时快闪（约 1–2 秒内闪 3–4 次），由 StateAggregator 负责到时回落空闲
///   空闲 / 离线 —— 常亮
/// </summary>
public static class AnimationEngine
{
    public static double Intensity(Models.AgentState state, double elapsedSeconds, bool animationsEnabled)
    {
        if (!animationsEnabled)
        {
            return 1.0;
        }

        return state switch
        {
            // 运行中：常亮（呼吸效果已按要求取消）
            Models.AgentState.Running => 1.0,
            Models.AgentState.WaitingForUser => SoftFlash(elapsedSeconds, periodSeconds: 1.6, min: 0.15, max: 1.0, edge: 0.25),
            Models.AgentState.Degraded => SoftFlash(elapsedSeconds, periodSeconds: 0.42, min: 0.10, max: 1.0, edge: 0.15),
            Models.AgentState.Fault => FaultDoubleFlash(elapsedSeconds),
            Models.AgentState.Completed => CompletedSlowBlink(elapsedSeconds),
            Models.AgentState.Idle => 1.0,
            Models.AgentState.Offline => 1.0,
            _ => 1.0,
        };
    }

    /// <summary>正弦呼吸。</summary>
    private static double Breathe(double t, double periodSeconds, double min, double max)
    {
        var phase = (t % periodSeconds) / periodSeconds;       // 0..1
        var s = (1 - Math.Cos(phase * 2 * Math.PI)) / 2.0;     // 0..1 平滑
        return min + (max - min) * s;
    }

    /// <summary>带柔边的方波闪烁（edge 为过渡占比）。</summary>
    private static double SoftFlash(double t, double periodSeconds, double min, double max, double edge)
    {
        var phase = (t % periodSeconds) / periodSeconds;
        double on;
        if (phase < edge)                       // 渐亮
        {
            on = SmoothStep(phase / edge);
        }
        else if (phase < 0.5)                   // 亮
        {
            on = 1.0;
        }
        else if (phase < 0.5 + edge)            // 渐暗
        {
            on = 1.0 - SmoothStep((phase - 0.5) / edge);
        }
        else                                    // 暗
        {
            on = 0.0;
        }
        return min + (max - min) * on;
    }

    /// <summary>常亮，周期性两次短暗（双闪）。周期 2.4s。</summary>
    private static double FaultDoubleFlash(double t)
    {
        const double period = 2.4;
        var phase = t % period;
        // 两次 120ms 的快速压暗，间隔 160ms
        if (InWindow(phase, 0.90, 0.12) || InWindow(phase, 1.20, 0.12))
        {
            return 0.25;
        }
        return 1.0;
    }

    /// <summary>
    /// 完成态：满环 + 持续慢闪。
    /// 周期 2 秒（亮 → 暗 → 亮），一直保持，直到下一个状态指令到来
    /// （状态切换由 StateAggregator 负责，完成态不再自动回落空闲）。
    /// </summary>
    private static double CompletedSlowBlink(double t)
    {
        return Breathe(t, periodSeconds: CompletedBlinkPeriodSeconds, min: 0.30, max: 1.0);
    }

    /// <summary>完成态慢闪周期（秒）。</summary>
    public const double CompletedBlinkPeriodSeconds = 2.0;

    private static bool InWindow(double phase, double start, double length)
    {
        return phase >= start && phase < start + length;
    }

    private static double SmoothStep(double x)
    {
        x = Math.Clamp(x, 0.0, 1.0);
        return x * x * (3 - 2 * x);
    }
}
