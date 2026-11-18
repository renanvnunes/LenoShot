using Avalonia;
using Avalonia.Input;
using LenoShot.Capture;
using LenoShot.Controls;
using LenoShot.Services;

namespace LenoShot.Editor;

/// <summary>
/// Editor da captura com rolagem: a imagem longa numa área rolável, com a mesma barra de
/// ferramentas de anotação da captura normal.
/// </summary>
public partial class EditorWindow : ChromeWindow
{
    public EditorWindow() : this(new ScreenImage(1, 1, new byte[4]), new PixelRect(0, 0, 800, 600), 1) { }

    public EditorWindow(ScreenImage image, PixelRect monitor, double scaling)
    {
        InitializeComponent();
        Icon = AppIcon.Create();

        Canvas.EditorMode = true;
        Canvas.Source = image;
        Canvas.Image = image.ToBitmap();
        Canvas.PixelScale = scaling;
        Canvas.Width = image.Width / scaling;
        Canvas.Height = image.Height / scaling;
        Canvas.Cursor = new Cursor(StandardCursorType.Arrow);
        Canvas.SelectAll();

        SizeText.Text = $"{image.Width} × {image.Height} px";

        Toolbar.Attach(Canvas);
        Toolbar.ShowScrollButton = false;
        Toolbar.CopyRequested += async (_, _) => await CopyAsync();
        Toolbar.SaveRequested += async (_, _) => await SaveAsync();
        Toolbar.CloseRequested += (_, _) => Close();

        // Abre centralizada no monitor da captura, com a largura da imagem (até 90% da tela)
        var width = (int)Math.Clamp(image.Width + 80 * scaling, 760 * scaling, monitor.Width * 0.9);
        var height = (int)(monitor.Height - 90 * scaling);
        var rect = new PixelRect(
            monitor.X + (monitor.Width - width) / 2, monitor.Y + (monitor.Height - height) / 2, width, height);
        Width = width / scaling;
        Height = height / scaling;
        Opened += (_, _) =>
        {
            if (TryGetPlatformHandle() is { } handle)
                X11Window.MoveResize(handle.Handle, rect);
            Canvas.Focus();
        };
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
        var bitmap = Canvas.RenderSelection();
        if (bitmap is null)
            return;
        await CaptureOutput.CopyAsync(this, bitmap);
        Close();
    }

    private async Task SaveAsync()
    {
        using var bitmap = Canvas.RenderSelection();
        if (bitmap is null)
            return;
        await CaptureOutput.SaveAsync(bitmap);
        Close();
    }
}
