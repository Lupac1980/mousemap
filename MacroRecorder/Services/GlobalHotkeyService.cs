using System.Runtime.InteropServices;
using MacroRecorder.Native;

namespace MacroRecorder.Services;

/// <summary>
/// Serviço de hotkeys GLOBAIS via RegisterHotKey/UnregisterHotKey.
///
/// Estratégia: uma thread oculta (message-only window) recebe as mensagens
/// WM_HOTKEY quando o usuário pressiona um atalho registrado em qualquer
/// aplicação do Windows. Isso é mais eficiente que filtrar tudo por hook LL.
///
/// Também oferece "TryRegister" para VALIDAÇÃO da hotkey durante o mapeamento:
/// se o Windows retornar erro 1409, a UI mostra o atalho em VERMELHO.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0003;
    private const uint WS_OVERLAPPEDWINDOW_LIKE = 0; // message-only window não precisa de estilo

    private IntPtr _hwnd = IntPtr.Zero;           // handle da janela message-only
    private Thread? _windowThread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _idLock = new();
    private int _nextId = 1;                       // id sequencial das hotkeys

    // Impede GC dos delegates WndProc enquanto a janela existe
    private WndProcDelegate? _wndProcDelegate;
    private IntPtr _wndProcPtr = IntPtr.Zero;

    // Mapa: id da hotkey -> Automation.Id (para saber qual macro disparou)
    private readonly Dictionary<int, string> _registered = new();

    /// <summary>Dispara (na thread da UI, via Dispatcher) quando uma hotkey registrada é pressionada. Arg = automationId.</summary>
    public event Action<string>? HotkeyPressed;

    /// <summary>Handle da janela message-only (para PostMessage de shutdown).</summary>
    public IntPtr Handle => _hwnd;

    // Delegate nativo do WindowProc - assinatura obrigatória
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    // ==================== INICIALIZAÇÃO ====================

    /// <summary>Cria a message-only window em thread dedicada com message loop.</summary>
    public void Start()
    {
        if (_hwnd != IntPtr.Zero) return; // já iniciado

        _wndProcDelegate = WndProc;
        _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);

        _windowThread = new Thread(() =>
        {
            try
            {
                string className = "MacroRecorder_HotkeyWindow";
                IntPtr hInstance = NativeMethods.GetModuleHandleW(null);

                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = _wndProcPtr,
                    hInstance = hInstance,
                    lpszClassName = className
                };

                if (RegisterClassExW(ref wc) == 0)
                    throw new InvalidOperationException($"Falha ao registrar classe da janela (erro {Marshal.GetLastWin32Error()}).");

                // HWND_MESSAGE = (IntPtr)(-3): janela sem representação visual.
                IntPtr hwnd = CreateWindowExW(0, className, "MacroRecorderHotkeys", 0,
                    0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

                // Fallback: alguns sistemas exigem parent válido; usamos IntPtr.Zero mesmo.
                if (hwnd == IntPtr.Zero)
                    throw new InvalidOperationException($"Falha ao criar message-only window (erro {Marshal.GetLastWin32Error()}).");

                _hwnd = hwnd;
                _ready.Set();

                // Message loop desta thread - necessário para receber WM_HOTKEY.
                while (true)
                {
                    int r = NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
                    if (r == 0 || r == -1) break;
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessageW(ref msg);
                }

                // Limpeza da thread: unregister-all + destroy
                UnregisterAllInternal();
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            catch
            {
                _ready.Set(); // evita deadlock no Wait() mesmo em falha
            }
        })
        { Name = "MacroRecorder-HotkeyWindow", IsBackground = true };

        _windowThread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Não foi possível criar a janela de hotkeys globais.");
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            string? automationId;
            lock (_idLock)
            {
                _registered.TryGetValue(id, out automationId);
            }
            if (automationId != null)
            {
                try { HotkeyPressed?.Invoke(automationId); } catch { /* nunca vazar p/ nativo */ }
            }
            return IntPtr.Zero; // consumida
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ==================== REGISTRO / VALIDAÇÃO ====================

    /// <summary>Resultado de uma tentativa de registro de hotkey.</summary>
    public record HotkeyResult(bool Success, int ErrorCode, string? ErrorMessage)
    {
        public bool IsConflict => ErrorCode == NativeTypes.ERROR_HOTKEY_ALREADY_REGISTERED;
    }

    /// <summary>
    /// Tenta registrar a hotkey GLOBALMENTE. Se falhar com erro 1409, significa
    /// "atalho em uso por outro aplicativo" - a UI deve pintar de vermelho.
    /// </summary>
    /// <param name="modifiers">Flags MOD_* (Control/Alt/Shift/Win).</param>
    /// <param name="vk">Virtual Key Code da tecla principal.</param>
    /// <param name="automationId">Id da automação dona da hotkey.</param>
    /// <param name="permanent">false = teste temporário (desregistra em seguida).</param>
    public HotkeyResult TryRegister(uint modifiers, uint vk, string automationId, bool permanent = true)
    {
        if (_hwnd == IntPtr.Zero)
            return new HotkeyResult(false, -1, "Serviço de hotkeys não iniciado.");

        // MOD_NOREPEAT sempre: evita flood ao segurar a combinação.
        uint mods = modifiers | NativeTypes.MOD_NOREPEAT;

        int id;
        lock (_idLock) { id = _nextId++; }

        // Para teste de conflito usamos um id temporário alto, sem gravar no mapa.
        bool ok = NativeMethods.RegisterHotKey(_hwnd, permanent ? id : 0x7FFF0000 + id, mods, vk);

        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            string msg = err switch
            {
                NativeTypes.ERROR_HOTKEY_ALREADY_REGISTERED => "Atalho em uso, escolha outro.",
                1410 => "Modificadores inválidos para esta tecla.",
                _ => $"Erro Win32 {err} ao registrar hotkey."
            };
            return new HotkeyResult(false, err, msg);
        }

        if (permanent)
        {
            lock (_idLock) { _registered[id] = automationId; }
        }
        else
        {
            // Foi apenas validação: libera imediatamente para não ocupar o slot.
            NativeMethods.UnregisterHotKey(_hwnd, 0x7FFF0000 + id);
        }

        return new HotkeyResult(true, 0, null);
    }

    /// <summary>Desregistra todas as hotkeys associadas a um automationId.</summary>
    public bool Unregister(string automationId)
    {
        if (_hwnd == IntPtr.Zero) return false;
        lock (_idLock)
        {
            var ids = _registered.Where(kv => kv.Value == automationId).Select(kv => kv.Key).ToList();
            foreach (var id in ids)
            {
                NativeMethods.UnregisterHotKey(_hwnd, id);
                _registered.Remove(id);
            }
            return ids.Count > 0;
        }
    }

    private void UnregisterAllInternal()
    {
        lock (_idLock)
        {
            foreach (var id in _registered.Keys)
                NativeMethods.UnregisterHotKey(_hwnd, id);
            _registered.Clear();
        }
    }

    public void Dispose()
    {
        if (_windowThread is { IsAlive: true })
        {
            UnregisterAllInternal();
            // WM_QUIT via PostThreadMessage não funciona sem thread id; usamos DestroyWindow
            // pela API PostMessage via mensagem customizada — abordagem simples e segura:
            // postar WM_CLOSE na própria janela.
            PostMessageClose(_hwnd);
            _windowThread.Join(TimeSpan.FromSeconds(3));
        }
        UnregisterAllInternal();
        _ready.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static void PostMessageClose(IntPtr hWnd)
    {
        const uint WM_CLOSE = 0x0010;
        try { PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { /* app morrendo */ }
    }
}
