using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using DshHaloRing.Models;

namespace DshHaloRing.Services;

/// <summary>状态源统一接口。后续接真实 DSH 时，只需另写一个实现（HTTP / 命名管道 / 事件订阅）。</summary>
public interface IStatusSource : IDisposable
{
    string Name { get; }

    /// <summary>每当得到新快照时触发。</summary>
    event EventHandler<StatusSnapshot>? SnapshotReceived;

    /// <summary>心跳丢失时触发（宿主无响应）。</summary>
    event EventHandler? HeartbeatLost;

    void Start();
    void Stop();
}

/// <summary>
/// 演示驱动：手动设置状态与进度，另提供“自动演示”模式循环播放各状态。
/// 这是当前小版本默认的数据来源。
/// </summary>
public sealed class DemoStatusSource : IStatusSource
{
    private readonly System.Timers.Timer _timer;
    private readonly Random _random = new();
    private bool _autoPlaying;
    private DateTimeOffset _autoStepAt;
    private int _autoIndex;
    private double _autoProgress;

    private static readonly (AgentState state, string detail)[] AutoCycle =
    {
        (AgentState.Running, "演示：正在调用模型"),
        (AgentState.Running, "演示：执行工具中"),
        (AgentState.WaitingForUser, "演示：等待授权确认"),
        (AgentState.Degraded, "演示：Provider 响应变慢"),
        (AgentState.Running, "演示：推理中"),
        (AgentState.Completed, "演示：回合完成"),
        (AgentState.Idle, "演示：待机"),
        (AgentState.Fault, "演示：请求失败"),
        (AgentState.Offline, "演示：心跳中断"),
    };

    public string Name => "demo";

    public event EventHandler<StatusSnapshot>? SnapshotReceived;
    public event EventHandler? HeartbeatLost;

    public DemoStatusSource()
    {
        _timer = new System.Timers.Timer(250) { AutoReset = true };
        _timer.Elapsed += (_, _) => Tick();
    }

    public bool IsAutoPlaying => _autoPlaying;

    /// <summary>最近一次推送的进度，供菜单切换状态时沿用。</summary>
    public double Progress { get; private set; }

    public void Start()
    {
        _timer.Start();
        Publish(AgentState.Idle, 0, "手动演示驱动");
    }

    public void Stop() => _timer.Stop();

    /// <summary>手动推送一次状态（菜单/设置窗口调用）。</summary>
    public void Push(AgentState state, double progress, string? detail = null)
    {
        _autoPlaying = false;
        Publish(state, progress, detail ?? "手动设置");
    }

    /// <summary>演示“心跳中断”：清空所有状态并触发心跳丢失事件。</summary>
    public void SimulateHeartbeatLost()
    {
        _autoPlaying = false;
        HeartbeatLost?.Invoke(this, EventArgs.Empty);
    }

    public void SetAutoPlaying(bool value)
    {
        _autoPlaying = value;
        _autoIndex = 0;
        _autoProgress = 0;
        _autoStepAt = DateTimeOffset.UtcNow;
    }

    private void Tick()
    {
        if (!_autoPlaying)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _autoProgress += 0.06;
        if (_autoProgress > 1.0)
        {
            _autoProgress = 0;
            _autoIndex = (_autoIndex + 1) % AutoCycle.Length;
            _autoStepAt = now;
        }

        var (state, detail) = AutoCycle[_autoIndex];
        Publish(state, _autoProgress, detail);
    }

    private void Publish(AgentState state, double progress, string? detail)
    {
        Progress = Math.Clamp(progress, 0, 1);
        SnapshotReceived?.Invoke(this, new StatusSnapshot
        {
            State = state,
            Progress = Progress,
            Detail = detail,
            TimestampUtc = DateTimeOffset.UtcNow,
        });
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}

/// <summary>
/// JSON 文件状态源：轮询读取一个 JSON 文件（如 <c>%LOCALAPPDATA%\DSH\status.json</c>）。
/// 约定格式同 <see cref="StatusSnapshot"/>：{"state":"running","progress":0.42,"detail":"..."}
/// 文件在超时时间内没有变化即视为心跳中断。
/// </summary>
public sealed class JsonFileStatusSource : IStatusSource
{
    private readonly string _path;
    private readonly int _pollMs;
    private readonly System.Timers.Timer _timer;
    private DateTimeOffset _lastUpdate = DateTimeOffset.MinValue;
    private DateTime _lastWriteTime = DateTime.MinValue;
    private bool _reportedLost;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonFileStatusSource(string path, int pollMilliseconds)
    {
        _path = path;
        _pollMs = Math.Max(100, pollMilliseconds);
        _timer = new System.Timers.Timer(_pollMs) { AutoReset = true };
        _timer.Elapsed += (_, _) => Tick();
    }

    public string Name => "json";

    public event EventHandler<StatusSnapshot>? SnapshotReceived;
    public event EventHandler? HeartbeatLost;

    public int HeartbeatTimeoutSeconds { get; set; } = 8;

    public void Start()
    {
        _startedAt = DateTimeOffset.UtcNow;
        _timer.Start();
        Tick();
    }

    public void Stop() => _timer.Stop();

    private void Tick()
    {
        try
        {
            if (!File.Exists(_path))
            {
                ReportLostIfNeeded();
                return;
            }

            var writeTime = File.GetLastWriteTimeUtc(_path);
            if (writeTime != _lastWriteTime)
            {
                _lastWriteTime = writeTime;
                var json = ReadAllTextWithRetry(_path);
                var snapshot = JsonSerializer.Deserialize<StatusSnapshot>(json, Options);
                if (snapshot != null)
                {
                    _lastUpdate = snapshot.TimestampUtc ?? DateTimeOffset.UtcNow;
                    _reportedLost = false;
                    SnapshotReceived?.Invoke(this, snapshot);
                    return;
                }
            }

            var reference = _lastUpdate > _startedAt ? _lastUpdate : _startedAt;
            if (DateTimeOffset.UtcNow - reference > TimeSpan.FromSeconds(HeartbeatTimeoutSeconds))
            {
                ReportLostIfNeeded();
            }
        }
        catch (IOException)
        {
            // 文件被占用：跳过本次轮询
        }
        catch (JsonException)
        {
            // 内容非法：跳过，等待下次写入
        }
    }

    private void ReportLostIfNeeded()
    {
        if (_reportedLost)
        {
            return;
        }
        _reportedLost = true;
        HeartbeatLost?.Invoke(this, EventArgs.Empty);
    }

    private static string ReadAllTextWithRetry(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(30);
            }
        }
        return string.Empty;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}

/// <summary>
/// DSH 状态桥数据源：读取 %USERPROFILE%\.dsh\dsh-status.txt。
///
/// 该文件由 DSH 宿主插件 dsh-trafficmonitor-status 每 250ms~1s 写入，
/// 格式为 key=value 的行文本（不是 JSON）：
///   version=1
///   state=running
///   progress=44
///   updated=1792765694881
///   detail=Agent 运行中（turn 12）
///
/// 与 WebSocket 方案相比，这条路径不需要 launchToken、不需要 cookie，
/// 也不会因为 DSH 重启而失效，是目前最稳的接法。
/// </summary>
public sealed class DshStatusFileSource : IStatusSource
{
    private readonly string _path;
    private readonly System.Timers.Timer _timer;
    private DateTimeOffset _lastUpdate = DateTimeOffset.MinValue;
    private DateTime _lastWriteTime = DateTime.MinValue;
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private bool _reportedLost;

    public DshStatusFileSource(string path, int pollMilliseconds)
    {
        _path = path;
        _timer = new System.Timers.Timer(Math.Max(100, pollMilliseconds)) { AutoReset = true };
        _timer.Elapsed += (_, _) => Tick();
    }

    public string Name => "dshfile";

    public event EventHandler<StatusSnapshot>? SnapshotReceived;
    public event EventHandler? HeartbeatLost;

    public int HeartbeatTimeoutSeconds { get; set; } = 8;

    public void Start()
    {
        _startedAt = DateTimeOffset.UtcNow;
        _timer.Start();
        Tick();
    }

    public void Stop() => _timer.Stop();

    private void Tick()
    {
        try
        {
            if (!File.Exists(_path))
            {
                ReportLostIfNeeded();
                return;
            }

            var writeTime = File.GetLastWriteTimeUtc(_path);
            if (writeTime == _lastWriteTime)
            {
                CheckHeartbeat();
                return;
            }
            _lastWriteTime = writeTime;

            var text = ReadAllTextWithRetry(_path);
            var fields = ParseKeyValue(text);
            if (fields.Count == 0)
            {
                return;
            }

            var snapshot = ToSnapshot(fields);
            _lastUpdate = snapshot.TimestampUtc ?? DateTimeOffset.UtcNow;
            _reportedLost = false;
            SnapshotReceived?.Invoke(this, snapshot);
        }
        catch (IOException)
        {
            // 文件正被写入：下一轮再读
        }
    }

    private void CheckHeartbeat()
    {
        var reference = _lastUpdate > _startedAt ? _lastUpdate : _startedAt;
        if (DateTimeOffset.UtcNow - reference > TimeSpan.FromSeconds(HeartbeatTimeoutSeconds))
        {
            ReportLostIfNeeded();
        }
    }

    private void ReportLostIfNeeded()
    {
        if (_reportedLost)
        {
            return;
        }
        _reportedLost = true;
        HeartbeatLost?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把 key=value 行文本解析成快照。未知键忽略。</summary>
    public static StatusSnapshot ToSnapshot(IReadOnlyDictionary<string, string> fields)
    {
        var snapshot = new StatusSnapshot();

        if (fields.TryGetValue("state", out var stateText))
        {
            snapshot.State = stateText.Trim().ToLowerInvariant() switch
            {
                "error" or "fault" or "blocked" => AgentState.Fault,
                "degraded" => AgentState.Degraded,
                "attention" => AgentState.WaitingForUser,
                "running" => AgentState.Running,
                "completed" => AgentState.Completed,
                "idle" => AgentState.Idle,
                "offline" => AgentState.Offline,
                _ => AgentState.Idle,
            };
        }

        if (fields.TryGetValue("progress", out var progressText) &&
            double.TryParse(progressText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var percent))
        {
            snapshot.Progress = Math.Clamp(percent / 100.0, 0.0, 1.0);
        }

        if (fields.TryGetValue("detail", out var detail))
        {
            snapshot.Detail = detail;
        }

        if (fields.TryGetValue("updated", out var updatedText) &&
            long.TryParse(updatedText, out var unixMs) && unixMs > 0)
        {
            try
            {
                snapshot.TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            }
            catch (ArgumentOutOfRangeException)
            {
                // 时间戳越界：忽略，用接收时间代替
            }
        }

        return snapshot;
    }

    /// <summary>解析 key=value 文本，跳过空行与无等号的行。</summary>
    public static Dictionary<string, string> ParseKeyValue(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0)
            {
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..];
            if (key.Length > 0)
            {
                result[key] = value;
            }
        }
        return result;
    }

    private static string ReadAllTextWithRetry(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(30);
            }
        }
        return string.Empty;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}