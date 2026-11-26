using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LenoShot.Capture;
using LenoShot.Overlay;

namespace LenoShot.Scroll;

/// <summary>
/// Conduz a captura com rolagem: marca a área com uma moldura, rola a página com a roda do
/// mouse (XTest), captura cada trecho quando a imagem estabiliza e emenda tudo.
/// O usuário pode pausar tirando o mouse da área, rolar manualmente, concluir ou cancelar.
/// </summary>
public sealed class ScrollCaptureController
{
    private const int MaxHeight = 16000;
    private const int FrameThickness = 3;

    private readonly ScrollCaptureRequest _request;
    private readonly List<FloatingWindow> _frame = [];
    private readonly ScrollPanel _panel = new();
    private bool _panelOverlapsArea;
    private bool _stop;
    private bool _cancelled;

    public event EventHandler? Finished;

    public ScrollCaptureController(ScrollCaptureRequest request)
    {
        _request = request;
        _panel.DoneRequested += (_, _) => _stop = true;
        _panel.CancelRequested += (_, _) => { _cancelled = true; _stop = true; };
    }

    public async Task RunAsync()
    {
        try
        {
            ShowChrome();
            await Task.Delay(450); // espera a sobreposição sumir e a moldura aparecer antes da 1ª captura

            var area = _request.Area;
            PointerInput.MovePointer(new PixelPoint(area.X + area.Width / 2, area.Y + area.Height / 2));

            // A tela muda logo depois que a sobreposição fecha (foco volta para a página, que
            // se redesenha). Só aceita o 1º quadro quando ele se mantém igual por um tempo.
            var first = await GrabStableAsync();
            for (var i = 0; i < 6 && first is not null; i++)
            {
                await Task.Delay(250);
                var again = await GrabStableAsync();
                if (again is null || again.Pixels.AsSpan().SequenceEqual(first.Pixels))
                    break;
                first = again;
            }
            if (first is null)
                return;

            var stitcher = new ScrollStitcher(first, _request.Scaling);
            var clicks = 3;
            var unchanged = 0;

            while (!_stop)
            {
                var pointerInside = area.Contains(PointerInput.GetPointer());
                if (pointerInside)
                    PointerInput.Scroll(clicks);

                await Task.Delay(pointerInside ? 180 : 250);
                if (_stop)
                    break;

                var frame = await GrabStableAsync();
                if (frame is null)
                    break;

                var result = await Task.Run(() => stitcher.Add(frame));
                if (result == StitchResult.NoChange)
                {
                    // Rolou e nada mudou duas vezes seguidas: chegou ao fim da página
                    if (pointerInside && ++unchanged >= 2)
                        break;
                }
                else
                {
                    unchanged = 0;
                    clicks = AdjustClicks(clicks, stitcher);
                }

                _panel.SetStatus(
                    pointerInside ? "Rolando a página…" : "Pausado",
                    $"{stitcher.Height:N0} px  ·  " + (pointerInside ? "tire o mouse da área para pausar" : "volte o mouse à área para continuar"));

                if (stitcher.Height >= MaxHeight)
                    break;
            }

            if (_cancelled)
                return;

            HideChrome();
            var image = await Task.Run(stitcher.Build);
            var window = new Editor.EditorWindow(image, _request.Monitor, _request.Scaling);
            window.Show();
            window.Activate();
        }
        finally
        {
            HideChrome();
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Ajusta a velocidade: passos grandes demais arriscam perder a sobreposição.</summary>
    private static int AdjustClicks(int clicks, ScrollStitcher stitcher)
    {
        var ratio = (double)stitcher.LastScrollPixels / Math.Max(1, stitcher.MovingBandHeight);
        if (ratio > 0.7) return Math.Max(1, clicks - 1);
        if (ratio < 0.35) return Math.Min(10, clicks + 1);
        return clicks;
    }

    /// <summary>Captura até duas imagens seguidas ficarem iguais (animações de rolagem suave).</summary>
    private async Task<ScreenImage?> GrabStableAsync()
    {
        var previous = Grab();
        for (var i = 0; i < 15 && previous is not null; i++)
        {
            await Task.Delay(70);
            var current = Grab();
            if (current is null || current.Pixels.AsSpan().SequenceEqual(previous.Pixels))
                return current ?? previous;
            previous = current;
        }
        return previous;
    }

    private ScreenImage? Grab()
    {
        // Se o painel teve de ficar sobre a área, some durante a captura
        if (!_panelOverlapsArea)
            return ScreenCapture.CaptureRect(_request.Area);

        _panel.Opacity = 0;
        try { return ScreenCapture.CaptureRect(_request.Area); }
        finally { _panel.Opacity = 1; }
    }

    // ───────────── Moldura e painel ─────────────

    private void ShowChrome()
    {
        var a = _request.Area;
        var t = FrameThickness;
        var accent = new SolidColorBrush(LenoShot.Services.AccentTheme.Color);

        // Quatro faixas finas em volta (por fora) da área, para não aparecerem na captura
        PixelRect[] sides =
        [
            new(a.X - t, a.Y - t, a.Width + 2 * t, t),
            new(a.X - t, a.Bottom, a.Width + 2 * t, t),
            new(a.X - t, a.Y, t, a.Height),
            new(a.Right, a.Y, t, a.Height),
        ];
        foreach (var side in sides)
        {
            var w = new FloatingWindow { Background = accent, ShowActivated = false, Title = "Leno Shot — moldura" };
            w.PlaceAt(side, _request.Scaling);
            w.Show();
            _frame.Add(w);
        }

        var panelRect = PlacePanel();
        _panel.PlaceAt(panelRect, _request.Scaling);
        _panel.Show();
    }

    private PixelRect PlacePanel()
    {
        var s = _request.Scaling;
        var size = new PixelSize((int)(440 * s), (int)(52 * s));
        var gap = (int)(10 * s);
        var a = _request.Area;
        var m = _request.Monitor;

        var x = Math.Clamp(a.X + (a.Width - size.Width) / 2, m.X + gap, m.Right - size.Width - gap);
        if (a.Bottom + gap + size.Height <= m.Bottom - gap)
            return new PixelRect(new PixelPoint(x, a.Bottom + gap), size);
        if (a.Y - gap - size.Height >= m.Y + gap)
            return new PixelRect(new PixelPoint(x, a.Y - gap - size.Height), size);

        // Área ocupa a tela toda: o painel fica por cima e se esconde a cada captura
        _panelOverlapsArea = true;
        return new PixelRect(new PixelPoint(x, m.Bottom - size.Height - gap * 3), size);
    }

    private bool _chromeHidden;

    private void HideChrome()
    {
        if (_chromeHidden)
            return;
        _chromeHidden = true;
        foreach (var w in _frame)
            w.Close();
        _frame.Clear();
        _panel.Close();
    }
}
