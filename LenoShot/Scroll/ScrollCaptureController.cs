using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LenoShot.Capture;
using LenoShot.Overlay;

namespace LenoShot.Scroll;

/// <summary>
/// Conduz a captura com rolagem: marca a área com uma moldura, rola a página devagar com a roda
/// do mouse (1 clique por vez), captura cada trecho quando a imagem assenta e emenda tudo.
/// Termina ao clicar em Parar, chegar ao fim da página ou atingir a altura máxima.
/// </summary>
public sealed class ScrollCaptureController
{
    /// <summary>Altura máxima da imagem final; ao chegar nela a rolagem termina sozinha.</summary>
    private const int MaxHeight = 5000;
    private const int FrameThickness = 3;

    private readonly ScrollCaptureRequest _request;
    private readonly List<FloatingWindow> _frame = [];
    private readonly ScrollPanel _panel = new();
    private bool _panelOverlapsArea;
    private PixelRect _panelRect;
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
            await Task.Delay(250); // espera a sobreposição sumir e a moldura aparecer antes da 1ª captura

            var area = _request.Area;
            // Se este 1º quadro pegar a tela em transição, o emendador recomeça sozinho quando
            // ela assentar; por isso não é preciso esperar aqui.
            var first = await GrabStableAsync(6);
            if (first is null)
                return;

            var stitcher = new ScrollStitcher(first, _request.Scaling);
            UpdatePanel(stitcher);

            // Rolagem automática lenta: 1 clique por vez, esperando a imagem assentar antes de
            // capturar. Passos pequenos garantem sobreposição entre quadros. Se o mouse sai da
            // área (ex.: para clicar em "Parar"), pausa; o usuário também pode rolar à mão.
            PointerInput.MovePointer(new PixelPoint(area.X + area.Width / 2, area.Y + area.Height / 2));
            var unchanged = 0;
            var lost = 0;
            var skipScroll = false;

            while (!_stop)
            {
                // Só pausa com o mouse sobre o painel (para dar tempo de clicar em "Parar"). Com o
                // mouse fora da área, leva o ponteiro até lá só para girar a roda e devolve.
                var pointer = PointerInput.GetPointer();
                var inside = !_panelRect.Contains(pointer);
                if (inside && !skipScroll)
                {
                    if (area.Contains(pointer))
                        PointerInput.Scroll(1);
                    else
                    {
                        PointerInput.MovePointer(new PixelPoint(area.X + area.Width / 2, area.Y + area.Height / 2));
                        PointerInput.Scroll(1);
                        PointerInput.MovePointer(pointer);
                    }
                }
                skipScroll = false;

                await Task.Delay(inside ? 140 : 200);
                if (_stop)
                    break;

                var frame = await GrabStableAsync(inside ? 8 : 2);
                if (frame is null)
                    break;

                var result = await Task.Run(() => stitcher.Add(frame));
                switch (result)
                {
                    case StitchResult.NoChange:
                        // Rolou e nada mudou várias vezes seguidas: chegou ao fim da página
                        if (inside && ++unchanged >= 4)
                            _stop = true;
                        continue;

                    case StitchResult.Lost:
                        unchanged = 0;
                        lost++;
                        if (lost == 1)
                        {
                            // Talvez a tela ainda esteja assentando: tenta de novo sem rolar
                            skipScroll = true;
                            continue;
                        }
                        // Não reconhece a imagem (foto que troca sozinha, por exemplo): assume que
                        // a página andou o mesmo passo anterior e segue, em vez de travar
                        if (inside && stitcher.AddAssumingStep(frame) != StitchResult.Lost)
                        {
                            lost = 0;
                            UpdatePanel(stitcher, inside);
                            continue;
                        }
                        if (lost >= 6)
                        {
                            _stop = true; // não consegue seguir: finaliza com o que já tem
                            continue;
                        }
                        _panel.SetStatus("Reencontrando a posição…", "aguarde um instante",
                            Math.Min(100, (int)(stitcher.Height * 100L / MaxHeight)));
                        continue;
                }

                unchanged = 0;
                lost = 0;
                UpdatePanel(stitcher, inside);
                if (stitcher.Height >= MaxHeight)
                {
                    _panel.SetStatus("Limite atingido", $"{MaxHeight:N0} px  ·  finalizando…", 100);
                    break;
                }
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

    private void UpdatePanel(ScrollStitcher stitcher, bool scrolling = true)
    {
        var percent = Math.Min(100, (int)(stitcher.Height * 100L / MaxHeight));
        _panel.SetStatus(
            scrolling ? "Rolando a página…" : "Pausado",
            $"{stitcher.Height:N0} / {MaxHeight:N0} px" + (scrolling ? "" : "  ·  volte o mouse à área para continuar"),
            percent);
    }

    /// <summary>Captura até duas imagens seguidas ficarem iguais (animações de rolagem suave).</summary>
    private async Task<ScreenImage?> GrabStableAsync(int maxTries)
    {
        // Painel sobre a área: precisa alternar a opacidade na thread da interface
        if (_panelOverlapsArea)
        {
            var previous = Grab();
            for (var i = 0; i < maxTries && previous is not null; i++)
            {
                await Task.Delay(70);
                var current = Grab();
                if (current is null || current.Pixels.AsSpan().SequenceEqual(previous.Pixels))
                    return current ?? previous;
                previous = current;
            }
            return previous;
        }

        // Caso comum: captura e comparação fora da thread da interface (evita travadas)
        return await Task.Run(async () =>
        {
            var previous = ScreenCapture.CaptureRect(_request.Area);
            for (var i = 0; i < maxTries && previous is not null; i++)
            {
                await Task.Delay(60);
                var current = ScreenCapture.CaptureRect(_request.Area);
                if (current is null || current.Pixels.AsSpan().SequenceEqual(previous.Pixels))
                    return current ?? previous;
                previous = current;
            }
            return previous;
        });
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
            var w = new FloatingWindow { Background = accent, ShowActivated = false, Title = "LenoShot — moldura" };
            w.PlaceAt(side, _request.Scaling);
            w.Show();
            _frame.Add(w);
        }

        var panelRect = PlacePanel();
        _panelRect = panelRect;
        _panel.PlaceAt(panelRect, _request.Scaling);
        _panel.Show();
    }

    private PixelRect PlacePanel()
    {
        var s = _request.Scaling;
        var size = new PixelSize((int)(500 * s), (int)(60 * s));
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
