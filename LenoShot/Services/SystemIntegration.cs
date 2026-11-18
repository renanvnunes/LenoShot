using System.Diagnostics;

namespace LenoShot.Services;

/// <summary>Integrações com o desktop Linux: notificações, início automático e atalho do GNOME.</summary>
public static class SystemIntegration
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    /// <summary>Instalado pelo pacote .deb (binário em /usr/lib/leno-shot).</summary>
    public static bool IsSystemInstall =>
        Environment.ProcessPath?.StartsWith("/usr/", StringComparison.Ordinal) == true;

    /// <summary>Comando usado por atalhos, menu e início automático.</summary>
    private static string LauncherPath => IsSystemInstall
        ? "/usr/bin/leno-shot"
        : Path.Combine(Home, ".local", "bin", "leno-shot");

    // ───────────── Notificações ─────────────

    public static void Notify(string title, string body, string? imagePath = null)
    {
        var args = new List<string> { "--app-name=Leno Shot", "--expire-time=3000" };
        if (imagePath is not null)
            args.Add($"--icon={imagePath}"); // o GNOME mostra a miniatura da imagem salva
        args.Add(title);
        args.Add(body);
        _ = Run("notify-send", args);
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
            bitmap.Save(Path.Combine(dir, "leno-shot.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
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
            if (File.Exists("/usr/share/applications/leno-shot.desktop") || !File.Exists(LauncherPath))
                return;

            var share = Path.Combine(Home, ".local", "share");
            ExportIcons(Path.Combine(share, "icons", "hicolor"));

            var apps = Path.Combine(share, "applications");
            Directory.CreateDirectory(apps);
            File.WriteAllText(Path.Combine(apps, "leno-shot.desktop"), $"""
                [Desktop Entry]
                Type=Application
                Name=Leno Shot
                GenericName=Captura de tela
                Comment=Capture, anote e compartilhe a tela
                Exec={LauncherPath} settings
                Icon=leno-shot
                Terminal=false
                Categories=Utility;Graphics;
                Keywords=screenshot;print;captura;tela;
                StartupWMClass=leno-shot
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
            Console.Error.WriteLine($"[leno-shot] não foi possível instalar o ícone: {ex.Message}");
        }
    }

    // ───────────── Início automático ─────────────

    private static string AutostartFile => Path.Combine(Home, ".config", "autostart", "leno-shot.desktop");

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
            Name=Leno Shot
            Comment=Captura de tela
            Exec={LauncherPath}
            Icon=leno-shot
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
        KeybindingBase + (slot == ShortcutSlot.Capture ? "leno-shot/" : "leno-shot-zoom/");

    private static string SlotSchema(ShortcutSlot slot) =>
        "org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:" + SlotPath(slot);

    private static string SlotName(ShortcutSlot slot) => slot == ShortcutSlot.Capture ? "Leno Shot" : "Leno Shot — zoom na gravação";

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
            Console.Error.WriteLine($"[leno-shot] falha ao executar {file}: {ex.Message}");
            return null;
        }
    }
}
