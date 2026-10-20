using System.Runtime.InteropServices;

namespace MacroRecorder.Native;

/// <summary>
/// Tipos e estruturas de baixo nível usados pelos hooks (WH_KEYBOARD_LL / WH_MOUSE_LL)
/// e pelo SendInput, conforme winuser.h do Windows SDK.
/// </summary>
public static class NativeTypes
{
    // ==================== ÍNDICES DE HOOK (SetWindowsHookEx) ====================
    public const int WH_KEYBOARD_LL = 13; // Hook global de teclado (baixo nível)
    public const int WH_MOUSE_LL    = 14; // Hook global de mouse  (baixo nível)

    // ==================== MENSAGENS DE TECLADO (hooks LL) ====================
    public const int WM_KEYDOWN     = 0x0100;
    public const int WM_KEYUP       = 0x0101;
    public const int WM_SYSKEYDOWN  = 0x0104;
    public const int WM_SYSKEYUP    = 0x0105;

    // ==================== MENSAGENS DE MOUSE (hooks + MSLLHOOKSTRUCT) ====================
    public const int WM_LBUTTONDOWN    = 0x0201; // Botão esquerdo pressionado
    public const int WM_LBUTTONUP      = 0x0202; // Botão esquerdo soltado
    public const int WM_RBUTTONDOWN    = 0x0204; // Botão direito pressionado
    public const int WM_RBUTTONUP      = 0x0205; // Botão direito soltado
    public const int WM_MBUTTONDOWN    = 0x0207; // Botão do meio pressionado
    public const int WM_MBUTTONUP      = 0x0208; // Botão do meio soltado
    public const int WM_MOUSEMOVE      = 0x0200; // Movimento do cursor
    public const int WM_LBUTTONDBLCLK  = 0x0203; // Duplo clique esquerdo
    public const int WM_RBUTTONDBLCLK  = 0x0206; // Duplo clique direito
    public const int WM_MOUSEWHEEL     = 0x020A; // Scroll vertical

    // ==================== FLAGS DE MODIFICADORES (RegisterHotKey) ====================
    public const uint MOD_ALT     = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT   = 0x0004;
    public const uint MOD_WIN     = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000; // Evita repetição ao segurar a tecla

    // ==================== ERROS DE RegisterHotKey ====================
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409; // Atalho já registrado por outro app

    // ==================== ENVIO SINTÉTICO (SendInput) ====================
    public const uint INPUT_MOUSE     = 0;
    public const uint INPUT_KEYBOARD  = 1;

    public const uint MOUSEEVENTF_MOVE       = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
    public const uint MOUSEEVENTF_ABSOLUTE   = 0x8000; // Coordenadas normalizadas 0..65535
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000; // Multi-monitor (desktop virtual)
    public const uint MOUSEEVENTF_WHEEL      = 0x0800; // Scroll

    public const uint KEYEVENTF_KEYUP        = 0x0002;
    public const uint KEYEVENTF_EXTENDEDKEY  = 0x0001;

    // ==================== CONSTANTES DO LOOP DE MENSAGENS ====================
    public const int PM_REMOVE = 1;
    public const int WM_QUIT   = 0x0012;

    /// <summary>Delegado nativo para procedimentos de hook (CallNextHookEx).</summary>
    public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>Estrutura MSLLHOOKSTRUCT - dados de um evento de mouse vindo do hook.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;      // Coordenadas ABSOLUTAS na tela (pixels físicos)
        public int mouseData; // Delta do wheel ou código do botão X
        public uint flags;    // LLMHF_*
        public uint time;     // TickCount do evento
        public IntPtr dwExtraInfo;
    }

    /// <summary>Estrutura KBDLLHOOKSTRUCT - dados de um evento de teclado vindo do hook.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;      // Virtual Key Code da tecla
        public uint scanCode;    // Scan code
        public uint flags;       // LLKHF_* (ex.: extended key)
        public uint time;        // TickCount do evento
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>INPUT para SendInput - union entre mouse/keyboard/hardware.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;            // Posição X (absoluta normalizada se MOUSEEVENTF_ABSOLUTE)
        public int dy;            // Posição Y
        public int mouseData;     // Delta do wheel
        public uint dwFlags;      // MOUSEEVENTF_*
        public uint time;         // 0 = tempo do sistema
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;        // Virtual Key Code
        public ushort wScan;
        public uint dwFlags;      // KEYEVENTF_*
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
