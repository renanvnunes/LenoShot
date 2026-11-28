namespace LenoShot.Capture;

public enum StitchResult
{
    /// <summary>A página rolou e as linhas novas foram adicionadas.</summary>
    Appended,
    /// <summary>A imagem não mudou (fim da página ou nada rolou).</summary>
    NoChange,
    /// <summary>Rolou demais: não há sobreposição; o trecho foi adicionado inteiro.</summary>
    NoOverlap,
    /// <summary>Rolou rápido demais: o quadro foi ignorado; volte a um ponto já capturado para continuar.</summary>
    Lost,
}

/// <summary>
/// Emenda capturas sucessivas da mesma área enquanto a página rola.
///
/// Cada linha de pixels vira um hash. Entre dois quadros:
/// 1. linhas iguais no topo e na base (mesma posição) são partes fixas — cabeçalho e rodapé;
/// 2. no miolo que se move, procuramos o deslocamento <c>dy</c> em que as linhas do quadro
///    anterior batem com as do novo; as <c>dy</c> linhas que surgiram embaixo são adicionadas.
/// </summary>
public sealed class ScrollStitcher
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _hashWidth; // ignora a barra de rolagem à direita, que muda a cada passo
    private readonly List<byte[]> _rows = [];
    private readonly List<ulong> _rowHashes = []; // Seg hashes por linha de _rows, para reencontrar posição
    private const int Seg = 32; // cada linha é dividida em trechos: uma foto/animação não invalida a linha toda

    private ScreenImage _last;
    private ulong[] _lastHashes;
    private int _top = -1;
    private int _bottom;
    private int _lastDy;
    private int _pos;
    private ulong[]? _pendingHashes;
    private const double MinScore = 0.2;  // fotos reamostradas ao rolar nunca batem; o que vale é o pico claro

    public ScrollStitcher(ScreenImage first, double scaling)
    {
        _width = first.Width;
        _height = first.Height;
        var scrollbar = (int)Math.Min(24 * scaling, _width / 10.0);
        _hashWidth = Math.Max(1, _width - scrollbar);

        _last = first;
        _lastHashes = HashRows(first);
        for (var y = 0; y < _height; y++)
        {
            _rows.Add(Row(first, y));
            for (var k = 0; k < Seg; k++)
                _rowHashes.Add(_lastHashes[y * Seg + k]);
        }
    }

    public int Height => _rows.Count + (_top < 0 ? 0 : _bottom);
    public bool HasScrolled => _top >= 0;
    public int LastScrollPixels => _lastDy;
    public int MovingBandHeight => _top < 0 ? _height : _height - _top - _bottom;

    public StitchResult Add(ScreenImage frame)
    {
        var hashes = HashRows(frame);

        // Quase tudo igual na mesma posição (cursor piscando, animação pequena): não rolou
        // (linhas lisas não contam: em fundo branco elas são iguais mesmo depois de rolar)
        int informative = 0, samePosition = 0;
        for (var y = 0; y < _height; y++)
        {
            var (i, m) = Compare(hashes, y, _lastHashes, y);
            informative += i;
            samePosition += m;
        }
        // Tolera trechos que mudam sozinhos (foto, vídeo, cursor piscando)
        if (informative == 0 || samePosition >= informative * 0.75)
            return StitchResult.NoChange;

        // Faixas fixas (cabeçalho/rodapé): só valem depois do 1º casamento bem-sucedido
        int top = _top, bottom = _bottom;
        var firstMatch = _top < 0;
        if (firstMatch)
            (top, bottom) = FixedBands(hashes);

        var bandStart = top;
        var bandHeight = _height - top - bottom;
        var (down, downScore) = FindOffset(_lastHashes, hashes, bandStart, bandHeight, up: false);
        var (up, upScore) = FindOffset(_lastHashes, hashes, bandStart, bandHeight, up: true);
        var goingUp = upScore > downScore + 1e-9;
        var dy = goingUp ? up : down;
        var found = dy > 0 && (goingUp ? upScore : downScore) >= MinScore;

        if (firstMatch)
        {
            if (!found)
            {
                // Nunca emendou nada e a tela já está em outro lugar (o 1º quadro pegou a página
                // em transição, ou ela andou demais): se a tela parou, recomeça a partir daqui.
                if (_pendingHashes is not null && SameFrame(hashes, _pendingHashes))
                    Restart(frame, hashes);
                else
                    _pendingHashes = hashes;
                return StitchResult.Lost;
            }
            _pendingHashes = null;
            _top = top;
            _bottom = bottom;
            _rows.RemoveRange(_rows.Count - _bottom, _bottom);
            _rowHashes.RemoveRange(_rowHashes.Count - _bottom * Seg, _bottom * Seg);
            _pos = _top;
        }

        int newPos;
        if (found)
        {
            newPos = _pos + (goingUp ? -dy : dy);
            _lastDy = dy;
        }
        else
        {
            // Perdeu o último quadro (rolou rápido, cabeçalho mudou...): procura o quadro em
            // toda a imagem já capturada, para continuar de onde o usuário voltar.
            var located = FindInImage(hashes, bandStart, bandHeight);
            if (located is null)
                return StitchResult.Lost;
            newPos = located.Value;
        }

        return ApplyAt(frame, hashes, bandStart, bandHeight, newPos);
    }

    /// <summary>
    /// Para a rolagem automática: quando o quadro não casa com nada (foto trocando sozinha, por
    /// exemplo) mas sabemos que a página andou um passo, assume o mesmo deslocamento do passo
    /// anterior em vez de parar. Só vale depois de ao menos uma emenda confirmada.
    /// </summary>
    public StitchResult AddAssumingStep(ScreenImage frame)
    {
        if (_top < 0 || _lastDy <= 0)
            return StitchResult.Lost;
        var hashes = HashRows(frame);
        return ApplyAt(frame, hashes, _top, _height - _top - _bottom, _pos + _lastDy);
    }

    private StitchResult ApplyAt(ScreenImage frame, ulong[] hashes, int bandStart, int bandHeight, int newPos)
    {
        // _pos = índice, em _rows, da 1ª linha do miolo visível. Só entra linha nova quando a
        // tela passa dos limites já capturados; voltar a uma região conhecida não duplica nada.
        _pos = newPos;
        var result = StitchResult.NoOverlap;
        if (_pos + bandHeight > _rows.Count)
        {
            var need = _pos + bandHeight - _rows.Count;
            for (var y = bandStart + bandHeight - need; y < bandStart + bandHeight; y++)
            {
                _rows.Add(Row(frame, y));
                for (var k = 0; k < Seg; k++)
                    _rowHashes.Add(hashes[y * Seg + k]);
            }
            result = StitchResult.Appended;
        }
        else if (_pos < _top)
        {
            var need = _top - _pos;
            var added = new List<byte[]>(need);
            var addedHashes = new List<ulong>(need * Seg);
            for (var y = bandStart; y < bandStart + need; y++)
            {
                added.Add(Row(frame, y));
                for (var k = 0; k < Seg; k++)
                    addedHashes.Add(hashes[y * Seg + k]);
            }
            _rows.InsertRange(_top, added);
            _rowHashes.InsertRange(_top * Seg, addedHashes);
            _pos = _top;
            result = StitchResult.Appended;
        }

        _last = frame;
        _lastHashes = hashes;
        return result;
    }

    /// <summary>
    /// Procura onde o miolo do quadro cai dentro de toda a imagem emendada (inclusive
    /// ultrapassando as bordas dela). Devolve o índice da 1ª linha do miolo, ou null.
    /// </summary>
    private int? FindInImage(ulong[] hashes, int bandStart, int bandHeight)
    {
        var minOverlap = Math.Max(16, bandHeight / 8);
        var count = _rowHashes.Count / Seg;
        var bestPos = 0;
        var bestScore = 0.0;
        var found = false;
        var all = new Dictionary<int, double>();

        for (var p = _top - (bandHeight - minOverlap); p <= count - minOverlap; p++)
        {
            var from = Math.Max(0, _top - p);
            var to = Math.Min(bandHeight, count - p);
            if (to - from < minOverlap)
                continue;

            // Triagem em 1 de cada 8 linhas; só o que parece promissor é avaliado por inteiro
            if (ImageScore(hashes, bandStart, p, from, to, 8, out var coarse) < 0 || coarse < 0.2)
                continue;
            if (ImageScore(hashes, bandStart, p, from, to, 1, out var score) < 0)
                continue;

            all[p] = score;
            var better = score > bestScore + 1e-9 ||
                         (Math.Abs(score - bestScore) < 1e-9 && found && Math.Abs(p - _pos) < Math.Abs(bestPos - _pos));
            if (better)
            {
                bestScore = score;
                bestPos = p;
                found = true;
            }
        }

        if (!found || bestScore < 0.3)
            return null;
        double second = 0;
        foreach (var (p, sc) in all)
            if (Math.Abs(p - bestPos) > 12 && sc > second)
                second = sc;
        return bestScore >= 1.5 * second ? bestPos : null;
    }

    private int ImageScore(ulong[] hashes, int bandStart, int p, int from, int to, int step, out double score)
    {
        int informative = 0, matches = 0;
        for (var i = from; i < to; i += step)
        {
            int ia = (bandStart + i) * Seg, ib = (p + i) * Seg;
            for (var k = 0; k < Seg; k++)
            {
                var h = hashes[ia + k];
                if (h == UniformMarker)
                    continue;
                informative++;
                if (_rowHashes[ib + k] == h)
                    matches++;
            }
        }
        score = informative == 0 ? 0 : (double)matches / informative;
        return informative < 5 * Seg / 4 ? -1 : 0;
    }

    /// <summary>Monta a imagem final: conteúdo emendado + rodapé fixo do último quadro.</summary>
    public ScreenImage Build()
    {
        var footer = _top < 0 ? 0 : _bottom;
        var height = _rows.Count + footer;
        var pixels = new byte[_width * 4 * height];

        for (var y = 0; y < _rows.Count; y++)
            Buffer.BlockCopy(_rows[y], 0, pixels, y * _width * 4, _width * 4);
        for (var y = 0; y < footer; y++)
            Buffer.BlockCopy(_last.Pixels, (_height - footer + y) * _last.Stride, pixels, (_rows.Count + y) * _width * 4, _width * 4);

        return new ScreenImage(_width, height, pixels);
    }

    private static bool SameFrame(ulong[] a, ulong[] b)
    {
        int inf = 0, m = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == UniformMarker)
                continue;
            inf++;
            if (a[i] == b[i])
                m++;
        }
        return inf > 0 && m >= inf * 0.9;
    }

    private void Restart(ScreenImage frame, ulong[] hashes)
    {
        _rows.Clear();
        _rowHashes.Clear();
        for (var y = 0; y < _height; y++)
        {
            _rows.Add(Row(frame, y));
            for (var k = 0; k < Seg; k++)
                _rowHashes.Add(hashes[y * Seg + k]);
        }
        _last = frame;
        _lastHashes = hashes;
        _pendingHashes = null;
        _top = -1;
        _bottom = 0;
        _pos = 0;
        _lastDy = 0;
    }

    private (int Top, int Bottom) FixedBands(ulong[] hashes)
    {
        var top = 0;
        while (top < _height && RowSame(hashes, top, _lastHashes, top))
            top++;
        var bottom = 0;
        while (bottom < _height - top && RowSame(hashes, _height - 1 - bottom, _lastHashes, _height - 1 - bottom))
            bottom++;

        // Faixas fixas grandes demais são improváveis: limita a um terço da altura cada
        return (Math.Min(top, _height / 3), Math.Min(bottom, _height / 3));
    }

    private static bool RowSame(ulong[] a, int ya, ulong[] b, int yb)
    {
        var (inf, m) = Compare(a, ya, b, yb);
        return m >= inf * 0.8;
    }

    /// <summary>Compara duas linhas trecho a trecho: (trechos com conteúdo, trechos iguais).</summary>
    private static (int Informative, int Matches) Compare(ulong[] a, int ya, ulong[] b, int yb)
    {
        int inf = 0, m = 0;
        int ia = ya * Seg, ib = yb * Seg;
        for (var k = 0; k < Seg; k++)
        {
            var h = a[ia + k];
            if (h == UniformMarker)
                continue;
            inf++;
            if (b[ib + k] == h)
                m++;
        }
        return (inf, m);
    }

    /// <summary>
    /// Procura quantas linhas o miolo subiu. Linhas de cor única (fundo liso) não servem como
    /// referência, então só as linhas "com conteúdo" contam para a nota de cada deslocamento.
    /// </summary>
    private (int Dy, double Score) FindOffset(ulong[] prev, ulong[] next, int start, int height, bool up)
    {
        var minOverlap = Math.Max(8, height / 20); // aceita saltos de até ~95% do miolo
        var scores = new double[height + 1];
        var bestDy = 0;
        var bestScore = 0.0;

        for (var dy = 1; dy <= height - minOverlap; dy++)
        {
            // Triagem rápida em 1 de cada 8 linhas; só o que parece promissor é avaliado por inteiro
            if (OffsetScore(prev, next, start, height, dy, up, 8, out var coarse) < 0 || coarse < 0.2)
                continue;
            if (OffsetScore(prev, next, start, height, dy, up, 1, out var score) < 0)
                continue;

            scores[dy] = score;
            // Empate (conteúdo repetitivo): prefere o deslocamento mais parecido com o anterior
            var better = score > bestScore + 1e-9 ||
                         (Math.Abs(score - bestScore) < 1e-9 && _lastDy > 0 && Math.Abs(dy - _lastDy) < Math.Abs(bestDy - _lastDy));
            if (better)
            {
                bestScore = score;
                bestDy = dy;
            }
        }

        // O encaixe verdadeiro se destaca dos demais deslocamentos (que não passam de acaso)
        double second = 0;
        for (var dy = 1; dy < scores.Length; dy++)
            if (Math.Abs(dy - bestDy) > 12 && scores[dy] > second)
                second = scores[dy];
        return (bestDy, bestScore >= 1.5 * second ? bestScore : 0);
    }

    private static int OffsetScore(ulong[] prev, ulong[] next, int start, int height, int dy, bool up, int step, out double score)
    {
        int informative = 0, matches = 0;
        for (var i = 0; i < height - dy; i += step)
        {
            // Para baixo: o novo quadro é o anterior deslocado dy linhas para cima; para cima, o inverso
            var (inf, m) = up
                ? Compare(prev, start + i, next, start + dy + i)
                : Compare(next, start + i, prev, start + dy + i);
            informative += inf;
            matches += m;
        }
        score = informative == 0 ? 0 : (double)matches / informative;
        return informative < 5 * Seg / 4 ? -1 : 0;
    }

    // Hash reservado para linhas de cor única
    private const ulong UniformMarker = 0x9E3779B97F4A7C15;

    private ulong[] HashRows(ScreenImage image)
    {
        var hashes = new ulong[image.Height * Seg];
        var segWidth = Math.Max(1, _hashWidth / Seg);
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.Pixels.AsSpan(y * image.Stride, _hashWidth * 4);
            var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(row);

            for (var k = 0; k < Seg; k++)
            {
                var from = k * segWidth;
                var to = k == Seg - 1 ? pixels.Length : Math.Min(pixels.Length, from + segWidth);
                if (from >= to)
                {
                    hashes[y * Seg + k] = UniformMarker;
                    continue;
                }

                var first = pixels[from];
                var uniform = true;
                var hash = 14695981039346656037UL; // FNV-1a
                for (var x = from; x < to; x++)
                {
                    var p = pixels[x];
                    if (p != first)
                        uniform = false;
                    // ignora os 2 bits baixos de cada canal: tolera ruído de decodificação de imagens
                    hash = (hash ^ (p & 0xFCFCFCFC)) * 1099511628211UL;
                }
                hashes[y * Seg + k] = uniform ? UniformMarker : hash;
            }
        }
        return hashes;
    }

    private byte[] Row(ScreenImage image, int y)
    {
        var row = new byte[_width * 4];
        Buffer.BlockCopy(image.Pixels, y * image.Stride, row, 0, row.Length);
        return row;
    }
}
