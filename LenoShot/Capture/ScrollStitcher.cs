namespace LenoShot.Capture;

public enum StitchResult
{
    /// <summary>A página rolou e as linhas novas foram adicionadas.</summary>
    Appended,
    /// <summary>A imagem não mudou (fim da página ou nada rolou).</summary>
    NoChange,
    /// <summary>Rolou demais: não há sobreposição; o trecho foi adicionado inteiro.</summary>
    NoOverlap,
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

    private ScreenImage _last;
    private ulong[] _lastHashes;
    private int _top = -1;
    private int _bottom;
    private int _lastDy;
    private int _appends;

    public ScrollStitcher(ScreenImage first, double scaling)
    {
        _width = first.Width;
        _height = first.Height;
        var scrollbar = (int)Math.Min(24 * scaling, _width / 10.0);
        _hashWidth = Math.Max(1, _width - scrollbar);

        _last = first;
        _lastHashes = HashRows(first);
        for (var y = 0; y < _height; y++)
            _rows.Add(Row(first, y));
    }

    public int Height => _rows.Count + (_top < 0 ? 0 : _bottom);
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
            if (hashes[y] == UniformMarker && _lastHashes[y] == UniformMarker)
                continue;
            informative++;
            if (hashes[y] == _lastHashes[y])
                samePosition++;
        }
        if (informative == 0 || samePosition >= informative * 0.9)
            return StitchResult.NoChange;

        if (_top < 0)
            DetectFixedBands(hashes);

        var bandStart = _top;
        var bandHeight = _height - _top - _bottom;
        var dy = FindOffset(_lastHashes, hashes, bandStart, bandHeight);

        StitchResult result;
        if (dy > 0)
        {
            // Linhas que apareceram embaixo do miolo
            for (var y = bandStart + bandHeight - dy; y < bandStart + bandHeight; y++)
                _rows.Add(Row(frame, y));
            _lastDy = dy;
            result = StitchResult.Appended;
        }
        else if (_appends == 0)
        {
            // Ainda não houve emenda: o 1º quadro provavelmente pegou a tela em transição
            // (sobreposição fechando, janela mudando de foco). Recomeça a partir deste quadro.
            _rows.Clear();
            for (var y = 0; y < _height; y++)
                _rows.Add(Row(frame, y));
            _top = -1;
            _bottom = 0;
            _lastDy = 0;
            result = StitchResult.NoOverlap;
        }
        else
        {
            for (var y = bandStart; y < bandStart + bandHeight; y++)
                _rows.Add(Row(frame, y));
            _lastDy = bandHeight;
            result = StitchResult.NoOverlap;
        }
        if (result == StitchResult.Appended)
            _appends++;

        _last = frame;
        _lastHashes = hashes;
        return result;
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

    private void DetectFixedBands(ulong[] hashes)
    {
        var top = 0;
        while (top < _height && hashes[top] == _lastHashes[top])
            top++;
        var bottom = 0;
        while (bottom < _height - top && hashes[_height - 1 - bottom] == _lastHashes[_height - 1 - bottom])
            bottom++;

        // Faixas fixas grandes demais são improváveis: limita a um terço da altura cada
        _top = Math.Min(top, _height / 3);
        _bottom = Math.Min(bottom, _height / 3);

        // O primeiro quadro entrou inteiro; o rodapé dele sai e volta no fim (do último quadro)
        _rows.RemoveRange(_rows.Count - _bottom, _bottom);
    }

    /// <summary>
    /// Procura quantas linhas o miolo subiu. Linhas de cor única (fundo liso) não servem como
    /// referência, então só as linhas "com conteúdo" contam para a nota de cada deslocamento.
    /// </summary>
    private int FindOffset(ulong[] prev, ulong[] next, int start, int height)
    {
        var minOverlap = Math.Max(8, height / 10);
        var bestDy = 0;
        var bestScore = 0.0;

        for (var dy = 1; dy <= height - minOverlap; dy++)
        {
            int informative = 0, matches = 0;
            for (var i = 0; i < height - dy; i++)
            {
                var h = next[start + i];
                if (h == UniformMarker)
                    continue;
                informative++;
                if (prev[start + dy + i] == h)
                    matches++;
            }

            if (informative < 5)
                continue;

            var score = (double)matches / informative;
            // Empate (conteúdo repetitivo): prefere o deslocamento mais parecido com o anterior
            var better = score > bestScore + 1e-9 ||
                         (Math.Abs(score - bestScore) < 1e-9 && _lastDy > 0 && Math.Abs(dy - _lastDy) < Math.Abs(bestDy - _lastDy));
            if (better)
            {
                bestScore = score;
                bestDy = dy;
            }
        }

        return bestScore >= 0.9 ? bestDy : 0;
    }

    // Hash reservado para linhas de cor única
    private const ulong UniformMarker = 0x9E3779B97F4A7C15;

    private ulong[] HashRows(ScreenImage image)
    {
        var hashes = new ulong[image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.Pixels.AsSpan(y * image.Stride, _hashWidth * 4);
            var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(row);

            var first = pixels[0];
            var uniform = true;
            var hash = 14695981039346656037UL; // FNV-1a
            foreach (var p in pixels)
            {
                if (p != first)
                    uniform = false;
                hash = (hash ^ p) * 1099511628211UL;
            }
            hashes[y] = uniform ? UniformMarker : hash;
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
