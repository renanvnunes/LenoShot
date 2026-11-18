using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LenoShot.Capture;

namespace LenoShot.Overlay;

public enum Tool
{
    None,
    Pen,
    Arrow,
    Rectangle,
    Ellipse,
    Counter,
    Marker,
    Text,
    Pixelate,
}

/// <summary>
/// Uma anotação desenhada sobre a captura. As coordenadas são em unidades lógicas
/// da sobreposição; a exportação aplica a escala do monitor.
/// </summary>
public abstract class Annotation
{
    protected Annotation(Color color, double size)
    {
        Color = color;
        Size = size;
    }

    public Color Color { get; }

    /// <summary>Espessura; pode mudar enquanto a forma está sendo desenhada (roda do mouse).</summary>
    public double Size { get; set; }

    /// <summary>Atualiza a forma enquanto o usuário arrasta.</summary>
    public virtual void Drag(Point start, Point current) { }

    /// <summary>Anotações muito pequenas (um clique acidental) são descartadas.</summary>
    public virtual bool IsEmpty => false;

    public abstract void Render(DrawingContext context);

    protected Pen CreatePen(double thickness, byte alpha = 255, PenLineCap cap = PenLineCap.Round) =>
        new(new SolidColorBrush(Color.FromArgb(alpha, Color.R, Color.G, Color.B)), thickness,
            lineCap: cap, lineJoin: PenLineJoin.Round);
}

public class PenAnnotation(Color color, double size) : Annotation(color, size)
{
    protected readonly List<Point> Points = [];

    public override bool IsEmpty => Points.Count < 2;

    public override void Drag(Point start, Point current)
    {
        if (Points.Count == 0)
            Points.Add(start);
        Points.Add(current);
    }

    public override void Render(DrawingContext context)
    {
        if (Points.Count < 2)
            return;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(Points[0], false);
            for (var i = 1; i < Points.Count; i++)
                g.LineTo(Points[i]);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, GetPen(), geometry);
    }

    protected virtual Pen GetPen() => CreatePen(Size);
}

/// <summary>Marca-texto: traço largo e translúcido.</summary>
public sealed class MarkerAnnotation(Color color, double size) : PenAnnotation(color, size)
{
    protected override Pen GetPen() => CreatePen(Size * 4 + 8, alpha: 100, cap: PenLineCap.Square);
}

public sealed class ArrowAnnotation(Color color, double size) : Annotation(color, size)
{
    private Point _from;
    private Point _to;

    public override bool IsEmpty => Distance(_from, _to) < 6;

    public override void Drag(Point start, Point current)
    {
        _from = start;
        _to = current;
    }

    public override void Render(DrawingContext context)
    {
        var length = Distance(_from, _to);
        if (length < 1)
            return;

        // Cabeça proporcional à espessura, mas nunca maior que a própria seta
        var headLength = Math.Min(Size * 4 + 10, length * 0.6);
        var headWidth = headLength * 0.75;
        var dir = new Vector((_to.X - _from.X) / length, (_to.Y - _from.Y) / length);
        var normal = new Vector(-dir.Y, dir.X);

        var baseCenter = _to - dir * headLength;
        var left = baseCenter + normal * (headWidth / 2);
        var right = baseCenter - normal * (headWidth / 2);

        // A linha termina dentro da cabeça para a ponta ficar afiada
        context.DrawLine(CreatePen(Size), _from, _to - dir * (headLength * 0.6));

        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(_to, true);
            g.LineTo(left);
            g.LineTo(right);
            g.EndFigure(true);
        }

        context.DrawGeometry(new SolidColorBrush(Color), CreatePen(1), head);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}

public sealed class RectangleAnnotation(Color color, double size) : Annotation(color, size)
{
    private Rect _rect;

    public override bool IsEmpty => _rect.Width < 3 || _rect.Height < 3;

    public override void Drag(Point start, Point current) => _rect = new Rect(start, current).Normalize();

    public override void Render(DrawingContext context) =>
        context.DrawRectangle(null, CreatePen(Size), new RoundedRect(_rect, 3));
}

public sealed class EllipseAnnotation(Color color, double size) : Annotation(color, size)
{
    private Rect _rect;

    public override bool IsEmpty => _rect.Width < 3 || _rect.Height < 3;

    public override void Drag(Point start, Point current) => _rect = new Rect(start, current).Normalize();

    public override void Render(DrawingContext context) =>
        context.DrawEllipse(null, CreatePen(Size), _rect.Center, _rect.Width / 2, _rect.Height / 2);
}

/// <summary>Marcador numerado (1, 2, 3…) para indicar passos numa captura.</summary>
public sealed class CounterAnnotation(Color color, double size, Point center, int number) : Annotation(color, size)
{
    private static readonly Typeface Font = new("Inter", FontStyle.Normal, FontWeight.Medium);

    public int Number { get; } = number;
    public double Radius => Size * 1.5 + 11;

    public override void Render(DrawingContext context)
    {
        // Sombra suave para destacar o marcador em qualquer fundo
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, center + new Vector(0, 1.5), Radius + 1, Radius + 1);
        context.DrawEllipse(new SolidColorBrush(Color), new Pen(Brushes.White, Math.Max(1.5, Radius * 0.12)), center, Radius, Radius);

        var text = new FormattedText(Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Font, Radius * 1.05, IsLight(Color) ? new SolidColorBrush(Color.Parse("#1C1C1E")) : Brushes.White);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    // Luminância relativa: em cores claras (amarelo, branco) o número fica escuro
    private static bool IsLight(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B > 150;
}

public sealed class TextAnnotation(Color color, double size, Point origin) : Annotation(color, size)
{
    private static readonly Typeface Font = new("Inter", FontStyle.Normal, FontWeight.Normal);

    public Point Origin { get; } = origin;
    public string Text { get; set; } = "";
    public double FontSize => Size * 3 + 12;

    public override bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    public FormattedText Format(string text) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, FontSize, new SolidColorBrush(Color));

    public override void Render(DrawingContext context)
    {
        if (Text.Length > 0)
            context.DrawText(Format(Text), Origin);
    }
}

/// <summary>Pixeliza a área para esconder informações sensíveis.</summary>
public sealed class PixelateAnnotation(ScreenImage source, double pixelScale) : Annotation(Colors.Transparent, 0)
{
    private Rect _rect;
    private Bitmap? _pixelated;

    public override bool IsEmpty => _rect.Width < 3 || _rect.Height < 3;

    public override void Drag(Point start, Point current)
    {
        _rect = new Rect(start, current).Normalize();
        _pixelated?.Dispose();
        _pixelated = null; // recalculado no próximo Render
    }

    public override void Render(DrawingContext context)
    {
        if (IsEmpty)
            return;

        _pixelated ??= BuildPixelated();
        if (_pixelated is null)
            return;

        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            context.DrawImage(_pixelated, _rect);
    }

    private WriteableBitmap? BuildPixelated()
    {
        var px = new PixelRect(
            (int)(_rect.X * pixelScale), (int)(_rect.Y * pixelScale),
            (int)(_rect.Width * pixelScale), (int)(_rect.Height * pixelScale))
            .Intersect(new PixelRect(0, 0, source.Width, source.Height));
        if (px.Width < 1 || px.Height < 1)
            return null;

        // Cada "bloco" vira um pixel da imagem reduzida, com a média das cores
        var block = Math.Max(4, (int)(10 * pixelScale));
        var w = Math.Max(1, (px.Width + block - 1) / block);
        var h = Math.Max(1, (px.Height + block - 1) / block);
        var small = new byte[w * h * 4];

        for (var by = 0; by < h; by++)
        for (var bx = 0; bx < w; bx++)
        {
            long b = 0, g = 0, r = 0, count = 0;
            var x0 = px.X + bx * block;
            var y0 = px.Y + by * block;
            var x1 = Math.Min(x0 + block, px.Right);
            var y1 = Math.Min(y0 + block, px.Bottom);

            for (var y = y0; y < y1; y++)
            {
                var row = y * source.Stride;
                for (var x = x0; x < x1; x++)
                {
                    var i = row + x * 4;
                    b += source.Pixels[i];
                    g += source.Pixels[i + 1];
                    r += source.Pixels[i + 2];
                    count++;
                }
            }

            var o = (by * w + bx) * 4;
            small[o] = (byte)(b / count);
            small[o + 1] = (byte)(g / count);
            small[o + 2] = (byte)(r / count);
            small[o + 3] = 255;
        }

        return new ScreenImage(w, h, small).ToBitmap();
    }
}
