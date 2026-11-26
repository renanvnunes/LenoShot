using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using LenoShot.Overlay;
using LenoShot.Scroll;
using LenoShot.Services;

namespace LenoShot.Recording;

/// <summary>
/// Conduz a gravação de uma área: moldura em volta, painel com opções de áudio, pausar e parar.
/// A moldura e o painel ficam fora da área gravada; se o painel não couber fora (área em tela
/// cheia), ele some ao começar e a gravação é controlada pela bandeja.
/// </summary>
public sealed class RecordController
{
    private const int FrameThickness = 2; // moldura fina e discreta, por fora da área gravada
    private const int PanelWidth = 800; // cabe: áudio, qualidade, formato, cancelar e gravar

    private readonly RecordRequest _request;
    private readonly List<FloatingWindow> _frame = [];
    private readonly RecordPanel _panel = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ScreenRecorder? _recorder;
    private LiveDrawWindow? _draw;
    private ZoomWindow? _zoom;
    private PixelRect _panelBase;        // posição/tamanho do painel fora do modo desenho
    private PixelRect _panelMonitor;     // monitor onde o painel está
    private double _panelScale = 1;
    private bool _panelGrowsUp;          // no modo desenho o painel cresce para cima (senão, para baixo)
    private bool _panelOverlapsArea;
    private bool _busy;
    private bool _closed;
    private string? _note;

    public event EventHandler? Finished;
    /// <summary>Muda quando começa, pausa ou termina — para a bandeja atualizar o menu.</summary>
    public event EventHandler? StateChanged;

    public bool IsRecording => _recorder is not null;
    public bool IsPaused => _recorder?.IsPaused == true;
    public bool IsDrawing => _draw?.IsDrawing == true;
    public bool IsZooming => _zoom?.IsOn == true;

    public RecordController(RecordRequest request)
    {
        _request = request;
        var settings = AppSettings.Current;
        _panel.SetAudio(settings.RecordMic, settings.RecordSystemAudio);
        _panel.SetQuality(settings.RecordMaxHeight);
        _panel.SetFormat(ParseFormat(settings.RecordFormat));
        UpdateReadyDetail();
        _panel.QualityChanged += (_, _) => UpdateReadyDetail();
        _panel.FormatChanged += (_, _) => UpdateReadyDetail();
        _panel.DiscardRequested += async (_, _) => await CancelRecordingAsync();

        _panel.StartRequested += async (_, _) => await StartAsync();
        _panel.CancelRequested += (_, _) => Close();
        _panel.PauseToggled += async (_, _) => await TogglePauseAsync();
        _panel.StopRequested += async (_, _) => await StopAsync();
        _panel.DrawToggled += (_, _) => ToggleDrawing();
        _panel.ZoomToggled += (_, _) => ToggleZoom();
        _panel.Moved += (_, rect) => OnPanelMoved(rect);
        _timer.Tick += (_, _) => UpdateTime();
    }

    /// <summary>
    /// Esconde as faixas da moldura e o painel que, na posição real (depois do gerenciador de
    /// janelas), cruzam a área gravada. O painel escondido passa a ser controlado pela bandeja.
    /// </summary>
    private void HideIntrudingWindows()
    {
        var area = _request.Area;

        // Diagnóstico: LENOSHOT_DEBUG=1 mostra onde as janelas pedidas realmente ficaram
        if (Environment.GetEnvironmentVariable("LENOSHOT_DEBUG") == "1")
        {
            Console.Error.WriteLine($"[LenoShot] área={area} monitor={_request.Monitor} escala={_request.Scaling}");
            foreach (var w in _frame)
                Console.Error.WriteLine($"[LenoShot] moldura pedida={w.CurrentRect} real={w.ActualRect()} visível={w.IsVisible}");
            Console.Error.WriteLine($"[LenoShot] painel base={_panelBase} real={_panel.ActualRect()} visível={_panel.IsVisible} cruza={_panelOverlapsArea}");
        }
        foreach (var w in _frame)
            if (w.ActualRect() is { } r && r.Intersects(area))
                w.Hide();

        if (!_panelOverlapsArea && _panel.ActualRect() is { } p && p.Intersects(area))
        {
            _panelOverlapsArea = true;
            _panel.Hide();
        }
    }

    /// <summary>Mostra o tamanho do vídeo: o da área ou, se a qualidade reduz, "área → vídeo".</summary>
    private void UpdateReadyDetail()
    {
        var a = _request.Area;
        var max = _panel.QualityHeight;
        var gif = _panel.Format == RecordFormat.Gif;
        // O GIF ainda é limitado a uma altura menor, para não pesar
        if (gif && (max == 0 || max > ScreenRecorder.GifMaxHeight))
            max = ScreenRecorder.GifMaxHeight;

        var size = max > 0 && a.Height > max
            ? $"{a.Width} × {a.Height} → {(int)Math.Round(a.Width * (double)max / a.Height / 2) * 2} × {max} px"
            : $"{a.Width} × {a.Height} px";
        _panel.SetReady(gif ? $"{size}  ·  GIF até {GifLimit.TotalSeconds:0}s, sem áudio" : size);
    }

    private static TimeSpan GifLimit => TimeSpan.FromSeconds(Math.Clamp(AppSettings.Current.GifMaxSeconds, 1, 120));

    public static RecordFormat ParseFormat(string? value) => value?.ToLowerInvariant() switch
    {
        "webm" => RecordFormat.WebM,
        "gif" => RecordFormat.Gif,
        _ => RecordFormat.Mp4,
    };

    public void Show()
    {
        if (!ScreenRecorder.IsFfmpegAvailable())
        {
            SystemIntegration.Notify("ffmpeg não encontrado", OperatingSystem.IsWindows()
                ? "Instale o ffmpeg para gravar a tela (winget install Gyan.FFmpeg) ou coloque o ffmpeg.exe junto do LenoShot"
                : "Instale o ffmpeg para gravar a tela (sudo apt install ffmpeg)");
            Close();
            return;
        }

        var a = _request.Area;
        var t = FrameThickness;
        // Cor de destaque do app, translúcida: marca a área sem chamar atenção
        var accent = new SolidColorBrush(AccentTheme.WithAlpha(170));

        // Quatro faixas finas por fora da área, para não aparecerem no vídeo
        PixelRect[] sides =
        [
            new(a.X - t, a.Y - t, a.Width + 2 * t, t),
            new(a.X - t, a.Bottom, a.Width + 2 * t, t),
            new(a.X - t, a.Y, t, a.Height),
            new(a.Right, a.Y, t, a.Height),
        ];
        foreach (var side in sides)
        {
            var w = new FloatingWindow
            {
                Background = accent,
                TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                ShowActivated = false,
                Title = "LenoShot — moldura",
            };
            w.UseOverrideRedirect(); // sem isso o GNOME empurra a faixa para dentro da área
            w.PlaceAt(side, _request.Scaling);
            w.Show();
            _frame.Add(w);
        }

        // O GNOME pode empurrar as faixas para dentro da tela (borda do monitor, barra do sistema):
        // depois de posicionadas, esconde as que caírem dentro da área, senão entrariam no vídeo
        DispatcherTimer.RunOnce(HideIntrudingWindows, TimeSpan.FromMilliseconds(500));

        _panelScale = _request.Scaling;
        _panelMonitor = _request.Monitor;
        _panelBase = PlacePanel();

        // Área ocupa o monitor inteiro: o painel vai para outro monitor, se houver, em vez de sumir
        if (_panelOverlapsArea && FindOtherMonitor() is { } other)
        {
            _panelScale = other.Scaling;
            _panelMonitor = other.Bounds;
            var size = new PixelSize((int)(PanelWidth * _panelScale), (int)(52 * _panelScale));
            var gap = (int)(10 * _panelScale);
            _panelBase = new PixelRect(other.Bounds.X + (other.Bounds.Width - size.Width) / 2,
                other.Bounds.Bottom - size.Height - gap * 6, size.Width, size.Height);
            _panelGrowsUp = true;
            _panelOverlapsArea = false;
        }
        _panel.PlaceAt(_panelBase, _panelScale);
        _panel.Show();

        // Modo desenho: a janela só aparece (congelando a área) quando o desenho é ligado
        _draw = new LiveDrawWindow(a, _request.Scaling);
        _draw.DrawingChanged += (_, _) => OnDrawingChanged();

        // Zoom: também só aparece (congelando a área) quando ligado
        _zoom = new ZoomWindow(a, _request.Scaling);
        _zoom.ZoomChanged += (_, _) =>
        {
            _panel.SetZoom(_zoom.IsOn);
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Liga/desliga o modo desenho (só durante a gravação).</summary>
    public void ToggleDrawing() => _ = SwitchModeAsync(drawing: true);

    /// <summary>Liga/desliga o zoom no mouse (só durante a gravação).</summary>
    public void ToggleZoom() => _ = SwitchModeAsync(drawing: false);

    /// <summary>
    /// Desenho e zoom congelam a área, então só um fica ligado por vez. Ao trocar de um para o
    /// outro, espera a janela anterior sumir, senão o novo instantâneo pegaria a janela antiga.
    /// </summary>
    private async Task SwitchModeAsync(bool drawing)
    {
        if (_recorder is null || _draw is null || _zoom is null)
            return;

        var wantOn = drawing ? !_draw.IsDrawing : !_zoom.IsOn;
        var otherWasOn = drawing ? _zoom.IsOn : _draw.IsDrawing;

        if (wantOn && otherWasOn)
        {
            if (drawing) _zoom.Dismiss(); else _draw.SetDrawing(false);
            await Task.Delay(150);
        }

        if (drawing)
            _draw.SetDrawing(wantOn);
        else
            _zoom.SetZoom(wantOn);
    }

    private void OnDrawingChanged()
    {
        var on = IsDrawing;
        _panel.SetDrawing(on);

        // As ferramentas de desenho ficam dentro do painel (que sempre está à vista, fora da área)
        _panel.ShowTools(on ? _draw!.Toolbar : null);
        _panel.PlaceAt(on ? DrawingPanelRect() : _panelBase, _panelScale);

        // Painel escondido (área em tela cheia, sem outro monitor): aparece só durante o desenho,
        // dentro da área — a tela congelada do vídeo mostra a barra, melhor que ficar sem ferramentas
        if (_panelOverlapsArea)
        {
            if (on)
                _panel.Show();
            else
                _panel.Hide();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// O usuário arrastou o painel: a nova posição vira a base. O monitor, a escala e o sentido em
    /// que o painel cresce no modo desenho são recalculados (pode ter mudado de monitor).
    /// </summary>
    private void OnPanelMoved(PixelRect rect)
    {
        var screen = _panel.Screens.ScreenFromPoint(rect.Center) ?? _panel.Screens.Primary;
        if (screen is null)
            return;

        _panelMonitor = screen.Bounds;
        _panelScale = screen.Scaling;

        var baseW = (int)(PanelWidth * _panelScale);
        var baseH = (int)(52 * _panelScale);
        var tall = (int)(108 * _panelScale);
        var gap = (int)(10 * _panelScale);

        // No modo desenho o painel está ampliado: volta ao tamanho base, mantendo o canto de ancoragem
        var drawing = IsDrawing;
        var x = drawing ? rect.X + (rect.Width - baseW) / 2 : rect.X;
        var y = drawing && _panelGrowsUp ? rect.Bottom - baseH : rect.Y;

        _panelBase = new PixelRect(x, y, baseW, baseH);
        _panelGrowsUp = y + tall > screen.Bounds.Bottom - gap;
        _panelOverlapsArea = _panelBase.Intersects(_request.Area);

        if (drawing)
            _panel.PlaceAt(DrawingPanelRect(), _panelScale);
    }

    /// <summary>Painel ampliado, com a barra de ferramentas embaixo (ou em cima, se ele cresce para cima).</summary>
    private PixelRect DrawingPanelRect()
    {
        var s = _panelScale;
        var gap = (int)(10 * s);
        var w = (int)(760 * s);
        var h = (int)(108 * s);
        var m = _panelMonitor;
        var cx = _panelBase.X + _panelBase.Width / 2;
        var x = Math.Clamp(cx - w / 2, m.X + gap, Math.Max(m.X + gap, m.Right - w - gap));
        var y = _panelGrowsUp ? _panelBase.Bottom - h : _panelBase.Y;
        return new PixelRect(x, y, w, h);
    }

    /// <summary>Outro monitor, que não cruza a área gravada (para o painel quando a área é a tela toda).</summary>
    private Screen? FindOtherMonitor()
    {
        var probe = new Avalonia.Controls.Window();
        return probe.Screens.All.FirstOrDefault(s => !s.Bounds.Intersects(_request.Area));
    }

    // ───────────── Ações ─────────────

    private async Task StartAsync()
    {
        if (_busy || _recorder is not null)
            return;
        _busy = true;

        var settings = AppSettings.Current;
        settings.RecordMic = _panel.MicWanted;
        settings.RecordSystemAudio = _panel.SystemAudioWanted;
        settings.RecordMaxHeight = _panel.QualityHeight;
        settings.RecordFormat = ScreenRecorder.Extension(_panel.Format);
        settings.Save();

        _panel.SetRecording();
        _panel.SetBusy("Iniciando…");

        // O painel dentro da área apareceria no vídeo: esconde antes de gravar
        if (_panelOverlapsArea)
            _panel.Hide();
        await Task.Delay(150);

        // Última conferência antes de gravar: nada de moldura nem painel dentro da área
        HideIntrudingWindows();

        var format = _panel.Format;
        var recorder = new ScreenRecorder(_request.Area, settings.RecordFps, _panel.MicEnabled, _panel.SystemAudioEnabled,
            _panel.QualityHeight, format, format == RecordFormat.Gif ? GifLimit : null);
        if (!await recorder.StartAsync())
        {
            SystemIntegration.Notify("Não foi possível gravar", Truncate(recorder.LastLog));
            _busy = false;
            Close();
            return;
        }

        _recorder = recorder;
        _note = recorder.Warning;
        _panel.ZoomButton.IsEnabled = _panel.DrawButton.IsEnabled = _panel.PauseButton.IsEnabled = _panel.DiscardButton.IsEnabled = _panel.StopButton.IsEnabled = true;
        _timer.Start();
        UpdateTime();
        _busy = false;
        if (Environment.GetEnvironmentVariable("LENOSHOT_DEBUG") == "1")
            DispatcherTimer.RunOnce(() => Console.Error.WriteLine(
                $"[LenoShot] gravando: painel real={_panel.ActualRect()} visível={_panel.IsVisible} cruza={_panelOverlapsArea}"),
                TimeSpan.FromSeconds(1.5));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task TogglePauseAsync()
    {
        if (_recorder is null || _busy)
            return;
        _busy = true;
        try
        {
            if (_recorder.IsPaused)
            {
                if (!await _recorder.ResumeAsync())
                    SystemIntegration.Notify("Não foi possível continuar", Truncate(_recorder.LastLog));
            }
            else
            {
                await _recorder.PauseAsync();
            }
        }
        finally
        {
            _busy = false;
        }
        UpdateTime();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAsync()
    {
        if (_recorder is null || _busy)
            return;
        _busy = true;
        _timer.Stop();

        var recorder = _recorder;
        _draw?.SetDrawing(false);
        _zoom?.Dismiss();
        _panel.Show();
        _panel.SetBusy(recorder.Format == RecordFormat.Mp4 ? "Salvando o vídeo…" : $"Convertendo para {ScreenRecorder.Extension(recorder.Format).ToUpperInvariant()}…");

        var file = await recorder.FinishAsync();
        if (file is null)
            SystemIntegration.Notify("Gravação vazia", Truncate(recorder.LastLog));
        else if (AppSettings.Current.NotifyOnSave)
            SystemIntegration.Notify("Gravação salva", file, revealPath: file);

        _recorder = null;
        _busy = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
        Close();
    }

    /// <summary>Descarta a gravação em andamento: nada é salvo.</summary>
    public async Task CancelRecordingAsync()
    {
        if (_recorder is null || _busy)
            return;
        _busy = true;
        _timer.Stop();

        var recorder = _recorder;
        _draw?.SetDrawing(false);
        _zoom?.Dismiss();
        _panel.SetBusy("Descartando…");

        await recorder.CancelAsync();
        SystemIntegration.Notify("Gravação descartada", "Nenhum arquivo foi salvo");

        _recorder = null;
        _busy = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void UpdateTime()
    {
        if (_recorder is null)
            return;
        _panel.SetTime(_recorder.Elapsed, _recorder.IsPaused, _note, _recorder.MaxDuration);

        // GIF: para sozinho ao chegar na duração máxima
        if (_recorder.MaxDuration is { } max && _recorder.Elapsed >= max && !_busy)
            _ = StopAsync();
    }

    private void Close()
    {
        if (_closed)
            return;
        _closed = true;
        _timer.Stop();
        foreach (var w in _frame)
            w.Close();
        _frame.Clear();
        _draw?.Close();
        _zoom?.Close();
        _panel.Close();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    // ───────────── Posição do painel ─────────────

    private PixelRect PlacePanel()
    {
        var s = _request.Scaling;
        var size = new PixelSize((int)(PanelWidth * s), (int)(52 * s));
        var tall = (int)(108 * s); // altura do painel no modo desenho (com a barra de ferramentas)
        var gap = (int)(10 * s);
        var a = _request.Area;
        var m = _request.Monitor;

        var x = Math.Clamp(a.X + (a.Width - size.Width) / 2, m.X + gap, m.Right - size.Width - gap);
        if (a.Bottom + gap + tall <= m.Bottom - gap)
            return new PixelRect(new PixelPoint(x, a.Bottom + gap), size);
        if (a.Y - gap - tall >= m.Y + gap)
        {
            _panelGrowsUp = true;
            return new PixelRect(new PixelPoint(x, a.Y - gap - size.Height), size);
        }

        _panelOverlapsArea = true;
        _panelGrowsUp = true;
        return new PixelRect(new PixelPoint(x, m.Bottom - size.Height - gap * 3), size);
    }

    private static string Truncate(string text) =>
        string.IsNullOrWhiteSpace(text) ? "Verifique o ffmpeg e o áudio do sistema" :
        text.Length > 200 ? text[^200..] : text;
}
