using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

namespace LenoShot.Capture;

/// <summary>Captura de tela no Windows via GDI (BitBlt da tela para um DIB de 32 bits).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Win32Capture
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000; // inclui janelas em camadas (menus, tooltips)

    /// <summary>
    /// Retângulo que cobre todos os monitores. No Windows a origem pode ser negativa
    /// (monitor à esquerda ou acima do principal).
    /// </summary>
    public static PixelRect VirtualScreen => new(
        GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
        GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));

    public static ScreenImage? CaptureAll(out PixelPoint pointer, out PixelPoint origin)
    {
        var screen = VirtualScreen;
        origin = screen.Position;
        pointer = Win32Input.GetPointer();
        return CaptureRect(screen);
    }

    public static ScreenImage? CaptureRect(PixelRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return null;

        var screenDc = GetDC(0);
        if (screenDc == 0)
            return null;

        var memoryDc = CreateCompatibleDC(screenDc);
        var info = new BitmapInfo
        {
            Size = 40, // tamanho do BITMAPINFOHEADER
            Width = rect.Width,
            Height = -rect.Height, // negativo = linhas de cima para baixo
            Planes = 1,
            BitCount = 32,
        };
        var dib = CreateDIBSection(screenDc, &info, 0, out var bits, 0, 0);
        var previous = dib != 0 ? SelectObject(memoryDc, dib) : 0;

        try
        {
            if (dib == 0 || bits == 0 ||
                !BitBlt(memoryDc, 0, 0, rect.Width, rect.Height, screenDc, rect.X, rect.Y, SrcCopy | CaptureBlt))
                return null;

            // O DIB já vem em BGRA, com a largura em múltiplos de 4 bytes (32 bits): é só copiar.
            var pixels = new byte[rect.Width * rect.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            // O canal alfa do GDI não é confiável: força opaco.
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;

            return new ScreenImage(rect.Width, rect.Height, pixels);
        }
        finally
        {
            if (previous != 0)
                SelectObject(memoryDc, previous);
            if (dib != 0)
                DeleteObject(dib);
            DeleteDC(memoryDc);
            ReleaseDC(0, screenDc);
        }
    }

    /// <summary>BITMAPINFO: o cabeçalho seguido de uma entrada de paleta (não usada em 32 bits).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint Palette0;
    }

    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] private static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint hwnd, nint hdc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint hdc);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint hdc, BitmapInfo* info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint dest, int x, int y, int width, int height, nint src, int srcX, int srcY, uint rop);
}
