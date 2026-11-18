using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LenoShot.Capture;

/// <summary>Pixels de uma captura de tela em BGRA 32 bits.</summary>
public sealed class ScreenImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;
    public int Stride => Width * 4;

    /// <summary>Reduz a imagem para a largura pedida, fazendo a média de cada bloco de pixels.</summary>
    public ScreenImage Downscale(int targetWidth)
    {
        if (targetWidth >= Width)
            return this;

        var targetHeight = (int)Math.Max(1, (long)Height * targetWidth / Width);
        var result = new byte[targetWidth * targetHeight * 4];
        var sx = (double)Width / targetWidth;
        var sy = (double)Height / targetHeight;

        for (var ty = 0; ty < targetHeight; ty++)
        {
            var y0 = (int)(ty * sy);
            var y1 = Math.Max(y0 + 1, Math.Min(Height, (int)((ty + 1) * sy)));
            for (var tx = 0; tx < targetWidth; tx++)
            {
                var x0 = (int)(tx * sx);
                var x1 = Math.Max(x0 + 1, Math.Min(Width, (int)((tx + 1) * sx)));
                int b = 0, g = 0, r = 0, n = 0;
                for (var y = y0; y < y1; y++)
                {
                    var i = y * Stride + x0 * 4;
                    for (var x = x0; x < x1; x++, i += 4)
                    {
                        b += Pixels[i]; g += Pixels[i + 1]; r += Pixels[i + 2]; n++;
                    }
                }
                var o = (ty * targetWidth + tx) * 4;
                result[o] = (byte)(b / n); result[o + 1] = (byte)(g / n); result[o + 2] = (byte)(r / n); result[o + 3] = 255;
            }
        }
        return new ScreenImage(targetWidth, targetHeight, result);
    }

    public WriteableBitmap ToBitmap() => Crop(new PixelRect(0, 0, Width, Height));

    public WriteableBitmap Crop(PixelRect rect)
    {
        rect = rect.Intersect(new PixelRect(0, 0, Width, Height));
        var bitmap = new WriteableBitmap(
            new PixelSize(Math.Max(rect.Width, 1), Math.Max(rect.Height, 1)),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using var buffer = bitmap.Lock();
        for (var y = 0; y < rect.Height; y++)
        {
            var srcOffset = (rect.Y + y) * Stride + rect.X * 4;
            Marshal.Copy(Pixels, srcOffset, buffer.Address + y * buffer.RowBytes, rect.Width * 4);
        }

        return bitmap;
    }
}
