using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace LenoShot.Overlay;

/// <summary>
/// Ícone de traço fino desenhado numa grade 24×24 (estilo Lucide).
/// Usa a cor de texto herdada do botão, então reage a hover/seleção.
/// </summary>
public sealed class LineIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LineIcon>();

    static LineIcon()
    {
        AffectsRender<LineIcon>(DataProperty, ForegroundProperty);
    }

    public LineIcon()
    {
        Width = 18;
        Height = 18;
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null)
            return;

        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        var pen = new Pen(Foreground ?? Brushes.White, 1.6 / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, pen, Data);
    }
}
