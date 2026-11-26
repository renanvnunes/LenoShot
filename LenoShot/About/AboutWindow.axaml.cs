using System.Reflection;
using Avalonia.Controls;
using LenoShot.Controls;
using LenoShot.Services;

namespace LenoShot.About;

/// <summary>Tela "Sobre": versão, criador, licença, tecnologias e link do código-fonte.</summary>
public partial class AboutWindow : ChromeWindow
{
    // Trocar pelo endereço do repositório quando ele existir
    public const string RepositoryUrl = "https://github.com";

    public AboutWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Create();
        AppIconImage.Source = AppIcon.CreateBitmap(256);
        EventHandler onAccent = (_, _) => AppIconImage.Source = AppIcon.CreateBitmap(256);
        AccentTheme.Changed += onAccent;
        Closed += (_, _) => AccentTheme.Changed -= onAccent;

        var version = AppVersion;
        VersionText.Text = $"Versão {version}";
        VersionValue.Text = version;
        CopyrightText.Text = $"© {DateTime.Now.Year} Renan Nunes · Licença MIT";

        LicenseText.Text = ReadLicense();
        LicenseButton.Click += (_, _) =>
        {
            LicensePanel.IsVisible = !LicensePanel.IsVisible;
            LicenseButton.Content = LicensePanel.IsVisible ? "MIT · ocultar" : "MIT · ver texto";
        };

        GitHubLink.Click += async (_, _) => await OpenRepositoryAsync();
        GitHubButton.Click += async (_, _) => await OpenRepositoryAsync();

        foreach (var tech in Technologies())
            TechPanel.Children.Add(new Border { Classes = { "chip" }, Child = new TextBlock { Text = tech } });
    }

    public static string AppVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>Tecnologias usadas, com as versões lidas do próprio app em execução.</summary>
    private static IEnumerable<string> Technologies()
    {
        var avalonia = typeof(Avalonia.Application).Assembly.GetName().Version;
        yield return $".NET {Environment.Version.Major}.{Environment.Version.Minor}";
        yield return "C#";
        yield return avalonia is null ? "Avalonia" : $"Avalonia {avalonia.Major}.{avalonia.Minor}";
        yield return "SkiaSharp";
        yield return "Native AOT";
    }

    private static string ReadLicense()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LICENSE");
        if (stream is null)
            return "MIT License — Copyright (c) 2026 Renan Nunes";
        using var reader = new StreamReader(stream);

        // O arquivo tem quebras fixas em ~80 colunas; junta as linhas de cada parágrafo
        // para o texto fluir na largura do cartão.
        var paragraphs = reader.ReadToEnd().Replace("\r", "").Trim()
            .Split("\n\n")
            .Select(p => string.Join(' ', p.Split('\n', StringSplitOptions.TrimEntries)));
        return string.Join("\n\n", paragraphs);
    }

    private async Task OpenRepositoryAsync() =>
        await Launcher.LaunchUriAsync(new Uri(RepositoryUrl));
}
