using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace LenoShot.Services;

/// <summary>Uma versão mais nova publicada nos Releases do GitHub.</summary>
public sealed record UpdateInfo(Version Version, string PageUrl, string AssetName, string AssetUrl, string? ChecksumsUrl);

/// <summary>Resultado de uma busca: <see cref="Update"/> é null quando já está na última versão ou houve erro.</summary>
public sealed record UpdateCheck(UpdateInfo? Update, string? Error);

/// <summary>
/// Busca e instala novas versões a partir dos Releases do GitHub. O instalador só é executado depois
/// de conferir o SHA-256 contra o arquivo SHA256SUMS do próprio release (tudo via HTTPS, e só de
/// endereços do repositório do app).
/// </summary>
public static class UpdateService
{
    private const string Repo = "renanvnunes/LenoShot";
    private const string DownloadPrefix = $"https://github.com/{Repo}/releases/download/";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public const string ReleasesUrl = $"https://github.com/{Repo}/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("LenoShot-updater");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Nova versão encontrada na última busca (null se nenhuma).</summary>
    public static UpdateInfo? Available { get; private set; }

    public static Version CurrentVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v
            ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0))
            : new Version(0, 0, 0);

    public static async Task<UpdateCheck> CheckAsync()
    {
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            var release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.ReleaseDto);

            AppSettings.Current.LastUpdateCheck = DateTime.UtcNow;
            AppSettings.Current.Save();

            if (release is null || !Version.TryParse(release.Tag.TrimStart('v', 'V'), out var version))
                return new UpdateCheck(null, "Resposta inesperada do GitHub");
            if (version <= CurrentVersion)
            {
                Available = null;
                return new UpdateCheck(null, null);
            }

            // O pacote certo para este sistema: instalador no Windows, .deb no Linux
            var suffix = OperatingSystem.IsWindows() ? "-setup.exe" : ".deb";
            var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                                                           && a.Url.StartsWith(DownloadPrefix, StringComparison.Ordinal));
            if (asset is null)
                return new UpdateCheck(null, $"A versão {version} não tem pacote para este sistema");

            var sums = release.Assets.FirstOrDefault(a => a.Name == "SHA256SUMS" && a.Url.StartsWith(DownloadPrefix, StringComparison.Ordinal));
            Available = new UpdateInfo(version, release.PageUrl, Path.GetFileName(asset.Name), asset.Url, sums?.Url);
            return new UpdateCheck(Available, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new UpdateCheck(null, "Nenhum release publicado ainda");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LenoShot] falha ao buscar atualização: {ex.Message}");
            return new UpdateCheck(null, "Não foi possível consultar o GitHub");
        }
    }

    /// <summary>
    /// Chamada ao iniciar: com "atualizar automaticamente" ligado, busca no máximo uma vez a cada 24h.
    /// No Windows instala sozinho; no Linux a instalação pede a senha, então só avisa.
    /// </summary>
    public static async Task AutoUpdateAsync()
    {
        var settings = AppSettings.Current;
        if (!settings.AutoUpdate || DateTime.UtcNow - settings.LastUpdateCheck < CheckInterval)
            return;

        if ((await CheckAsync()).Update is not { } update)
            return;

        if (OperatingSystem.IsWindows())
        {
            SystemIntegration.Notify("Atualizando o LenoShot", $"Instalando a versão {update.Version}…");
            await InstallAsync(update);
        }
        else
        {
            SystemIntegration.Notify("Nova versão do LenoShot", $"A versão {update.Version} está disponível. Instale em Configurações → Atualizações.");
        }
    }

    /// <summary>Baixa, confere e instala. Devolve uma mensagem de erro, ou null se deu certo (o app é reiniciado).</summary>
    public static async Task<string?> InstallAsync(UpdateInfo update)
    {
        try
        {
            if (update.ChecksumsUrl is null)
                return "O release não tem SHA256SUMS; instalação recusada";

            var dir = Path.Combine(Path.GetTempPath(), $"LenoShot-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, update.AssetName);

            await using (var src = await Http.GetStreamAsync(update.AssetUrl))
            await using (var dst = File.Create(file))
                await src.CopyToAsync(dst);

            var expected = (await Http.GetStringAsync(update.ChecksumsUrl))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == update.AssetName)?[0];

            await using (var stream = File.OpenRead(file))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                if (expected is null || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(dir, true);
                    return "O arquivo baixado não confere com o SHA256SUMS; instalação cancelada";
                }
            }

            return OperatingSystem.IsWindows() ? InstallWindows(file) : await InstallLinuxAsync(file, update);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LenoShot] falha ao atualizar: {ex}");
            return "Não foi possível baixar a atualização";
        }
    }

    private static string? InstallWindows(string setup)
    {
        // O instalador fecha este app (comando "quit"), troca os arquivos e abre o app de novo
        Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = false });
        Shutdown();
        return null;
    }

    private static async Task<string?> InstallLinuxAsync(string deb, UpdateInfo update)
    {
        if (!SystemIntegration.IsSystemInstall)
        {
            await SystemIntegration.RunAsync("xdg-open", [update.PageUrl]);
            return "Esta cópia não veio do .deb; abri a página do release para baixar";
        }

        // pkexec mostra o diálogo de senha do sistema
        using var apt = Process.Start(new ProcessStartInfo("pkexec") { ArgumentList = { "apt-get", "install", "-y", deb } });
        if (apt is null)
            return "Não foi possível iniciar a instalação";
        await apt.WaitForExitAsync();
        if (apt.ExitCode != 0)
            return "A instalação foi cancelada ou falhou";

        // Reabre o app já atualizado depois que este processo sair
        Process.Start(new ProcessStartInfo("sh") { ArgumentList = { "-c", "sleep 1; exec /usr/bin/LenoShot daemon" } });
        Shutdown();
        return null;
    }

    private static void Shutdown() => Dispatcher.UIThread.Post(() =>
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    });

    internal sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
        [JsonPropertyName("html_url")] public string PageUrl { get; set; } = "";
        [JsonPropertyName("assets")] public List<AssetDto> Assets { get; set; } = [];
    }

    internal sealed class AssetDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string Url { get; set; } = "";
    }
}

// Geração de código do System.Text.Json (Native AOT não usa reflexão)
[JsonSerializable(typeof(UpdateService.ReleaseDto))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
