using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;

namespace LenoShot.Controls;

/// <summary>
/// Janela com barra de título própria, no visual do app (não a do sistema):
/// botões redondos à esquerda, título centralizado, cantos arredondados.
/// Arrastar a barra move a janela; duplo clique maximiza; bordas invisíveis redimensionam.
/// </summary>
public class ChromeWindow : Window
{
    public static readonly StyledProperty<object?> TitleBarContentProperty =
        AvaloniaProperty.Register<ChromeWindow, object?>(nameof(TitleBarContent));

    public static readonly StyledProperty<bool> ShowMaximizeButtonProperty =
        AvaloniaProperty.Register<ChromeWindow, bool>(nameof(ShowMaximizeButton), true);

    /// <summary>Conteúdo extra à direita da barra de título (ex.: dimensões da imagem).</summary>
    public object? TitleBarContent
    {
        get => GetValue(TitleBarContentProperty);
        set => SetValue(TitleBarContentProperty, value);
    }

    public bool ShowMaximizeButton
    {
        get => GetValue(ShowMaximizeButtonProperty);
        set => SetValue(ShowMaximizeButtonProperty, value);
    }

    public ChromeWindow()
    {
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

        if (Application.Current?.TryFindResource("ChromeWindowTheme", out var theme) == true && theme is ControlTheme ct)
        {
            Theme = ct;

            // O Fluent do Avalonia 12 define o template de Window via Style, que tem prioridade sobre
            // ControlTheme. Como valor local, o nosso template vence.
            if (ct.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == TemplateProperty)?.Value is IControlTemplate template)
                Template = template;
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (e.NameScope.Find<Control>("PART_TitleBar") is { } titleBar)
        {
            titleBar.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed)
                    return;
                if (args.ClickCount == 2 && CanResize && ShowMaximizeButton)
                    ToggleMaximize();
                else
                    BeginMoveDrag(args);
            };
        }

        if (e.NameScope.Find<Button>("PART_Close") is { } close)
            close.Click += (_, _) => Close();
        if (e.NameScope.Find<Button>("PART_Minimize") is { } minimize)
            minimize.Click += (_, _) => WindowState = WindowState.Minimized;
        if (e.NameScope.Find<Button>("PART_Maximize") is { } maximize)
            maximize.Click += (_, _) => ToggleMaximize();

        // Alças de redimensionamento nas bordas e cantos
        (string Name, WindowEdge Edge)[] grips =
        [
            ("PART_GripN", WindowEdge.North), ("PART_GripS", WindowEdge.South),
            ("PART_GripW", WindowEdge.West), ("PART_GripE", WindowEdge.East),
            ("PART_GripNW", WindowEdge.NorthWest), ("PART_GripNE", WindowEdge.NorthEast),
            ("PART_GripSW", WindowEdge.SouthWest), ("PART_GripSE", WindowEdge.SouthEast),
        ];
        foreach (var (name, edge) in grips)
        {
            if (e.NameScope.Find<Control>(name) is not { } grip)
                continue;
            grip.IsVisible = CanResize;
            grip.PointerPressed += (_, args) =>
            {
                if (args.GetCurrentPoint(grip).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal)
                    BeginResizeDrag(edge, args);
            };
        }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
