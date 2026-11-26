using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LenoShot.About;
using LenoShot.Capture;
using LenoShot.Overlay;
using LenoShot.Recording;
using LenoShot.Services;
using LenoShot.Scroll;
using LenoShot.Settings;

namespace LenoShot;

public partial class App : Application
{
    public static string StartupCommand { get; set; } = "daemon";

    private SingleInstance? _ipc;
    private CaptureSession? _session;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        AccentTheme.Load();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // O app fica rodando na bandeja: um erro numa captura não deve derrubá-lo.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine($"[LenoShot] erro: {e.Exception}");
                e.Handled = true;
            };
            desktop.Exit += (_, _) => _ipc?.Dispose();

            SetupTray(desktop);
            SystemIntegration.InstallDesktopEntry();
            _ = Task.Run(SystemIntegration.UpdateDesktopIcons);

            // Nova cor: troca o ícone da bandeja e o das janelas abertas
            AccentTheme.Changed += (_, _) =>
            {
                if (_tray is not null)
                    _tray.Icon = AppIcon.Create();
                foreach (var w in desktop.Windows)
                    if (w.Icon is not null)
                        w.Icon = AppIcon.Create();
            };

            _ipc = SingleInstance.StartServer(cmd => Dispatcher.UIThread.Post(() => HandleCommand(cmd)));

            if (StartupCommand != "daemon")
                Dispatcher.UIThread.Post(() => HandleCommand(StartupCommand));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void HandleCommand(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.FirstOrDefault())
        {
            case "capture":
                // capture [--delay N]; sem --delay usa o atraso configurado para o atalho
                var delay = AppSettings.Current.ShortcutDelaySeconds;
                var index = Array.FindIndex(parts, p => p is "--delay" or "-d");
                if (index >= 0 && index + 1 < parts.Length && int.TryParse(parts[index + 1], out var d))
                    delay = d;
                StartCapture(delay);
                break;
            case "record-area" when parts.Length == 5 && parts.Skip(1).All(p => int.TryParse(p, out _)):
                // record-area X Y LARGURA ALTURA: começa direto no painel de gravação (para scripts)
                StartRecordingArea(new PixelRect(int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]), int.Parse(parts[4])));
                break;
            case "record-zoom":
                _record?.ToggleZoom();
                break;
            case "record-draw":
                _record?.ToggleDrawing();
                break;
            case "record-pause":
                _ = _record?.TogglePauseAsync();
                break;
            case "record-stop":
                _ = _record?.StopAsync();
                break;
            case "record-cancel":
                _ = _record?.CancelRecordingAsync();
                break;
            case "settings":
                OpenSettings();
                break;
            case "about":
                OpenAbout();
                break;
            case "quit":
                (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                break;
        }
    }

    private CountdownWindow? _countdown;

    private void StartCapture(int delaySeconds = 0)
    {
        if (_session is not null || _countdown is not null || _scroll is not null || _record is not null)
            return; // já existe uma captura em andamento

        if (delaySeconds > 0)
        {
            StartCountdown(delaySeconds);
            return;
        }

        var session = CaptureSession.Start();
        if (session is null)
            return;

        _session = session;
        session.Ended += (_, _) => _session = null;
        session.ScrollCaptureRequested += (_, request) => StartScrollCapture(request);
        session.RecordRequested += (_, request) => StartRecording(request);
        session.Show();
    }

    private void StartCountdown(int seconds)
    {
        // Mostra a contagem no meio do monitor onde está o mouse
        var pointer = X11Input.GetPointer();
        var probe = new Window();
        var screen = probe.Screens.ScreenFromPoint(pointer) ?? probe.Screens.Primary;
        var monitor = screen?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
        var scaling = screen?.Scaling ?? 1;
        var side = (int)(120 * scaling);

        _countdown = new CountdownWindow(seconds);
        _countdown.PlaceAt(new PixelRect(monitor.X + (monitor.Width - side) / 2, monitor.Y + (monitor.Height - side) / 2, side, side), scaling);
        _countdown.Cancelled += (_, _) => _countdown = null;
        _countdown.Completed += async (_, _) =>
        {
            // Espera a contagem sumir da tela antes de capturar
            await Task.Delay(250);
            _countdown = null;
            StartCapture();
        };
        _countdown.Show();
    }

    private ScrollCaptureController? _scroll;

    private async void StartScrollCapture(ScrollCaptureRequest request)
    {
        if (_scroll is not null)
            return;

        _scroll = new ScrollCaptureController(request);
        _scroll.Finished += (_, _) => _scroll = null;
        await _scroll.RunAsync();
    }

    private RecordController? _record;
    private NativeMenuItem? _zoomItem;
    private NativeMenuItem? _drawItem;
    private NativeMenuItem? _pauseItem;
    private NativeMenuItem? _stopItem;
    private NativeMenuItem? _cancelItem;
    private TrayIcon? _tray;

    private void StartRecording(RecordRequest request)
    {
        if (_record is not null)
            return;

        _record = new RecordController(request);
        _record.StateChanged += (_, _) => UpdateRecordMenu();
        _record.Finished += (_, _) =>
        {
            _record = null;
            UpdateRecordMenu();
        };
        _record.Show();
    }

    private void StartRecordingArea(PixelRect area)
    {
        if (_session is not null || _record is not null)
            return;

        var probe = new Window();
        var screen = probe.Screens.ScreenFromPoint(area.Center) ?? probe.Screens.Primary;
        StartRecording(new RecordRequest(area, screen?.Bounds ?? area, screen?.Scaling ?? 1));
    }

    private void UpdateRecordMenu()
    {
        var recording = _record?.IsRecording == true;
        if (_zoomItem is not null)
        {
            _zoomItem.IsEnabled = recording;
            _zoomItem.Header = _record?.IsZooming == true ? "Sair do zoom" : "Zoom no mouse";
        }
        if (_drawItem is not null)
        {
            _drawItem.IsEnabled = recording;
            _drawItem.Header = _record?.IsDrawing == true ? "Sair do modo desenho" : "Desenhar na gravação";
        }
        if (_pauseItem is not null)
        {
            _pauseItem.IsEnabled = recording;
            _pauseItem.Header = _record?.IsPaused == true ? "Continuar gravação" : "Pausar gravação";
        }
        if (_stopItem is not null)
            _stopItem.IsEnabled = recording;
        if (_cancelItem is not null)
            _cancelItem.IsEnabled = recording;
    }

    private SettingsWindow? _settingsWindow;

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private AboutWindow? _aboutWindow;

    public void OpenAbout()
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = new AboutWindow();
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show();
        _aboutWindow.Activate();
    }

    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var captureItem = new NativeMenuItem("Capturar área");
        captureItem.Click += (_, _) => StartCapture();

        var delayMenu = new NativeMenu();
        foreach (var seconds in new[] { 3, 5, 10 })
        {
            var item = new NativeMenuItem($"{seconds} segundos");
            item.Click += (_, _) => StartCapture(seconds);
            delayMenu.Items.Add(item);
        }
        var delayItem = new NativeMenuItem("Capturar com atraso") { Menu = delayMenu };

        var recordItem = new NativeMenuItem("Gravar tela…");
        recordItem.Click += (_, _) => StartCapture();
        _zoomItem = new NativeMenuItem("Zoom no mouse") { IsEnabled = false };
        _zoomItem.Click += (_, _) => _record?.ToggleZoom();
        _drawItem = new NativeMenuItem("Desenhar na gravação") { IsEnabled = false };
        _drawItem.Click += (_, _) => _record?.ToggleDrawing();
        _pauseItem = new NativeMenuItem("Pausar gravação") { IsEnabled = false };
        _pauseItem.Click += (_, _) => _ = _record?.TogglePauseAsync();
        _stopItem = new NativeMenuItem("Parar gravação") { IsEnabled = false };
        _stopItem.Click += (_, _) => _ = _record?.StopAsync();
        _cancelItem = new NativeMenuItem("Descartar gravação") { IsEnabled = false };
        _cancelItem.Click += (_, _) => _ = _record?.CancelRecordingAsync();

        var settingsItem = new NativeMenuItem("Configurações…");
        settingsItem.Click += (_, _) => OpenSettings();

        var aboutItem = new NativeMenuItem("Sobre o LenoShot");
        aboutItem.Click += (_, _) => OpenAbout();

        var quitItem = new NativeMenuItem("Sair");
        quitItem.Click += (_, _) => desktop.Shutdown();

        var tray = _tray = new TrayIcon
        {
            Icon = AppIcon.Create(),
            ToolTipText = "LenoShot",
            Menu = new NativeMenu { Items = { captureItem, delayItem, new NativeMenuItemSeparator(), recordItem, _zoomItem, _drawItem, _pauseItem, _stopItem, _cancelItem, new NativeMenuItemSeparator(), settingsItem, aboutItem, new NativeMenuItemSeparator(), quitItem } },
        };
        // Durante uma gravação, clicar no ícone da bandeja para a gravação
        tray.Clicked += (_, _) =>
        {
            if (_record?.IsRecording == true)
                _ = _record.StopAsync();
            else
                StartCapture();
        };

        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }
}
