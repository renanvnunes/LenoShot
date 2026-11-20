using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LenoShot.Controls;
using LenoShot.Services;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace LenoShot.Settings;

public partial class SettingsWindow : ChromeWindow
{
    private static readonly string[] Palette = ["#FF453A", "#FFD60A", "#32D74B", "#0A84FF", "#FFFFFF", "#1C1C1E"];

    private static readonly (string Keys, string Action)[] CaptureKeys =
    [
        ("P", "Caneta"), ("A", "Seta"),
        ("R", "Retângulo"), ("C", "Círculo"),
        ("N", "Marcador numerado"), ("M", "Marca-texto"),
        ("T", "Texto"), ("B", "Pixelizar"),
        ("Shift", "Quadrado / círculo perfeito"), ("Ctrl + Z", "Desfazer"),
        ("Enter", "Copiar"), ("Ctrl + S", "Salvar"),
        ("L", "Captura com rolagem"), ("V", "Gravar vídeo da área"),
        ("1 – 6", "Escolher a cor"), ("Esc", "Cancelar"),
    ];

    private static readonly int[] DelayOptions = [0, 3, 5, 10];
    private static readonly int[] FpsOptions = [15, 24, 30, 60];

    private readonly AppSettings _settings = AppSettings.Current;
    private readonly Dictionary<SystemIntegration.ShortcutSlot, string?> _shortcuts = new();
    private SystemIntegration.ShortcutSlot? _recordingSlot;
    private bool _recording => _recordingSlot is not null;

    private Button ButtonFor(SystemIntegration.ShortcutSlot slot) =>
        slot == SystemIntegration.ShortcutSlot.Capture ? ShortcutButton : ZoomShortcutButton;

    private TextBlock HintFor(SystemIntegration.ShortcutSlot slot) =>
        slot == SystemIntegration.ShortcutSlot.Capture ? ShortcutHint : ZoomShortcutHint;

    private static string HintText(SystemIntegration.ShortcutSlot slot) =>
        slot == SystemIntegration.ShortcutSlot.Capture
            ? "Clique e pressione a nova combinação de teclas"
            : "Lupa no mouse com o fundo desfocado. Clique e pressione a combinação de teclas";

    public SettingsWindow()
    {
        InitializeComponent();

        Icon = AppIcon.Create();
        AppIconImage.Source = AppIcon.CreateBitmap();
        SubtitleText.Text = $"Configurações · versão {About.AboutWindow.AppVersion}";
        AboutButton.Click += (_, _) => (Avalonia.Application.Current as App)?.OpenAbout();

        // Pasta
        FolderText.Text = _settings.SaveFolder;
        FolderButton.Click += async (_, _) => await ChooseFolderAsync();

        // Vídeo
        VideoFolderText.Text = _settings.VideoFolder;
        VideoFolderButton.Click += async (_, _) => await ChooseVideoFolderAsync();
        FpsCombo.SelectedIndex = Math.Max(0, Array.IndexOf(FpsOptions, _settings.RecordFps));
        FpsCombo.SelectionChanged += (_, _) =>
            Update(s => s.RecordFps = FpsOptions[Math.Max(0, FpsCombo.SelectedIndex)]);

        // Notificações
        NotifySaveToggle.IsChecked = _settings.NotifyOnSave;
        NotifySaveToggle.IsCheckedChanged += (_, _) => Update(s => s.NotifyOnSave = NotifySaveToggle.IsChecked == true);
        NotifyCopyToggle.IsChecked = _settings.NotifyOnCopy;
        NotifyCopyToggle.IsCheckedChanged += (_, _) => Update(s => s.NotifyOnCopy = NotifyCopyToggle.IsChecked == true);

        // Atraso do atalho
        DelayCombo.SelectedIndex = Math.Max(0, Array.IndexOf(DelayOptions, _settings.ShortcutDelaySeconds));
        DelayCombo.SelectionChanged += (_, _) =>
            Update(s => s.ShortcutDelaySeconds = DelayOptions[Math.Max(0, DelayCombo.SelectedIndex)]);

        // Início automático
        AutostartToggle.IsChecked = SystemIntegration.IsAutostartEnabled;
        AutostartToggle.IsCheckedChanged += (_, _) => SystemIntegration.SetAutostart(AutostartToggle.IsChecked == true);

        // Espessura
        ThicknessSlider.Value = _settings.DefaultThickness;
        ThicknessText.Text = $"{_settings.DefaultThickness:0}";
        ThicknessSlider.ValueChanged += (_, e) =>
        {
            ThicknessText.Text = $"{e.NewValue:0}";
            Update(s => s.DefaultThickness = e.NewValue);
        };

        BuildColorSwatches();
        BuildKeysGrid();

        // Atalhos globais (captura e zoom)
        foreach (var slot in new[] { SystemIntegration.ShortcutSlot.Capture, SystemIntegration.ShortcutSlot.Zoom })
        {
            var current = slot;
            ButtonFor(current).Click += async (_, _) =>
            {
                if (_recordingSlot == current)
                    await StopRecordingAsync(null);
                else
                {
                    if (_recording)
                        await StopRecordingAsync(null);
                    await StartRecordingAsync(current);
                }
            };
        }
        Opened += async (_, _) =>
        {
            foreach (var slot in new[] { SystemIntegration.ShortcutSlot.Capture, SystemIntegration.ShortcutSlot.Zoom })
            {
                _shortcuts[slot] = await SystemIntegration.GetShortcutAsync(slot);
                ButtonFor(slot).Content = ShortcutFormat.ToDisplay(_shortcuts[slot]);
            }
        };
        Closing += async (_, _) =>
        {
            if (_recording)
                await StopRecordingAsync(null);
        };
    }

    private void Update(Action<AppSettings> change)
    {
        change(_settings);
        _settings.Save();
    }

    // ───────────── Pasta de destino ─────────────

    private async Task ChooseFolderAsync()
    {
        var start = await StorageProvider.TryGetFolderFromPathAsync(_settings.SaveFolder);
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Escolha onde salvar as capturas",
            SuggestedStartLocation = start,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            Update(s => s.SaveFolder = path);
            FolderText.Text = path;
        }
    }

    private async Task ChooseVideoFolderAsync()
    {
        var start = await StorageProvider.TryGetFolderFromPathAsync(_settings.VideoFolder);
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Escolha onde salvar as gravações",
            SuggestedStartLocation = start,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            Update(s => s.VideoFolder = path);
            VideoFolderText.Text = path;
        }
    }

    // ───────────── Cor padrão ─────────────

    private void BuildColorSwatches()
    {
        foreach (var hex in Palette)
        {
            var button = new Button
            {
                Classes = { "swatch" },
                Content = new Border { Classes = { "ring" }, Child = new Ellipse { Fill = SolidColorBrush.Parse(hex) } },
            };
            button.Classes.Set("selected", string.Equals(hex, _settings.DefaultColor, StringComparison.OrdinalIgnoreCase));
            button.Click += (_, _) =>
            {
                Update(s => s.DefaultColor = hex);
                foreach (var b in ColorSwatches.Children.OfType<Button>())
                    b.Classes.Set("selected", b == button);
            };
            ColorSwatches.Children.Add(button);
        }
    }

    // ───────────── Atalhos da captura (referência) ─────────────

    private void BuildKeysGrid()
    {
        for (var i = 0; i < CaptureKeys.Length; i++)
        {
            var (keys, action) = CaptureKeys[i];
            var row = i / 2;
            var col = i % 2 * 2;

            var cap = new Border
            {
                Background = SolidColorBrush.Parse("#3A3A3C"),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2),
                Margin = new Thickness(0, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = keys, FontSize = 11.5, FontWeight = FontWeight.Light, Foreground = SolidColorBrush.Parse("#F5F5F7") },
            };
            var label = new TextBlock
            {
                Text = action, FontSize = 12.5, Foreground = SolidColorBrush.Parse("#C7C7CC"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
            };

            Grid.SetRow(cap, row); Grid.SetColumn(cap, col);
            Grid.SetRow(label, row); Grid.SetColumn(label, col + 1);
            KeysGrid.Children.Add(cap);
            KeysGrid.Children.Add(label);
        }
    }

    // ───────────── Gravação do atalho global ─────────────

    private async Task StartRecordingAsync(SystemIntegration.ShortcutSlot slot)
    {
        _recordingSlot = slot;
        ButtonFor(slot).Classes.Add("recording");
        ButtonFor(slot).Content = "Pressione…";
        HintFor(slot).Text = $"Esc cancela. Use Ctrl/Alt/{ShortcutFormat.SuperName} + tecla, F1–F24 ou Print.";

        // Desliga o atalho atual enquanto grava, senão o sistema o intercepta e dispara a ação
        await SystemIntegration.SetShortcutAsync("", slot);

        // No Windows a tecla Print Screen não chega à janela: o app a recebe à parte
        SystemIntegration.ListenPrintScreen(async accelerator =>
        {
            if (_recordingSlot == slot)
                await StopRecordingAsync(accelerator);
        });
    }

    private async Task StopRecordingAsync(string? newShortcut)
    {
        if (_recordingSlot is not { } slot)
            return;
        _recordingSlot = null;
        ButtonFor(slot).Classes.Remove("recording");
        SystemIntegration.ListenPrintScreen(null);

        string? hint = null;
        if (newShortcut is not null)
        {
            if (await SystemIntegration.SetShortcutAsync(newShortcut, slot))
            {
                _shortcuts[slot] = newShortcut;
                SystemIntegration.SaveShortcut(newShortcut, slot);
            }
            else
            {
                // Recusado (outro programa já usa a combinação): volta ao atalho anterior
                hint = $"{ShortcutFormat.ToDisplay(newShortcut)} já está em uso por outro programa. Tente outra combinação.";
                newShortcut = null;
            }
        }

        if (newShortcut is null)
            await SystemIntegration.SetShortcutAsync(_shortcuts.GetValueOrDefault(slot) ?? "", slot);
        ButtonFor(slot).Content = ShortcutFormat.ToDisplay(_shortcuts.GetValueOrDefault(slot));
        HintFor(slot).Text = hint ?? HintText(slot);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        if (!_recording)
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            await StopRecordingAsync(null);
            return;
        }

        // Teclas modificadoras sozinhas ainda não formam um atalho
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        var accelerator = ShortcutFormat.ToAccelerator(e.Key, e.KeyModifiers);
        if (accelerator is null)
        {
            HintFor(_recordingSlot!.Value).Text = $"Essa combinação não pode ser usada. Tente com Ctrl, Alt ou {ShortcutFormat.SuperName}.";
            return;
        }

        await StopRecordingAsync(accelerator);
    }
}
