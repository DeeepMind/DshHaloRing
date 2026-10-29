using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DshHaloRing.Services;

/// <summary>
/// 托盘注册诊断：直接探测 Win32 Shell_NotifyIcon 的返回码，
/// 并记录出错位置。托盘不可用时把原因写进启动日志，避免"双击后什么都没有"。
/// </summary>
public static class TrayDiagnostics
{
    // ── Win32 ────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr hWnd);

    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;

    /// <summary>探测结果。</summary>
    public sealed record ProbeResult(bool Success, int LastError, string ErrorText, int StructSize, string TaskbarState);

    /// <summary>
    /// 用一次性的探针窗口真实调用 Shell_NotifyIcon，然后立即注销。
    /// 返回详细的失败原因（含 Win32 错误码与可读文本）。
    /// </summary>
    public static ProbeResult Probe()
    {
        int structSize = Marshal.SizeOf(typeof(NOTIFYICONDATA));
        string taskbar = DescribeTaskbar();

        IntPtr hwnd = IntPtr.Zero;
        try
        {
            hwnd = CreateWindowEx(0, "STATIC", "DshHaloRing.TrayProbe", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                int e0 = Marshal.GetLastWin32Error();
                return new ProbeResult(false, e0, $"创建探针窗口失败：{DescribeError(e0)}", structSize, taskbar);
            }

            var data = new NOTIFYICONDATA
            {
                cbSize = structSize,
                hWnd = hwnd,
                uID = 0xD5A1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = 0x0400 + 1,
                hIcon = System.Drawing.SystemIcons.Application.Handle,
                szTip = "DSH HaloRing 探针",
            };

            bool ok = Shell_NotifyIcon(NIM_ADD, ref data);
            int err = ok ? 0 : Marshal.GetLastWin32Error();

            if (ok)
            {
                // 成功则立刻移除，不留残留图标
                Shell_NotifyIcon(NIM_DELETE, ref data);
                return new ProbeResult(true, 0, "无错误", structSize, taskbar);
            }

            return new ProbeResult(false, err, DescribeError(err), structSize, taskbar);
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, -1, $"探测异常：{ex.GetType().Name}: {ex.Message}", structSize, taskbar);
        }
        finally
        {
            if (hwnd != IntPtr.Zero && IsWindow(hwnd))
            {
                DestroyWindow(hwnd);
            }
        }
    }

    /// <summary>把探测结果整理成多行文本，写进启动日志。</summary>
    public static string Describe(ProbeResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Shell_NotifyIcon(NIM_ADD) 成功={r.Success}");
        if (!r.Success)
        {
            sb.AppendLine($"  Win32 错误码 = {r.LastError} (0x{r.LastError:X8})");
            sb.AppendLine($"  错误说明     = {r.ErrorText}");
        }
        sb.AppendLine($"  NOTIFYICONDATA 大小 = {r.StructSize} 字节（x64 期望 976）");
        sb.AppendLine($"  任务栏状态 = {r.TaskbarState}");
        return sb.ToString();
    }

    private static string DescribeTaskbar()
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero)
        {
            return "未找到 Shell_TrayWnd（任务栏不存在？）";
        }
        return $"Shell_TrayWnd 存在 (hwnd={tray})，IsWindow={IsWindow(tray)}";
    }

    /// <summary>把常见 Win32 错误码翻成可操作的中文说明。</summary>
    public static string DescribeError(int code) => code switch
    {
        0 => "无错误",
        5 => "ERROR_ACCESS_DENIED：权限被拒绝。常见于：进程被提权/以管理员运行、"
           + "UAC 被关闭(EnableLUA=0)、或有 shell hook 拦截。可尝试以普通用户身份启动。",
        87 => "ERROR_INVALID_PARAMETER：结构体或参数不合法",
        1400 => "ERROR_INVALID_WINDOW_HANDLE：窗口句柄无效",
        1407 => "ERROR_WINDOW_OF_OTHER_THREAD：窗口属于其它线程",
        1460 => "ERROR_TIMEOUT：Shell 未响应",
        unchecked((int)0x80004005) => "E_FAIL：Shell 内部失败（常被前面的权限错误掩盖）",
        _ => "未知错误，请查阅 Win32 错误码表",
    };
}
