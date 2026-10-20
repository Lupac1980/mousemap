using System.Runtime.InteropServices;
using MacroRecorder.Native;

namespace MacroRecorder.Native;

/// <summary>
/// Wrapper fino (P/Invoke) sobre as APIs nativas do User32.dll usadas pelo app:
/// hooks de baixo nível, hotkeys globais, injeção de eventos e utilitários de cursor.
/// </summary>
public static class NativeMethods
{
    // ==================== HOOKS (WH_KEYBOARD_LL / WH_MOUSE_LL) ====================

    /// <summary>Instala um hook global de baixo nível.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(
        int idHook, NativeTypes.LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    /// <summary>Remove um hook instalado. Retorna false em erro.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    /// <summary>Encaminha o evento para o próximo hook da cadeia (obrigatório).</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>Obtém o handle do módulo atual (necessário para SetWindowsHookEx).</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    // ==================== HOTKEYS GLOBAIS (RegisterHotKey) ====================

    /// <summary>Registra uma hotkey global vinculada a uma janela/thread.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>Libera uma hotkey registrada.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ==================== LOOP DE MENSAGENS (thread dos hooks) ====================

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG lpmsg);

    /// <summary>Retira mensagens do filtro sem despachar (usado no loop customizado).</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

    /// <summary>Posta WM_QUIT para encerrar o GetMessage de uma thread.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint id, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public NativeTypes.POINT pt;
    }

    // ==================== INJEÇÃO DE EVENTOS (SendInput) ====================

    /// <summary>Injeta eventos sintéticos de mouse/teclado no stream do sistema.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, NativeTypes.INPUT[] pInputs, int cbSize);

    // ==================== CURSOR / TELA ====================

    /// <summary>Move o cursor do mouse para a posição absoluta informada.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int X, int Y);

    /// <summary>Obtém a posição atual do cursor.</summary>
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out NativeTypes.POINT lpPoint);

    /// <summary>Largura do primary monitor em pixels físicos.</summary>
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public const int SM_CXSCREEN = 0; // Largura do monitor primário
    public const int SM_CYSCREEN = 1; // Altura do monitor primário

    /// <summary>Converte VK para caractere de exibição (para preview do atalho).</summary>
    [DllImport("user32.dll")]
    public static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
}
