using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;

namespace LenoShot.Services;

/// <summary>Destinos de uma captura pronta: área de transferência ou arquivo, com notificação.</summary>
public static class CaptureOutput
{
    // No X11 a área de transferência é "preguiçosa": a imagem só é codificada quando
    // outro app pede para colar. Por isso o bitmap precisa continuar vivo depois de copiar.
    private static Bitmap? s_clipboardBitmap;

    /// <summary>Copia a imagem. Assume a posse do bitmap (não descarte depois).</summary>
    public static async Task CopyAsync(TopLevel topLevel, Bitmap bitmap)
    {
        if (topLevel.Clipboard is null)
            return;

        await topLevel.Clipboard.SetBitmapAsync(bitmap);

        s_clipboardBitmap?.Dispose();
        s_clipboardBitmap = bitmap;

        if (AppSettings.Current.NotifyOnCopy)
            SystemIntegration.Notify("Captura copiada",
                $"Imagem de {bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} pronta para colar");
    }

    /// <summary>Salva como PNG na pasta configurada e devolve o caminho.</summary>
    public static async Task<string> SaveAsync(Bitmap bitmap)
    {
        var dir = AppSettings.Current.SaveFolder;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"leno-shot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");

        await Task.Run(() => bitmap.Save(file, PngBitmapEncoderOptions.Default));

        if (AppSettings.Current.NotifyOnSave)
            SystemIntegration.Notify("Captura salva", file, imagePath: file);
        return file;
    }
}
