using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using LenoShot.Capture;

namespace LenoShot.Scroll;

/// <summary>
/// Janela sem bordas, sempre por cima, posicionada em pixels exatos.
/// O GNOME ignora a posição pedida antes de a janela abrir, então ela é aplicada depois via X11.
/// </summary>
public class FloatingWindow : Window
{
    private PixelRect _target;

    public FloatingWindow() : this(asNotification: true) { }

    /// <param name="asNotification">
    /// Janelas "notificação" não recebem foco do teclado; use false quando a janela precisa dele.
    /// </param>
    protected FloatingWindow(bool asNotification)
    {
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = true; // o Mutter só move/redimensiona livremente janelas redimensionáveis

        // Template mínimo (só o conteúdo): o template padrão do Avalonia 12 desenha uma moldura
        // translúcida em volta, que aparece como uma "borda" em janelas transparentes.
        Template = new FuncControlTemplate<Window>((window, scope) => new ContentPresenter
        {
            Name = "PART_ContentPresenter",
            [!ContentPresenter.ContentProperty] = window[!ContentProperty],
            [!ContentPresenter.BackgroundProperty] = window[!BackgroundProperty],
        }.RegisterInNameScope(scope));
        // Tipo "notificação": o GNOME e extensões (cantos arredondados, sombra) não mexem nela
        if (asNotification && TryGetPlatformHandle() is { } handle)
            X11Window.SetWindowType(handle.Handle, "_NET_WM_WINDOW_TYPE_NOTIFICATION");

        Opened += (_, _) => Apply();
    }

    public void PlaceAt(PixelRect rect, double scaling)
    {
        _target = rect;
        Width = rect.Width / scaling;
        Height = rect.Height / scaling;
        Position = rect.Position;
        if (IsVisible)
            Apply();
    }

    /// <summary>
    /// Passa a janela para override-redirect: o gerenciador de janelas deixa de movê-la ou limitá-la
    /// (ela fica exatamente onde foi pedida, mesmo sobre a barra de tarefas). Chamar antes do Show().
    /// </summary>
    public void UseOverrideRedirect()
    {
        if (TryGetPlatformHandle() is { } handle)
            X11Window.SetOverrideRedirect(handle.Handle);
    }

    /// <summary>Foco do teclado para a janela (necessário em janelas override-redirect).</summary>
    public void FocusInput()
    {
        if (TryGetPlatformHandle() is { } handle)
            X11Window.FocusInput(handle.Handle);
    }

    /// <summary>Posição e tamanho atuais em pixels (o que foi pedido com PlaceAt/MoveTo).</summary>
    public PixelRect CurrentRect => _target;

    /// <summary>Onde a janela realmente está na tela agora (pode diferir do pedido), ou null se não abriu.</summary>
    public PixelRect? ActualRect() =>
        IsVisible && TryGetPlatformHandle() is { } handle ? X11Window.GetRect(handle.Handle) : null;

    /// <summary>Move a janela (sem mudar o tamanho).</summary>
    public void MoveTo(PixelPoint position)
    {
        _target = new PixelRect(position, _target.Size);
        Position = position;
        if (IsVisible)
            Apply();
    }

    private void Apply()
    {
        if (TryGetPlatformHandle() is { } handle)
            X11Window.MoveResize(handle.Handle, _target);
    }
}
