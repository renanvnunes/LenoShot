using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using LenoShot.Capture;

namespace LenoShot.Scroll;

/// <summary>
/// Janela sem bordas, sempre por cima, posicionada em pixels exatos.
/// O GNOME ignora a posição pedida antes de a janela abrir, então ela é aplicada depois direto
/// no sistema (X11 ou Win32).
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
        // O Mutter só move/redimensiona livremente janelas redimensionáveis. No Windows é o contrário:
        // janela redimensionável ganha borda de redimensionar e tamanho mínimo, o que estragaria as
        // faixas de 2 px da moldura.
        CanResize = !OperatingSystem.IsWindows();

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
            PlatformWindow.MarkAsNotification(handle.Handle);

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
            PlatformWindow.SetOverrideRedirect(handle.Handle);
    }

    /// <summary>Foco do teclado para a janela (necessário em janelas override-redirect).</summary>
    public void FocusInput()
    {
        if (TryGetPlatformHandle() is { } handle)
            PlatformWindow.FocusInput(handle.Handle);
    }

    /// <summary>Posição e tamanho atuais em pixels (o que foi pedido com PlaceAt/MoveTo).</summary>
    public PixelRect CurrentRect => _target;

    /// <summary>Onde a janela realmente está na tela agora (pode diferir do pedido), ou null se não abriu.</summary>
    public PixelRect? ActualRect()
    {
        if (!IsVisible || TryGetPlatformHandle() is not { } handle || PlatformWindow.GetRect(handle.Handle) is not { } r)
            return null;
        // No Windows a janela pode ser maior que o pedido, mas só a parte pedida fica visível (ver Apply)
        return OperatingSystem.IsWindows() && _target.Width > 0
            ? new PixelRect(r.X, r.Y, Math.Min(r.Width, _target.Width), Math.Min(r.Height, _target.Height))
            : r;
    }

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
        if (TryGetPlatformHandle() is not { } handle)
            return;

        PlatformWindow.MoveResize(handle.Handle, _target);

        // Windows: tamanho mínimo imposto pelo sistema (faixas finas da moldura) → recorta o excesso
        if (OperatingSystem.IsWindows() && PlatformWindow.GetRect(handle.Handle) is { } actual)
            PlatformWindow.ClipToSize(handle.Handle, _target.Size,
                clip: actual.Width > _target.Width || actual.Height > _target.Height);
    }
}
