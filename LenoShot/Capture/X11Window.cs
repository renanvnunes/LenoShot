using System.Runtime.InteropServices;
using Avalonia;

namespace LenoShot.Capture;

/// <summary>
/// Posiciona a sobreposição em tela cheia num monitor específico.
/// O GNOME (Mutter) ignora a posição pedida pelo Avalonia, então falamos direto com o
/// gerenciador de janelas via EWMH (_NET_WM_FULLSCREEN_MONITORS + _NET_WM_STATE_FULLSCREEN).
/// </summary>
internal static partial class X11Window
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXinerama = "libXinerama.so.1";

    private const long SubstructureNotifyMask = 1L << 19;
    private const long SubstructureRedirectMask = 1L << 20;
    private const int ClientMessage = 33;
    private const int NetWmStateAdd = 1;

    public static void MakeFullscreenOn(IntPtr window, PixelRect monitor)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return;

        try
        {
            var root = XDefaultRootWindow(display);
            XMoveResizeWindow(display, window, monitor.X, monitor.Y, (uint)monitor.Width, (uint)monitor.Height);

            var index = FindMonitorIndex(display, monitor);
            if (index >= 0)
            {
                // topo, base, esquerda, direita: todos o mesmo monitor; 1 = pedido de um app comum
                SendClientMessage(display, root, window, "_NET_WM_FULLSCREEN_MONITORS", index, index, index, index, 1);
            }

            var fullscreen = XInternAtom(display, "_NET_WM_STATE_FULLSCREEN", false);
            SendClientMessage(display, root, window, "_NET_WM_STATE", NetWmStateAdd, (long)fullscreen, 0, 1, 0);
            XFlush(display);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    /// <summary>Move/redimensiona uma janela comum (depois de aberta) para um retângulo exato.</summary>
    public static void MoveResize(IntPtr window, PixelRect rect)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return;
        try
        {
            XMoveResizeWindow(display, window, rect.X, rect.Y, (uint)rect.Width, (uint)rect.Height);
            XFlush(display);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    /// <summary>
    /// Define o tipo EWMH da janela (ex.: "_NET_WM_WINDOW_TYPE_NOTIFICATION"). Janelas que não são
    /// "normais" ficam sem os efeitos do GNOME/extensões (sombra, cantos, borda). Deve ser chamado
    /// antes de a janela aparecer.
    /// </summary>
    public static void SetWindowType(IntPtr window, string type)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return;
        try
        {
            var property = XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
            var value = XInternAtom(display, type, false);
            const IntPtr xaAtom = 4;
            XChangeProperty(display, window, property, xaAtom, 32, 0, ref value, 1);
            XFlush(display);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    /// <summary>
    /// Tira a janela do controle do gerenciador de janelas (override-redirect). O GNOME empurra
    /// janelas gerenciadas para dentro da área útil (fora da barra de tarefas); com override-redirect
    /// a janela fica exatamente onde foi pedida, sem sombra nem decoração. Deve ser chamado ANTES
    /// de a janela ser mostrada.
    /// </summary>
    public static void SetOverrideRedirect(IntPtr window)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return;
        try
        {
            // XSetWindowAttributes tem 112 bytes (LP64); override_redirect fica no offset 88
            var attributes = new XSetWindowAttributes { OverrideRedirect = 1 };
            XChangeWindowAttributes(display, window, 1UL << 9 /* CWOverrideRedirect */, ref attributes);
            XFlush(display);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    /// <summary>Dá o foco do teclado à janela (sem o gerenciador de janelas, nada a ativa sozinho).</summary>
    public static void FocusInput(IntPtr window)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return;
        try
        {
            XSetInputFocus(display, window, 2 /* RevertToParent */, 0 /* CurrentTime */);
            XFlush(display);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 112)]
    private struct XSetWindowAttributes
    {
        [FieldOffset(88)] public int OverrideRedirect;
    }

    /// <summary>
    /// Posição e tamanho reais da janela na tela (o gerenciador de janelas pode ter movido a janela
    /// do lugar pedido, por exemplo para fora da barra do sistema).
    /// </summary>
    public static PixelRect? GetRect(IntPtr window)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return null;
        try
        {
            if (XGetGeometry(display, window, out var root, out _, out _, out var w, out var h, out _, out _) == 0)
                return null;
            // x e y do XGetGeometry são relativos ao pai (a moldura do gerenciador): converte para a raiz
            if (XTranslateCoordinates(display, window, root, 0, 0, out var rx, out var ry, out _) == 0)
                return null;
            return new PixelRect(rx, ry, (int)w, (int)h);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static int FindMonitorIndex(IntPtr display, PixelRect monitor)
    {
        var screens = XineramaQueryScreens(display, out var count);
        if (screens == IntPtr.Zero)
            return -1;

        try
        {
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<XineramaScreenInfo>(screens + i * Marshal.SizeOf<XineramaScreenInfo>());
                if (info.XOrg == monitor.X && info.YOrg == monitor.Y)
                    return info.ScreenNumber;
            }
            return -1;
        }
        finally
        {
            XFree(screens);
        }
    }

    private static void SendClientMessage(IntPtr display, IntPtr root, IntPtr window, string type,
        long d0, long d1, long d2, long d3, long d4)
    {
        var ev = new XClientMessageEvent
        {
            Type = ClientMessage,
            SendEvent = 1,
            Display = display,
            Window = window,
            MessageType = XInternAtom(display, type, false),
            Format = 32,
            Data0 = (IntPtr)d0,
            Data1 = (IntPtr)d1,
            Data2 = (IntPtr)d2,
            Data3 = (IntPtr)d3,
            Data4 = (IntPtr)d4,
        };
        XSendEvent(display, root, false, (IntPtr)(SubstructureRedirectMask | SubstructureNotifyMask), ref ev);
    }

    // XEvent tem 192 bytes; os campos de XClientMessageEvent ficam no começo.
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XClientMessageEvent
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public IntPtr Serial;
        [FieldOffset(16)] public int SendEvent;
        [FieldOffset(24)] public IntPtr Display;
        [FieldOffset(32)] public IntPtr Window;
        [FieldOffset(40)] public IntPtr MessageType;
        [FieldOffset(48)] public int Format;
        [FieldOffset(56)] public IntPtr Data0;
        [FieldOffset(64)] public IntPtr Data1;
        [FieldOffset(72)] public IntPtr Data2;
        [FieldOffset(80)] public IntPtr Data3;
        [FieldOffset(88)] public IntPtr Data4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XineramaScreenInfo
    {
        public int ScreenNumber;
        public short XOrg;
        public short YOrg;
        public short Width;
        public short Height;
    }

    [LibraryImport(LibX11)] private static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport(LibX11)] private static partial int XCloseDisplay(IntPtr display);
    [LibraryImport(LibX11)] private static partial IntPtr XDefaultRootWindow(IntPtr display);
    [LibraryImport(LibX11)] private static partial int XFlush(IntPtr display);
    [LibraryImport(LibX11)] private static partial int XFree(IntPtr data);

    [LibraryImport(LibX11)]
    private static partial int XMoveResizeWindow(IntPtr display, IntPtr window, int x, int y, uint width, uint height);

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [LibraryImport(LibX11)]
    private static partial int XSendEvent(IntPtr display, IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool propagate,
        IntPtr eventMask, ref XClientMessageEvent ev);

    [LibraryImport(LibX11)]
    private static partial int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type,
        int format, int mode, ref IntPtr data, int count);

    [LibraryImport(LibX11)]
    private static partial int XChangeWindowAttributes(IntPtr display, IntPtr window, ulong valueMask, ref XSetWindowAttributes attributes);

    [LibraryImport(LibX11)]
    private static partial int XSetInputFocus(IntPtr display, IntPtr window, int revertTo, ulong time);

    [LibraryImport(LibX11)]
    private static partial int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y,
        out uint width, out uint height, out uint border, out uint depth);

    [LibraryImport(LibX11)]
    private static partial int XTranslateCoordinates(IntPtr display, IntPtr source, IntPtr dest, int srcX, int srcY,
        out int destX, out int destY, out IntPtr child);

    [LibraryImport(LibXinerama)]
    private static partial IntPtr XineramaQueryScreens(IntPtr display, out int count);
}
