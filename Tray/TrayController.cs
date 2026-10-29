using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DshHaloRing.Models;
using DshHaloRing.Services;
using Forms = System.Windows.Forms;

namespace DshHaloRing.Tray;

/// <summary>
/// 托盘控制器：创建 NotifyIcon、右键菜单、把渲染帧推给系统托盘。
/// 图标按 32px 主尺寸渲染（Windows 托盘在高 DPI 下会取 16/20/24/32 中合适的一帧）。
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    /// <summary>
    /// 当前渲染所用配置。保存设置后由主程序调用 <see cref="UpdateSettings"/> 替换，
    /// 否则托盘会一直使用启动时的旧配置，导致“保存后外观没变化”。
    /// </summary>
    private AppSettings _settings;
    private readonly StateAggregator _aggregator;
    private readonly IconPackager _packager = new();
    private readonly Forms.ToolStripMenuItem _progressLabel;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly List<System.Drawing.Icon> _iconCache = new();
    private AgentState _lastRenderedState = AgentState.Offline;
    private double _lastProgress;

    private static readonly int[] IcoSizes = { 48, 32, 24, 20, 16 };

    public TrayController(AppSettings settings, StateAggregator aggregator)
    {
        _settings = settings;
        _aggregator = aggregator;

        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new System.Drawing.Font("Segoe UI", 9f),
        };

        _progressLabel = new Forms.ToolStripMenuItem("进度：--") { Enabled = false };
        _menu.Items.Add(_progressLabel);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var settingsItem = new Forms.ToolStripMenuItem("外观设置…");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(settingsItem);

        var resetItem = new Forms.ToolStripMenuItem("恢复默认外观");
        resetItem.Click += (_, _) => ResetAppearanceRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(resetItem);

        // 开机自启动：勾选状态在菜单弹出时实时回读注册表，
        // 避免与设置窗口里的开关不同步。
        _startupItem = new Forms.ToolStripMenuItem("开机自启动")
        {
            CheckOnClick = false,
        };
        _startupItem.Click += (_, _) => ToggleStartup();
        _menu.Items.Add(_startupItem);

        _menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
            Text = "DSH HaloRing",
        };
        _notifyIcon.MouseClick += OnMouseClick;
        _menu.Opening += OnMenuOpening;

        UpdateIcon(AgentState.Offline, 0, 1.0);
    }

    /// <summary>当前托盘提示文本（自检/诊断用）。</summary>
    public string TooltipText => _notifyIcon?.Text ?? string.Empty;

    /// <summary>菜单项文本列表（自检用，验证菜单结构）。</summary>
    public IReadOnlyList<string> MenuItemsForTest()
    {
        var list = new List<string>();
        foreach (var obj in _menu.Items)
        {
            if (obj is Forms.ToolStripMenuItem item && !string.IsNullOrEmpty(item.Text))
            {
                list.Add(item.Text);
            }
        }
        return list;
    }

    /// <summary>
    /// 切换开机自启动。写入 HKCU Run 项后回读真实状态，
    /// 避免注册表不可写时菜单显示与实际不符。
    /// </summary>
    private void ToggleStartup()
    {
        bool wanted = !StartupManager.IsEnabled();
        StartupManager.SetEnabled(wanted);

        bool actual = StartupManager.IsEnabled();
        _startupItem.Checked = actual;

        if (actual == wanted)
        {
            ShowBalloonMessage(
                wanted ? "已启用开机自启动" : "已关闭开机自启动",
                wanted
                    ? "下次开机时 DSH HaloRing 会自动启动并驻留托盘。"
                    : "开机时将不再自动启动本程序。");
        }
        else
        {
            // 注册表写入被拒（组策略/权限）——告知用户，别让它看起来像成功了
            ShowBalloonMessage("无法修改开机自启动",
                "注册表 HKCU\\...\\Run 不可写，请检查组策略或改用设置窗口手动配置。");
        }
    }

    /// <summary>菜单弹出前同步一次勾选状态。</summary>
    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _startupItem.Checked = StartupManager.IsEnabled();
        _progressLabel.Text = $"进度：{Math.Round(_lastProgress * 100)}%";
    }

    /// <summary>
    /// 替换托盘使用的配置（保存设置后调用）。
    /// 不替换的话托盘会一直渲染启动时的旧外观。
    /// </summary>
    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        _lastRenderedState = (AgentState)(-1);   // 强制下一帧重绘
    }

    public event EventHandler? SettingsRequested;
    public event EventHandler? ResetAppearanceRequested;
    public event EventHandler? ExitRequested;

    /// <summary>由渲染循环每帧调用；仅在像素实质变化时替换图标。</summary>
    public void UpdateIcon(AgentState state, double progress, double intensity)
        => UpdateIconWithSettings(_settings, state, progress, intensity);

    /// <summary>
    /// 用指定配置渲染一帧并更新托盘图标。
    /// 主题实时预览时传入“临时配置”，不必等用户点保存即可看到效果。
    /// </summary>
    public void UpdateIconWithSettings(AppSettings renderSettings, AgentState state,
        double progress, double intensity)
    {
        var icoBytes = _packager.BuildMultiSizeIcoBytes(renderSettings, state, progress, intensity, IcoSizes);
        var icon = _packager.CreateIconFromBytes(icoBytes);
        _iconCache.Add(icon);
        // 只保留最近 8 帧的图标对象，其余立即释放，避免长时间运行累积
        while (_iconCache.Count > 8)
        {
            var old = _iconCache[0];
            _iconCache.RemoveAt(0);
            old.Dispose();
        }
        _notifyIcon.Icon = icon;
        _lastRenderedState = state;
        _lastProgress = progress;

        var name = state.ToDisplayName();
        var text = renderSettings.ShowProgressTooltip
            ? $"DSH · {name} · 进度 {Math.Round(progress * 100)}%"
            : $"DSH · {name}";
        // NotifyIcon.Text 上限 127 字符
        _notifyIcon.Text = text.Length > 120 ? text[..120] : text;

        _progressLabel.Text = $"进度：{Math.Round(progress * 100)}%";
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left)
        {
            return;
        }

        switch (_settings.TrayClickAction)
        {
            case "preview":
                ShowBalloon();
                break;
            default:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void ShowBalloon()
    {
        var state = _aggregator.Current;
        _notifyIcon.BalloonTipTitle = $"DSH · {state.ToDisplayName()}";
        _notifyIcon.BalloonTipText = _progressLabel.Text ?? string.Empty;
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(2500);
    }

    public void ShowBalloonMessage(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.MouseClick -= OnMouseClick;
        _notifyIcon.Dispose();
        _menu.Dispose();
        foreach (var cached in _iconCache)
        {
            cached.Dispose();
        }
        _iconCache.Clear();
        _packager.Dispose();
    }
}
