using System.Runtime.InteropServices;
using MacroRecorder.Native;

namespace MacroRecorder.Services;

/// <summary>
/// Gerencia hooks GLOBAIS de baixo nível (WH_KEYBOARD_LL e WH_MOUSE_LL) em uma
/// thread dedicada com message loop próprio (GetMessage/DispatchMessage), pois
/// hooks de baixo nível exigem um thread message loop ativo na mesma thread.
///
/// Observação importante: este hook é usado para GRAVAÇÃO (capturar todos os
/// cliques do usuário) e para detectar o fim da gravação pela própria hotkey.
/// Para disparo das macros já salvas usamos RegisterHotKey (mais eficiente).
/// </summary>
public sealed class HookManager : IDisposable
{
    private IntPtr _keyboardHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;

    // Mantém referência aos delegates: se forem coletados pelo GC, o callback
    // nativo aponta para memória liberada => AccessViolationException.
    private NativeTypes.LowLevelProc? _keyboardProc;
    private NativeTypes.LowLevelProc? _mouseProc;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private readonly ManualResetEventSlim _ready = new(false);

    /// <summary>Dispara a cada evento de teclado capturado (vkCode, pressionada?).</summary>
    public event Action<uint, bool>? KeyboardEvent;

    /// <summary>Dispara a cada evento de mouse capturado (wParam, lParam - já clonados).</summary>
    public event Action<int, NativeTypes.MSLLHOOKSTRUCT>? MouseEvent;

    public bool IsInstalled => _keyboardHook != IntPtr.Zero || _mouseHook != IntPtr.Zero;

    // ==================== INSTALAÇÃO ====================

    /// <summary>
    /// Instala um ou ambos os hooks globais em uma thread de fundo com message loop.
    /// </summary>
    public void Install(bool keyboard, bool mouse)
    {
        if (IsInstalled) throw new InvalidOperationException("Hooks já instalados.");

        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;

        var tcsReady = new ManualResetEventSlim(false);
        Exception? startupError = null;

        _hookThread = new Thread(() =>
        {
            try
            {
                _hookThreadId = AppContextData.CurrentThreadId();
                IntPtr hModule = NativeMethods.GetModuleHandleW(null);

                if (keyboard)
                {
                    _keyboardHook = NativeMethods.SetWindowsHookExW(
                        NativeTypes.WH_KEYBOARD_LL, _keyboardProc!, hModule, 0);
                    if (_keyboardHook == IntPtr.Zero)
                        throw new Win32ExceptionSafe("SetWindowsHookEx(WH_KEYBOARD_LL)", Marshal.GetLastWin32Error());
                }

                if (mouse)
                {
                    _mouseHook = NativeMethods.SetWindowsHookExW(
                        NativeTypes.WH_MOUSE_LL, _mouseProc!, hModule, 0);
                    if (_mouseHook == IntPtr.Zero)
                        throw new Win32ExceptionSafe("SetWindowsHookEx(WH_MOUSE_LL)", Marshal.GetLastWin32Error());
                }

                _ready.Set();

                // ---- Message loop obrigatório para low-level hooks ----
                // GetMessage retorna 0 quando recebe WM_QUIT (postado por Dispose).
                while (true)
                {
                    int result = NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
                    if (result == 0 || result == -1) break; // WM_QUIT ou erro
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessageW(ref msg);
                }
            }
            catch (Exception ex)
            {
                startupError = ex;
                _ready.Set();
            }
            finally
            {
                tcsReady.Set();
                // Garantia de liberação caso a thread morra sem Dispose()
                RemoveHooksInternal();
            }
        })
        {
            Name = "MacroRecorder-HookThread",
            IsBackground = true
        };

        _hookThread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
        tcsReady.Wait(TimeSpan.FromMilliseconds(1)); // apenas informativo

        if (startupError != null)
        {
            Dispose();
            throw startupError;
        }
    }

    // ==================== CALLBACKS NATIVOS ====================

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // nCode < 0: obrigatório repassar sem processar (docs MSDN)
        if (nCode >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<NativeTypes.KBDLLHOOKSTRUCT>(lParam);
                bool isKeyDown = (int)wParam switch
                {
                    NativeTypes.WM_KEYDOWN    => true,  // 0x0100
                    NativeTypes.WM_SYSKEYDOWN => true,  // 0x0104
                    _ => false                          // WM_KEYUP / WM_SYSKEYUP
                };
                KeyboardEvent?.Invoke(info.vkCode, isKeyDown);
            }
            catch
            {
                // Nunca deixar exceção vazar para o código nativo: travaria o sistema.
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<NativeTypes.MSLLHOOKSTRUCT>(lParam);
                MouseEvent?.Invoke((int)wParam, info);
            }
            catch
            {
                // idem: engolir exceções para não corromper a cadeia de hooks
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // ==================== REMOÇÃO / DISPOSE ====================

    private void RemoveHooksInternal()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Libera os hooks e encerra a thread do message loop com segurança.
    /// Chamar SEMPRE ao sair do app (requisito crítico do enunciado).
    /// </summary>
    public void Dispose()
    {
        if (_hookThread is { IsAlive: true })
        {
            // WM_QUIT postado para a thread do hook encerra o GetMessage.
            NativeMethods.PostThreadMessageW(_hookThreadId, NativeTypes.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread.Join(TimeSpan.FromSeconds(3));
        }

        // Unhook também é chamado no finally da thread, mas garantimos aqui:
        RemoveHooksInternal();

        _keyboardProc = null;
        _mouseProc = null;
        _ready.Dispose();
    }

    ~HookManager() => Dispose();
}

/// <summary>Helper para obter o ManagedThreadId -> NativeThreadId atual.</summary>
internal static class AppContextData
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public static uint CurrentThreadId() => GetCurrentThreadId();
}

/// <summary>Exceção amigável para falhas de APIs Win32 com código de erro.</summary>
public sealed class Win32ExceptionSafe(string api, int errorCode)
    : Exception($"Falha ao chamar {api}. Código de erro Win32: {errorCode}");
