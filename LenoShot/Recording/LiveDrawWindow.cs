using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LenoShot.Capture;
using LenoShot.Overlay;
using LenoShot.Scroll;

namespace LenoShot.Recording;

/// <summary>
/// Modo desenho da gravação: congela a área gravada num instantâneo e deixa marcar em cima dele
/// com as mesmas ferramentas da captura de tela. Ao sair, a gravação volta ao vivo.
///
/// Por que congelar, em vez de uma camada transparente por cima? Os grabs do X11 (inclusive o
/// x11grab do ffmpeg) ignoram o canal alfa das janelas: uma camada transparente sairia como um
/// retângulo preto no vídeo. Uma janela opaca com a imagem congelada aparece certa no vídeo,
/// com suavização, marca-texto translúcido e pixelização.
/// </summary>
public sealed class LiveDrawWindow : FloatingWindow
{
    private readonly SelectionCanvas _canvas = new() { EditorMode = true };
    private readonly PixelRect _area;
    private readonly double _scaling;

    public LiveDrawWindow(PixelRect area, double scaling) : base(asNotification: true)
    {
        _area = area;
        _scaling = scaling;

        Title = "LenoShot — desenho";
        Background = Brushes.Black;
        ShowActivated = false;

        _canvas.Width = area.Width / scaling;
        _canvas.Height = area.Height / scaling;
        _canvas.PixelScale = scaling;
        Content = _canvas;

        Toolbar.Attach(_canvas);
        Toolbar.UseLiveMode();
        Toolbar.CloseRequested += (_, _) => SetDrawing(false);

        UseOverrideRedirect(); // posição exata: alinhada com a tela viva, sem o GNOME mexer
        PlaceAt(area, scaling);
    }

    /// <summary>Barra de ferramentas; fica numa janela própria, fora da área gravada.</summary>
    public AnnotationToolbar Toolbar { get; } = new();

    public bool IsDrawing { get; private set; }

    /// <summary>Disparado quando o modo desenho liga ou desliga (inclusive por Esc).</summary>
    public event EventHandler? DrawingChanged;

    public void SetDrawing(bool on)
    {
        if (IsDrawing == on)
            return;

        if (on)
        {
            // Congela o que está na área agora, antes de a janela aparecer
            var snapshot = ScreenCapture.CaptureRect(_area);
            if (snapshot is null)
                return;

            _canvas.ClearAnnotations();
            _canvas.Source = snapshot;
            _canvas.Image = snapshot.ToBitmap();
            _canvas.SelectAll();
            IsDrawing = true;

            PlaceAt(_area, _scaling);
            Show();
            FocusInput();
            _canvas.Focus();
            if (_canvas.CurrentTool == Tool.None)
                Toolbar.SelectTool(Tool.Pen);
        }
        else
        {
            _canvas.CommitText();
            _canvas.ClearAnnotations();
            IsDrawing = false;
            Hide();
        }

        DrawingChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        FocusInput();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (IsDrawing && Toolbar.HandleTextInput(e))
            e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsDrawing && Toolbar.HandleKey(e))
            e.Handled = true;
    }
}
