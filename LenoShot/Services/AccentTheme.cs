using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace LenoShot.Services;

/// <summary>
/// Cor de destaque do app, escolhida pelo usuário. Botões, seleção, moldura da gravação e o
/// ícone derivam dela. Os estilos usam os recursos dinâmicos "Accent*", que mudam na hora.
/// </summary>
public static class AccentTheme
{
    public const string DefaultHex = "#6E7BFF";

    /// <summary>Cores oferecidas nas configurações (todas legíveis com texto branco).</summary>
    public static readonly (string Name, string Hex)[] Presets =
    [
        ("Índigo", DefaultHex),
        ("Azul", "#0A84FF"),
        ("Turquesa", "#1FA2B8"),
        ("Verde", "#2DA44E"),
        ("Laranja", "#E8790C"),
        ("Vermelho", "#E5484D"),
        ("Rosa", "#E5487F"),
        ("Roxo", "#A55EEA"),
        ("Grafite", "#7C7C86"),
    ];

    /// <summary>Disparado depois que a cor muda (para atualizar ícones de janelas e bandeja).</summary>
    public static event EventHandler? Changed;

    public static Color Color { get; private set; } = Color.Parse(DefaultHex);

    public static bool IsDefault => Color == Color.Parse(DefaultHex);

    /// <summary>Cor do fundo do ícone: um pouco mais escura que o destaque.</summary>
    public static Color IconColor => Mix(Color, Colors.Black, 0.15);

    /// <summary>Destaque com transparência (moldura da gravação, por exemplo).</summary>
    public static Color WithAlpha(byte alpha) => Color.FromArgb(alpha, Color.R, Color.G, Color.B);

    public static Color Parse(string? hex) =>
        Avalonia.Media.Color.TryParse(hex, out var c) ? Avalonia.Media.Color.FromRgb(c.R, c.G, c.B) : Avalonia.Media.Color.Parse(DefaultHex);

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Aplica a cor salva nas configurações. Chamar na inicialização.</summary>
    public static void Load() => Apply(Parse(AppSettings.Current.AccentColor), raiseChanged: false);

    /// <summary>Volta à cor padrão sem salvar (usado ao gerar os ícones do pacote).</summary>
    public static void UseDefault() => Apply(Color.Parse(DefaultHex), raiseChanged: false);

    /// <summary>Troca a cor do app inteiro, salva a escolha e atualiza o ícone.</summary>
    public static void Set(Color color)
    {
        AppSettings.Current.AccentColor = ToHex(color);
        AppSettings.Current.Save();
        Apply(color, raiseChanged: true);
        _ = Task.Run(SystemIntegration.UpdateDesktopIcons);
    }

    private static void Apply(Color color, bool raiseChanged)
    {
        Color = color;
        if (Application.Current is not { } app)
            return;

        var r = app.Resources;
        r["Accent"] = new SolidColorBrush(color);
        r["AccentHover"] = new SolidColorBrush(Mix(color, Colors.White, 0.15));
        r["AccentSoft"] = new SolidColorBrush(WithAlpha(0x2E));
        r["AccentSoftHover"] = new SolidColorBrush(WithAlpha(0x40));
        r["AccentText"] = new SolidColorBrush(Mix(color, Colors.White, 0.40));
        r["AccentTextHover"] = new SolidColorBrush(Mix(color, Colors.White, 0.55));

        // Controles do Fluent (ToggleSwitch, ComboBox, Slider…) usam a paleta do tema
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent &&
            fluent.Palettes.TryGetValue(ThemeVariant.Dark, out var palette))
            palette.Accent = color;

        if (raiseChanged)
            Changed?.Invoke(null, EventArgs.Empty);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
