using System.Runtime.InteropServices;
using Avalonia;

namespace LenoShot.Capture;

/// <summary>Captura a tela inteira (todos os monitores) via libX11.</summary>
internal static partial class X11Capture
{
    private const string LibX11 = "libX11.so.6";
    private const int ZPixmap = 2;

    /// <summary>Captura todos os monitores e informa onde está o ponteiro do mouse.</summary>
    public static ScreenImage? CaptureRoot(out PixelPoint pointer)
    {
        pointer = default;
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return null;

        try
        {
            var screen = XDefaultScreen(display);
            var root = XRootWindow(display, screen);
            XQueryPointer(display, root, out _, out _, out var px, out var py, out _, out _, out _);
            pointer = new PixelPoint(px, py);
            return Grab(display, root, new PixelRect(0, 0, XDisplayWidth(display, screen), XDisplayHeight(display, screen)));
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    /// <summary>Captura só um retângulo da tela (coordenadas globais, em pixels).</summary>
    public static ScreenImage? CaptureRect(PixelRect rect)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return null;

        try
        {
            return Grab(display, XRootWindow(display, XDefaultScreen(display)), rect);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static ScreenImage? Grab(IntPtr display, IntPtr root, PixelRect rect)
    {
        var imagePtr = XGetImage(display, root, rect.X, rect.Y, (uint)rect.Width, (uint)rect.Height, ~0UL, ZPixmap);
        if (imagePtr == IntPtr.Zero)
            return null;

        var image = Marshal.PtrToStructure<XImageHeader>(imagePtr);
        try
        {
            if (image.BitsPerPixel != 32)
                return null; // telas de 16/24 bpp não são suportadas por enquanto

            var width = rect.Width;
            var height = rect.Height;
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                Marshal.Copy(image.Data + y * image.BytesPerLine, pixels, y * width * 4, width * 4);

            // X11 entrega BGRX: força o alfa para opaco.
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;

            return new ScreenImage(width, height, pixels);
        }
        finally
        {
            XFree(image.Data);
            XFree(imagePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XImageHeader
    {
        public int Width;
        public int Height;
        public int XOffset;
        public int Format;
        public IntPtr Data;
        public int ByteOrder;
        public int BitmapUnit;
        public int BitmapBitOrder;
        public int BitmapPad;
        public int Depth;
        public int BytesPerLine;
        public int BitsPerPixel;
    }

    [LibraryImport(LibX11)] private static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport(LibX11)] private static partial int XCloseDisplay(IntPtr display);
    [LibraryImport(LibX11)] private static partial int XDefaultScreen(IntPtr display);
    [LibraryImport(LibX11)] private static partial IntPtr XRootWindow(IntPtr display, int screen);
    [LibraryImport(LibX11)] private static partial int XDisplayWidth(IntPtr display, int screen);
    [LibraryImport(LibX11)] private static partial int XDisplayHeight(IntPtr display, int screen);
    [LibraryImport(LibX11)] private static partial int XFree(IntPtr data);

    [LibraryImport(LibX11)]
    private static partial IntPtr XGetImage(IntPtr display, IntPtr drawable, int x, int y,
        uint width, uint height, ulong planeMask, int format);

    [LibraryImport(LibX11)]
    private static partial int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);
}
