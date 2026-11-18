using System.Runtime.InteropServices;
using Avalonia;

namespace LenoShot.Capture;

/// <summary>Controle do mouse via XTest, usado pela captura com rolagem.</summary>
internal static partial class X11Input
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXtst = "libXtst.so.6";

    private const uint WheelUp = 4;
    private const uint WheelDown = 5;

    public static PixelPoint GetPointer()
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return default;
        try
        {
            XQueryPointer(display, XDefaultRootWindow(display), out _, out _, out var x, out var y, out _, out _, out _);
            return new PixelPoint(x, y);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    public static void MovePointer(PixelPoint point) => WithDisplay(d =>
        XTestFakeMotionEvent(d, -1, point.X, point.Y, 0));

    /// <summary>Gira a roda do mouse onde o ponteiro estiver (positivo = para baixo).</summary>
    public static void Scroll(int clicks) => WithDisplay(d =>
    {
        var button = clicks > 0 ? WheelDown : WheelUp;
        for (var i = 0; i < Math.Abs(clicks); i++)
        {
            XTestFakeButtonEvent(d, button, true, 0);
            XTestFakeButtonEvent(d, button, false, 0);
        }
    });

    private static void WithDisplay(Action<IntPtr> action)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return;
        try
        {
            action(display);
            XFlush(display);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    [LibraryImport(LibX11)] private static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport(LibX11)] private static partial int XCloseDisplay(IntPtr display);
    [LibraryImport(LibX11)] private static partial IntPtr XDefaultRootWindow(IntPtr display);
    [LibraryImport(LibX11)] private static partial int XFlush(IntPtr display);

    [LibraryImport(LibX11)]
    private static partial int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [LibraryImport(LibXtst)]
    private static partial int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, ulong delay);

    [LibraryImport(LibXtst)]
    private static partial int XTestFakeButtonEvent(IntPtr display, uint button,
        [MarshalAs(UnmanagedType.Bool)] bool isPress, ulong delay);
}
