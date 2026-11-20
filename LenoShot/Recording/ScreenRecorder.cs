using System.Diagnostics;
using System.Text;
using Avalonia;
using LenoShot.Services;

namespace LenoShot.Recording;

/// <summary>
/// Grava uma área da tela com o ffmpeg (x11grab no Linux, gdigrab no Windows), com microfone
/// e/ou áudio do computador (PulseAudio no Linux, DirectShow no Windows).
/// A pausa funciona gravando em segmentos: pausar encerra o segmento atual, continuar abre
/// outro, e ao parar tudo é emendado num único MP4 (sem recodificar).
/// </summary>
public sealed class ScreenRecorder
{
    private readonly PixelRect _area;
    private readonly int _fps;
    private readonly bool _mic;
    private readonly bool _system;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"leno-shot-rec-{Guid.NewGuid():N}");
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

    public ScreenRecorder(PixelRect area, int fps, bool mic, bool systemAudio, int maxHeight = 0)
    {
        _maxHeight = maxHeight;
        // yuv420p exige largura e altura pares
        _area = new PixelRect(area.X, area.Y, Math.Max(2, area.Width & ~1), Math.Max(2, area.Height & ~1));
        _fps = Math.Clamp(fps, 5, 60);
        _mic = mic;
        _system = systemAudio;
    }

    public bool IsPaused { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Aviso não fatal (ex.: uma fonte de áudio não encontrada).</summary>
    public string? Warning { get; private set; }

    /// <summary>Últimas linhas de erro do ffmpeg, para mostrar ao usuário.</summary>
    public string LastLog => _log.ToString().Trim();

    public static bool IsFfmpegAvailable() => FindFfmpeg() is not null;

    /// <summary>
    /// Caminho do ffmpeg: ao lado do app (no Windows dá para colocar o ffmpeg.exe junto) ou no PATH.
    /// No Windows também lê o PATH atual do registro, para achar um ffmpeg instalado (ex.: pelo
    /// winget) depois que o app já estava aberto.
    /// </summary>
    private static string? FindFfmpeg()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var dirs = new List<string> { AppContext.BaseDirectory };
        dirs.AddRange(SplitPath(Environment.GetEnvironmentVariable("PATH")));
        if (OperatingSystem.IsWindows())
        {
            dirs.AddRange(SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)));
            dirs.AddRange(SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)));
        }

        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(dir), exe);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // entrada inválida no PATH
            }
        }
        return null;
    }

    private static IEnumerable<string> SplitPath(string? path) =>
        (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => dir.Trim('"'));

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
            var output = Path.Combine(dir, $"leno-shot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

            var list = Path.Combine(_tempDir, "list.txt");
            // Barras normais: o ffmpeg aceita também no Windows e evita escapes
            await File.WriteAllLinesAsync(list, valid.Select(f => $"file '{f.Replace('\\', '/').Replace("'", "'\\''")}'"));

            var ok = await RunFfmpegAsync(
                ["-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", list,
                 "-c", "copy", "-movflags", "+faststart", output]);
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

    // ───────────── Segmentos ─────────────

    private async Task<bool> StartSegmentAsync()
    {
        var file = Path.Combine(_tempDir, $"seg{_segments.Count:000}.mkv");
        _log.Clear();

        var args = new List<string> { "-y", "-loglevel", "warning" };

        // Entrada 0: vídeo
        if (OperatingSystem.IsWindows())
        {
            // gdigrab: as coordenadas são da área de trabalho virtual (podem ser negativas)
            args.AddRange(["-thread_queue_size", "1024", "-f", "gdigrab", "-framerate", _fps.ToString(),
                "-offset_x", _area.X.ToString(), "-offset_y", _area.Y.ToString(),
                "-video_size", $"{_area.Width}x{_area.Height}", "-draw_mouse", "1", "-i", "desktop"]);
        }
        else
        {
            var display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
            args.AddRange(["-thread_queue_size", "1024", "-f", "x11grab", "-framerate", _fps.ToString(),
                "-video_size", $"{_area.Width}x{_area.Height}", "-draw_mouse", "1",
                "-i", $"{display}+{_area.X},{_area.Y}"]);
        }

        // Entradas seguintes: áudio (microfone e/ou o que sai nas caixas)
        var audio = 0;
        foreach (var source in new[] { _micSource, _systemSource })
        {
            if (source is null)
                continue;
            if (OperatingSystem.IsWindows())
                args.AddRange(["-thread_queue_size", "1024", "-f", "dshow", "-audio_buffer_size", "50", "-i", $"audio={source}"]);
            else
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
            Console.Error.WriteLine($"[leno-shot] erro ao parar a gravação: {ex.Message}");
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
            var psi = new ProcessStartInfo(FindFfmpeg() ?? "ffmpeg")
            {
                UseShellExecute = false,
                CreateNoWindow = true, // no Windows, sem isso abre uma janela de console
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
        if (OperatingSystem.IsWindows())
        {
            await ResolveWindowsAudioSourcesAsync();
            return;
        }

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

    // Dispositivos DirectShow que entregam o que sai nas caixas (precisam estar ativados no Windows)
    private static readonly string[] LoopbackNames =
        ["stereo mix", "mixagem estéreo", "mixagem estereo", "mixage stéréo", "mezcla estéreo",
         "stereomix", "what u hear", "wave out", "virtual-audio-capturer", "cable output"];

    private static bool IsLoopback(string device) =>
        LoopbackNames.Any(n => device.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// No Windows o áudio vem do DirectShow: o microfone é o primeiro dispositivo de entrada; o
    /// áudio do computador precisa de um dispositivo de loopback ("Mixagem estéreo" ou similar).
    /// </summary>
    private async Task ResolveWindowsAudioSourcesAsync()
    {
        if (!_mic && !_system)
            return;

        var devices = await ListDirectShowAudioDevicesAsync();
        if (_mic)
        {
            _micSource = devices.FirstOrDefault(d => !IsLoopback(d));
            if (_micSource is null)
                Warning = "Microfone não encontrado — gravando sem ele.";
        }

        if (_system)
        {
            _systemSource = devices.FirstOrDefault(IsLoopback);
            if (_systemSource is null)
                Warning = (Warning is null ? "" : Warning + " ") +
                          "Áudio do PC indisponível: ative a \"Mixagem estéreo\" no Windows — gravando sem ele.";
        }
    }

    /// <summary>Nomes dos dispositivos de áudio do DirectShow, como o ffmpeg os lista.</summary>
    private static async Task<List<string>> ListDirectShowAudioDevicesAsync()
    {
        var devices = new List<string>();
        try
        {
            var psi = new ProcessStartInfo(FindFfmpeg() ?? "ffmpeg")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var arg in new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return devices;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var output = await process.StandardError.ReadToEndAsync();
            await stdout;
            await process.WaitForExitAsync();

            // Formato novo: [dshow @ ...] "Nome" (audio)
            // Formato antigo: uma seção "DirectShow audio devices" seguida de [dshow @ ...]  "Nome"
            var inAudioSection = false;
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
                    inAudioSection = true;
                else if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
                    inAudioSection = false;

                if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase))
                    continue;
                var start = line.IndexOf('"');
                var end = line.LastIndexOf('"');
                if (start < 0 || end <= start)
                    continue;
                if (line.Contains("(audio)", StringComparison.Ordinal) || (inAudioSection && !line.Contains("(video)")))
                    devices.Add(line[(start + 1)..end]);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[leno-shot] não foi possível listar os dispositivos de áudio: {ex.Message}");
        }
        return devices;
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
            Console.Error.WriteLine($"[leno-shot] não foi possível limpar {_tempDir}: {ex.Message}");
        }
    }
}
