using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

namespace LenoShot.Capture;

/// <summary>
/// Posicionamento de janelas em pixels exatos no Windows. Diferente do GNOME, o Windows respeita
/// a posição pedida; isto só garante o tamanho exato em pixels (sem arredondar pela escala).
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class Win32Window
{
    private static readonly nint HwndTopmost = -1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    public static void MoveResize(nint window, PixelRect rect) =>
        SetWindowPos(window, 0, rect.X, rect.Y, rect.Width, rect.Height, SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate);

    /// <summary>Cobre o monitor inteiro, acima de tudo (inclusive da barra de tarefas).</summary>
    public static void MakeFullscreenOn(nint window, PixelRect monitor) =>
        SetWindowPos(window, HwndTopmost, monitor.X, monitor.Y, monitor.Width, monitor.Height, SwpNoOwnerZOrder);

    public static void FocusInput(nint window)
    {
        SetWindowPos(window, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        SetForegroundWindow(window);
    }

    /// <summary>
    /// O Windows impõe um tamanho mínimo às janelas (uma faixa de 2 px volta com 39 px). Recorta a
    /// parte visível (e clicável) para o tamanho pedido; com <paramref name="clip"/> falso remove o recorte.
    /// </summary>
    public static void ClipToSize(nint window, int width, int height, bool clip)
    {
        // Depois do SetWindowRgn a região pertence ao sistema (não deve ser liberada)
        SetWindowRgn(window, clip ? CreateRectRgn(0, 0, width, height) : 0, true);
    }

    public static PixelRect? GetRect(nint window) =>
        GetWindowRect(window, out var r) ? new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);
}
