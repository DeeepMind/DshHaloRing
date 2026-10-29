using System;
using DshHaloRing.Models;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace DshHaloRing.Services;

/// <summary>
/// 把渲染好的位图换成 Win32 HICON，并负责销毁旧句柄。
/// NotifyIcon 需要 Icon 对象，而 WPF 的 IconBitmapEncoder 只写 ico 文件，
/// 这里直接内存编码 ico 流 + DestroyIcon 管理生命周期。
/// </summary>
public sealed class IconPackager : IDisposable
{
    /// <summary>托盘图标候选尺寸，从大到小排（大尺寸在前，避免爆破式缩放）。</summary>
    public static readonly int[] DefaultSizes = { 64, 48, 32, 24, 20, 16 };

    private readonly List<IntPtr> _liveHandles = new();
    private IntPtr _currentHandle = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    /// <summary>把一张位图编码成单尺寸 ico 的内存字节。</summary>
    public static byte[] EncodeSingleIco(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var pngStream = new MemoryStream();
        encoder.Save(pngStream);
        var png = pngStream.ToArray();

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        // ICONDIR
        bw.Write((ushort)0);                 // reserved
        bw.Write((ushort)1);                 // type = icon
        bw.Write((ushort)1);                 // count
        // ICONDIRENTRY
        bw.Write((byte)(image.PixelWidth >= 256 ? 0 : image.PixelWidth));
        bw.Write((byte)(image.PixelHeight >= 256 ? 0 : image.PixelHeight));
        bw.Write((byte)0);                   // color count
        bw.Write((byte)0);                   // reserved
        bw.Write((ushort)1);                 // planes
        bw.Write((ushort)32);                // bit count
        bw.Write(png.Length);                // bytes in resource
        bw.Write(6 + 16);                    // image offset
        bw.Write(png);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>把多个尺寸编码进一个多尺寸 ico 文件（用于生成应用图标 / 导出自定义图标）。</summary>
    public static void WriteMultiSizeIco(string path, IReadOnlyList<BitmapSource> images)
    {
        var pngs = new List<byte[]>(images.Count);
        foreach (var img in images)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(img));
            using var s = new MemoryStream();
            encoder.Save(s);
            pngs.Add(s.ToArray());
        }

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)images.Count);

        int offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            var img = images[i];
            bw.Write((byte)(img.PixelWidth >= 256 ? 0 : img.PixelWidth));
            bw.Write((byte)(img.PixelHeight >= 256 ? 0 : img.PixelHeight));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(pngs[i].Length);
            bw.Write(offset);
            offset += pngs[i].Length;
        }

        foreach (var png in pngs)
        {
            bw.Write(png);
        }
    }

    /// <summary>把 ico 字节流转成可用 Icon 实例（句柄由本类托管）。</summary>
    public System.Drawing.Icon CreateIconFromBytes(byte[] icoBytes)
    {
        using var ms = new MemoryStream(icoBytes);
        using var parsed = new System.Drawing.Icon(ms);
        var handle = CopyIcon(parsed.Handle);
        _liveHandles.Add(handle);
        return System.Drawing.Icon.FromHandle(handle);
    }

    /// <summary>多尺寸渲染后合成一个 ico 字节流（托盘实际按 DPI 选帧）。</summary>
    public byte[] BuildMultiSizeIcoBytes(AppSettings settings, AgentState state, double progress,
        double intensity, IEnumerable<int>? sizes = null)
    {
        var images = new List<BitmapSource>();
        foreach (var size in sizes ?? DefaultSizes)
        {
            images.Add(RingIconRenderer.RenderBgraPremultipliedAsStraight(settings, state, progress, intensity, size));
        }
        return EncodeMultiIco(images);
    }

    public static byte[] EncodeMultiIco(IReadOnlyList<BitmapSource> images)
    {
        var pngs = new List<byte[]>(images.Count);
        foreach (var img in images)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(img));
            using var s = new MemoryStream();
            encoder.Save(s);
            pngs.Add(s.ToArray());
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            var img = images[i];
            bw.Write((byte)(img.PixelWidth >= 256 ? 0 : img.PixelWidth));
            bw.Write((byte)(img.PixelHeight >= 256 ? 0 : img.PixelHeight));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(pngs[i].Length);
            bw.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs)
        {
            bw.Write(png);
        }
        bw.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        foreach (var h in _liveHandles)
        {
            if (h != IntPtr.Zero)
            {
                DestroyIcon(h);
            }
        }
        _liveHandles.Clear();
        _currentHandle = IntPtr.Zero;
    }
}
