using Avalonia;
using Avalonia.Media;
using LenoShot.Services;

namespace LenoShot;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var command = args.Length > 0 ? string.Join(' ', args) : "daemon";

        // Usado pelo empacotamento: gera os ícones e sai, sem abrir o app
        if (args is ["--export-icons", var dir])
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            AccentTheme.UseDefault(); // o pacote sempre leva o ícone padrão, não a cor de quem empacota
            SystemIntegration.ExportIcons(dir);
            return 0;
        }

        // Gera o ícone do Windows (.ico); usado para atualizar packaging/leno-shot.ico
        if (args is ["--export-ico", var file])
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            SystemIntegration.ExportIco(file);
            return 0;
        }

        // Se já existe uma instância rodando, só repassa o comando e sai.
        if (SingleInstance.TrySendToRunningInstance(command))
            return 0;

        App.StartupCommand = command;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // WithInterFont só registra a fonte; isto a torna a padrão de todo o app
            // (sem isso as janelas usam a fonte do sistema, que não tem os pesos finos).
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .LogToTrace();
}
