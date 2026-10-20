using MacroRecorder.Models;
using MacroRecorder.Native;

namespace MacroRecorder.Services;

/// <summary>
/// Gravador de automações: recebe os eventos brutos do HookManager (mouse e
/// teclado), interpreta a semântica (clique, duplo clique, drag & drop) e
/// monta a lista de RecordedMouseEvent com timestamps relativos.
///
/// A gravação SÓ termina quando o usuário pressiona a hotkey que acabou de
/// criar - sinalizada via CheckStopHotkey pelo MainWindow.
/// </summary>
public sealed class AutomationRecorder
{
    // Limiares de interpretação dos eventos crus
    private const int CLICK_MOVE_TOLERANCE_PX = 5;   // se mover < 5px entre down/up => clique
    private const long DOUBLE_CLICK_MAX_GAP_MS = 500; // janela de duplo clique do Windows
    private const int MOVE_SAMPLE_INTERVAL_MS = 25;  // amostragem máx. de mouse moves (~40/s)

    private readonly List<RecordedMouseEvent> _events = new();
    private long _startTick;              // TickCount64 do início da gravação
    private bool _isRecording;

    // Estado para detectar cliques x drags
    private NativeTypes.POINT _downPoint;         // posição do último button-down
    private MouseEventType _downType;             // LeftDown/RightDown/MiddleDown
    private bool _buttonHeld;                     // há um botão pressionado aguardando up?
    private bool _movedDuringDrag;                // houve movimento relevante após o down?
    private long _lastMoveSampleTick;             // throttling de WM_MOUSEMOVE
    private long _lastClickUpTick;                // p/ detecção de duplo clique
    private NativeTypes.POINT _lastClickPos;

    /// <summary>True enquanto a gravação está ativa.</summary>
    public bool IsRecording => _isRecording;

    /// <summary>Evento capturado (para log ao vivo na UI, opcional).</summary>
    public event Action<RecordedMouseEvent>? EventCaptured;

    /// <summary>Hotkey alvo que encerra a gravação (modifiers MOD_* + vk).</summary>
    public uint StopModifiers { get; private set; }
    public uint StopVk { get; private set; }

    // Estado das teclas modificadoras visto pelo hook LL (o RegisterHotKey não
    // está ativo durante a gravação da própria hotkey, então detectamos aqui).
    private bool _ctrlDown, _altDown, _shiftDown, _winDown;

    /// <summary>Inicia a captura. A partir daqui, HandleMouse deve ser chamado pelo hook.</summary>
    public void Start(uint stopModifiers, uint stopVk)
    {
        if (_isRecording) throw new InvalidOperationException("Gravação já em andamento.");

        _events.Clear();
        _startTick = Environment.TickCount64;
        _buttonHeld = false;
        _movedDuringDrag = false;
        _lastClickUpTick = 0;
        _lastMoveSampleTick = 0;
        StopModifiers = stopModifiers;
        StopVk = stopVk;
        _isRecording = true;
    }

    /// <summary>Interrompe a captura e devolve a sequência completa de eventos.</summary>
    public IReadOnlyList<RecordedMouseEvent> Stop()
    {
        _isRecording = false;
        return _events.ToList(); // cópia defensiva
    }

    // ==================== ENTRADA DO HOOK DE MOUSE ====================

    /// <summary>
    /// Processa um evento bruto de mouse vindo do WH_MOUSE_LL.
    /// wParam = mensagem WM_*, info = estrutura com posição absoluta e tempo.
    /// </summary>
    public void HandleMouse(int wParam, NativeTypes.MSLLHOOKSTRUCT info)
    {
        if (!_isRecording) return;

        // IMPORTANTE: ignora eventos sintéticos (injetados por SendInput) para
        // nunca gravar as próprias reproduções. flag bit 0 = LLMHF_INJECTED.
        if ((info.flags & 0x01) != 0) return;

        long now = RelativeMs(info.time);

        switch (wParam)
        {
            case NativeTypes.WM_LBUTTONDOWN:  OnButton(now, info, MouseEventType.LeftDown);   break;
            case NativeTypes.WM_RBUTTONDOWN:  OnButton(now, info, MouseEventType.RightDown);  break;
            case NativeTypes.WM_MBUTTONDOWN:  OnButton(now, info, MouseEventType.MiddleDown); break;

            case NativeTypes.WM_LBUTTONUP:    OnButtonUp(now, info, MouseEventType.LeftUp,   MouseEventType.LeftDown);   break;
            case NativeTypes.WM_RBUTTONUP:    OnButtonUp(now, info, MouseEventType.RightUp,  MouseEventType.RightDown);  break;
            case NativeTypes.WM_MBUTTONUP:    OnButtonUp(now, info, MouseEventType.MiddleUp, MouseEventType.MiddleDown); break;

            case NativeTypes.WM_MOUSEMOVE:    OnMove(now, info); break;

            case NativeTypes.WM_LBUTTONDBLCLK: OnDoubleClick(now, info, MouseEventType.LeftDoubleClick); break;
            case NativeTypes.WM_RBUTTONDBLCLK: OnDoubleClick(now, info, MouseEventType.RightDoubleClick); break;

            case NativeTypes.WM_MOUSEWHEEL:
                AddEvent(MouseEventType.Wheel, info.pt.X, info.pt.Y, now, info.mouseData);
                break;
        }
    }

    private void OnButton(long now, NativeTypes.MSLLHOOKSTRUCT info, MouseEventType downType)
    {
        _downPoint = info.pt;
        _downType = downType;
        _buttonHeld = true;
        _movedDuringDrag = false;
        AddEvent(downType, info.pt.X, info.pt.Y, now);
    }

    private void OnButtonUp(long now, NativeTypes.MSLLHOOKSTRUCT info, MouseEventType upType, MouseEventType expectedDown)
    {
        AddEvent(upType, info.pt.X, info.pt.Y, now);

        // Detecta duplo clique "de verdade": se o intervalo entre clicks for curto
        // e na mesma posição, o Windows já teria enviado DBLCLK - nada extra aqui.

        _lastClickUpTick = now;
        _lastClickPos = info.pt;
        _buttonHeld = false;
    }

    private void OnMove(long now, NativeTypes.MSLLHOOKSTRUCT info)
    {
        // Sem botão pressionado: movimento livre só é gravado se for parte de uma
        // sequência rica; para manter o JSON enxuto, gravamos movimentos apenas
        // DURANTE drag (botão pressionado) - requisito crítico do enunciado.
        if (!_buttonHeld) return;

        // Throttling: limita a ~40 amostras/seg para não explodir o JSON.
        if (now - _lastMoveSampleTick < MOVE_SAMPLE_INTERVAL_MS) 
        {
            // mas sempre registra o ponto final se houver mudança grande
            if (Distance(info.pt, _downPoint) < CLICK_MOVE_TOLERANCE_PX) return;
        }
        _lastMoveSampleTick = now;

        if (Distance(info.pt, _downPoint) >= CLICK_MOVE_TOLERANCE_PX)
            _movedDuringDrag = true;

        if (_movedDuringDrag)
            AddEvent(MouseEventType.Move, info.pt.X, info.pt.Y, now);
    }

    private void OnDoubleClick(long now, NativeTypes.MSLLHOOKSTRUCT info, MouseEventType dblType)
    {
        AddEvent(dblType, info.pt.X, info.pt.Y, now);
        _lastClickUpTick = now;
        _lastClickPos = info.pt;
    }

    // ==================== ENTRADA DO HOOK DE TECLADO ====================

    /// <summary>
    /// Processa um evento bruto de teclado vindo do WH_KEYBOARD_LL durante a
    /// gravação: mantém o estado dos modificadores e verifica se o usuário
    /// pressionou a hotkey de parada. Retorna true se a gravação deve encerrar.
    /// </summary>
    public bool CheckStopHotkey(uint vkCode, bool isKeyDown)
    {
        if (!_isRecording) return false;

        // Atualiza estado dos modificadores (VK: Ctrl=0x11, Alt=0x12, Shift=0x10, Win=0x5B/0x5C)
        switch (vkCode)
        {
            case 0x11: _ctrlDown = isKeyDown; break;
            case 0x12: _altDown = isKeyDown; break;
            case 0x10: _shiftDown = isKeyDown; break;
            case 0x5B:
            case 0x5C: _winDown = isKeyDown; break;
        }

        if (!isKeyDown) return false;

        // Compara a tecla pressionada + modificadores ativos com a hotkey alvo
        uint activeMods = 0;
        if (_ctrlDown)  activeMods |= NativeTypes.MOD_CONTROL;
        if (_altDown)   activeMods |= NativeTypes.MOD_ALT;
        if (_shiftDown) activeMods |= NativeTypes.MOD_SHIFT;
        if (_winDown)   activeMods |= NativeTypes.MOD_WIN;

        return vkCode == StopVk && activeMods == StopModifiers;
    }

    // ==================== HELPERS ====================

    /// <summary>Converte o time do hook (uint TickCount32) em ms relativos ao início.</summary>
    private long RelativeMs(uint hookTime)
    {
        // O campo time do MSLLHOOKSTRUCT é GetTickCount (uint). Para robustez,
        // usamos Environment.TickCount64 no instante do processamento, que chega
        // com latência sub-milissegundo do hook LL - suficiente para delays reais.
        _ = hookTime;
        return Environment.TickCount64 - _startTick;
    }

    private static double Distance(NativeTypes.POINT a, NativeTypes.POINT b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private void AddEvent(MouseEventType type, int x, int y, long timestampMs, int wheelDelta = 0)
    {
        var evt = new RecordedMouseEvent
        {
            Type = type,
            X = x,
            Y = y,
            TimestampMs = timestampMs,
            WheelDelta = wheelDelta
        };
        _events.Add(evt);
        EventCaptured?.Invoke(evt);
    }
}
