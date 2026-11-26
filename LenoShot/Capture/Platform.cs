using Avalonia;

namespace LenoShot.Capture;

// Fachadas que escolhem a implementação do sistema: X11 no Linux, Win32 no Windows.
// O resto do app só fala com estas classes.

/// <summary>Captura de tela (pixels BGRA, coordenadas globais).</summary>
internal static class ScreenCapture
{
    /// <summary>
    /// Captura todos os monitores. <paramref name="origin"/> é onde fica o canto superior esquerdo
    /// da imagem em coordenadas globais (no X11 é sempre 0,0; no Windows pode ser negativo).
    /// </summary>
    public static ScreenImage? CaptureAll(out PixelPoint pointer, out PixelPoint origin)
    {
        if (OperatingSystem.IsWindows())
            return Win32Capture.CaptureAll(out pointer, out origin);
        origin = default;
        return X11Capture.CaptureRoot(out pointer);
    }

    /// <summary>Captura só um retângulo da tela (coordenadas globais, em pixels).</summary>
    public static ScreenImage? CaptureRect(PixelRect rect) =>
        OperatingSystem.IsWindows() ? Win32Capture.CaptureRect(rect) : X11Capture.CaptureRect(rect);
}

/// <summary>Posição do mouse e eventos sintéticos (rolagem automática).</summary>
internal static class PointerInput
{
    public static PixelPoint GetPointer() =>
        OperatingSystem.IsWindows() ? Win32Input.GetPointer() : X11Input.GetPointer();

    public static void MovePointer(PixelPoint point)
    {
        if (OperatingSystem.IsWindows())
            Win32Input.MovePointer(point);
        else
            X11Input.MovePointer(point);
    }

    /// <summary>Gira a roda do mouse onde o ponteiro estiver (positivo = para baixo).</summary>
    public static void Scroll(int clicks)
    {
        if (OperatingSystem.IsWindows())
            Win32Input.Scroll(clicks);
        else
            X11Input.Scroll(clicks);
    }
}

/// <summary>Posicionamento de janelas em pixels exatos, fora do controle do gerenciador de janelas.</summary>
internal static class PlatformWindow
{
    public static void MoveResize(IntPtr window, PixelRect rect)
    {
        if (OperatingSystem.IsWindows())
            Win32Window.MoveResize(window, rect);
        else
            X11Window.MoveResize(window, rect);
    }

    public static void MakeFullscreenOn(IntPtr window, PixelRect monitor)
    {
        if (OperatingSystem.IsWindows())
            Win32Window.MakeFullscreenOn(window, monitor);
        else
            X11Window.MakeFullscreenOn(window, monitor);
    }

    /// <summary>
    /// Marca a janela como "notificação" (sem efeitos do GNOME/extensões). Antes de a janela
    /// aparecer. No Windows não há equivalente nem necessidade.
    /// </summary>
    public static void MarkAsNotification(IntPtr window)
    {
        if (!OperatingSystem.IsWindows())
            X11Window.SetWindowType(window, "_NET_WM_WINDOW_TYPE_NOTIFICATION");
    }

    /// <summary>
    /// Tira a janela do controle do gerenciador de janelas (X11 override-redirect). Antes de a
    /// janela aparecer. O Windows já respeita a posição pedida, então lá não faz nada.
    /// </summary>
    public static void SetOverrideRedirect(IntPtr window)
    {
        if (!OperatingSystem.IsWindows())
            X11Window.SetOverrideRedirect(window);
    }

    /// <summary>Dá o foco do teclado à janela.</summary>
    public static void FocusInput(IntPtr window)
    {
        if (OperatingSystem.IsWindows())
            Win32Window.FocusInput(window);
        else
            X11Window.FocusInput(window);
    }

    /// <summary>Posição e tamanho reais da janela na tela.</summary>
    public static PixelRect? GetRect(IntPtr window) =>
        OperatingSystem.IsWindows() ? Win32Window.GetRect(window) : X11Window.GetRect(window);
}
