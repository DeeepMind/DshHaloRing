using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DshHaloRing.Models;
using DshHaloRing.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DshHaloRing.Views;

/// <summary>
/// 外观设置窗口。左侧实时预览（含状态动画），右侧分区分组编辑，
/// 保存时深拷贝回写到 AppSettings 并持久化。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _working;
    /// <summary>打开窗口时的原始配置快照：取消时用它把托盘图标恢复原状。</summary>
    private readonly AppSettings _original;
    private readonly Dictionary<AgentState, Border> _swatches = new();
    private readonly Dictionary<AgentState, Slider> _opacitySliders = new();
    private readonly Dictionary<AgentState, TextBlock> _opacityTexts = new();
    private readonly DispatcherTimer _previewTimer;
    private readonly DateTimeOffset _previewStart = DateTimeOffset.UtcNow;
    private bool _initializing = true;

    public SettingsWindow(AppSettings source)
    {
        InitializeComponent();
        _working = CloneSettings(source);
        _original = CloneSettings(source);

        BuildStateColorRows();
        LoadFromSettings();

        _previewTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _previewTimer.Tick += (_, _) => RefreshPreview();
        _previewTimer.Start();

        Closed += (_, _) => _previewTimer.Stop();
        _initializing = false;
        RefreshPreview();
    }

    /// <summary>保存后的配置（仅在对话框返回 true 时有效）。</summary>
    public AppSettings? Result { get; private set; }

    public event EventHandler<AppSettings>? SettingsApplied;

    /// <summary>
    /// 主题切换时的实时通知：只更新托盘图标外观，不落盘、不关窗口。
    /// 让用户点选主题后立即看到托盘变化。
    /// </summary>
    public event EventHandler<AppSettings>? ThemeChangedLive;

    private static AppSettings CloneSettings(AppSettings src)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(src);
        var clone = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        clone.EnsureDefaults();
        return clone;
    }

    private void BuildStateColorRows()
    {
        foreach (AgentState state in Enum.GetValues<AgentState>())
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

            var label = new TextBlock
            {
                Text = state.ToDisplayName(),
                Style = (Style)FindResource("FieldLabel"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var swatch = new Border
            {
                Width = 30,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD6)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Gray,
            };
            var captured = state;
            swatch.MouseLeftButtonUp += (_, _) => PickColorForState(captured);
            Grid.SetColumn(swatch, 1);
            row.Children.Add(swatch);
            _swatches[state] = swatch;

            var slider = new Slider
            {
                Minimum = 0.05,
                Maximum = 1.0,
                Margin = new Thickness(12, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            slider.ValueChanged += (_, _) =>
            {
                if (_initializing) return;
                _working.StyleFor(captured).Opacity = slider.Value;
                if (_opacityTexts.TryGetValue(captured, out var t))
                {
                    t.Text = $"{slider.Value:P0}";
                }
                UpdateSwatch(captured);
                RefreshPreview();
            };
            Grid.SetColumn(slider, 2);
            row.Children.Add(slider);
            _opacitySliders[state] = slider;

            var opacityText = new TextBlock
            {
                Style = (Style)FindResource("FieldLabel"),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(opacityText, 3);
            row.Children.Add(opacityText);
            _opacityTexts[state] = opacityText;

            StateColorList.Children.Add(row);
        }
    }

    private void PickColorForState(AgentState state)
    {
        var style = _working.StyleFor(state);
        var current = style.ToMediaColor();
        if (TryPickColor(current, out var picked))
        {
            style.Color = RingIconRenderer.ToHex(picked);
            UpdateSwatch(state);
            RefreshPreview();
        }
    }

    private bool TryPickColor(Color current, out Color picked)
    {
        var previous = new Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            AllowFullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };

        if (previous.ShowDialog() == Forms.DialogResult.OK)
        {
            picked = Color.FromRgb(previous.Color.R, previous.Color.G, previous.Color.B);
            return true;
        }

        picked = current;
        return false;
    }

    private void UpdateSwatch(AgentState state)
    {
        var style = _working.StyleFor(state);
        var c = style.ToMediaColor();
        if (_swatches.TryGetValue(state, out var swatch))
        {
            swatch.Background = new SolidColorBrush(c);
        }
        if (_opacitySliders.TryGetValue(state, out var slider) && Math.Abs(slider.Value - style.Opacity) > 0.001)
        {
            slider.Value = style.Opacity;
        }
        if (_opacityTexts.TryGetValue(state, out var text))
        {
            text.Text = $"{style.Opacity:P0}";
        }
    }

    private void LoadFromSettings()
    {
        _initializing = true;

        PreviewStateCombo.Items.Clear();
        PreviewStateCombo.ItemsSource = null;
        var states = new List<AgentState>(Enum.GetValues<AgentState>());
        PreviewStateCombo.ItemsSource = states;
        PreviewStateCombo.SelectedItem = AgentState.Running;

        var ring = _working.Ring;
        StartAngleSlider.Value = ring.StartAngleDegrees;
        RadiusSlider.Value = ring.RadiusRatio;
        ThicknessSlider.Value = ring.ThicknessRatio;
        GlowSlider.Value = ring.GlowStrength;
        DotSlider.Value = _working.Dot.RadiusRatio;
        ClockwiseCheck.IsChecked = ring.Clockwise;
        RoundCapCheck.IsChecked = ring.RoundCaps;
        ShowTrackCheck.IsChecked = ring.ShowTrack;
        ShowProgressCheck.IsChecked = ring.ShowProgress;
        AnimationsCheck.IsChecked = ring.AnimationsEnabled;

        CornerSlider.Value = _working.Canvas.CornerRadiusRatio;
        TransparentBackgroundCheck.IsChecked = _working.Canvas.TransparentBackground;
        UpdateBackgroundSwatch();
        SelectThemeCombo(_working.Theme);

        foreach (var state in Enum.GetValues<AgentState>())
        {
            UpdateSwatch(state);
        }

        HeartbeatBox.Text = _working.HeartbeatTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        JsonPathBox.Text = _working.JsonStatusPath ?? string.Empty;
        SelectSourceCombo(_working.StatusSource);
        StartupCheck.IsChecked = StartupManager.IsEnabled();

        UpdateNumericLabels();
        _initializing = false;
    }

    private void SelectSourceCombo(string key)
    {
        foreach (var obj in SourceCombo.Items)
        {
            if (obj is ComboBoxItem item && string.Equals(item.Tag as string, key, StringComparison.OrdinalIgnoreCase))
            {
                SourceCombo.SelectedItem = item;
                return;
            }
        }
        SourceCombo.SelectedIndex = 0;
    }

    /// <summary>按主题 key 选中下拉项；找不到就退回第一项。</summary>
    private void SelectThemeCombo(string? key)
    {
        foreach (var obj in ThemeCombo.Items)
        {
            if (obj is ComboBoxItem item && string.Equals(item.Tag as string, key, StringComparison.OrdinalIgnoreCase))
            {
                ThemeCombo.SelectedItem = item;
                return;
            }
        }
        ThemeCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// 切换主题：立即把该主题的底板色与轨道色写入工作配置，
    /// 同步刷新色块与预览，使托盘图标在外观上即时反映。
    /// </summary>
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }
        if (ThemeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string key)
        {
            return;
        }

        var mode = ThemePreset.ParseKey(key);
        _working.ApplyTheme(mode);

        // 主题改变了底板/轨道色，界面控件需要跟随
        UpdateBackgroundSwatch();
        RefreshPreview();

        // 实时通知主程序刷新托盘图标（不落盘、不关闭窗口）
        ThemeChangedLive?.Invoke(this, CloneSettings(_working));
    }

    private void UpdateNumericLabels()
    {
        StartAngleText.Text = $"{StartAngleSlider.Value:0}°";
        RadiusText.Text = $"{RadiusSlider.Value:0.00}";
        ThicknessText.Text = $"{ThicknessSlider.Value:0.000}";
        GlowText.Text = $"{GlowSlider.Value:0.00}";
        DotText.Text = $"{DotSlider.Value:0.00}";
        CornerText.Text = $"{CornerSlider.Value:0.00}";
    }

    private void UpdateBackgroundSwatch()
    {
        var c = RingIconRenderer.ParseColor(_working.Canvas.BackgroundColor, Color.FromRgb(0x20, 0x20, 0x20));
        BackgroundSwatch.Background = new SolidColorBrush(c);
        BackgroundHexText.Text = RingIconRenderer.ToHex(c);
    }

    // ── 事件 ────────────────────────────────────────────────────

    private void OnRingGeometryChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        var ring = _working.Ring;
        ring.StartAngleDegrees = Math.Round(StartAngleSlider.Value, 1);
        ring.RadiusRatio = Math.Round(RadiusSlider.Value, 3);
        ring.ThicknessRatio = Math.Round(ThicknessSlider.Value, 4);
        ring.GlowStrength = Math.Round(GlowSlider.Value, 3);
        ring.Clockwise = ClockwiseCheck.IsChecked == true;
        ring.RoundCaps = RoundCapCheck.IsChecked == true;
        ring.ShowTrack = ShowTrackCheck.IsChecked == true;
        ring.ShowProgress = ShowProgressCheck.IsChecked == true;
        ring.AnimationsEnabled = AnimationsCheck.IsChecked == true;
        _working.Dot.RadiusRatio = Math.Round(DotSlider.Value, 3);

        UpdateNumericLabels();
        RefreshPreview();
    }

    private void OnBackgroundChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }
        _working.Canvas.TransparentBackground = TransparentBackgroundCheck.IsChecked == true;
        _working.Canvas.CornerRadiusRatio = Math.Round(CornerSlider.Value, 3);
        UpdateNumericLabels();
        RefreshPreview();
    }

    private void OnBackgroundSwatchClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var current = RingIconRenderer.ParseColor(_working.Canvas.BackgroundColor, Color.FromRgb(0x20, 0x20, 0x20));
        if (TryPickColor(current, out var picked))
        {
            _working.Canvas.BackgroundColor = RingIconRenderer.ToHex(picked);
            UpdateBackgroundSwatch();
            RefreshPreview();
        }
    }

    private void OnPreviewStateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            RefreshPreview();
        }
    }

    private void OnPreviewProgressChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PreviewProgressText != null)
        {
            PreviewProgressText.Text = $"{PreviewProgressSlider.Value:0}%";
        }
        if (!_initializing)
        {
            RefreshPreview();
        }
    }

    private void OnPreviewAnimationToggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            RefreshPreview();
        }
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }
        if (SourceCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _working.StatusSource = tag;
        }
    }

    private void OnBrowseJson(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 DSH 状态 JSON 文件",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            JsonPathBox.Text = dlg.FileName;
        }
    }

    private void OnExportIcon(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出自定义托盘图标",
            Filter = "图标文件 (*.ico)|*.ico",
            FileName = "DshHaloRing.ico",
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        var state = PreviewStateCombo.SelectedItem is AgentState s ? s : AgentState.Running;
        double progress = PreviewProgressSlider.Value / 100.0;
        var images = new List<BitmapSource>();
        foreach (var size in new[] { 256, 128, 64, 48, 32, 24, 20, 16 })
        {
            images.Add(RingIconRenderer.RenderBgraPremultipliedAsStraight(_working, state, progress, 1.0, size));
        }
        IconPackager.WriteMultiSizeIco(dlg.FileName, images);
        System.Windows.MessageBox.Show(this, $"已导出：\n{dlg.FileName}", "导出完成",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        defaults.EnsureDefaults();
        defaults.StatusSource = _working.StatusSource;
        defaults.JsonStatusPath = _working.JsonStatusPath;
        defaults.HeartbeatTimeoutSeconds = _working.HeartbeatTimeoutSeconds;
        // 保留当前主题选择：恢复默认外观不应把用户切好的主题改回去
        defaults.ApplyTheme(_working.ThemeMode);
        CopyInto(_working, defaults);
        LoadFromSettings();
        RefreshPreview();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // 本窗口用 Show() 以非模态方式打开，DialogResult 只在 ShowDialog() 下可用，
        // 直接设置会抛 InvalidOperationException，因此一律用 Close()。
        Result = null;
        // 取消时把托盘图标恢复成已保存的配置
        ThemeChangedLive?.Invoke(this, CloneSettings(_original));
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(HeartbeatBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            if (!double.TryParse(HeartbeatBox.Text, out _))
            {
                HeartbeatBox.Text = _working.HeartbeatTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            }
        }
        _working.HeartbeatTimeoutSeconds = ParseInt(HeartbeatBox.Text, _working.HeartbeatTimeoutSeconds, 1, 600);
        _working.JsonStatusPath = string.IsNullOrWhiteSpace(JsonPathBox.Text) ? null : JsonPathBox.Text.Trim();

        StartupManager.SetEnabled(StartupCheck.IsChecked == true);
        _working.StartWithWindows = StartupCheck.IsChecked == true;

        Result = CloneSettings(_working);
        SettingsApplied?.Invoke(this, Result);
        // 非模态窗口不能用 DialogResult，直接关闭即可
        Close();
    }

    private static int ParseInt(string? text, int fallback, int min, int max)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            return Math.Clamp(v, min, max);
        }
        return fallback;
    }

    private static void CopyInto(AppSettings target, AppSettings source)
    {
        target.Ring = source.Ring;
        target.Canvas = source.Canvas;
        target.Dot = source.Dot;
        target.Theme = source.Theme;
        target.StateStyles = source.StateStyles;
        target.HeartbeatTimeoutSeconds = source.HeartbeatTimeoutSeconds;
        target.JsonStatusPath = source.JsonStatusPath;
        target.StatusSource = source.StatusSource;
        target.CompletedFlashMilliseconds = source.CompletedFlashMilliseconds;
        target.ShowProgressTooltip = source.ShowProgressTooltip;
        target.TrayClickAction = source.TrayClickAction;
    }

    private void RefreshPreview()
    {
        if (PreviewImage == null || PreviewStateCombo == null)
        {
            return;
        }

        var state = PreviewStateCombo.SelectedItem is AgentState s ? s : AgentState.Running;
        double progress = PreviewProgressSlider.Value / 100.0;
        double intensity = 1.0;

        if (PreviewAnimateCheck.IsChecked == true && _working.Ring.AnimationsEnabled)
        {
            double elapsed = (DateTimeOffset.UtcNow - _previewStart).TotalSeconds;
            intensity = AnimationEngine.Intensity(state, elapsed, true);
        }

        PreviewImage.Source = RingIconRenderer.Render(_working, state, progress, intensity, 256);
    }
}
