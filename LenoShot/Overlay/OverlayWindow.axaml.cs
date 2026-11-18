using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using LenoShot.Capture;
using LenoShot.Services;

namespace LenoShot.Overlay;

/// <summary>Sobreposição em tela cheia de um monitor: imagem congelada, seleção e anotações.</summary>
public partial class OverlayWindow : Window
{
    private readonly CaptureSession _session;
    private readonly PixelRect _screenBounds;
    private readonly double _scaling;

    // Exigido pelo designer/XAML loader
    public OverlayWindow() : this(new CaptureSession(), new ScreenImage(1, 1, new byte[4]), new PixelRect(0, 0, 1, 1), 1) { }

    internal OverlayWindow(CaptureSession session, ScreenImage image, PixelRect screenBounds, double scaling)
    {
        InitializeComponent();

        _session = session;
        _screenBounds = screenBounds;
        _scaling = scaling;

        Position = screenBounds.Position;
        Width = screenBounds.Width / scaling;
        Height = screenBounds.Height / scaling;

        Selection.Source = image;
        Selection.Image = image.ToBitmap();
        Selection.PixelScale = scaling;
        Selection.Width = Width;
        Selection.Height = Height;
        Selection.SelectionStarted += (_, _) =>
        {
            Toolbar.IsVisible = false;
            _session.OnSelectionStarted(this);
        };
        Selection.SelectionCompleted += (_, _) => ShowToolbar();

        Toolbar.Attach(Selection);
        Toolbar.ShowScrollButton = true;
        Toolbar.SaveRequested += async (_, _) => await SaveAsync();
        Toolbar.CopyRequested += async (_, _) => await CopyAsync();
        Toolbar.CloseRequested += (_, _) => _session.End();
        Toolbar.ScrollCaptureRequested += (_, _) => StartScrollCapture();
        Toolbar.RecordRequested += (_, _) => StartRecord();

        // Se a janela for fechada por fora (ex.: gerenciador de janelas), encerra a sessão toda
        Closed += (_, _) => _session.End();

        // O GNOME ignora a posição pedida pelo Avalonia; depois que a janela abre,
        // pedimos tela cheia no monitor certo direto ao gerenciador de janelas.
        // Override-redirect (antes de abrir): o gerenciador de janelas não anima nem reposiciona a
        // janela, então ela aparece direto, sem fade/piscada, exatamente sobre o monitor.
        _overrideRedirect = false;
        if (TryGetPlatformHandle() is { } pre)
        {
            X11Window.SetOverrideRedirect(pre.Handle);
            _overrideRedirect = true;
        }

        Opened += (_, _) =>
        {
            if (TryGetPlatformHandle() is { } handle)
            {
                if (_overrideRedirect)
                {
                    X11Window.MoveResize(handle.Handle, _screenBounds);
                    X11Window.FocusInput(handle.Handle);
                }
                else
                    X11Window.MakeFullscreenOn(handle.Handle, _screenBounds);
            }
            else
                WindowState = WindowState.FullScreen;
            Activate();
            Selection.Focus();
        };
    }

    private bool _overrideRedirect;

    /// <summary>Limpa a seleção quando o usuário começa a selecionar em outro monitor.</summary>
    internal void ResetSelection()
    {
        Selection.Reset();
        Toolbar.IsVisible = false;
        Toolbar.ResetUndo();
    }

    // O foco do teclado acompanha o monitor onde está o mouse
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (!IsActive)
            Activate();
        Selection.Focus();
    }

    private void StartScrollCapture()
    {
        if (Selection.SelectionInPixels is not { } px)
            return;

        var global = new PixelRect(px.X + _screenBounds.X, px.Y + _screenBounds.Y, px.Width, px.Height);
        _session.RequestScrollCapture(new ScrollCaptureRequest(global, _screenBounds, _scaling));
    }

    private void StartRecord()
    {
        if (Selection.SelectionInPixels is not { } px)
            return;

        var global = new PixelRect(px.X + _screenBounds.X, px.Y + _screenBounds.Y, px.Width, px.Height);
        _session.RequestRecord(new RecordRequest(global, _screenBounds, _scaling));
    }

    private void ShowToolbar()
    {
        if (Selection.SelectionRect is not { } sel)
            return;

        Toolbar.IsVisible = true;
        Toolbar.Measure(Size.Infinity);
        var size = Toolbar.DesiredSize;

        var x = Math.Clamp(sel.Right - size.Width, 8, Bounds.Width - size.Width - 8);
        var y = sel.Bottom + 10;
        if (y + size.Height > Bounds.Height - 8)
            y = sel.Top - size.Height - 10;      // sem espaço embaixo: vai para cima
        if (y < 8)
            y = sel.Bottom - size.Height - 10;   // sem espaço em cima: fica dentro da seleção

        Canvas.SetLeft(Toolbar, x);
        Canvas.SetTop(Toolbar, y);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (Toolbar.HandleTextInput(e))
            e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Toolbar.HandleKey(e))
            e.Handled = true;
    }

    private async Task CopyAsync()
    {
        var bitmap = Selection.RenderSelection();
        if (bitmap is null)
            return;

        await CaptureOutput.CopyAsync(this, bitmap);
        _session.End();
    }

    private async Task SaveAsync()
    {
        using var bitmap = Selection.RenderSelection();
        if (bitmap is null)
            return;

        await CaptureOutput.SaveAsync(bitmap);
        _session.End();
    }
}
