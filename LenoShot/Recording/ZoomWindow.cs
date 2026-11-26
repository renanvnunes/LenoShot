using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LenoShot.Capture;
using LenoShot.Scroll;

namespace LenoShot.Recording;

/// <summary>
/// Zoom na gravação: congela a área, desfoca e escurece o fundo e mostra uma lupa que segue o
/// mouse. Como o modo desenho, é uma janela opaca com a imagem congelada (os grabs do X11 ignoram
/// transparência), então a tela do vídeo fica parada enquanto o zoom está ligado. A lupa e o
/// desfoque entram e saem com animação.
/// </summary>
public sealed class ZoomWindow : FloatingWindow
{
    private readonly PixelRect _area;
    private readonly double _scaling;
    private readonly Image _sharp = new() { Stretch = Stretch.Fill };
    // Desfoque calculado uma vez ao ligar o zoom (imagem reduzida e esticada com interpolação).
    // Um BlurEffect refaria o desfoque da tela inteira a cada quadro da animação: pesado, e
    // atrapalhava a própria gravação.
    private readonly Image _blurred = new() { Stretch = Stretch.Fill, Opacity = 0 };
    private readonly Border _dim = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
        Opacity = 0,
        IsHitTestVisible = false,
    };
    private readonly ZoomLens _lens = new();
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Bitmap? _bitmap;
    private Bitmap? _blurBitmap;
    private double _progress; // 0 = sem efeito, 1 = efeito completo
    private double _target;

    public ZoomWindow(PixelRect area, double scaling) : base(asNotification: true)
    {
        _area = area;
        _scaling = scaling;

        Title = "LenoShot — zoom";
        Background = Brushes.Black;
        ShowActivated = false;

        var root = new Grid { ClipToBounds = true, Width = area.Width / scaling, Height = area.Height / scaling };
        root.Children.Add(_sharp);
        root.Children.Add(_blurred);
        root.Children.Add(_dim);
        root.Children.Add(_lens);
        Content = root;

        RenderOptions.SetBitmapInterpolationMode(_blurred, BitmapInterpolationMode.HighQuality);
        UseOverrideRedirect(); // posição exata sobre a tela viva

        _lens.ExitRequested += (_, _) => SetZoom(false);
        _animation.Tick += (_, _) => Step();
        PlaceAt(area, scaling);
    }

    public bool IsOn { get; private set; }

    /// <summary>Disparado quando o zoom liga ou desliga.</summary>
    public event EventHandler? ZoomChanged;

    public void SetZoom(bool on)
    {
        if (IsOn == on)
            return;

        if (on)
        {
            // Se a janela ainda está sumindo, só reverte a animação (recapturar pegaria a própria janela)
            if (!IsVisible)
            {
                var snapshot = X11Capture.CaptureRect(_area);
                if (snapshot is null)
                    return;

                var old = _bitmap;
                var oldBlur = _blurBitmap;
                _bitmap = snapshot.ToBitmap();
                _blurBitmap = snapshot.Downscale(Math.Max(96, snapshot.Width / 12)).ToBitmap();
                _sharp.Source = _lens.Source = _bitmap;
                _blurred.Source = _blurBitmap;
                old?.Dispose();
                oldBlur?.Dispose();

                _progress = 0;
                ApplyProgress();
                _lens.Pointer = ToLocal(X11Input.GetPointer());

                PlaceAt(_area, _scaling);
                Show();
                FocusInput();
                _lens.Focus();
            }
            _target = 1;
            _animation.Start();
        }
        else
        {
            _target = 0;
            _animation.Start();
        }

        IsOn = on;
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Some na hora, sem animação (ao parar a gravação).</summary>
    public void Dismiss()
    {
        _animation.Stop();
        _progress = _target = 0;
        IsOn = false;
        if (IsVisible)
            Hide();
    }

    private Point ToLocal(PixelPoint p) => new(
        Math.Clamp((p.X - _area.X) / _scaling, 0, _area.Width / _scaling),
        Math.Clamp((p.Y - _area.Y) / _scaling, 0, _area.Height / _scaling));

    private void Step()
    {
        _progress += (_target - _progress) * 0.28;
        if (Math.Abs(_target - _progress) < 0.01)
        {
            _progress = _target;
            _animation.Stop();
            if (_target == 0 && IsVisible)
                Hide();
        }
        ApplyProgress();
    }

    private void ApplyProgress()
    {
        _blurred.Opacity = _progress;
        _dim.Opacity = _progress;
        _lens.Progress = _progress;
        _lens.InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            SetZoom(false);
            e.Handled = true;
        }
    }

    /// <summary>A lupa: círculo com a imagem ampliada em volta do mouse. Recebe o mouse da janela.</summary>
    private sealed class ZoomLens : Control
    {
        private const double BaseRadius = 170;

        public Bitmap? Source { get; set; }
        public Point Pointer { get; set; }
        public double Zoom { get; private set; } = 2.5;
        public double Progress { get; set; }

        public event EventHandler? ExitRequested;

        public ZoomLens()
        {
            Focusable = true;
            FocusAdorner = null;
            Cursor = new Cursor(StandardCursorType.Cross);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            Pointer = e.GetPosition(this);
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

        // A roda do mouse muda a ampliação
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            Zoom = Math.Clamp(Zoom + (e.Delta.Y > 0 ? 0.25 : -0.25), 1.5, 6);
            InvalidateVisual();
            e.Handled = true;
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            context.FillRectangle(Brushes.Transparent, bounds); // recebe o mouse
            if (Source is null || Progress < 0.01)
                return;

            var eased = 1 - Math.Pow(1 - Progress, 3);
            var radius = BaseRadius * eased;
            var zoom = 1 + (Zoom - 1) * eased;
            var rect = new Rect(Pointer.X - radius, Pointer.Y - radius, radius * 2, radius * 2);

            // Sombra suave
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), null, Pointer + new Vector(0, 8), radius + 8, radius + 8);

            // Imagem ampliada em volta do ponto do mouse, recortada no círculo
            using (context.PushGeometryClip(new EllipseGeometry(rect)))
            using (context.PushTransform(
                Matrix.CreateTranslation(-Pointer.X, -Pointer.Y) * Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(Pointer.X, Pointer.Y)))
            {
                context.DrawImage(Source, bounds);
            }

            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 3), Pointer, radius, radius);
        }
    }
}
