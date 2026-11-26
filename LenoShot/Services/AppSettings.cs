using System.Text.Json;
using System.Text.Json.Serialization;

namespace LenoShot.Services;

/// <summary>
/// Preferências do usuário, guardadas em ~/.config/LenoShot/settings.json
/// (no Windows, %APPDATA%\LenoShot\settings.json).
/// </summary>
public sealed class AppSettings
{
    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LenoShot");

    // ───────────── Gravação de tela ─────────────

    public string VideoFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LenoShot");
    public int RecordFps { get; set; } = 30;

    /// <summary>Altura máxima do vídeo em pixels (1080, 720, 480); 0 = tamanho original da área.</summary>
    public int RecordMaxHeight { get; set; }
    public bool RecordMic { get; set; }
    public bool RecordSystemAudio { get; set; } = true;

    /// <summary>Formato do arquivo: "mp4", "webm" ou "gif".</summary>
    public string RecordFormat { get; set; } = "mp4";

    /// <summary>Duração máxima de um GIF, em segundos (a gravação para sozinha ao chegar nela).</summary>
    public int GifMaxSeconds { get; set; } = 10;

    /// <summary>Cor de destaque do app (botões, seleção, ícone).</summary>
    public string AccentColor { get; set; } = AccentTheme.DefaultHex;

    public string DefaultColor { get; set; } = "#FF453A";
    public double DefaultThickness { get; set; } = 4;
    public bool NotifyOnSave { get; set; } = true;
    public bool NotifyOnCopy { get; set; } = true;

    /// <summary>Segundos de espera quando a captura é aberta pelo atalho (0 = imediata).</summary>
    public int ShortcutDelaySeconds { get; set; }

    // ───────────── Atalhos globais (só Windows; no Linux ficam nos atalhos do GNOME) ─────────────

    /// <summary>Atalho da captura no formato do GNOME (ex.: "Print", "&lt;Control&gt;&lt;Shift&gt;s"); vazio = nenhum.</summary>
    public string? CaptureShortcut { get; set; } = "Print";
    /// <summary>Atalho do zoom na gravação; vazio = nenhum.</summary>
    public string? ZoomShortcut { get; set; }

    /// <summary>Linux: o atalho padrão (Print) já foi aplicado uma vez; depois disso vale a escolha do usuário.</summary>
    public bool LinuxShortcutInitialized { get; set; }

    // ───────────── Persistência ─────────────

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LenoShot", "settings.json");

    private static AppSettings? s_current;

    public static AppSettings Current => s_current ??= Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings)
                       ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LenoShot] configurações inválidas, usando padrões: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings));
    }
}

// Geração de código do System.Text.Json: necessária para funcionar com Native AOT (sem reflexão).
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
