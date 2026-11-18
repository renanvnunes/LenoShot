using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LenoShot.Services;

/// <summary>
/// Ícone do app desenhado em vetor: "squircle" chapado em índigo, cantos de visor e um
/// obturador no centro. Gera qualquer tamanho sem arquivos de imagem.
/// </summary>
internal static class AppIcon
{
    public static WindowIcon Create()
    {
        using var bitmap = CreateBitmap(128);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    public static Bitmap CreateBitmap(int size = 128)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
        using (var ctx = bitmap.CreateDrawingContext())
        using (ctx.PushTransform(Matrix.CreateScale(size / 100.0, size / 100.0)))
            Draw(ctx);
        return bitmap;
    }

    /// <summary>Desenha numa grade de 100×100: cor chapada, sem degradê.</summary>
    private static void Draw(DrawingContext ctx)
    {
        // "Squircle" (superelipse), o formato dos ícones modernos do macOS
        ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#5B5BD6")), null, Squircle(50, 50, 44, 5));

        // Cantos do visor, finos e com pontas arredondadas
        var pen = new Pen(Brushes.White, 5.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        const double a = 27, b = 73, l = 12;
        DrawCorner(ctx, pen, new Point(a, a + l), new Point(a, a), new Point(a + l, a));
        DrawCorner(ctx, pen, new Point(b - l, a), new Point(b, a), new Point(b, a + l));
        DrawCorner(ctx, pen, new Point(a, b - l), new Point(a, b), new Point(a + l, b));
        DrawCorner(ctx, pen, new Point(b - l, b), new Point(b, b), new Point(b, b - l));

        // Obturador: um círculo sólido no centro
        ctx.DrawEllipse(Brushes.White, null, new Point(50, 50), 8.5, 8.5);
    }

    /// <summary>Superelipse |x/r|^n + |y/r|^n = 1.</summary>
    private static StreamGeometry Squircle(double cx, double cy, double r, double n)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        const int steps = 120;
        for (var i = 0; i <= steps; i++)
        {
            var t = i * 2 * Math.PI / steps;
            var cos = Math.Cos(t);
            var sin = Math.Sin(t);
            var x = cx + r * Math.Sign(cos) * Math.Pow(Math.Abs(cos), 2 / n);
            var y = cy + r * Math.Sign(sin) * Math.Pow(Math.Abs(sin), 2 / n);
            if (i == 0) g.BeginFigure(new Point(x, y), true);
            else g.LineTo(new Point(x, y));
        }
        g.EndFigure(true);
        return geometry;
    }

    private static void DrawCorner(DrawingContext ctx, IPen pen, Point p1, Point corner, Point p2)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(p1, false);
            g.LineTo(corner);
            g.LineTo(p2);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geometry);
    }
}
