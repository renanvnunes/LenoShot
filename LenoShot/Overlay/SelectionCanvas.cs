using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LenoShot.Capture;

namespace LenoShot.Overlay;

/// <summary>
/// Mostra a captura congelada, controla a área selecionada e as anotações desenhadas sobre ela.
/// </summary>
public sealed class SelectionCanvas : Control
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
    private static readonly IPen Border = new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 1.5);
    private static readonly IBrush PillBackground = new SolidColorBrush(Color.FromArgb(220, 28, 28, 32));
    private static readonly Typeface LabelFont = new("Inter", FontStyle.Normal, FontWeight.Light);

    private readonly List<Annotation> _annotations = [];
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    [Flags]
    private enum Edges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

    private const double HandleMargin = 8;

    private Point _anchor;
    private bool _selecting;
    private Edges _adjusting;
    private Rect _adjustStart;
    private Annotation? _drawing;
    private TextAnnotation? _editingText;
    private string? _toast;
    private Point _lastPointer;

    public SelectionCanvas()
    {
        // Precisa de foco para receber o texto digitado (TextInput só vai ao elemento focado)
        Focusable = true;
        FocusAdorner = null;
        InputMethod.SetIsInputMethodEnabled(this, true);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast = null;
            InvalidateVisual();
        };
    }

    public ScreenImage? Source { get; set; }
    public Bitmap? Image { get; set; }

    /// <summary>Quantos pixels físicos existem por unidade lógica (escala do monitor).</summary>
    public double PixelScale { get; set; } = 1;

    public Rect? SelectionRect { get; private set; }

    /// <summary>
    /// Modo editor (imagem da captura com rolagem): a imagem inteira já é a seleção,
    /// sem escurecer, sem alças, e a roda do mouse rola (Ctrl + roda muda a espessura).
    /// </summary>
    public bool EditorMode { get; set; }

    /// <summary>Seleciona a imagem inteira (usado no modo editor).</summary>
    public void SelectAll()
    {
        SelectionRect = new Rect(0, 0, Width, Height);
        InvalidateVisual();
    }

    public Tool CurrentTool { get; set; } = Tool.None;
    public Color CurrentColor { get; set; } = Color.Parse("#FF453A");
    public double CurrentSize { get; set; } = 4;

    public bool IsEditingText => _editingText is not null;
    public bool CanUndo => _annotations.Count > 0;

    public event EventHandler? SelectionStarted;
    public event EventHandler? SelectionCompleted;
    public event EventHandler? AnnotationsChanged;

    /// <summary>Disparado quando a espessura muda pela roda do mouse (para a barra atualizar o controle).</summary>
    public event EventHandler? ThicknessChanged;

    public PixelRect? SelectionInPixels => SelectionRect is { } r
        ? new PixelRect(
            (int)Math.Round(r.X * PixelScale),
            (int)Math.Round(r.Y * PixelScale),
            (int)Math.Round(r.Width * PixelScale),
            (int)Math.Round(r.Height * PixelScale))
        : null;

    // ───────────────────────────── Mouse ─────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var pos = e.GetPosition(this);
        Focus();
        CommitText();

        if (EditorMode && CurrentTool == Tool.None)
            return; // no editor não há seleção para criar: escolha uma ferramenta

        if (CurrentTool == Tool.Pixelate && Source is null)
            return; // sem imagem congelada (desenho ao vivo) não há o que pixelizar

        var hit = CurrentTool == Tool.None ? HitTest(pos) : Edges.None;
        if (hit != Edges.None)
        {
            // Mover ou redimensionar a seleção existente
            _anchor = pos;
            _adjusting = hit;
            _adjustStart = SelectionRect!.Value;
            SelectionStarted?.Invoke(this, EventArgs.Empty);
        }
        else if (CurrentTool == Tool.None || SelectionRect is null)
        {
            _anchor = pos;
            _selecting = true;
            SelectionRect = null;
            SelectionStarted?.Invoke(this, EventArgs.Empty);
        }
        else if (CurrentTool == Tool.Text)
        {
            _editingText = new TextAnnotation(CurrentColor, CurrentSize, pos);
        }
        else if (CurrentTool == Tool.Counter)
        {
            var next = _annotations.OfType<CounterAnnotation>().Count() + 1;
            _annotations.Add(new CounterAnnotation(CurrentColor, CurrentSize, pos, next));
            AnnotationsChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _anchor = pos;
            _drawing = CreateAnnotation(CurrentTool);
        }

        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        _lastPointer = pos;

        if (_selecting)
        {
            SelectionRect = new Rect(_anchor, pos).Normalize();
            InvalidateVisual();
        }
        else if (_adjusting != Edges.None)
        {
            SelectionRect = Adjust(_adjustStart, _adjusting, pos - _anchor);
            InvalidateVisual();
        }
        else if (_drawing is not null)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _drawing is RectangleAnnotation or EllipseAnnotation)
                pos = Square(_anchor, pos);
            _drawing.Drag(_anchor, pos);
            InvalidateVisual();
        }
        else if (CurrentTool == Tool.None && !EditorMode)
        {
            Cursor = new Cursor(CursorFor(HitTest(pos)));
        }

        if (_toast is not null)
            InvalidateVisual(); // o aviso de espessura acompanha o mouse
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);

        if (_adjusting != Edges.None)
        {
            _adjusting = Edges.None;
            SelectionCompleted?.Invoke(this, EventArgs.Empty);
        }
        else if (_selecting)
        {
            _selecting = false;
            if (SelectionRect is { Width: >= 4, Height: >= 4 })
                SelectionCompleted?.Invoke(this, EventArgs.Empty);
            else
                SelectionRect = null;
        }
        else if (_drawing is not null)
        {
            if (!_drawing.IsEmpty)
            {
                _annotations.Add(_drawing);
                AnnotationsChanged?.Invoke(this, EventArgs.Empty);
            }
            _drawing = null;
        }

        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var busy = _drawing is not null || _editingText is not null;

        // No editor a roda rola a imagem; a espessura fica no Ctrl + roda.
        // Enquanto desenha (botão pressionado), a roda sempre ajusta a espessura.
        if (EditorMode && !busy && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        CurrentSize = Math.Clamp(CurrentSize + (e.Delta.Y > 0 ? 1 : -1), 1, 20);
        ShowToast($"Espessura {CurrentSize:0}");
        ThicknessChanged?.Invoke(this, EventArgs.Empty);

        // Atualiza em tempo real a forma ou o texto que está sendo feito
        if (_drawing is not null)
            _drawing.Size = CurrentSize;
        if (_editingText is not null)
            _editingText.Size = CurrentSize;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Descobre se o ponto está numa borda/canto (redimensionar) ou dentro (mover).</summary>
    private Edges HitTest(Point p)
    {
        if (SelectionRect is not { } r)
            return Edges.None;

        var outer = r.Inflate(HandleMargin);
        if (!outer.Contains(p))
            return Edges.None;

        var edges = Edges.None;
        if (Math.Abs(p.X - r.Left) <= HandleMargin) edges |= Edges.Left;
        else if (Math.Abs(p.X - r.Right) <= HandleMargin) edges |= Edges.Right;
        if (Math.Abs(p.Y - r.Top) <= HandleMargin) edges |= Edges.Top;
        else if (Math.Abs(p.Y - r.Bottom) <= HandleMargin) edges |= Edges.Bottom;

        if (edges != Edges.None)
            return edges;
        return r.Contains(p) ? Edges.Move : Edges.None;
    }

    private Rect Adjust(Rect start, Edges edges, Vector delta)
    {
        var bounds = new Rect(Bounds.Size);

        if (edges == Edges.Move)
        {
            var x = Math.Clamp(start.X + delta.X, 0, bounds.Width - start.Width);
            var y = Math.Clamp(start.Y + delta.Y, 0, bounds.Height - start.Height);
            return new Rect(x, y, start.Width, start.Height);
        }

        double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        if (edges.HasFlag(Edges.Left)) left = Math.Clamp(left + delta.X, 0, bounds.Width);
        if (edges.HasFlag(Edges.Right)) right = Math.Clamp(right + delta.X, 0, bounds.Width);
        if (edges.HasFlag(Edges.Top)) top = Math.Clamp(top + delta.Y, 0, bounds.Height);
        if (edges.HasFlag(Edges.Bottom)) bottom = Math.Clamp(bottom + delta.Y, 0, bounds.Height);

        // Normalize permite "virar" a seleção ao arrastar uma borda além da oposta
        var rect = new Rect(new Point(left, top), new Point(right, bottom)).Normalize();
        return rect.Width < 4 || rect.Height < 4 ? start : rect;
    }

    private static StandardCursorType CursorFor(Edges edges) => edges switch
    {
        Edges.Move => StandardCursorType.SizeAll,
        Edges.Left | Edges.Top => StandardCursorType.TopLeftCorner,
        Edges.Right | Edges.Bottom => StandardCursorType.BottomRightCorner,
        Edges.Right | Edges.Top => StandardCursorType.TopRightCorner,
        Edges.Left | Edges.Bottom => StandardCursorType.BottomLeftCorner,
        Edges.Left or Edges.Right => StandardCursorType.SizeWestEast,
        Edges.Top or Edges.Bottom => StandardCursorType.SizeNorthSouth,
        _ => StandardCursorType.Cross,
    };

    /// <summary>Ajusta o ponto para que o arraste forme um quadrado (ou círculo).</summary>
    private static Point Square(Point anchor, Point current)
    {
        var dx = current.X - anchor.X;
        var dy = current.Y - anchor.Y;
        var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Point(anchor.X + Math.CopySign(side, dx), anchor.Y + Math.CopySign(side, dy));
    }

    private Annotation CreateAnnotation(Tool tool) => tool switch
    {
        Tool.Ellipse => new EllipseAnnotation(CurrentColor, CurrentSize),
        Tool.Pen => new PenAnnotation(CurrentColor, CurrentSize),
        Tool.Marker => new MarkerAnnotation(CurrentColor, CurrentSize),
        Tool.Arrow => new ArrowAnnotation(CurrentColor, CurrentSize),
        Tool.Rectangle => new RectangleAnnotation(CurrentColor, CurrentSize),
        Tool.Pixelate => new PixelateAnnotation(Source!, PixelScale),
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    // ───────────────────────────── Texto ─────────────────────────────

    public void TypeText(string text)
    {
        if (_editingText is null)
            return;
        _editingText.Text += text;
        InvalidateVisual();
    }

    public void Backspace()
    {
        if (_editingText is not { Text.Length: > 0 } t)
            return;
        t.Text = t.Text[..^1];
        InvalidateVisual();
    }

    public void CommitText()
    {
        if (_editingText is null)
            return;
        if (!_editingText.IsEmpty)
        {
            _annotations.Add(_editingText);
            AnnotationsChanged?.Invoke(this, EventArgs.Empty);
        }
        _editingText = null;
        InvalidateVisual();
    }

    public void CancelText()
    {
        _editingText = null;
        InvalidateVisual();
    }

    // ───────────────────────────── Ações ─────────────────────────────

    /// <summary>Volta ao estado inicial (sem seleção nem anotações).</summary>
    public void Reset()
    {
        SelectionRect = null;
        _annotations.Clear();
        _drawing = null;
        _editingText = null;
        _selecting = false;
        _adjusting = Edges.None;
        InvalidateVisual();
    }

    /// <summary>Apaga todas as anotações (mantém a seleção).</summary>
    public void ClearAnnotations()
    {
        CommitText();
        if (_annotations.Count == 0)
            return;
        _annotations.Clear();
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void Undo()
    {
        if (_annotations.Count == 0)
            return;
        _annotations.RemoveAt(_annotations.Count - 1);
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Gera a imagem final: o recorte da seleção com as anotações aplicadas.</summary>
    public Bitmap? RenderSelection()
    {
        CommitText();
        if (Source is null || SelectionRect is not { } sel || SelectionInPixels is not { } px)
            return null;

        using var crop = Source.Crop(px);
        var result = new RenderTargetBitmap(crop.PixelSize);
        using (var ctx = result.CreateDrawingContext())
        {
            ctx.DrawImage(crop, new Rect(0, 0, crop.PixelSize.Width, crop.PixelSize.Height));
            using (ctx.PushTransform(Matrix.CreateTranslation(-sel.X, -sel.Y) * Matrix.CreateScale(PixelScale, PixelScale)))
            {
                foreach (var annotation in _annotations)
                    annotation.Render(ctx);
            }
        }
        return result;
    }

    private void ShowToast(string text)
    {
        _toast = text;
        _toastTimer.Stop();
        _toastTimer.Start();
        InvalidateVisual();
    }

    // ───────────────────────────── Desenho ─────────────────────────────

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Image is not null)
            context.DrawImage(Image, bounds);
        else if (EditorMode)
            context.FillRectangle(Brushes.Transparent, bounds); // desenho ao vivo: invisível, mas recebe o mouse

        if (EditorMode)
        {
            foreach (var annotation in _annotations)
                annotation.Render(context);
            _drawing?.Render(context);
            if (_editingText is not null)
                DrawEditingText(context, _editingText);
            DrawToast(context);
            return;
        }

        if (SelectionRect is not { } sel)
        {
            context.FillRectangle(Dim, bounds);
            DrawPill(context, "Arraste para selecionar  ·  Esc para cancelar", new Point(bounds.Width / 2, 40), 13, center: true);
            DrawToast(context);
            return;
        }

        // Escurece as quatro faixas ao redor da seleção
        context.FillRectangle(Dim, new Rect(0, 0, bounds.Width, sel.Top));
        context.FillRectangle(Dim, new Rect(0, sel.Bottom, bounds.Width, bounds.Height - sel.Bottom));
        context.FillRectangle(Dim, new Rect(0, sel.Top, sel.Left, sel.Height));
        context.FillRectangle(Dim, new Rect(sel.Right, sel.Top, bounds.Width - sel.Right, sel.Height));

        using (context.PushClip(sel))
        {
            foreach (var annotation in _annotations)
                annotation.Render(context);
            _drawing?.Render(context);
            if (_editingText is not null)
                DrawEditingText(context, _editingText);
        }

        context.DrawRectangle(null, Border, sel);
        foreach (var corner in new[] { sel.TopLeft, sel.TopRight, sel.BottomLeft, sel.BottomRight })
            context.DrawEllipse(Brushes.White, null, corner, 3.5, 3.5);

        var px = SelectionInPixels!.Value;
        var labelY = sel.Top - 30 < 4 ? sel.Top + 8 : sel.Top - 30;
        DrawPill(context, $"{px.Width} × {px.Height}", new Point(sel.Left, labelY), 12);

        DrawToast(context);
    }

    private static void DrawEditingText(DrawingContext context, TextAnnotation text)
    {
        text.Render(context);

        // Cursor de digitação no fim do texto
        var measured = text.Format(text.Text.Length > 0 ? text.Text : " ");
        var x = text.Origin.X + (text.Text.Length > 0 ? measured.WidthIncludingTrailingWhitespace : 0) + 1;
        var caret = new Pen(new SolidColorBrush(text.Color), 2);
        context.DrawLine(caret, new Point(x, text.Origin.Y + 2), new Point(x, text.Origin.Y + measured.Height - 2));

        var frame = new Rect(text.Origin.X - 6, text.Origin.Y - 4,
            Math.Max(measured.WidthIncludingTrailingWhitespace, 40) + 14, measured.Height + 8);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 1, DashStyle.Dash),
            new RoundedRect(frame, 4));
    }

    private void DrawToast(DrawingContext context)
    {
        if (_toast is not null)
            DrawPill(context, _toast, _lastPointer + new Vector(18, 18), 12);
    }

    private static void DrawPill(DrawingContext context, string label, Point at, double fontSize, bool center = false)
    {
        var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFont, fontSize, Brushes.White);
        var padX = center ? 14 : 8;
        var padY = center ? 8 : 4;
        var pill = new Rect(0, 0, text.Width + padX * 2, text.Height + padY * 2);
        pill = pill.WithX(center ? at.X - pill.Width / 2 : at.X).WithY(at.Y);

        context.DrawRectangle(PillBackground, null, new RoundedRect(pill, center ? pill.Height / 2 : 7));
        context.DrawText(text, new Point(pill.X + padX, pill.Y + padY));
    }
}
