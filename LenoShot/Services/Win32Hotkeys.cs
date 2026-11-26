using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LenoShot.Services;

/// <summary>
/// Atalhos globais no Windows.
///
/// A maioria usa RegisterHotKey: uma janela só de mensagens (HWND_MESSAGE), criada na thread da
/// interface, recebe o WM_HOTKEY pelo laço de mensagens do próprio Avalonia.
///
/// A tecla Print Screen é diferente: no Windows 11 ela abre a Ferramenta de Captura, e o sistema
/// fica com ela antes do RegisterHotKey. Um gancho de teclado de baixo nível (WH_KEYBOARD_LL) a pega
/// antes e a "engole". O gancho roda numa thread só dele: todo o teclado do sistema passa por ele,
/// então não pode esperar a interface (que às vezes fica ocupada capturando a tela).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Win32Hotkeys
{
    private const uint WmHotkey = 0x0312;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const int WhKeyboardLl = 13;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSnapshot = 0x2C;
    private static readonly nint HwndMessage = -3;

    private static readonly Dictionary<int, Action> s_actions = [];
    private static nint s_window;

    // Atalhos com Print Screen, lidos pela thread do gancho (o array inteiro é trocado de uma vez)
    private static volatile PrintEntry[] s_printEntries = [];
    private static volatile Action<string>? s_printListener;
    private static Thread? s_hookThread;
    private static bool s_swallowKeyUp;

    private sealed record PrintEntry(int Id, uint Modifiers, Action Action);

    /// <summary>
    /// Registra (ou troca) o atalho <paramref name="id"/>. Atalho vazio só remove o anterior.
    /// Devolve false se a combinação é inválida ou já está em uso por outro programa.
    /// </summary>
    public static bool Register(int id, string? accelerator, Action action)
    {
        if (!EnsureWindow())
            return false;

        UnregisterHotKey(s_window, id);
        s_actions.Remove(id);
        s_printEntries = s_printEntries.Where(e => e.Id != id).ToArray();
        if (string.IsNullOrEmpty(accelerator))
            return true;

        if (!TryParse(accelerator, out var modifiers, out var vk))
            return false;

        if (vk == VkSnapshot)
        {
            EnsureHook();
            s_printEntries = [.. s_printEntries, new PrintEntry(id, modifiers, action)];
            return true;
        }

        if (!RegisterHotKey(s_window, id, modifiers | ModNoRepeat, vk))
            return false;

        s_actions[id] = action;
        return true;
    }

    /// <summary>
    /// Enquanto definido, qualquer Print Screen (com ou sem modificadores) é entregue aqui, no formato
    /// do atalho, em vez de disparar a ação. Usado pela tela de configurações ao gravar um atalho
    /// (sem isso, a tecla abriria a Ferramenta de Captura). Chamado de outra thread.
    /// </summary>
    public static void ListenPrintScreen(Action<string>? listener)
    {
        if (listener is not null)
            EnsureHook();
        s_printListener = listener;
    }

    private static void EnsureHook()
    {
        if (s_hookThread is not null)
            return;
        s_hookThread = new Thread(HookLoop) { IsBackground = true, Name = "leno-shot keyboard hook" };
        s_hookThread.Start();
    }

    private static void HookLoop()
    {
        if (SetWindowsHookExW(WhKeyboardLl, &KeyboardProc, GetModuleHandleW(null), 0) == 0)
        {
            Console.Error.WriteLine("[leno-shot] não foi possível instalar o gancho do teclado (Print Screen)");
            return;
        }

        // O gancho só é chamado enquanto esta thread processa mensagens
        Msg msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    [UnmanagedCallersOnly]
    private static nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && ((KbdLlHook*)lParam)->VkCode == VkSnapshot)
        {
            try
            {
                var message = (uint)wParam;
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    var modifiers = CurrentModifiers();
                    if (s_printListener is { } listener)
                    {
                        listener(ToAccelerator(modifiers));
                        s_swallowKeyUp = true;
                        return 1;
                    }
                    if (s_printEntries.FirstOrDefault(e => e.Modifiers == modifiers) is { } entry)
                    {
                        entry.Action();
                        s_swallowKeyUp = true;
                        return 1;
                    }
                }
                else if (s_swallowKeyUp)
                {
                    s_swallowKeyUp = false;
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[leno-shot] erro no atalho: {ex}");
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private static uint CurrentModifiers()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        var modifiers = 0u;
        if (Down(0x11)) modifiers |= ModControl;
        if (Down(0x12)) modifiers |= ModAlt;
        if (Down(0x10)) modifiers |= ModShift;
        if (Down(0x5B) || Down(0x5C)) modifiers |= ModWin;
        return modifiers;
    }

    /// <summary>Modificadores + Print no formato de atalho (mesma ordem de <see cref="ShortcutFormat.ToAccelerator"/>).</summary>
    private static string ToAccelerator(uint modifiers) =>
        ((modifiers & ModControl) != 0 ? "<Control>" : "") +
        ((modifiers & ModAlt) != 0 ? "<Alt>" : "") +
        ((modifiers & ModShift) != 0 ? "<Shift>" : "") +
        ((modifiers & ModWin) != 0 ? "<Super>" : "") +
        "Print";

    /// <summary>"&lt;Control&gt;&lt;Shift&gt;s" → modificadores e código de tecla virtual do Windows.</summary>
    private static bool TryParse(string accelerator, out uint modifiers, out uint vk)
    {
        var (mods, key) = ShortcutFormat.Parse(accelerator);
        modifiers = 0;
        foreach (var mod in mods)
        {
            modifiers |= mod switch
            {
                "Control" or "Primary" or "Ctrl" => ModControl,
                "Alt" => ModAlt,
                "Shift" => ModShift,
                "Super" => ModWin,
                _ => 0,
            };
        }

        vk = key switch
        {
            [var c] when c is >= 'a' and <= 'z' => (uint)char.ToUpperInvariant(c),
            [var c] when c is >= 'A' and <= 'Z' or >= '0' and <= '9' => c,
            ['F', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 24 => (uint)(0x70 + f - 1),
            "Print" => 0x2C,
            "Pause" => 0x13,
            "Insert" => 0x2D,
            "Home" => 0x24,
            "End" => 0x23,
            "Page_Up" => 0x21,
            "Page_Down" => 0x22,
            _ => 0,
        };
        return vk != 0;
    }

    private static bool EnsureWindow()
    {
        if (s_window != 0)
            return true;

        var instance = GetModuleHandleW(null);
        fixed (char* name = "LenoShotHotkeys")
        {
            var wc = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = &WndProc,
                Instance = instance,
                ClassName = name,
            };
            RegisterClassExW(&wc);
            s_window = CreateWindowExW(0, name, name, 0, 0, 0, 0, 0, HwndMessage, 0, instance, 0);
        }

        if (s_window == 0)
            Console.Error.WriteLine("[leno-shot] não foi possível criar a janela dos atalhos globais");
        return s_window != 0;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmHotkey && s_actions.TryGetValue((int)wParam, out var action))
        {
            // Uma exceção aqui (código chamado pelo Windows) derrubaria o processo
            try { action(); }
            catch (Exception ex) { Console.Error.WriteLine($"[leno-shot] erro no atalho: {ex}"); }
            return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHook
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowsHookExW(int id, delegate* unmanaged<int, nint, nint, nint> proc, nint module, uint threadId);

    [LibraryImport("user32.dll")] private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll")] private static partial int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(Msg* msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(Msg* msg);
    [LibraryImport("kernel32.dll")] private static partial nint GetModuleHandleW(char* name);
    [LibraryImport("user32.dll")] private static partial ushort RegisterClassExW(WndClassEx* wc);
    [LibraryImport("user32.dll")] private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);
}
