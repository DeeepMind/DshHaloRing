using System;
using DshHaloRing.Models;
using System.Collections.Generic;
namespace DshHaloRing.Services;

/// <summary>
/// 状态合成器。
///
/// 需求：多个状态可能同时成立，但灯只能显示一个 → 采用固定优先级，而不是“最后变化的状态优先”。
///   故障 > 降级 > 等待用户输入/授权 > 运行中 > 完成(短时覆盖) > 空闲 > 离线
/// 优先级表中“完成”高于“空闲”，同时“完成”只保留一段短时间后自动清除。
/// </summary>
public sealed class StateAggregator
{
    private readonly Dictionary<AgentState, double> _active = new();

    public AgentState Current { get; private set; } = AgentState.Idle;

    public event EventHandler<AgentState>? CurrentChanged;

    /// <summary>置为成立/不成立某状态。</summary>
    public void Set(AgentState state, bool active)
    {
        if (active)
        {
            if (state == AgentState.Completed)
            {
                // 完成态的短时覆盖由 TryEnterShortFlash 负责计时，这里只登记
                _active[AgentState.Completed] = _active.TryGetValue(AgentState.Completed, out var v) ? v : 0;
            }
            else
            {
                _active[state] = 0;
            }
        }
        else
        {
            _active.Remove(state);
        }
        Recompute();
    }

    /// <summary>
    /// 进入“完成”状态：满环 + 持续慢闪。
    /// 不再设定超时——完成态会一直保持，直到状态源推送下一个状态。
    /// </summary>
    public void EnterCompleted()
    {
        _active[AgentState.Completed] = 0;
        Recompute();
    }

    /// <summary>兼容旧调用：参数已忽略，完成态不再按时长自动清除。</summary>
    public void FlashCompleted(int durationMilliseconds)
    {
        _ = durationMilliseconds;
        EnterCompleted();
    }

    /// <summary>整体重置（切换状态源时用）。</summary>
    public void Reset()
    {
        _active.Clear();
        Recompute();
    }

    /// <summary>清除“完成”而不清除其它状态。</summary>
    public void ClearCompleted()
    {
        _active.Remove(AgentState.Completed);
        Recompute();
    }

    /// <summary>
    /// 完成态已改为“持续显示直到新状态”，因此每周期的 Tick 不再做超时清除。
    /// 保留方法以兼容渲染循环的调用。
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        _ = now;
    }

    private void Recompute()
    {
        // 关键：没有任何状态成立 ≠ 离线。
        // “离线”必须由状态源显式声明（心跳中断时才置为 Offline），
        // 否则启动瞬间的空白窗口会被误判成离线。
        if (_active.Count == 0)
        {
            return;
        }

        AgentState best = AgentState.Offline;
        int bestPriority = int.MinValue;

        foreach (var state in _active.Keys)
        {
            var p = state.Priority();
            if (p > bestPriority)
            {
                bestPriority = p;
                best = state;
            }
        }

        if (best != Current)
        {
            Current = best;
            CurrentChanged?.Invoke(this, best);
        }
    }
}
