using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DshHaloRing.Models;
using DshHaloRing.Services;
using DshHaloRing.Tray;
using DshHaloRing.Views;

namespace DshHaloRing;

/// <summary>
/// 应用入口：单实例守卫 → 加载配置 → 建托盘 → 启动渲染循环 → （按需）打开设置窗口。
/// 无主窗口常驻；ShutdownMode = OnExplicitShutdown，只有“退出”菜单会结束进程。
/// </summary>
public partial class App : Application
{
    /// <summary>启动探针：在 App 构造函数（WPF 最早可埋点的位置）写一行，确认进程真的进到了 WPF。</summary>
    public App()
    {
        DshHaloRing.Verify.StartupTrace.Mark("App 构造函数执行");
    }

    private const string MutexName = @"Local\DshHaloRing.SingleInstance.v1";

    private Mutex? _singleInstanceMutex;
    private AppSettings _settings = new();
    private StateAggregator? _aggregator;
    private IconAnimationLoop? _loop;
    private TrayController? _tray;
    private IStatusSource? _source;
    private DemoStatusSource? _demo;
    private SettingsWindow? _settingsWindow;

    /// <summary>托盘通道是否可用（启动时探测，仅用于日志与提示）。</summary>
    public static bool TrayProbeSucceeded { get; private set; } = true;

    /// <summary>托盘不可用时的原因描述。</summary>
    public static string? TrayProbeError { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Verify.StartupTrace.Mark($"OnStartup 进入，args=[{string.Join(",", e.Args)}]");

        // ① 先装异常兜底，之后任何分支出错都能留痕
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // ② 自检/诊断分支必须排在单实例判断之前，
        //    否则残留实例会让 --verify 等命令被静默挡掉（曾导致自检“假通过/假失败”）。
        if (e.Args.Length > 0 && e.Args[0].Equals("--verify", StringComparison.OrdinalIgnoreCase))
        {
            string outDir = e.Args.Length > 1
                ? e.Args[1]
                : System.IO.Path.Combine(AppContext.BaseDirectory, "verify-out");
            int exitCode = Verify.Verifier.Run(outDir);
            Environment.Exit(exitCode);
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--diag", StringComparison.OrdinalIgnoreCase))
        {
            Verify.Diagnostics.Run();
            Environment.Exit(0);
            return;
        }

        // 动画实测：验证「运行中」确为呼吸效果
        if (e.Args.Length > 0 && e.Args[0].Equals("--animcheck", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(Verify.AnimationCheck.Run());
            return;
        }

        // 生成图标样图供人工确认外观，不创建托盘
        if (e.Args.Length > 0 && e.Args[0].Equals("--sample", StringComparison.OrdinalIgnoreCase))
        {
            string dir = e.Args.Length > 1
                ? e.Args[1]
                : System.IO.Path.Combine(AppContext.BaseDirectory, "sample-out");
            int code = Verify.IconSampler.Run(dir);
            Environment.Exit(code);
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--tray-selftest", StringComparison.OrdinalIgnoreCase))
        {
            int secs = 3;
            if (e.Args.Length > 1 && int.TryParse(e.Args[1], out var parsed))
            {
                secs = Math.Clamp(parsed, 1, 60);
            }
            int code = Verify.TraySelfTest.Run(secs);
            Environment.Exit(code);
            return;
        }

        // ③ 单实例守卫：绝不 take ownership（initiallyOwned: false）。
        // Mutex 线程亲和，谁持有就必须同线程 ReleaseMutex()，
        // 而 OnExit 经 DispatcherOperation 回调执行，线程身份不保证一致，
        // 跨线程释放会抛 "Object synchronization method was called from an unsynchronized block"。
        // 只把命名 Mutex 当作“存在性标记”，进程结束时由操作系统回收。
        bool isFirstInstance = true;
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
            isFirstInstance = createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            // 无权创建命名对象时按“首实例”继续，不影响主功能
            _singleInstanceMutex = null;
        }

        if (!isFirstInstance)
        {
            // 已有实例：不再静默消失，而是提示一次后正常退出。
            Verify.StartupTrace.Mark("检测到已有实例，本次启动直接退出");
            ShowAlreadyRunningNotice();
            return;
        }

        _settings = SettingsStore.Load();
        Verify.StartupTrace.Mark($"配置已加载，存储目录 = {SettingsStore.DirectoryPath}");

        // 先探测托盘通道是否可用，把结果写进日志（双击无图标时靠它定位）
        var probe = TrayDiagnostics.Probe();
        Verify.StartupTrace.Mark("托盘通道探测：\n" + TrayDiagnostics.Describe(probe));
        TrayProbeSucceeded = probe.Success;
        TrayProbeError = probe.Success ? null : probe.ErrorText;

        _aggregator = new StateAggregator();
        _demo = new DemoStatusSource();

        _tray = new TrayController(_settings, _aggregator);
        _tray.SettingsRequested += (_, _) => ShowSettingsWindow();
        _tray.ResetAppearanceRequested += (_, _) => ResetAppearance();
        _tray.ExitRequested += (_, _) => Shutdown();

        WireStatusSource(CreateStatusSource(_settings.StatusSource));

        _loop = new IconAnimationLoop(_aggregator, _settings);
        _loop.FrameChanged += (_, frame) =>
            _tray?.UpdateIcon(frame.State, frame.Progress, frame.Intensity);
        _loop.Start();

        _demo.Start();
        Verify.StartupTrace.Mark("渲染循环已启动");

        bool startHidden = true;
        foreach (var arg in e.Args)
        {
            if (arg.Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                startHidden = false;
            }
        }

        if (!startHidden)
        {
            ShowSettingsWindow();
        }
        else if (TrayProbeSucceeded)
        {
            _tray.ShowBalloonMessage("DSH HaloRing 已启动", "托盘环已就绪。右键图标可切换状态、调整进度或打开外观设置。");
        }
        else
        {
            // 托盘探测失败时不弹悬浮窗，只留日志与提示，避免出现多余的窗口
            Verify.StartupTrace.Mark("托盘探测失败，未启用任何兜底窗口");
        }

        Verify.StartupTrace.Mark("启动完成");
    }

    /// <summary>按配置构造状态源。demo 是内置演示，json/dshfile 读本地文件。</summary>
    private IStatusSource? CreateStatusSource(string kind)
    {
        switch ((kind ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "json":
                return BuildJsonSource();
            case "dshfile":
                return BuildDshFileSource();
            default:
                return _demo;
        }
    }

    /// <summary>
    /// DSH 状态桥数据源：读 %USERPROFILE%\.dsh\dsh-status.txt。
    /// 这是接入真实 DSH 状态的推荐方式（无需 token / WebSocket）。
    /// </summary>
    private DshStatusFileSource BuildDshFileSource()
    {
        var path = _settings.JsonStatusPath;
        // dshfile 模式下若路径为空，默认指向状态桥文件
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith("dsh-status.txt", StringComparison.OrdinalIgnoreCase))
        {
            path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "dsh-status.txt");
            _settings.JsonStatusPath = path;
        }
        return new DshStatusFileSource(path, _settings.PollIntervalMilliseconds)
        {
            HeartbeatTimeoutSeconds = _settings.HeartbeatTimeoutSeconds,
        };
    }

    private JsonFileStatusSource? BuildJsonSource()
    {
        var path = _settings.JsonStatusPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = System.IO.Path.Combine(SettingsStore.DirectoryPath, "status.json");
            _settings.JsonStatusPath = path;
        }
        return new JsonFileStatusSource(path, _settings.PollIntervalMilliseconds)
        {
            HeartbeatTimeoutSeconds = _settings.HeartbeatTimeoutSeconds,
        };
    }

    private void WireStatusSource(IStatusSource? source)
    {
        if (_source != null)
        {
            _source.SnapshotReceived -= OnSnapshot;
            _source.HeartbeatLost -= OnHeartbeatLost;
            _source.Stop();
            if (!ReferenceEquals(_source, _demo))
            {
                _source.Dispose();
            }
        }

        _source = source;
        if (_source == null)
        {
            return;
        }

        _source.SnapshotReceived += OnSnapshot;
        _source.HeartbeatLost += OnHeartbeatLost;
        _source.Start();
    }

    private void OnSnapshot(object? sender, StatusSnapshot snapshot)
    {
        if (_aggregator == null || _loop == null)
        {
            return;
        }

        // 完成态：进度强制满环 100%，并保持慢闪直到下一个状态指令
        if (snapshot.State == AgentState.Completed)
        {
            _loop.SetProgress(1.0);
            _loop.RestartAnimation();
            _aggregator.EnterCompleted();
            Verify.StartupTrace.Mark("进入完成态：满环 + 慢闪（保持到下一个状态）");
            return;
        }

        // 空闲态：进度强制满环 100%，用于与「离线」（空环）区分。
        // 状态源此时给的 progress 通常是 0，直接透传会让空闲和离线长得一样。
        _loop.SetProgress(snapshot.State == AgentState.Idle ? 1.0 : snapshot.Progress);

        // 关键：只让“快照里声明的那个状态”成立，其余全部撤销，
        // 再由优先级表合成最终显示状态（这样降级+运行同时成立时，降级胜出）。
        foreach (AgentState state in Enum.GetValues<AgentState>())
        {
            if (state == AgentState.Completed)
            {
                _aggregator.ClearCompleted();
                continue;
            }
            _aggregator.Set(state, state == snapshot.State);
        }
    }

    private void OnHeartbeatLost(object? sender, EventArgs e)
    {
        if (_aggregator == null)
        {
            return;
        }

        foreach (AgentState state in Enum.GetValues<AgentState>())
        {
            _aggregator.Set(state, state == AgentState.Offline);
        }
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }
            return;
        }

        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.SettingsApplied += (_, updated) => ApplySettings(updated);
        // 主题切换：立即刷新托盘图标，不必先点保存
        _settingsWindow.ThemeChangedLive += (_, preview) => PreviewThemeOnTray(preview);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// 主题实时预览：把传入的配置（含新主题色）渲染到托盘图标上，
    /// 但不写入磁盘、不重建渲染循环，用户取消则恢复到原设置。
    /// </summary>
    private void PreviewThemeOnTray(AppSettings preview)
    {
        if (_loop == null || _tray == null || _aggregator == null)
        {
            return;
        }

        preview.EnsureDefaults();
        var state = _aggregator.Current;
        double progress = _loop.Progress;
        _tray.UpdateIconWithSettings(preview, state, progress, 1.0);
        Verify.StartupTrace.Mark($"主题预览已应用到托盘：{preview.Theme}（底板 {preview.Canvas.BackgroundColor}）");
    }

    private void ApplySettings(AppSettings updated)
    {
        updated.EnsureDefaults();
        _settings = updated;

        // 托盘持有自己的配置引用，必须同步替换，否则保存后外观不会变化
        _tray?.UpdateSettings(_settings);

        // 保存失败必须让用户知道，而不是“看起来保存了”
        bool saved = SettingsStore.Save(_settings);
        if (!saved)
        {
            Verify.StartupTrace.Mark($"配置保存失败：{SettingsStore.LastError}");
            _tray?.ShowBalloonMessage("外观设置未能保存到磁盘",
                $"已回退到：{SettingsStore.DirectoryPath}。原因：{SettingsStore.LastError}");
        }

        // 重建渲染循环：先记下当前进度，避免保存设置后进度被清零
        double currentProgress = _loop?.Progress ?? 0;
        _loop?.Stop();
        _loop = null;

        var aggregator = _aggregator ?? new StateAggregator();
        _aggregator = aggregator;
        _loop = new IconAnimationLoop(aggregator, _settings);
        _loop.FrameChanged += (_, frame) => _tray?.UpdateIcon(frame.State, frame.Progress, frame.Intensity);
        // 重建循环：立即落到当前进度，避免从 0 重新爬升
        _loop.SnapProgress(currentProgress);
        _loop.Start();
        // 立刻用新配置重绘一帧，避免等下一个动画节拍才更新
        _tray?.UpdateIconWithSettings(_settings, aggregator.Current, currentProgress, 1.0);

        string desired = _settings.StatusSource == "json" ? "json"
            : _settings.StatusSource == "dshfile" ? "dshfile" : "demo";
        bool needRewire = desired != (_source?.Name ?? string.Empty);
        if (needRewire)
        {
            WireStatusSource(CreateStatusSource(desired));
        }

        _tray?.ShowBalloonMessage("设置已保存", "托盘环外观已更新。");
    }

    private void ResetAppearance()
    {
        var defaults = new AppSettings();
        defaults.EnsureDefaults();
        defaults.StatusSource = _settings.StatusSource;
        defaults.JsonStatusPath = _settings.JsonStatusPath;
        defaults.HeartbeatTimeoutSeconds = _settings.HeartbeatTimeoutSeconds;
        ApplySettings(defaults);
    }

    /// <summary>
    /// 已有实例在运行时的提示。
    /// 不用 MessageBox：托盘程序的第二实例被双击时弹模态框反而更烦人；
    /// 这里只写日志 + 结束本次进程。想看到提示的调试场景可查看 startup-trace.log。
    /// </summary>
    private void ShowAlreadyRunningNotice()
    {
        try
        {
            var info = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                Visible = true,
                BalloonTipTitle = "DSH HaloRing 已在运行",
                BalloonTipText = "程序已驻留在系统托盘中，请查看通知区域。",
                Text = "DSH HaloRing",
            };
            info.ShowBalloonTip(3000);
            // 给 Shell 一点时间展示气泡，再清理退出
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1600),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    info.Visible = false;
                    info.Dispose();
                }
                catch
                {
                    // 忽略
                }
                Shutdown(0);
            };
            timer.Start();
        }
        catch
        {
            Shutdown(0);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 托盘程序不应因单帧渲染异常而整体崩溃
        e.Handled = true;
        CrashLog.Write("DispatcherUnhandledException", e.Exception);
        try
        {
            _tray?.ShowBalloonMessage("DSH HaloRing 异常", e.Exception.Message);
        }
        catch
        {
            // 忽略二次异常
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            CrashLog.Write("AppDomain.UnhandledException", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出路径必须绝对安静：任何清理动作失败都不能再抛异常，
        // 否则进程会以 0xE0434352 崩掉（这正是此前“双击后什么都没发生”的原因）。
        TryRun(() => _loop?.Dispose(), "loop.Dispose");
        TryRun(() => _source?.Stop(), "source.Stop");
        TryRun(() =>
        {
            if (!ReferenceEquals(_source, _demo))
            {
                _source?.Dispose();
            }
        }, "source.Dispose");
        TryRun(() => _demo?.Dispose(), "demo.Dispose");
        TryRun(() => _tray?.Dispose(), "tray.Dispose");

        // 不再调用 ReleaseMutex：本进程从未取得 Mutex 所有权，
        // 句柄随 Dispose / 进程退出由操作系统回收。
        TryRun(() => _singleInstanceMutex?.Dispose(), "mutex.Dispose");

        base.OnExit(e);
    }

    private static void TryRun(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            CrashLog.Write($"OnExit/{what}", ex, fatal: false);
        }
    }
}
