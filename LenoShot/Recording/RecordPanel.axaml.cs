using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using LenoShot.Capture;
using LenoShot.Scroll;

namespace LenoShot.Recording;

/// <summary>Painel de controle da gravação: opções de áudio antes de começar; pausar e parar durante.</summary>
public partial class RecordPanel : FloatingWindow
{
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF453A"));
    private static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#FFD60A"));

    public event EventHandler? StartRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? PauseToggled;
    public event EventHandler? StopRequested;
    public event EventHandler? DrawToggled;
    public event EventHandler? ZoomToggled;
    public event EventHandler? QualityChanged;

    /// <summary>Alturas oferecidas (0 = original), na ordem em que o botão alterna.</summary>
    public static readonly int[] QualityOptions = [0, 1080, 720, 480];

    /// <summary>Altura máxima escolhida para o vídeo (0 = tamanho original da área).</summary>
    public int QualityHeight { get; private set; }

    /// <summary>Disparado quando o usuário termina de arrastar o painel (nova posição em pixels).</summary>
    public event EventHandler<PixelRect>? Moved;

    private bool _dragging;
    private PixelPoint _dragPointerStart;
    private PixelPoint _dragWindowStart;

    public bool MicEnabled { get; private set; }
    public bool SystemAudioEnabled { get; private set; }

    public RecordPanel()
    {
        InitializeComponent();
        UseOverrideRedirect(); // o GNOME não pode empurrar o painel para dentro da área gravada

        StartButton.Click += (_, _) => StartRequested?.Invoke(this, EventArgs.Empty);
        CancelButton.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        PauseButton.Click += (_, _) => PauseToggled?.Invoke(this, EventArgs.Empty);
        StopButton.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        DrawButton.Click += (_, _) => DrawToggled?.Invoke(this, EventArgs.Empty);
        ZoomButton.Click += (_, _) => ZoomToggled?.Invoke(this, EventArgs.Empty);
        // Arrasta pelo fundo do painel. Botões tratam o próprio clique, então não iniciam o arraste.
        // Usa a posição global do mouse: a janela anda junto com o ponteiro, então a posição
        // relativa à janela não serve de referência.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            _dragging = true;
            _dragPointerStart = X11Input.GetPointer();
            _dragWindowStart = CurrentRect.Position;
            e.Pointer.Capture(this);
        });
        AddHandler(PointerMovedEvent, (_, _) =>
        {
            if (!_dragging)
                return;
            var pointer = X11Input.GetPointer();
            MoveTo(_dragWindowStart + (pointer - _dragPointerStart));
        });
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (!_dragging)
                return;
            _dragging = false;
            e.Pointer.Capture(null);
            Moved?.Invoke(this, CurrentRect);
        });

        QualityButton.Click += (_, _) =>
        {
            var next = (Array.IndexOf(QualityOptions, QualityHeight) + 1) % QualityOptions.Length;
            SetQuality(QualityOptions[next]);
            QualityChanged?.Invoke(this, EventArgs.Empty);
        };
        MicButton.Click += (_, _) => SetAudio(!MicEnabled, SystemAudioEnabled);
        SystemButton.Click += (_, _) => SetAudio(MicEnabled, !SystemAudioEnabled);
    }

    public void SetQuality(int maxHeight)
    {
        QualityHeight = Array.IndexOf(QualityOptions, maxHeight) >= 0 ? maxHeight : 0;
        QualityButton.Content = QualityHeight == 0 ? "Original" : $"{QualityHeight}p";
    }

    public void SetAudio(bool mic, bool system)
    {
        MicEnabled = mic;
        SystemAudioEnabled = system;
        MicButton.Classes.Set("primary", mic);
        SystemButton.Classes.Set("primary", system);
    }

    public void SetReady(string detail)
    {
        Pulse.Fill = Red;
        StatusText.Text = "Pronto para gravar";
        DetailText.Text = detail;
    }

    /// <summary>Troca para os controles de gravação em andamento.</summary>
    public void SetRecording()
    {
        MicButton.IsVisible = SystemButton.IsVisible = QualityButton.IsVisible = CancelButton.IsVisible = StartButton.IsVisible = false;
        ZoomButton.IsVisible = DrawButton.IsVisible = PauseButton.IsVisible = StopButton.IsVisible = true;
        Pulse.Fill = Red;
    }

    public void SetZoom(bool on) => ZoomButton.Classes.Set("primary", on);

    public void SetDrawing(bool on) => DrawButton.Classes.Set("primary", on);

    /// <summary>Mostra a barra de ferramentas do desenho dentro do painel (null a esconde).</summary>
    public void ShowTools(Overlay.AnnotationToolbar? toolbar)
    {
        if (toolbar?.Content is Avalonia.Controls.Border border)
        {
            // Já está dentro do painel: sem fundo, borda e sombra próprios
            border.Background = Brushes.Transparent;
            border.BorderThickness = default;
            border.BoxShadow = default;
            border.Margin = default;
            border.Padding = default;
        }
        ToolbarHost.Content = toolbar;
        ToolbarHost.IsVisible = toolbar is not null;
    }

    public void SetTime(TimeSpan elapsed, bool paused, string? note)
    {
        StatusText.Text = (paused ? "Pausado  ·  " : "Gravando  ·  ") + FormatTime(elapsed);
        DetailText.Text = note ?? DescribeAudio();
        PauseButton.Content = paused ? "Continuar" : "Pausar";
        Pulse.Fill = paused ? Yellow : Red;
        Pulse.Opacity = paused || (int)elapsed.TotalSeconds % 2 == 0 ? 1 : 0.35;
    }

    public void SetBusy(string status)
    {
        ZoomButton.IsEnabled = DrawButton.IsEnabled = PauseButton.IsEnabled = StopButton.IsEnabled = false;
        StatusText.Text = status;
        DetailText.Text = "";
    }

    private string DescribeAudio() => (MicEnabled, SystemAudioEnabled) switch
    {
        (true, true) => "Microfone e áudio do PC",
        (true, false) => "Só microfone",
        (false, true) => "Só áudio do PC",
        _ => "Sem áudio",
    };

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (StartButton.IsVisible && e.Key == Key.Escape)
            CancelRequested?.Invoke(this, EventArgs.Empty);
        else if (StartButton.IsVisible && e.Key == Key.Enter)
            StartRequested?.Invoke(this, EventArgs.Empty);
    }
}
