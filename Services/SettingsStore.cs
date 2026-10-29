using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DshHaloRing.Models;

namespace DshHaloRing.Services;

/// <summary>
/// 配置读写。存储目录按顺序尝试，第一个真正可写的位置生效：
///   1. %APPDATA%\DshHaloRing            （常规位置）
///   2. %LOCALAPPDATA%\DshHaloRing       （漫游被策略禁用时）
///   3. &lt;程序目录&gt;\data                 （便携模式 / 前两者皆不可写）
///
/// 关键教训：写入失败绝不能静默吞掉。此前的实现把 APPDATA 写不进去的异常
/// 全部 catch 掉，导致程序看似“什么都没发生”，也无法诊断。现在会记录
/// 实际使用的目录，并在全部失败时向调用方暴露出来。
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string? _resolvedDirectory;
    private static string? _lastError;

    /// <summary>实际生效的存储目录（首次访问时探测并缓存）。</summary>
    public static string DirectoryPath => _resolvedDirectory ??= ResolveDirectory();

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    /// <summary>最近一次写入失败的原因；成功时为 null。</summary>
    public static string? LastError => _lastError;

    public static IReadOnlyList<string> CandidateDirectories { get; } = BuildCandidates();

    private static IReadOnlyList<string> BuildCandidates()
    {
        var list = new List<string>();
        void Add(string? root, string leaf)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }
            try
            {
                list.Add(Path.Combine(root, leaf));
            }
            catch (ArgumentException)
            {
                // 非法路径直接跳过
            }
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DshHaloRing");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshHaloRing");

        // 兜底：程序目录（末尾带 data，避免污染 bin）
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            list.Add(Path.Combine(baseDir, "data"));
        }

        return list;
    }

    private static string ResolveDirectory()
    {
        foreach (var candidate in CandidateDirectories)
        {
            if (CanWrite(candidate))
            {
                return candidate;
            }
        }

        // 全都不行：仍返回第一个候选，让写入失败在 Save 时显式报出来
        _lastError = "所有候选目录均不可写：" + string.Join("; ", CandidateDirectories);
        return CandidateDirectories.Count > 0
            ? CandidateDirectories[0]
            : AppContext.BaseDirectory;
    }

    /// <summary>真正落盘一次探测文件来确认可写（仅判断目录是否存在不够）。</summary>
    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "probe", System.Text.Encoding.UTF8);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public static AppSettings Load()
    {
        foreach (var candidate in CandidateDirectories)
        {
            var path = Path.Combine(candidate, "settings.json");
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded != null)
                {
                    loaded.EnsureDefaults();
                    _resolvedDirectory = candidate;
                    return loaded;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _lastError = $"{path}: {ex.Message}";
                TryBackupCorrupted(path);
            }
        }

        var fresh = new AppSettings();
        fresh.EnsureDefaults();
        return fresh;
    }

    /// <summary>保存配置，返回是否成功；失败原因见 <see cref="LastError"/>。</summary>
    public static bool Save(AppSettings settings)
    {
        settings.EnsureDefaults();
        var json = JsonSerializer.Serialize(settings, Options);

        foreach (var candidate in CandidateDirectories)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var path = Path.Combine(candidate, "settings.json");
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json, System.Text.Encoding.UTF8);
                File.Copy(tmp, path, overwrite: true);
                File.Delete(tmp);
                _resolvedDirectory = candidate;
                _lastError = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _lastError = $"{candidate}: {ex.Message}";
            }
        }

        return false;
    }

    private static void TryBackupCorrupted(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Copy(path, path + ".corrupt", overwrite: true);
            }
        }
        catch (IOException)
        {
            // 备份失败不影响启动
        }
    }
}
