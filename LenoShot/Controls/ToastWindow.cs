using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LenoShot.Scroll;
using LenoShot.Services;

namespace LenoShot.Controls;

/// <summary>
/// Aviso discreto no canto inferior direito da tela, no visual do app. Usado no Windows, onde não há
/// notify-send. Some sozinho; clicar fecha (e, se for uma captura salva, mostra o arquivo no Explorer).
/// </summary>
public sealed class ToastWindow : FloatingWindow
{
    private const double ToastWidth = 360;
    private const double ScreenMargin = 12;

    private static ToastWindow? s_current;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private Bitmap? _thumbnail;

    public static void Show(string title, string body, string? imagePath)
    {
        // Um aviso por vez: o novo substitui o anterior
        s_current?.Close();
        var toast = new ToastWindow(title, body, imagePath);
        s_current = toast;
        toast.Closed += (_, _) =>
        {
            if (s_current == toast)
                s_current = null;
        };
        toast.Open();
    }

    private ToastWindow(string title, string body, string? imagePath)
    {
        Title = "LenoShot — aviso";
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowActivated = false;

        var text = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = title, FontSize = 13.5, FontWeight = FontWeight.SemiBold,
                    Foreground = SolidColorBrush.Parse("#F5F5F7"),
                },
                new TextBlock
                {
                    Text = body, FontSize = 12, Foreground = SolidColorBrush.Parse("#AEAEB2"),
                    TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2,
                },
            },
        };

        Control picture;
        if (imagePath is not null && LoadThumbnail(imagePath) is { } thumbnail)
        {
            _thumbnail = thumbnail;
            picture = new Border
            {
                Width = 56, Height = 56, CornerRadius = new CornerRadius(6), ClipToBounds = true,
                Child = new Image { Source = thumbnail, Stretch = Stretch.UniformToFill },
            };
        }
        else
        {
            picture = new Image { Source = AppIcon.CreateBitmap(64), Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        Grid.SetColumn(text, 1);
        row.Children.Add(picture);
        row.Children.Add(text);

        Content = new Border
        {
            Width = ToastWidth,
            Background = SolidColorBrush.Parse("#1C1C1E"),
            BorderBrush = SolidColorBrush.Parse("#26FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Child = row,
        };

        if (imagePath is not null)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, "Mostrar no Explorer");
        }
        PointerPressed += (_, _) =>
        {
            if (imagePath is not null && File.Exists(imagePath))
                ShowInExplorer(imagePath);
            Close();
        };

        _timer.Tick += (_, _) => Close();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _thumbnail?.Dispose();
        };
    }

    private void Open()
    {
        var screen = Screens.Primary;
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1040);
        var scaling = screen?.Scaling ?? 1;

        var content = (Control)Content!;
        content.Measure(new Size(ToastWidth, double.PositiveInfinity));
        var width = (int)Math.Ceiling(ToastWidth * scaling);
        var height = (int)Math.Ceiling(content.DesiredSize.Height * scaling);
        var margin = (int)(ScreenMargin * scaling);

        PlaceAt(new PixelRect(area.Right - width - margin, area.Bottom - height - margin, width, height), scaling);
        Show();
        _timer.Start();
    }

    private static Bitmap? LoadThumbnail(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 112);
        }
        catch
        {
            return null;
        }
    }

    private static void ShowInExplorer(string path)
    {
        try
        {
            // O Explorer espera exatamente /select,"caminho" (não aceita o argumento todo entre aspas)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LenoShot] não foi possível abrir o Explorer: {ex.Message}");
        }
    }
}
