using System.Diagnostics;
using System.Text;
using Avalonia;
using LenoShot.Services;

namespace LenoShot.Recording;

/// <summary>Formato do arquivo final da gravação.</summary>
public enum RecordFormat
{
    Mp4,
    WebM,
    Gif,
}

/// <summary>
/// Grava uma área da tela com o ffmpeg (x11grab), com microfone e/ou áudio do computador.
/// A pausa funciona gravando em segmentos: pausar encerra o segmento atual, continuar abre
/// outro, e ao parar tudo é emendado num único arquivo. MP4 é emendado sem recodificar;
/// WebM (VP9/Opus) e GIF são convertidos a partir dos segmentos no final.
/// </summary>
public sealed class ScreenRecorder
{
    private readonly PixelRect _area;
    private readonly int _fps;
    private readonly bool _mic;
    private readonly bool _system;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"LenoShot-rec-{Guid.NewGuid():N}");
    private readonly List<string> _segments = [];
    private readonly Stopwatch _clock = new();
    private readonly StringBuilder _log = new();

    // ffmpeg em andamento: se o app for encerrado (Sair, SIGTERM) no meio da gravação,
    // o ffmpeg não pode ficar órfão capturando a tela e o microfone.
    private static readonly HashSet<Process> s_running = [];

    static ScreenRecorder() => AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();

    private static void KillAll()
    {
        lock (s_running)
        {
            foreach (var p in s_running)
            {
                try { if (!p.HasExited) p.Kill(); } catch { /* já encerrou */ }
            }
        }
    }

    private Process? _process;
    private string? _micSource;
    private string? _systemSource;

    private readonly int _maxHeight;

    /// <summary>GIF: quadros por segundo e altura máxima, para o arquivo não ficar pesado.</summary>
    public const int GifFps = 12;
    public const int GifMaxHeight = 480;

    public ScreenRecorder(PixelRect area, int fps, bool mic, bool systemAudio, int maxHeight = 0,
        RecordFormat format = RecordFormat.Mp4, TimeSpan? maxDuration = null)
    {
        Format = format;
        MaxDuration = maxDuration;
        // GIF não tem som
        if (format == RecordFormat.Gif)
            mic = systemAudio = false;
        _maxHeight = maxHeight;
        // yuv420p exige largura e altura pares
        _area = new PixelRect(area.X, area.Y, Math.Max(2, area.Width & ~1), Math.Max(2, area.Height & ~1));
        _fps = Math.Clamp(fps, 5, 60);
        _mic = mic;
        _system = systemAudio;
    }

    public RecordFormat Format { get; }

    /// <summary>Duração máxima (usada no GIF); null = sem limite.</summary>
    public TimeSpan? MaxDuration { get; }

    public bool IsPaused { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Aviso não fatal (ex.: uma fonte de áudio não encontrada).</summary>
    public string? Warning { get; private set; }

    /// <summary>Últimas linhas de erro do ffmpeg, para mostrar ao usuário.</summary>
    public string LastLog => _log.ToString().Trim();

    public static bool IsFfmpegAvailable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, "ffmpeg")));
    }

    // ───────────── Controle ─────────────

    /// <summary>Começa a gravar. Devolve false (com <see cref="LastLog"/>) se o ffmpeg não subiu.</summary>
    public async Task<bool> StartAsync()
    {
        Directory.CreateDirectory(_tempDir);
        await ResolveAudioSourcesAsync();

        if (!await StartSegmentAsync())
        {
            Cleanup();
            return false;
        }

        _clock.Start();
        return true;
    }

    public async Task PauseAsync()
    {
        if (IsPaused || _process is null)
            return;
        _clock.Stop();
        IsPaused = true;
        await StopSegmentAsync();
    }

    public async Task<bool> ResumeAsync()
    {
        if (!IsPaused)
            return true;
        if (!await StartSegmentAsync())
            return false;
        IsPaused = false;
        _clock.Start();
        return true;
    }

    /// <summary>Encerra e grava o arquivo final. Devolve o caminho, ou null se não houve nada para salvar.</summary>
    public async Task<string?> FinishAsync()
    {
        _clock.Stop();
        await StopSegmentAsync();

        try
        {
            var valid = _segments.Where(f => File.Exists(f) && new FileInfo(f).Length > 1024).ToList();
            if (valid.Count == 0)
                return null;

            var dir = AppSettings.Current.VideoFolder;
            Directory.CreateDirectory(dir);
            var output = Path.Combine(dir, $"LenoShot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{Extension(Format)}");

            var list = Path.Combine(_tempDir, "list.txt");
            await File.WriteAllLinesAsync(list, valid.Select(f => $"file '{f.Replace("'", "'\\''")}'"));

            var args = new List<string> { "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", list };
            if (MaxDuration is { } max)
                args.AddRange(["-t", max.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)]);

            switch (Format)
            {
                case RecordFormat.WebM:
                    // VP9 em qualidade constante; "realtime" + cpu-used alto para não demorar demais
                    args.AddRange(["-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "32", "-deadline", "realtime",
                        "-cpu-used", "8", "-row-mt", "1", "-pix_fmt", "yuv420p",
                        "-c:a", "libopus", "-b:a", "128k"]);
                    break;
                case RecordFormat.Gif:
                    // Paleta própria do vídeo (bem melhor que a paleta padrão), poucos quadros e altura limitada
                    args.AddRange(["-an", "-filter_complex",
                        $"fps={GifFps},scale=-2:'min(ih,{GifMaxHeight})':flags=lanczos,split[a][b];" +
                        "[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle",
                        "-loop", "0"]);
                    break;
                default:
                    args.AddRange(["-c", "copy", "-movflags", "+faststart"]);
                    break;
            }
            args.Add(output);

            var ok = await RunFfmpegAsync(args);
            return ok && File.Exists(output) ? output : null;
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>Descarta a gravação.</summary>
    public async Task CancelAsync()
    {
        _clock.Stop();
        await StopSegmentAsync();
        Cleanup();
    }

    public static string Extension(RecordFormat format) => format switch
    {
        RecordFormat.WebM => "webm",
        RecordFormat.Gif => "gif",
        _ => "mp4",
    };

    // ───────────── Segmentos ─────────────

    private async Task<bool> StartSegmentAsync()
    {
        var file = Path.Combine(_tempDir, $"seg{_segments.Count:000}.mkv");
        _log.Clear();

        var display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
        var args = new List<string> { "-y", "-loglevel", "warning" };

        // Entrada 0: vídeo
        args.AddRange(["-thread_queue_size", "1024", "-f", "x11grab", "-framerate", _fps.ToString(),
            "-video_size", $"{_area.Width}x{_area.Height}", "-draw_mouse", "1",
            "-i", $"{display}+{_area.X},{_area.Y}"]);

        // Entradas seguintes: áudio (microfone e/ou o que sai nas caixas)
        var audio = 0;
        foreach (var source in new[] { _micSource, _systemSource })
        {
            if (source is null)
                continue;
            args.AddRange(["-thread_queue_size", "1024", "-f", "pulse", "-i", source]);
            audio++;
        }

        if (audio == 2)
        {
            args.AddRange(["-filter_complex", "[1:a][2:a]amix=inputs=2:duration=longest:normalize=0[a]",
                "-map", "0:v", "-map", "[a]"]);
        }
        else if (audio == 1)
        {
            args.AddRange(["-map", "0:v", "-map", "1:a"]);
        }

        // Qualidade escolhida: reduz para a altura máxima (nunca amplia); a largura segue a proporção
        // e fica par (-2), como o yuv420p exige
        if (_maxHeight > 0 && _area.Height > _maxHeight)
            args.AddRange(["-vf", $"scale=-2:{_maxHeight}:flags=lanczos"]);

        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p"]);
        if (audio > 0)
            args.AddRange(["-c:a", "aac", "-b:a", "160k"]);
        args.Add(file);

        var process = StartFfmpeg(args, redirectInput: true);
        if (process is null)
            return false;

        _process = process;
        lock (s_running) s_running.Add(process);
        _segments.Add(file);

        // Se o ffmpeg falhar logo no início (fonte inválida, X sem permissão…), não vai subir
        await Task.Delay(700);
        if (process.HasExited)
        {
            _process = null;
            return false;
        }
        return true;
    }

    private async Task StopSegmentAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                // "q" faz o ffmpeg fechar o arquivo direito (o arquivo fica válido)
                await process.StandardInput.WriteAsync("q");
                await process.StandardInput.FlushAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await process.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { process.Kill(); await process.WaitForExitAsync(); }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LenoShot] erro ao parar a gravação: {ex.Message}");
            try { if (!process.HasExited) process.Kill(); } catch { /* já encerrou */ }
        }
        finally
        {
            lock (s_running) s_running.Remove(process);
            process.Dispose();
        }
    }

    // ───────────── ffmpeg e áudio ─────────────

    private Process? StartFfmpeg(IEnumerable<string> args, bool redirectInput)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                RedirectStandardInput = redirectInput,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            var process = Process.Start(psi);
            if (process is null)
                return null;

            // Precisa consumir a saída, senão o ffmpeg trava quando o buffer enche
            process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
            process.OutputDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            return process;
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            return null;
        }
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        lock (_log)
        {
            _log.AppendLine(line);
            if (_log.Length > 2000)
                _log.Remove(0, _log.Length - 2000);
        }
    }

    private async Task<bool> RunFfmpegAsync(IEnumerable<string> args)
    {
        var process = StartFfmpeg(args, redirectInput: false);
        if (process is null)
            return false;
        using (process)
        {
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
    }

    private async Task ResolveAudioSourcesAsync()
    {
        if (_mic)
        {
            _micSource = (await SystemIntegration.RunAsync("pactl", ["get-default-source"]))?.Trim();
            if (string.IsNullOrEmpty(_micSource))
            {
                _micSource = null;
                Warning = "Microfone não encontrado — gravando sem ele.";
            }
        }

        if (_system)
        {
            var sink = (await SystemIntegration.RunAsync("pactl", ["get-default-sink"]))?.Trim();
            if (string.IsNullOrEmpty(sink))
                Warning = (Warning is null ? "" : Warning + " ") + "Áudio do computador não encontrado — gravando sem ele.";
            else
                _systemSource = sink + ".monitor";
        }
    }

    private void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LenoShot] não foi possível limpar {_tempDir}: {ex.Message}");
        }
    }
}
