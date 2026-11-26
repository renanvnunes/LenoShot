using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

namespace LenoShot.Capture;

/// <summary>Posição e controle do mouse no Windows (usado pela captura com rolagem).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Win32Input
{
    private const uint InputMouse = 0;
    private const uint MouseEventWheel = 0x0800;
    private const int WheelDelta = 120;

    public static PixelPoint GetPointer() =>
        GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : default;

    public static void MovePointer(PixelPoint point) => SetCursorPos(point.X, point.Y);

    /// <summary>Gira a roda do mouse onde o ponteiro estiver (positivo = para baixo).</summary>
    public static void Scroll(int clicks)
    {
        // No Windows, roda para baixo é delta negativo
        var delta = clicks > 0 ? -WheelDelta : WheelDelta;
        for (var i = 0; i < Math.Abs(clicks); i++)
        {
            var input = new MouseInput { Type = InputMouse, MouseData = delta, Flags = MouseEventWheel };
            SendInput(1, &input, sizeof(MouseInput));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    /// <summary>INPUT com a união já como MOUSEINPUT (40 bytes em 64 bits).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct MouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int Dx;
        [FieldOffset(12)] public int Dy;
        [FieldOffset(16)] public int MouseData;
        [FieldOffset(20)] public uint Flags;
        [FieldOffset(24)] public uint Time;
        [FieldOffset(32)] public nint ExtraInfo;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, MouseInput* inputs, int size);
}
