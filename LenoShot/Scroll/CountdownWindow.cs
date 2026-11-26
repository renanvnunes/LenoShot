using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace LenoShot.Scroll;

/// <summary>
/// Contagem regressiva circular antes da captura com atraso. Clicar nela cancela.
/// </summary>
public sealed class CountdownWindow : FloatingWindow
{
    private readonly CountdownRing _ring;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly TimeSpan _total;
    private DateTime _start;

    public event EventHandler? Completed;
    public event EventHandler? Cancelled;

    public CountdownWindow(int seconds)
    {
        Title = "Leno Shot — contagem";
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowActivated = false;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Clique para cancelar");

        _total = TimeSpan.FromSeconds(seconds);
        _ring = new CountdownRing();
        Content = _ring;

        _timer.Tick += (_, _) => Tick();
        Opened += (_, _) =>
        {
            _start = DateTime.UtcNow;
            _timer.Start();
            Tick();
        };
        PointerPressed += (_, _) =>
        {
            _timer.Stop();
            Close();
            Cancelled?.Invoke(this, EventArgs.Empty);
        };
    }

    private void Tick()
    {
        var elapsed = DateTime.UtcNow - _start;
        var remaining = _total - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            _timer.Stop();
            Close();
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _ring.Seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        _ring.Progress = elapsed / _total;
        _ring.InvalidateVisual();
    }

    private sealed class CountdownRing : Control
    {
        private static readonly Typeface Font = new("Inter", FontStyle.Normal, FontWeight.Thin);

        public int Seconds { get; set; }
        public double Progress { get; set; }

        public override void Render(DrawingContext context)
        {
            var size = Math.Min(Bounds.Width, Bounds.Height);
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var radius = size / 2 - 2;

            context.DrawEllipse(new SolidColorBrush(Color.Parse("#1C1C1E")), new Pen(new SolidColorBrush(Color.Parse("#26FFFFFF")), 1), center, radius, radius);

            // Anel de progresso: trilho + arco que vai se fechando
            var ringRadius = radius - 9;
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#33FFFFFF")), 4), center, ringRadius, ringRadius);
            var remaining = Math.Clamp(1 - Progress, 0, 1);
            if (remaining > 0.001)
            {
                var angle = remaining * 2 * Math.PI;
                var startPoint = new Point(center.X, center.Y - ringRadius);
                var endPoint = new Point(center.X + ringRadius * Math.Sin(angle), center.Y - ringRadius * Math.Cos(angle));
                var arc = new StreamGeometry();
                using (var g = arc.Open())
                {
                    g.BeginFigure(startPoint, false);
                    g.ArcTo(endPoint, new Size(ringRadius, ringRadius), 0, angle > Math.PI, SweepDirection.Clockwise);
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(new SolidColorBrush(LenoShot.Services.AccentTheme.Color), 4, lineCap: PenLineCap.Round), arc);
            }

            var text = new FormattedText(Seconds.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Font, size * 0.36, Brushes.White);
            context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
        }
    }
}
