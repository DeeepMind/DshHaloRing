using System;
using DshHaloRing.Models;
using System.Collections.Generic;
using System.Windows.Threading;
namespace DshHaloRing.Services;

/// <summary>
/// 渲染调度：以配置的帧率（默认 30fps）推进动画、驱动 StateAggregator 的计时，
/// 并在图标内容真正变化时通知托盘更新。
/// </summary>
public sealed class IconAnimationLoop : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly StateAggregator _aggregator;
    private readonly AppSettings _settings;
    private readonly StopwatchLite _clock = new();

    private double _progress;          // 当前实际渲染的进度（平滑后）
    private double _targetProgress;    // 目标进度（状态源给出的值）
    private double _lastIntensity = double.NaN;
    private double _lastProgress = double.NaN;
    private AgentState _lastState = (AgentState)(-1);
    private DateTimeOffset _stateChangedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// 进度推进速度：每秒移动多少个“满环比例”。
    /// 0.35 表示从 0 到 100% 约需 2.9 秒，肉眼能看到弧长连续增长。
    /// </summary>
    private const double ProgressSpeedPerSecond = 0.35;

    /// <summary>
    /// 重绘阈值：进度变化小于此值时不重绘。
    /// 设得足够小（0.002），保证平滑推进时每一帧都能看到变化。
    /// </summary>
    private const double ProgressRenderThreshold = 0.002;

    public IconAnimationLoop(StateAggregator aggregator, AppSettings settings)
    {
        _aggregator = aggregator;
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(5, settings.Ring.FrameRate)),
        };
        _timer.Tick += OnTick;
    }

    /// <summary>帧变化回调：state 当前生效状态，progress 进度，intensity 动画强度。</summary>
    public event EventHandler<FrameInfo>? FrameChanged;

    public AgentState CurrentState => _aggregator.Current;

    public void Start()
    {
        _clock.Restart();
        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(5, _settings.Ring.FrameRate));
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>
    /// 设置目标进度。渲染时用线性插值逐步逼近，避免环弧长“跳跃式”变化。
    /// </summary>
    public void SetProgress(double progress)
    {
        _targetProgress = Math.Clamp(progress, 0, 1);
    }

    /// <summary>立即跳到目标进度（状态切换时用，不做平滑）。</summary>
    public void SnapProgress(double progress)
    {
        _targetProgress = Math.Clamp(progress, 0, 1);
        _progress = _targetProgress;
    }

    public double Progress => _progress;

    public void RestartAnimation()
    {
        _clock.Restart();
        _stateChangedAt = DateTimeOffset.UtcNow;
        _lastIntensity = double.NaN;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        _aggregator.Tick(now);

        var state = _aggregator.Current;
        if (state != _lastState)
        {
            _lastState = state;
            _stateChangedAt = now;
            _lastIntensity = double.NaN;
        }

        double elapsed = (now - _stateChangedAt).TotalSeconds;
        double intensity = AnimationEngine.Intensity(state, elapsed, _settings.Ring.AnimationsEnabled);

        // 进度线性逼近目标：每帧走固定步长，产生连续的弧长增长而不是跳变。
        // 步长按“每秒推进多少进度”换算到帧间隔，与帧率解耦。
        double dt = _timer.Interval.TotalSeconds;
        double maxStep = ProgressSpeedPerSecond * dt;
        double gap = _targetProgress - _progress;
        if (Math.Abs(gap) <= maxStep)
        {
            _progress = _targetProgress;   // 足够近，直接落位
        }
        else
        {
            _progress += Math.Sign(gap) * maxStep;
        }
        _progress = Math.Clamp(_progress, 0, 1);

        double intensityDelta = double.IsNaN(_lastIntensity) ? 1 : Math.Abs(intensity - _lastIntensity);
        double progressDelta = double.IsNaN(_lastProgress) ? 1 : Math.Abs(_progress - _lastProgress);

        // 收敛判定：变化足够小就不重绘，避免无谓的 GDI 句柄开销
        if (state == _lastState && progressDelta < ProgressRenderThreshold && intensityDelta < 0.01)
        {
            return;
        }

        _lastIntensity = intensity;
        _lastProgress = _progress;

        FrameChanged?.Invoke(this, new FrameInfo(state, _progress, intensity, _clock.Elapsed));
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    public readonly record struct FrameInfo(AgentState State, double Progress, double Intensity, TimeSpan Elapsed);

    /// <summary>不依赖 System.Diagnostics.Stopwatch 的轻量计时，避免高频重置开销。</summary>
    private sealed class StopwatchLite
    {
        private DateTime _start = DateTime.UtcNow;
        public void Restart() => _start = DateTime.UtcNow;
        public TimeSpan Elapsed => DateTime.UtcNow - _start;
    }
}
