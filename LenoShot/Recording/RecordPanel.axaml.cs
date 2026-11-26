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
    public event EventHandler? FormatChanged;
    public event EventHandler? DiscardRequested;

    /// <summary>Alturas oferecidas (0 = original), na ordem em que o botão alterna.</summary>
    public static readonly int[] QualityOptions = [0, 1080, 720, 480];

    /// <summary>Altura máxima escolhida para o vídeo (0 = tamanho original da área).</summary>
    public int QualityHeight { get; private set; }

    /// <summary>Disparado quando o usuário termina de arrastar o painel (nova posição em pixels).</summary>
    public event EventHandler<PixelRect>? Moved;

    private bool _dragging;
    private PixelPoint _dragPointerStart;
    private PixelPoint _dragWindowStart;

    /// <summary>Formatos oferecidos, na ordem em que o botão alterna.</summary>
    public static readonly RecordFormat[] FormatOptions = [RecordFormat.Mp4, RecordFormat.WebM, RecordFormat.Gif];

    public RecordFormat Format { get; private set; }

    // Descartar pede um segundo clique para confirmar (evita perder a gravação sem querer)
    private readonly Avalonia.Threading.DispatcherTimer _discardTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _discardArmed;

    private bool _micWanted;
    private bool _systemWanted;

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
        DiscardButton.Click += (_, _) =>
        {
            if (_discardArmed)
            {
                DisarmDiscard();
                DiscardRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            _discardArmed = true;
            DiscardButton.Content = "Confirmar descarte";
            DiscardButton.Classes.Set("primary", true);
            _discardTimer.Start();
        };
        _discardTimer.Tick += (_, _) => DisarmDiscard();
        // Arrasta pelo fundo do painel. Botões tratam o próprio clique, então não iniciam o arraste.
        // Usa a posição global do mouse: a janela anda junto com o ponteiro, então a posição
        // relativa à janela não serve de referência.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || IsInsideComboBox(e.Source))
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

        QualityCombo.SelectionChanged += (_, _) =>
        {
            if (QualityCombo.SelectedIndex < 0 || QualityOptions[QualityCombo.SelectedIndex] == QualityHeight)
                return;
            QualityHeight = QualityOptions[QualityCombo.SelectedIndex];
            QualityChanged?.Invoke(this, EventArgs.Empty);
        };
        FormatCombo.SelectionChanged += (_, _) =>
        {
            if (FormatCombo.SelectedIndex < 0 || FormatOptions[FormatCombo.SelectedIndex] == Format)
                return;
            SetFormat(FormatOptions[FormatCombo.SelectedIndex]);
            FormatChanged?.Invoke(this, EventArgs.Empty);
        };
        MicButton.Click += (_, _) => SetAudio(!MicEnabled, SystemAudioEnabled);
        SystemButton.Click += (_, _) => SetAudio(MicEnabled, !SystemAudioEnabled);
    }

    private static bool IsInsideComboBox(object? source)
    {
        for (var v = source as Avalonia.Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            if (v is Avalonia.Controls.ComboBox)
                return true;
        return false;
    }

    private void DisarmDiscard()
    {
        _discardTimer.Stop();
        _discardArmed = false;
        DiscardButton.Content = "Descartar";
        DiscardButton.Classes.Set("primary", false);
    }

    /// <summary>Escolhe o formato. GIF não tem som: os botões de áudio ficam desligados enquanto ele estiver escolhido.</summary>
    public void SetFormat(RecordFormat format)
    {
        Format = format;
        FormatCombo.SelectedIndex = Math.Max(0, Array.IndexOf(FormatOptions, format));
        var gif = format == RecordFormat.Gif;
        MicButton.IsEnabled = SystemButton.IsEnabled = !gif;
        ApplyAudio();
    }

    public void SetQuality(int maxHeight)
    {
        QualityHeight = Array.IndexOf(QualityOptions, maxHeight) >= 0 ? maxHeight : 0;
        QualityCombo.SelectedIndex = Array.IndexOf(QualityOptions, QualityHeight);
    }

    /// <summary>Escolha de áudio do usuário (guardada mesmo com GIF, que grava sem som).</summary>
    public void SetAudio(bool mic, bool system)
    {
        _micWanted = mic;
        _systemWanted = system;
        ApplyAudio();
    }

    /// <summary>Áudio que será gravado de fato: o escolhido, ou nenhum no GIF.</summary>
    private void ApplyAudio()
    {
        var gif = Format == RecordFormat.Gif;
        MicEnabled = _micWanted && !gif;
        SystemAudioEnabled = _systemWanted && !gif;
        MicButton.Classes.Set("primary", MicEnabled);
        SystemButton.Classes.Set("primary", SystemAudioEnabled);
    }

    /// <summary>Escolha de áudio para salvar nas preferências (ignora o "sem som" do GIF).</summary>
    public bool MicWanted => _micWanted;
    public bool SystemAudioWanted => _systemWanted;

    public void SetReady(string detail)
    {
        Pulse.Fill = Red;
        StatusText.Text = "Pronto para gravar";
        DetailText.Text = detail;
    }

    /// <summary>Troca para os controles de gravação em andamento.</summary>
    public void SetRecording()
    {
        MicButton.IsVisible = SystemButton.IsVisible = QualityCombo.IsVisible = FormatCombo.IsVisible = CancelButton.IsVisible = StartButton.IsVisible = false;
        ZoomButton.IsVisible = DrawButton.IsVisible = PauseButton.IsVisible = DiscardButton.IsVisible = StopButton.IsVisible = true;
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

    public void SetTime(TimeSpan elapsed, bool paused, string? note, TimeSpan? limit = null)
    {
        StatusText.Text = (paused ? "Pausado  ·  " : "Gravando  ·  ") + FormatTime(elapsed)
            + (limit is { } max ? " / " + FormatTime(max) : "");
        DetailText.Text = note ?? DescribeAudio();
        PauseButton.Content = paused ? "Continuar" : "Pausar";
        Pulse.Fill = paused ? Yellow : Red;
        Pulse.Opacity = paused || (int)elapsed.TotalSeconds % 2 == 0 ? 1 : 0.35;
    }

    public void SetBusy(string status)
    {
        DisarmDiscard();
        ZoomButton.IsEnabled = DrawButton.IsEnabled = PauseButton.IsEnabled = DiscardButton.IsEnabled = StopButton.IsEnabled = false;
        StatusText.Text = status;
        DetailText.Text = "";
    }

    private string DescribeAudio() => Format == RecordFormat.Gif ? "GIF  ·  sem áudio" : (MicEnabled, SystemAudioEnabled) switch
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
