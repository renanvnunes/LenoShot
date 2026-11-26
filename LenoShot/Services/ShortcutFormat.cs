using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Input;

namespace LenoShot.Services;

/// <summary>
/// Converte teclas do Avalonia para o formato de atalho do GNOME e para texto legível.
/// O mesmo formato é usado para guardar os atalhos no Windows.
/// </summary>
public static partial class ShortcutFormat
{
    /// <summary>
    /// Gera o atalho do GNOME (ex.: "&lt;Control&gt;&lt;Shift&gt;s") ou null se a tecla não puder ser usada.
    /// </summary>
    public static string? ToAccelerator(Key key, KeyModifiers modifiers)
    {
        var keyName = KeyName(key);
        if (keyName is null)
            return null;

        // Letras e números sozinhos atrapalhariam a digitação normal: exigem um modificador
        var needsModifier = keyName.Length == 1;
        if (needsModifier && (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
            return null;

        var sb = new StringBuilder();
        if (modifiers.HasFlag(KeyModifiers.Control)) sb.Append("<Control>");
        if (modifiers.HasFlag(KeyModifiers.Alt)) sb.Append("<Alt>");
        if (modifiers.HasFlag(KeyModifiers.Shift)) sb.Append("<Shift>");
        if (modifiers.HasFlag(KeyModifiers.Meta)) sb.Append("<Super>");
        sb.Append(keyName);
        return sb.ToString();
    }

    /// <summary>"&lt;Control&gt;&lt;Shift&gt;s" → "Ctrl + Shift + S".</summary>
    public static string ToDisplay(string? accelerator)
    {
        if (string.IsNullOrEmpty(accelerator))
            return "Nenhum";

        var (modifiers, key) = Parse(accelerator);
        var parts = modifiers
            .Select(m => m switch
            {
                "Control" or "Primary" or "Ctrl" => "Ctrl",
                "Super" => SuperName,
                var other => other,
            })
            .ToList();

        parts.Add(key.Length == 1 ? key.ToUpperInvariant() : key);
        return string.Join(" + ", parts);
    }

    /// <summary>Nome da tecla Super para o usuário ("Win" no Windows).</summary>
    public static string SuperName => OperatingSystem.IsWindows() ? "Win" : "Super";

    /// <summary>"&lt;Control&gt;&lt;Shift&gt;s" → (["Control", "Shift"], "s").</summary>
    public static (List<string> Modifiers, string Key) Parse(string accelerator) =>
        (ModifierRegex().Matches(accelerator).Select(m => m.Groups[1].Value).ToList(),
         ModifierRegex().Replace(accelerator, ""));

    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => $"F{key - Key.F1 + 1}",
        Key.PrintScreen or Key.Print => "Print",
        Key.Pause => "Pause",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "Page_Up",
        Key.PageDown => "Page_Down",
        _ => null,
    };

    [GeneratedRegex("<([^>]+)>")]
    private static partial Regex ModifierRegex();
}
