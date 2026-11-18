using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using LenoShot.Services;

namespace LenoShot.Overlay;

/// <summary>
/// Barra de ferramentas de anotação, usada na captura e no editor da captura com rolagem.
/// Controla ferramenta, cor e desfazer de um <see cref="SelectionCanvas"/> e trata os atalhos de teclado.
/// </summary>
public partial class AnnotationToolbar : UserControl
{
    private static readonly Color[] Palette =
    [
        Color.Parse("#FF453A"), // vermelho
        Color.Parse("#FFD60A"), // amarelo
        Color.Parse("#32D74B"), // verde
        Color.Parse("#0A84FF"), // azul
        Color.Parse("#FFFFFF"), // branco
        Color.Parse("#1C1C1E"), // preto
    ];

    private readonly Dictionary<Tool, Button> _toolButtons;
    private SelectionCanvas? _canvas;

    public event EventHandler? SaveRequested;
    public event EventHandler? CopyRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? ScrollCaptureRequested;
    public event EventHandler? RecordRequested;

    public AnnotationToolbar()
    {
        InitializeComponent();

        _toolButtons = new()
        {
            [Tool.Pen] = PenButton,
            [Tool.Arrow] = ArrowButton,
            [Tool.Rectangle] = RectangleButton,
            [Tool.Ellipse] = EllipseButton,
            [Tool.Counter] = CounterButton,
            [Tool.Marker] = MarkerButton,
            [Tool.Text] = TextButton,
            [Tool.Pixelate] = PixelateButton,
        };
        foreach (var (tool, button) in _toolButtons)
            button.Click += (_, _) => SelectTool(tool);

        UndoButton.Click += (_, _) => _canvas?.Undo();
        SaveButton.Click += (_, _) => SaveRequested?.Invoke(this, EventArgs.Empty);
        CopyButton.Click += (_, _) => CopyRequested?.Invoke(this, EventArgs.Empty);
        CloseButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        ScrollButton.Click += (_, _) => ScrollCaptureRequested?.Invoke(this, EventArgs.Empty);
        RecordButton.Click += (_, _) => RecordRequested?.Invoke(this, EventArgs.Empty);
        ClearButton.Click += (_, _) => _canvas?.ClearAnnotations();
    }

    /// <summary>
    /// Barra do desenho ao vivo (durante a gravação): sem salvar/copiar/pixelizar, com "apagar tudo",
    /// e o botão de fechar sai do modo desenho.
    /// </summary>
    public void UseLiveMode()
    {
        ShowScrollButton = false;
        SaveButton.IsVisible = CopyButton.IsVisible = false;
        ClearButton.IsVisible = true;
        ThicknessDivider.IsVisible = ThicknessSlider.IsVisible = true;
        _liveMode = true;
        ToolTip.SetTip(CloseButton, "Sair do modo desenho  (Esc)");
    }

    private bool _liveMode;

    /// <summary>Mostra os botões de captura com rolagem e de gravação (só fazem sentido na sobreposição).</summary>
    public bool ShowScrollButton
    {
        get => ScrollButton.IsVisible;
        set => ScrollButton.IsVisible = RecordButton.IsVisible = value;
    }

    /// <summary>Liga a barra a uma área de desenho e aplica as preferências do usuário.</summary>
    public void Attach(SelectionCanvas canvas)
    {
        _canvas = canvas;

        var settings = AppSettings.Current;
        if (Color.TryParse(settings.DefaultColor, out var defaultColor))
            canvas.CurrentColor = defaultColor;
        canvas.CurrentSize = settings.DefaultThickness;
        canvas.AnnotationsChanged += (_, _) => UndoButton.IsEnabled = canvas.CanUndo;

        // Espessura: o controle e a roda do mouse mantêm o mesmo valor
        ThicknessSlider.Value = canvas.CurrentSize;
        ThicknessSlider.ValueChanged += (_, e) => canvas.CurrentSize = e.NewValue;
        canvas.ThicknessChanged += (_, _) => ThicknessSlider.Value = canvas.CurrentSize;
        ThicknessSlider.Focusable = false;

        BuildSwatches();

        // Botões não recebem foco: Enter/Espaço devem ir para o app, não "clicar" no último botão.
        foreach (var button in this.GetLogicalDescendants().OfType<Button>())
            button.Focusable = false;
    }

    public void ResetUndo() => UndoButton.IsEnabled = false;

    private void BuildSwatches()
    {
        Swatches.Children.Clear();
        foreach (var color in Palette)
        {
            var button = new Button
            {
                Classes = { "swatch" },
                Focusable = false,
                Content = new Border { Classes = { "ring" }, Child = new Ellipse { Fill = new SolidColorBrush(color) } },
            };
            button.Classes.Set("selected", color == _canvas!.CurrentColor);
            button.Click += (_, _) => SelectColor(color);
            Swatches.Children.Add(button);
        }
    }

    private void SelectColor(Color color)
    {
        if (_canvas is null)
            return;

        _canvas.CurrentColor = color;
        for (var i = 0; i < Swatches.Children.Count; i++)
            if (Swatches.Children[i] is Button b)
                b.Classes.Set("selected", Palette[i] == color);

        // Escolher uma cor sem ferramenta ativa já liga a caneta
        if (_canvas.CurrentTool == Tool.None)
            SelectTool(Tool.Pen);
    }

    public void SelectTool(Tool tool)
    {
        if (_canvas is null)
            return;

        _canvas.CommitText();

        // Clicar de novo na ferramenta ativa volta para o modo de seleção
        _canvas.CurrentTool = _canvas.CurrentTool == tool ? Tool.None : tool;

        foreach (var (t, button) in _toolButtons)
            button.Classes.Set("selected", t == _canvas.CurrentTool);

        _canvas.Cursor = new Cursor(_canvas.CurrentTool switch
        {
            Tool.Text => StandardCursorType.Ibeam,
            Tool.None when _canvas.EditorMode => StandardCursorType.Arrow,
            _ => StandardCursorType.Cross,
        });
    }

    // ───────────── Teclado ─────────────

    public bool HandleTextInput(TextInputEventArgs e)
    {
        if (_canvas is not { IsEditingText: true } || string.IsNullOrEmpty(e.Text))
            return false;
        _canvas.TypeText(e.Text);
        return true;
    }

    /// <summary>Trata os atalhos de teclado. Devolve true se a tecla foi usada.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (_canvas is null)
            return false;

        // Enquanto digita, as teclas pertencem ao texto. Só as teclas de controle são
        // marcadas como tratadas: se marcar todas, o Avalonia não gera o TextInput (letras/acentos).
        if (_canvas.IsEditingText)
        {
            switch (e.Key)
            {
                case Key.Escape: _canvas.CancelText(); return true;
                case Key.Enter: _canvas.CommitText(); return true;
                case Key.Back: _canvas.Backspace(); return true;
                default: return false;
            }
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Escape: CloseRequested?.Invoke(this, EventArgs.Empty); return true;
            case Key.Enter:
            case Key.C when ctrl: CopyRequested?.Invoke(this, EventArgs.Empty); return true;
            case Key.S when ctrl: SaveRequested?.Invoke(this, EventArgs.Empty); return true;
            case Key.Z when ctrl: _canvas.Undo(); return true;
            case Key.L when !ctrl && ShowScrollButton: ScrollCaptureRequested?.Invoke(this, EventArgs.Empty); return true;
            case >= Key.D1 and <= Key.D6 when !ctrl: SelectColor(Palette[e.Key - Key.D1]); return true;
            case Key.Delete when _liveMode: _canvas.ClearAnnotations(); return true;
            case Key.V when !ctrl && ShowScrollButton: RecordRequested?.Invoke(this, EventArgs.Empty); return true;
            case Key.P when !ctrl: SelectTool(Tool.Pen); return true;
            case Key.A when !ctrl: SelectTool(Tool.Arrow); return true;
            case Key.R when !ctrl: SelectTool(Tool.Rectangle); return true;
            case Key.C when !ctrl: SelectTool(Tool.Ellipse); return true;
            case Key.N when !ctrl: SelectTool(Tool.Counter); return true;
            case Key.M when !ctrl: SelectTool(Tool.Marker); return true;
            case Key.T when !ctrl: SelectTool(Tool.Text); return true;
            case Key.B when !ctrl: SelectTool(Tool.Pixelate); return true;
            default: return false;
        }
    }
}
