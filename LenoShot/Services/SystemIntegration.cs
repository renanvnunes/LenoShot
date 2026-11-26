using System.Diagnostics;

namespace LenoShot.Services;

/// <summary>Integrações com o desktop Linux: notificações, início automático e atalho do GNOME.</summary>
public static class SystemIntegration
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    /// <summary>Instalado pelo pacote .deb (binário em /usr/lib/LenoShot).</summary>
    public static bool IsSystemInstall =>
        Environment.ProcessPath?.StartsWith("/usr/", StringComparison.Ordinal) == true;

    /// <summary>Comando usado por atalhos, menu e início automático.</summary>
    private static string LauncherPath => IsSystemInstall
        ? "/usr/bin/LenoShot"
        : Path.Combine(Home, ".local", "bin", "LenoShot");

    // ───────────── Notificações ─────────────

    /// <summary>
    /// Mostra uma notificação. Com <paramref name="revealPath"/>, clicar nela (ou no botão
    /// "Mostrar na pasta") abre o gerenciador de arquivos com o arquivo selecionado.
    /// </summary>
    public static void Notify(string title, string body, string? imagePath = null, string? revealPath = null)
    {
        var args = new List<string> { "--app-name=LenoShot", "--expire-time=3000" };
        if (imagePath is not null)
            args.Add($"--icon={imagePath}"); // o GNOME mostra a miniatura da imagem salva
        if (revealPath is not null)
        {
            // "default" é o clique no corpo da notificação; com ações o notify-send espera a resposta
            args.Add("--action=default=Mostrar na pasta");
            args.Add("--action=reveal=Mostrar na pasta");
        }
        args.Add(title);
        args.Add(body);

        if (revealPath is null)
        {
            _ = Run("notify-send", args);
            return;
        }

        _ = Task.Run(async () =>
        {
            var action = (await Run("notify-send", args))?.Trim();
            if (action is "default" or "reveal")
                await RevealInFolderAsync(revealPath);
        });
    }

    /// <summary>Abre a pasta do arquivo, com ele selecionado quando o gerenciador de arquivos permite.</summary>
    public static async Task RevealInFolderAsync(string path)
    {
        // Interface padrão (Nautilus, Nemo, Dolphin…): abre a pasta já selecionando o arquivo
        var uri = new Uri(path).AbsoluteUri.Replace("'", "%27");
        var result = await Run("gdbus", ["call", "--session", "--dest", "org.freedesktop.FileManager1",
            "--object-path", "/org/freedesktop/FileManager1", "--method", "org.freedesktop.FileManager1.ShowItems",
            $"['{uri}']", ""]);
        if (string.IsNullOrWhiteSpace(result))
            await Run("xdg-open", [Path.GetDirectoryName(path) ?? path]);
    }

    // ───────────── Ícone e menu de aplicativos ─────────────

    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256, 512];

    /// <summary>
    /// Instala o ícone (tema hicolor) e a entrada no menu de aplicativos em ~/.local/share.
    /// Roda a cada início, então ícone e atalhos ficam sempre atualizados.
    /// </summary>
    /// <summary>Gera os PNGs do ícone em todos os tamanhos, na estrutura do tema hicolor.</summary>
    public static void ExportIcons(string hicolorDir)
    {
        foreach (var size in IconSizes)
        {
            var dir = Path.Combine(hicolorDir, $"{size}x{size}", "apps");
            Directory.CreateDirectory(dir);
            using var bitmap = AppIcon.CreateBitmap(size);
            bitmap.Save(Path.Combine(dir, "LenoShot.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    public static void InstallDesktopEntry()
    {
        try
        {
            // Instalado pelo .deb: ícone e menu já estão em /usr/share
            if (IsSystemInstall)
                return;

            // Com o pacote .deb instalado, o atalho do sistema é o válido. Um build de desenvolvimento
            // não pode criar um atalho local que o sobreponha: ele apontaria para um programa que
            // não existe e o GNOME esconderia o ícone do app.
            if (File.Exists("/usr/share/applications/LenoShot.desktop") || !File.Exists(LauncherPath))
                return;

            var share = Path.Combine(Home, ".local", "share");
            ExportIcons(Path.Combine(share, "icons", "hicolor"));

            var apps = Path.Combine(share, "applications");
            Directory.CreateDirectory(apps);
            File.WriteAllText(Path.Combine(apps, "LenoShot.desktop"), $"""
                [Desktop Entry]
                Type=Application
                Name=LenoShot
                GenericName=Captura de tela
                Comment=Capture, anote e compartilhe a tela
                Exec={LauncherPath} settings
                Icon=LenoShot
                Terminal=false
                Categories=Utility;Graphics;
                Keywords=screenshot;print;captura;tela;
                StartupWMClass=LenoShot
                Actions=capture;delay;

                [Desktop Action capture]
                Name=Capturar área
                Exec={LauncherPath} capture

                [Desktop Action delay]
                Name=Capturar em 5 segundos
                Exec={LauncherPath} capture --delay 5

                """);

            _ = Run("gtk-update-icon-cache", ["-f", "-t", Path.Combine(share, "icons", "hicolor")]);
            _ = Run("update-desktop-database", [apps]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LenoShot] não foi possível instalar o ícone: {ex.Message}");
        }
    }

    /// <summary>
    /// Ícone do menu de aplicativos na cor escolhida. O pacote .deb instala o ícone padrão em
    /// /usr/share (só o root altera); uma cópia em ~/.local/share/icons tem prioridade sobre ele.
    /// Com a cor padrão a cópia é removida e volta a valer o ícone do sistema.
    /// </summary>
    public static void UpdateDesktopIcons()
    {
        try
        {
            var hicolor = Path.Combine(Home, ".local", "share", "icons", "hicolor");
            if (IsSystemInstall && AccentTheme.IsDefault)
            {
                var removed = false;
                foreach (var size in IconSizes)
                {
                    var file = Path.Combine(hicolor, $"{size}x{size}", "apps", "LenoShot.png");
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        removed = true;
                    }
                }
                if (!removed)
                    return;
            }
            else if (IsSystemInstall || File.Exists(Path.Combine(hicolor, "256x256", "apps", "LenoShot.png")))
            {
                // Desenhar exige a thread da interface
                Avalonia.Threading.Dispatcher.UIThread.Invoke(() => ExportIcons(hicolor));
            }
            else
            {
                return; // build de desenvolvimento sem ícone instalado: nada a atualizar
            }

            // Avisa o sistema que os ícones mudaram
            Directory.SetLastWriteTimeUtc(hicolor, DateTime.UtcNow);
            _ = Run("gtk-update-icon-cache", ["-f", "-t", hicolor]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LenoShot] não foi possível atualizar o ícone: {ex.Message}");
        }
    }

    // ───────────── Início automático ─────────────

    private static string AutostartFile => Path.Combine(Home, ".config", "autostart", "LenoShot.desktop");

    public static bool IsAutostartEnabled => File.Exists(AutostartFile);

    public static void SetAutostart(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(AutostartFile);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
        File.WriteAllText(AutostartFile, $"""
            [Desktop Entry]
            Type=Application
            Name=LenoShot
            Comment=Captura de tela
            Exec={LauncherPath}
            Icon=LenoShot
            X-GNOME-Autostart-enabled=true
            NoDisplay=true

            """);
    }

    // ───────────── Atalho global (GNOME) ─────────────

    private const string KeybindingsSchema = "org.gnome.settings-daemon.plugins.media-keys";
    private const string KeybindingBase = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/";

    /// <summary>Atalhos globais do app: cada um é uma entrada de atalho personalizado do GNOME.</summary>
    public enum ShortcutSlot { Capture, Zoom }

    private static string SlotPath(ShortcutSlot slot) =>
        KeybindingBase + (slot == ShortcutSlot.Capture ? "LenoShot/" : "LenoShot-zoom/");

    private static string SlotSchema(ShortcutSlot slot) =>
        "org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:" + SlotPath(slot);

    private static string SlotName(ShortcutSlot slot) => slot == ShortcutSlot.Capture ? "LenoShot" : "LenoShot — zoom na gravação";

    private static string SlotCommand(ShortcutSlot slot) =>
        $"{LauncherPath} {(slot == ShortcutSlot.Capture ? "capture" : "record-zoom")}";

    /// <summary>Atalho atual no formato do GNOME, por exemplo "&lt;Shift&gt;Print".</summary>
    public static async Task<string?> GetShortcutAsync(ShortcutSlot slot = ShortcutSlot.Capture)
    {
        var value = await Run("gsettings", ["get", SlotSchema(slot), "binding"]);
        value = value?.Trim().Trim('\'');
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static async Task SetShortcutAsync(string accelerator, ShortcutSlot slot = ShortcutSlot.Capture)
    {
        var schema = SlotSchema(slot);
        await Run("gsettings", ["set", schema, "name", SlotName(slot)]);
        await Run("gsettings", ["set", schema, "command", SlotCommand(slot)]);
        await Run("gsettings", ["set", schema, "binding", accelerator]);

        // Garante que o atalho está registrado na lista de atalhos personalizados
        var path = SlotPath(slot);
        var list = await Run("gsettings", ["get", KeybindingsSchema, "custom-keybindings"]) ?? "";
        if (!list.Contains(path))
        {
            var items = list.Replace("@as", "").Trim().Trim('[', ']')
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Append($"'{path}'");
            await Run("gsettings", ["set", KeybindingsSchema, "custom-keybindings", $"[{string.Join(", ", items)}]"]);
        }
    }

    // ───────────── Utilitário ─────────────

    internal static Task<string?> RunAsync(string file, IEnumerable<string> args) => Run(file, args);

    private static async Task<string?> Run(string file, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return null;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return output;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LenoShot] falha ao executar {file}: {ex.Message}");
            return null;
        }
    }
}
