using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Threading;
using Microsoft.Win32;

namespace LenoShot.Services;

/// <summary>
/// Integrações com o desktop: notificações, início automático e atalhos globais.
/// No Linux usa notify-send, ~/.config/autostart e os atalhos do GNOME; no Windows, um aviso
/// próprio, a chave Run do registro e RegisterHotKey.
/// </summary>
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
        if (OperatingSystem.IsWindows())
        {
            Dispatcher.UIThread.Post(() => Controls.ToastWindow.Show(title, body, imagePath, revealPath));
            return;
        }

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

    /// <summary>
    /// Gera o ícone do Windows (.ico) com vários tamanhos. Usado pelo .exe e pelo instalador.
    /// Como nos ícones do próprio Windows, o 256 vai em PNG e os menores em bitmap (DIB de 32 bits),
    /// que qualquer leitor de .ico entende.
    /// </summary>
    public static void ExportIco(string file)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = sizes.Select(size =>
        {
            using var bitmap = AppIcon.CreateBitmap(size);
            if (size >= 256)
            {
                using var png = new MemoryStream();
                bitmap.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                return png.ToArray();
            }
            return IcoDib(bitmap, size);
        }).ToList();

        using var output = new BinaryWriter(File.Create(file));
        output.Write((ushort)0);            // reservado
        output.Write((ushort)1);            // tipo: ícone
        output.Write((ushort)sizes.Length);

        var offset = 6 + 16 * sizes.Length; // cabeçalho + uma entrada de 16 bytes por tamanho
        for (var i = 0; i < sizes.Length; i++)
        {
            output.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // largura (0 = 256)
            output.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); // altura
            output.Write((byte)0);          // cores da paleta (nenhuma)
            output.Write((byte)0);          // reservado
            output.Write((ushort)1);        // planos
            output.Write((ushort)32);       // bits por pixel
            output.Write((uint)images[i].Length);
            output.Write((uint)offset);
            offset += images[i].Length;
        }
        foreach (var image in images)
            output.Write(image);
    }

    /// <summary>
    /// Uma imagem do .ico em bitmap: cabeçalho BITMAPINFOHEADER (altura em dobro, por causa da
    /// máscara), pixels BGRA de baixo para cima, sem pré-multiplicar, e a máscara AND (zerada:
    /// a transparência vem do canal alfa).
    /// </summary>
    private static unsafe byte[] IcoDib(Avalonia.Media.Imaging.Bitmap bitmap, int size)
    {
        var pixels = new byte[size * size * 4];
        fixed (byte* p = pixels)
            bitmap.CopyPixels(new Avalonia.PixelRect(0, 0, size, size), (IntPtr)p, pixels.Length, size * 4);

        // O Avalonia entrega BGRA pré-multiplicado; o .ico espera a cor "pura"
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a is 0 or 255)
                continue;
            for (var c = 0; c < 3; c++)
                pixels[i + c] = (byte)Math.Min(255, pixels[i + c] * 255 / a);
        }

        var maskStride = (size + 31) / 32 * 4;
        using var dib = new MemoryStream();
        using var w = new BinaryWriter(dib);
        w.Write(40u);                       // tamanho do cabeçalho
        w.Write(size);                      // largura
        w.Write(size * 2);                  // altura: imagem + máscara
        w.Write((ushort)1);                 // planos
        w.Write((ushort)32);                // bits por pixel
        w.Write(0u);                        // sem compressão
        w.Write((uint)(pixels.Length + maskStride * size));
        w.Write(0); w.Write(0); w.Write(0u); w.Write(0u);
        for (var y = size - 1; y >= 0; y--) // linhas de baixo para cima
            w.Write(pixels, y * size * 4, size * 4);
        w.Write(new byte[maskStride * size]);
        w.Flush();
        return dib.ToArray();
    }

    /// <summary>
    /// Instala o ícone (tema hicolor) e a entrada no menu de aplicativos em ~/.local/share.
    /// Roda a cada início, então ícone e atalhos ficam sempre atualizados. Só no Linux.
    /// </summary>
    public static void InstallDesktopEntry()
    {
        if (OperatingSystem.IsWindows())
            return;

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

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "LenoShot";

    public static bool IsAutostartEnabled => OperatingSystem.IsWindows()
        ? Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunValue) is not null
        : File.Exists(AutostartFile);

    public static void SetAutostart(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindowsAutostart(enabled);
            return;
        }

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

    [SupportedOSPlatform("windows")]
    private static void SetWindowsAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe)
            key.SetValue(RunValue, $"\"{exe}\"");
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    // ───────────── Atalhos globais ─────────────

    /// <summary>Atalhos globais do app (no GNOME, cada um é uma entrada de atalho personalizado).</summary>
    public enum ShortcutSlot { Capture, Zoom }

    private static string SlotCommandName(ShortcutSlot slot) => slot == ShortcutSlot.Capture ? "capture" : "record-zoom";

    private static Action<string>? s_runCommand;

    /// <summary>
    /// Liga os atalhos globais salvos. No Windows o próprio app registra os atalhos e executa o
    /// comando; no Linux o GNOME chama `LenoShot capture`, então não há nada a fazer.
    /// </summary>
    public static void RegisterShortcuts(Action<string> runCommand)
    {
        s_runCommand = runCommand;
        if (!OperatingSystem.IsWindows())
            return;

        foreach (var slot in Enum.GetValues<ShortcutSlot>())
        {
            var accelerator = GetSavedShortcut(slot);
            if (!RegisterWindowsShortcut(slot, accelerator))
                Console.Error.WriteLine($"[LenoShot] atalho {accelerator} indisponível (em uso por outro programa?)");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool RegisterWindowsShortcut(ShortcutSlot slot, string? accelerator) =>
        Win32Hotkeys.Register((int)slot + 1, accelerator, () => s_runCommand?.Invoke(SlotCommandName(slot)));

    private static string? GetSavedShortcut(ShortcutSlot slot) =>
        slot == ShortcutSlot.Capture ? AppSettings.Current.CaptureShortcut : AppSettings.Current.ZoomShortcut;

    /// <summary>
    /// Enquanto um atalho é gravado na tela de configurações: entrega a tecla Print Screen (com
    /// modificadores) a <paramref name="onPressed"/>, já na thread da interface. No Windows a tecla
    /// não chega à janela (o sistema a usa para a Ferramenta de Captura); no Linux não é preciso.
    /// </summary>
    public static void ListenPrintScreen(Action<string>? onPressed)
    {
        if (OperatingSystem.IsWindows())
            Win32Hotkeys.ListenPrintScreen(onPressed is null
                ? null
                : accelerator => Dispatcher.UIThread.Post(() => onPressed(accelerator)));
    }

    private const string KeybindingsSchema = "org.gnome.settings-daemon.plugins.media-keys";
    private const string KeybindingBase = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/";

    private static string SlotPath(ShortcutSlot slot) =>
        KeybindingBase + (slot == ShortcutSlot.Capture ? "LenoShot/" : "LenoShot-zoom/");

    private static string SlotSchema(ShortcutSlot slot) =>
        "org.gnome.settings-daemon.plugins.media-keys.custom-keybinding:" + SlotPath(slot);

    private static string SlotName(ShortcutSlot slot) => slot == ShortcutSlot.Capture ? "LenoShot" : "LenoShot — zoom na gravação";

    private static string SlotCommand(ShortcutSlot slot) => $"{LauncherPath} {SlotCommandName(slot)}";

    /// <summary>Atalho atual no formato do GNOME, por exemplo "&lt;Shift&gt;Print".</summary>
    public static async Task<string?> GetShortcutAsync(ShortcutSlot slot = ShortcutSlot.Capture)
    {
        if (OperatingSystem.IsWindows())
            return GetSavedShortcut(slot) is { Length: > 0 } saved ? saved : null;

        var value = await Run("gsettings", ["get", SlotSchema(slot), "binding"]);
        value = value?.Trim().Trim('\'');
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Troca o atalho (vazio desliga). Devolve false se o sistema recusou a combinação
    /// (no Windows, quando outro programa já usa o atalho).
    /// </summary>
    public static async Task<bool> SetShortcutAsync(string accelerator, ShortcutSlot slot = ShortcutSlot.Capture)
    {
        if (OperatingSystem.IsWindows())
            return RegisterWindowsShortcut(slot, accelerator);

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
        return true;
    }

    /// <summary>
    /// Linux (GNOME): na primeira execução assume a tecla Print para a captura, mesmo que outro
    /// programa (inclusive o atalho de captura do próprio GNOME) já a use. Também remove entradas de
    /// atalho de versões antigas, que ficavam em "leno-shot/" e continuavam presas à tecla.
    /// Depois da primeira vez a escolha do usuário é respeitada.
    /// </summary>
    public static async Task EnsureLinuxDefaultShortcutAsync()
    {
        if (OperatingSystem.IsWindows() || await Run("gsettings", ["list-schemas"]) is not { } schemas
            || !schemas.Contains(KeybindingsSchema))
            return;

        await RemoveLegacyShortcutsAsync();

        if (AppSettings.Current.LinuxShortcutInitialized)
            return;

        await FreeKeyAsync("Print");
        if (await SetShortcutAsync("Print", ShortcutSlot.Capture))
        {
            AppSettings.Current.LinuxShortcutInitialized = true;
            AppSettings.Current.Save();
        }
    }

    private static List<string> ParseStringList(string? gsettingsOutput) =>
        System.Text.RegularExpressions.Regex.Matches(gsettingsOutput ?? "", "'([^']*)'").Select(m => m.Groups[1].Value).ToList();

    private static Task SetStringList(string schema, string key, IEnumerable<string> items) =>
        Run("gsettings", ["set", schema, key, $"[{string.Join(", ", items.Select(i => $"'{i}'"))}]"]);

    private static async Task RemoveLegacyShortcutsAsync()
    {
        var all = ParseStringList(await Run("gsettings", ["get", KeybindingsSchema, "custom-keybindings"]));
        var legacy = all.Where(p => p.EndsWith("/leno-shot/") || p.EndsWith("/leno-shot-zoom/")).ToList();
        if (legacy.Count == 0)
            return;

        foreach (var path in legacy)
            await Run("gsettings", ["set", $"{KeybindingsSchema}.custom-keybinding:{path}", "binding", ""]);
        await SetStringList(KeybindingsSchema, "custom-keybindings", all.Except(legacy));
    }

    /// <summary>Solta o atalho (ex.: "Print") dos atalhos do GNOME e de outros atalhos personalizados.</summary>
    private static async Task FreeKeyAsync(string accelerator)
    {
        // Atalhos nativos de captura do GNOME (só os que existem nesta versão)
        foreach (var (schema, keys) in new[]
        {
            ("org.gnome.shell.keybindings", new[] { "show-screenshot-ui", "screenshot", "screenshot-window" }),
            (KeybindingsSchema, new[] { "screenshot", "window-screenshot", "area-screenshot" }),
        })
        {
            var existing = (await Run("gsettings", ["list-keys", schema]) ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var key in keys.Where(existing.Contains))
            {
                var bindings = ParseStringList(await Run("gsettings", ["get", schema, key]));
                if (bindings.Contains(accelerator))
                    await SetStringList(schema, key, bindings.Where(b => b != accelerator));
            }
        }

        // Atalhos personalizados de outros programas na mesma tecla
        var ours = new[] { SlotPath(ShortcutSlot.Capture), SlotPath(ShortcutSlot.Zoom) };
        foreach (var path in ParseStringList(await Run("gsettings", ["get", KeybindingsSchema, "custom-keybindings"])).Except(ours))
        {
            var schema = $"{KeybindingsSchema}.custom-keybinding:{path}";
            if ((await Run("gsettings", ["get", schema, "binding"]))?.Trim().Trim('\'') == accelerator)
                await Run("gsettings", ["set", schema, "binding", ""]);
        }
    }

    /// <summary>Guarda o atalho escolhido (no Windows; no Linux o GNOME já guarda).</summary>
    public static void SaveShortcut(string? accelerator, ShortcutSlot slot)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (slot == ShortcutSlot.Capture)
            AppSettings.Current.CaptureShortcut = accelerator ?? "";
        else
            AppSettings.Current.ZoomShortcut = accelerator ?? "";
        AppSettings.Current.Save();
    }

    // ───────────── Utilitário ─────────────

    internal static Task<string?> RunAsync(string file, IEnumerable<string> args) => Run(file, args);

    private static async Task<string?> Run(string file, IEnumerable<string> args)
    {
        try
        {
            // CreateNoWindow: no Windows, sem isso cada programa de console abre uma janela preta
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
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
