using System.Runtime.InteropServices.WindowsRuntime;
using LLCL.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LLCL.Services;

/// <summary>
/// 由 64×32 的 Minecraft 皮肤贴图合成头像。
/// <para>
/// 与 PCL-CE 的 MySkin 一致：从皮肤上裁出两个 8×8 区域再叠放。
///  · 脸层   位于 (8,8)，放大后画成 48×48，居中偏内
///  · 帽子层 位于 (40,8)，放大后画成 56×56，铺满整图
/// 因为帽子层比脸层大一圈，四周会露出帽檐，视觉上才有「立体头像」的效果，
/// 直接把整张皮肤缩放当作头像会把身体部位也显示出来。
/// </para>
/// </summary>
public static class SkinHeadRenderer
{
    /// <summary>
    /// 输出头像边长。与启动按钮宽度保持一致。
    /// 位图直接按最终显示尺寸生成，界面上 1:1 显示，
    /// 这样不会触发缩放插值，像素保持清晰。
    /// </summary>
    public const int Size = 140;

    /// <summary>脸层相对输出图的内缩，按比例取 8/140。</summary>
    private const int HatMargin = 8;

    /// <summary>皮肤贴图中脸层左上角坐标。</summary>
    private const int FaceU = 8;
    private const int FaceV = 8;

    /// <summary>皮肤贴图中帽子层左上角坐标。</summary>
    private const int HatU = 40;
    private const int HatV = 8;

    /// <summary>单个部位在皮肤图上的边长。</summary>
    private const int PartSize = 8;

    /// <summary>
    /// 把皮肤贴图合成为头像位图。失败返回 null。
    /// </summary>
    public static async Task<ImageSource?> RenderAsync(byte[] skinPng, CancellationToken ct = default)
    {
        if (skinPng.Length == 0) return null;

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(skinPng.AsBuffer());
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixelWidth = (int)decoder.PixelWidth;
            var pixelHeight = (int)decoder.PixelHeight;

            if (pixelWidth < 64 || pixelHeight < 32)
            {
                Logger.Warn($"皮肤尺寸不足：{pixelWidth}×{pixelHeight}");
                return null;
            }

            var scale = (int)Math.Round(pixelWidth / 64d);
            if (scale <= 0) scale = 1;

            var skin = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            var skinPixels = skin.DetachPixelData();
            var stride = pixelWidth * 4;

            // 输出缓冲：先画脸层，再用帽子层覆盖
            var output = new byte[Size * Size * 4];
            var outStride = Size * 4;

            // 1) 脸层：8×8 放大到 (Size-8)×(Size-8)，四周内缩 4px
            var faceSize = Size - HatMargin * 2;
            BlitScaled(skinPixels, stride, scale, FaceU, FaceV,
                output, outStride, HatMargin, HatMargin, faceSize, faceSize);

            // 2) 帽子层：若该区域全透明则跳过，否则铺满整图，外圈即帽檐
            if (!IsRegionEmpty(skinPixels, stride, scale, HatU, HatV))
            {
                BlitScaled(skinPixels, stride, scale, HatU, HatV,
                    output, outStride, 0, 0, Size, Size);
            }

            var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, Size, Size, BitmapAlphaMode.Premultiplied);
            result.CopyFromBuffer(output.AsBuffer());

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(result);
            return source;
        }
        catch (Exception ex)
        {
            Logger.Warn($"合成头像失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把皮肤图上 (u,v) 起 8×8 的区域按最近邻放大并写入输出缓冲。
    /// 最近邻可以保持像素风，不会因为插值糊掉。
    /// </summary>
    private static void BlitScaled(byte[] src, int srcStride, int unit, int u, int v,
        byte[] dst, int dstStride, int dx, int dy, int dw, int dh)
    {
        var srcX = u * unit;
        var srcY = v * unit;
        var srcPartSize = PartSize * unit;

        for (var y = 0; y < dh; y++)
        {
            var sy = srcY + y * srcPartSize / dh;
            var rowBase = sy * srcStride;

            for (var x = 0; x < dw; x++)
            {
                var sx = srcX + x * srcPartSize / dw;
                var si = rowBase + sx * 4;
                var di = (dy + y) * dstStride + (dx + x) * 4;

                dst[di + 0] = src[si + 0];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }
    }

    /// <summary>检查皮肤图上某 8×8 区域是否全透明（旧皮肤没有帽子层）。</summary>
    private static bool IsRegionEmpty(byte[] src, int stride, int unit, int u, int v)
    {
        var srcX = u * unit;
        var srcY = v * unit;
        var size = PartSize * unit;

        for (var y = 0; y < size; y++)
        {
            var rowBase = (srcY + y) * stride;
            for (var x = 0; x < size; x++)
            {
                if (src[rowBase + (srcX + x) * 4 + 3] != 0) return false;
            }
        }

        return true;
    }
}
