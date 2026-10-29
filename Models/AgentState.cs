using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace DshHaloRing.Models;

/// <summary>
/// DSH 运行状态。数值不要随意改动：枚举顺序被用作“能否进入某状态”的语义，
/// 真正的显示优先级由 <see cref="AgentStateExtensions.Priority"/> 显式给出。
/// </summary>
public enum AgentState
{
    Idle = 0,          // 空闲/待机：无活动会话，但宿主在线
    Running = 1,       // 正常运行/活动：执行工具、调用模型、推理中
    WaitingForUser = 2,// 需要关注：等待用户输入、等待授权/审批、计划待确认
    Degraded = 3,      // 降级运行：部分能力受限、Provider 异常、上下文接近压缩线
    Fault = 4,         // 故障/阻塞：请求失败、会话无法打开、关键错误
    Completed = 5,     // 完成/回合结束：短时覆盖显示（1–2 秒）后回落
    Offline = 6,       // 离线/失联：宿主无响应、心跳中断
}

public static class AgentStateExtensions
{
    /// <summary>
    /// 固定显示优先级，数值越大越优先。
    /// 故障 > 降级 > 等待用户输入/授权 > 运行中 > 完成(短时覆盖) > 空闲 > 离线。
    /// 注意：离线虽排在最后，但当它成立时通常其它状态都不成立，所以实际会显示。
    /// </summary>
    public static int Priority(this AgentState state) => state switch
    {
        AgentState.Fault => 100,
        AgentState.Degraded => 90,
        AgentState.WaitingForUser => 80,
        AgentState.Running => 70,
        AgentState.Completed => 60,
        AgentState.Idle => 20,
        AgentState.Offline => 10,
        _ => 0,
    };

    public static string ToDisplayName(this AgentState state) => state switch
    {
        AgentState.Idle => "空闲/待机",
        AgentState.Running => "运行中",
        AgentState.WaitingForUser => "等待输入/授权",
        AgentState.Degraded => "降级运行",
        AgentState.Fault => "故障/阻塞",
        AgentState.Completed => "完成/回合结束",
        AgentState.Offline => "离线/失联",
        _ => state.ToString(),
    };

    public static string ToKey(this AgentState state) => state switch
    {
        AgentState.Idle => "idle",
        AgentState.Running => "running",
        AgentState.WaitingForUser => "waiting",
        AgentState.Degraded => "degraded",
        AgentState.Fault => "fault",
        AgentState.Completed => "completed",
        AgentState.Offline => "offline",
        _ => "idle",
    };

    public static bool TryParseKey(string? key, out AgentState state)
    {
        switch ((key ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "idle": case "待机": case "空闲": state = AgentState.Idle; return true;
            case "running": case "active": case "运行中": case "运行": state = AgentState.Running; return true;
            case "waiting": case "waitingforuser": case "waiting_for_user": case "等待": state = AgentState.WaitingForUser; return true;
            case "degraded": case "降级": state = AgentState.Degraded; return true;
            case "fault": case "error": case "blocked": case "故障": case "阻塞": state = AgentState.Fault; return true;
            case "completed": case "done": case "complete": case "完成": state = AgentState.Completed; return true;
            case "offline": case "lost": case "离线": case "失联": state = AgentState.Offline; return true;
            default: state = AgentState.Idle; return false;
        }
    }
}

/// <summary>单条状态快照，可序列化为 JSON 作为状态源输入。</summary>
public sealed class StatusSnapshot
{
    [JsonPropertyName("state")]
    public AgentState State { get; set; } = AgentState.Idle;

    /// <summary>任务进度 0..1。</summary>
    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    /// <summary>可选：状态文字，托盘提示与设置窗口预览用。</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>可选：归属时间戳（UTC）。为空时以文件修改时间/接收时间代替。</summary>
    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset? TimestampUtc { get; set; }

    public StatusSnapshot Clone() => new()
    {
        State = State,
        Progress = Progress,
        Detail = Detail,
        TimestampUtc = TimestampUtc,
    };
}

/// <summary>状态配色与不透明度（0..1）。</summary>
public sealed class StateStyle
{
    /// <summary>#RRGGBB 或 #AARRGGBB。</summary>
    public string Color { get; set; } = "#10CD52";
    public double Opacity { get; set; } = 1.0;

    public Color ToMediaColor()
    {
        var fallback = System.Windows.Media.Color.FromRgb(0x10, 0xCD, 0x52);
        try
        {
            if (ColorConverter.ConvertFromString(Color) is System.Windows.Media.Color c)
            {
                return c;
            }
        }
        catch
        {
            // 忽略非法颜色串，回退默认
        }
        return fallback;
    }

    public static StateStyle FromHex(string hex, double opacity = 1.0) => new() { Color = hex, Opacity = opacity };
}

/// <summary>进度小圆点（中心指示）的样式。默认关闭：只用周围圆环做状态指示。</summary>
public sealed class DotStyle
{
    public bool Enabled { get; set; } = false;
    /// <summary>圆点半径占图标半径的比例。</summary>
    public double RadiusRatio { get; set; } = 0.30;
}

/// <summary>
/// 环截面的“管状”渐变：环不是纯色圆条，而是一根有高光的圆管。
/// 沿半径方向：内缘暗 → 中间亮 → 外缘暗。
/// </summary>
public sealed class TubeGradient
{
    public bool Enabled { get; set; } = true;

    /// <summary>最亮处的不透明度（截面中心）。</summary>
    public double PeakOpacity { get; set; } = 1.0;

    /// <summary>两侧最暗处相对于主色的提亮系数（0=保持原色但变透明，1=不提亮）。</summary>
    public double EdgeOpacity { get; set; } = 0.34;

    /// <summary>边缘提亮量 0..1：给暗侧掺一点白，形成圆管的柔和高光过渡。</summary>
    public double EdgeLighten { get; set; } = 0.10;

    /// <summary>高光偏向屏幕光源方向的比例：0=正中，+1=偏外侧，-1=偏内侧。</summary>
    public double HighlightBias { get; set; } = 0.0;
}

/// <summary>环形指示灯外观设置。默认：无轨道、圆环居中、线宽约为图标边长的 13%。</summary>
public sealed class RingStyle
{
    /// <summary>起始角度（度）。0° 指向 12 点方向，顺时针为正。默认 0°（顶部起始）。</summary>
    public double StartAngleDegrees { get; set; } = 0.0;
    public bool Clockwise { get; set; } = true;
    /// <summary>环线宽占图标边长的比例。17% 让环在托盘小尺寸下更醒目。</summary>
    public double ThicknessRatio { get; set; } = 0.17;
    /// <summary>
    /// 环半径占“可用半径”的比例。可用半径已扣掉线宽与一圈底板边距；
    /// 0.92 让环与底板边缘之间留出一条深色底板，视觉上更透气。
    /// </summary>
    public double RadiusRatio { get; set; } = 0.92;
    public bool RoundCaps { get; set; } = true;
    /// <summary>进度弧是否可见。</summary>
    public bool ShowProgress { get; set; } = true;
    /// <summary>轨道（未完成部分）颜色。灰色轨道让圆环始终成形，进度段在轨道上推进。</summary>
    public string TrackColor { get; set; } = "#3F3F3F";
    /// <summary>显示灰色轨道：未完成部分以低亮灰呈现，形成完整圆环轮廓。</summary>
    public bool ShowTrack { get; set; } = true;
    /// <summary>辉光强度 0..1。已取消辉光效果，默认 0；保留字段以兼容旧配置。</summary>
    public double GlowStrength { get; set; } = 0.0;

    /// <summary>
    /// 环截面渐变：沿半径方向从内缘到外缘的明暗变化，模拟圆管高光。
    /// Enabled=false 时环为纯色。
    /// </summary>
    public TubeGradient Tube { get; set; } = new();
    /// <summary>是否启用呼吸/闪烁动画。</summary>
    public bool AnimationsEnabled { get; set; } = true;
    /// <summary>动画目标帧率。</summary>
    public int FrameRate { get; set; } = 30;

    public RingStyle Clone()
    {
        var c = (RingStyle)MemberwiseClone();
        return c;
    }
}

/// <summary>图标底板：圆形底盘，托盘里呈现为圆形徽章。</summary>
public sealed class CanvasStyle
{
    /// <summary>不透明圆形底板（拟物徽章感）。</summary>
    public bool TransparentBackground { get; set; } = false;
    public string BackgroundColor { get; set; } = "#202020";
    /// <summary>底板圆角比例：≥0.499 画成圆形，0 = 方形。</summary>
    public double CornerRadiusRatio { get; set; } = 0.5;
}

/// <summary>视觉主题：夜间（深色底板）/ 日间（浅色底板）。</summary>
public enum ThemeMode
{
    Dark = 0,
    Light = 1,
}

/// <summary>
/// 主题预设。切换主题时只替换这几个颜色，不影响用户自定义的其他参数。
/// </summary>
public sealed class ThemePreset
{
    public string BackgroundColor { get; init; } = "#202020";
    public string TrackColor { get; init; } = "#3F3F3F";
    public string DisplayName { get; init; } = "";

    public static readonly ThemePreset Dark = new()
    {
        BackgroundColor = "#202020",
        TrackColor = "#3F3F3F",
        DisplayName = "夜间（深色底板）",
    };

    public static readonly ThemePreset Light = new()
    {
        BackgroundColor = "#F2F2F2",
        TrackColor = "#C8C8C8",
        DisplayName = "日间（浅色底板）",
    };

    public static ThemePreset For(ThemeMode mode) => mode == ThemeMode.Light ? Light : Dark;

    public static ThemeMode ParseKey(string? key) =>
        string.Equals(key, "light", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Light : ThemeMode.Dark;

    public static string ToKey(ThemeMode mode) => mode == ThemeMode.Light ? "light" : "dark";

    /// <summary>把该主题的底板色与轨道色写入配置。</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.Canvas.BackgroundColor = BackgroundColor;
        settings.Ring.TrackColor = TrackColor;
    }

    /// <summary>根据当前配置的底板色反推主题（用于首次加载旧配置时推断）。</summary>
    public static ThemeMode DetectFrom(AppSettings settings)
    {
        var c = RingIconRendererColors.Parse(settings.Canvas.BackgroundColor);
        double luma = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return luma > 160 ? ThemeMode.Light : ThemeMode.Dark;
    }
}

/// <summary>轻量颜色解析工具，避免 Models 层依赖 WPF 渲染细节。</summary>
internal static class RingIconRendererColors
{
    public static (byte R, byte G, byte B) Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return (0x20, 0x20, 0x20);
        }
        try
        {
            if (System.Windows.Media.ColorConverter.ConvertFromString(hex) is System.Windows.Media.Color c)
            {
                return (c.R, c.G, c.B);
            }
        }
        catch (FormatException)
        {
            // 非法输入回退深色
        }
        return (0x20, 0x20, 0x20);
    }
}

/// <summary>应用全部配置，持久化到 %APPDATA%\DshHaloRing\settings.json。</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public RingStyle Ring { get; set; } = new();
    public CanvasStyle Canvas { get; set; } = new();
    public DotStyle Dot { get; set; } = new();

    /// <summary>视觉主题：dark（夜间深底）/ light（日间浅底）。</summary>
    public string Theme { get; set; } = "dark";

    public Dictionary<string, StateStyle> StateStyles { get; set; } = CreateDefaults();

    /// <summary>心跳超时（秒）。超时无更新则判为离线。</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 8;

    /// <summary>“完成”状态的快闪持续时长（毫秒），之后回落为空闲。</summary>
    public int CompletedFlashMilliseconds { get; set; } = 1500;

    /// <summary>状态源类型：demo / json。留接口，后续可加 http、pipe。</summary>
    public string StatusSource { get; set; } = "demo";

    /// <summary>JSON 状态源的文件路径。</summary>
    public string? JsonStatusPath { get; set; }

    /// <summary>状态源轮询间隔（毫秒）。</summary>
    public int PollIntervalMilliseconds { get; set; } = 500;

    public bool StartWithWindows { get; set; }
    public bool ShowProgressTooltip { get; set; } = true;

    /// <summary>托盘点击行为：settings / preview / toggleProgress。</summary>
    public string TrayClickAction { get; set; } = "settings";

    public static Dictionary<string, StateStyle> CreateDefaults() => new()
    {
        [AgentState.Running.ToKey()] = StateStyle.FromHex("#10CD52"),
        [AgentState.WaitingForUser.ToKey()] = StateStyle.FromHex("#F5A623"),
        [AgentState.Degraded.ToKey()] = StateStyle.FromHex("#FF7A1A"),
        [AgentState.Fault.ToKey()] = StateStyle.FromHex("#FF3B30"),
        [AgentState.Completed.ToKey()] = StateStyle.FromHex("#10CD52"),
        [AgentState.Idle.ToKey()] = StateStyle.FromHex("#2F6B45"),
        [AgentState.Offline.ToKey()] = StateStyle.FromHex("#5A5A5A"),
    };

    public StateStyle StyleFor(AgentState state)
    {
        var key = state.ToKey();
        if (StateStyles.TryGetValue(key, out var s))
        {
            return s;
        }
        var defaults = CreateDefaults();
        var created = defaults.TryGetValue(key, out var d) ? d : StateStyle.FromHex("#10CD52");
        StateStyles[key] = created;
        return created;
    }

    /// <summary>便捷访问：当前主题枚举。</summary>
    public ThemeMode ThemeMode
    {
        get => ThemePreset.ParseKey(Theme);
        set => Theme = ThemePreset.ToKey(value);
    }

    /// <summary>
    /// 切换主题：写入底板色与轨道色，其余参数保持不变。
    /// 调用方负责触发重绘。
    /// </summary>
    public void ApplyTheme(ThemeMode mode)
    {
        ThemeMode = mode;
        ThemePreset.For(mode).ApplyTo(this);
    }

    /// <summary>补齐缺失的状态配色（升级/手改配置后容错）。</summary>
    public void EnsureDefaults()
    {
        Ring ??= new RingStyle();
        Canvas ??= new CanvasStyle();
        Dot ??= new DotStyle();
        StateStyles ??= CreateDefaults();
        foreach (var kv in CreateDefaults())
        {
            StateStyles.TryAdd(kv.Key, kv.Value);
        }
        if (HeartbeatTimeoutSeconds <= 0) HeartbeatTimeoutSeconds = 8;
        if (Ring.FrameRate is < 5 or > 60) Ring.FrameRate = 30;
        if (PollIntervalMilliseconds < 100) PollIntervalMilliseconds = 500;

        // 旧配置没有 Theme 字段：从底板色反推，避免升级后主题下拉显示错位。
        // 反推出来后一并把该主题的底板色/轨道色补齐，保证与主题预设一致。
        if (string.IsNullOrWhiteSpace(Theme))
        {
            var detected = ThemePreset.DetectFrom(this);
            Theme = ThemePreset.ToKey(detected);
            ThemePreset.For(detected).ApplyTo(this);
        }
    }
}
