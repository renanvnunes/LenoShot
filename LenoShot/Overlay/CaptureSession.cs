using Avalonia;
using Avalonia.Controls;
using LenoShot.Capture;

namespace LenoShot.Overlay;

/// <summary>
/// Uma captura em andamento: uma sobreposição por monitor, todas com a mesma imagem congelada.
/// A seleção fica em um monitor por vez; começar a selecionar num monitor limpa os outros.
/// </summary>
public sealed class CaptureSession
{
    private readonly List<OverlayWindow> _windows = [];
    private bool _ended;

    public event EventHandler? Ended;

    /// <summary>Disparado quando o usuário pede a captura com rolagem (retângulo em coordenadas globais).</summary>
    public event EventHandler<ScrollCaptureRequest>? ScrollCaptureRequested;

    /// <summary>Disparado quando o usuário pede para gravar a área selecionada.</summary>
    public event EventHandler<RecordRequest>? RecordRequested;

    public static CaptureSession? Start()
    {
        var full = X11Capture.CaptureRoot(out var pointer);
        if (full is null)
            return null;

        var probe = new Window();
        var screens = probe.Screens.All;
        var session = new CaptureSession();

        var monitors = screens.Count > 0
            ? screens.Select(s => (s.Bounds, s.Scaling))
            : [(new PixelRect(0, 0, full.Width, full.Height), 1.0)];

        foreach (var (bounds, scaling) in monitors)
        {
            var clipped = bounds.Intersect(new PixelRect(0, 0, full.Width, full.Height));
            if (clipped.Width <= 0 || clipped.Height <= 0)
                continue;

            var image = new ScreenImage(clipped.Width, clipped.Height, CropPixels(full, clipped));
            var window = new OverlayWindow(session, image, clipped, scaling);
            if (clipped.Contains(pointer))
                session._windows.Insert(0, window); // o monitor com o mouse abre primeiro e recebe o foco
            else
                session._windows.Add(window);
        }

        return session._windows.Count > 0 ? session : null;
    }

    public void Show()
    {
        // Abre os outros monitores primeiro e por último o do mouse, para ele ficar com o foco
        for (var i = _windows.Count - 1; i >= 0; i--)
            _windows[i].Show();
    }

    internal void OnSelectionStarted(OverlayWindow source)
    {
        foreach (var window in _windows)
            if (window != source)
                window.ResetSelection();
    }

    internal void RequestScrollCapture(ScrollCaptureRequest request)
    {
        End();
        ScrollCaptureRequested?.Invoke(this, request);
    }

    internal void RequestRecord(RecordRequest request)
    {
        End();
        RecordRequested?.Invoke(this, request);
    }

    public void End()
    {
        if (_ended)
            return;
        _ended = true;

        foreach (var window in _windows)
            window.Close();
        Ended?.Invoke(this, EventArgs.Empty);
    }

    private static byte[] CropPixels(ScreenImage source, PixelRect rect)
    {
        var result = new byte[rect.Width * rect.Height * 4];
        for (var y = 0; y < rect.Height; y++)
            Buffer.BlockCopy(source.Pixels, (rect.Y + y) * source.Stride + rect.X * 4, result, y * rect.Width * 4, rect.Width * 4);
        return result;
    }
}

/// <summary>Área a capturar com rolagem e o monitor onde ela está (pixels globais).</summary>
public sealed record ScrollCaptureRequest(PixelRect Area, PixelRect Monitor, double Scaling);

/// <summary>Área a gravar em vídeo e o monitor onde ela está (pixels globais).</summary>
public sealed record RecordRequest(PixelRect Area, PixelRect Monitor, double Scaling);
