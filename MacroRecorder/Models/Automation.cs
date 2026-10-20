using System.Windows.Input;

namespace MacroRecorder.Models;

/// <summary>
/// Tipos de eventos de mouse que o gravador captura e o player reproduz.
/// Persistido como string no JSON para legibilidade.
/// </summary>
public enum MouseEventType
{
    Move,        // WM_MOUSEMOVE (segmentos de movimento - usados em drag & drop)
    LeftDown,    // WM_LBUTTONDOWN
    LeftUp,      // WM_LBUTTONUP
    LeftClick,   // Clique esquerdo completo (down + up na mesma posição)
    LeftDoubleClick, // WM_LBUTTONDBLCLK
    RightDown,   // WM_RBUTTONDOWN
    RightUp,     // WM_RBUTTONUP
    RightClick,  // Clique direito completo
    RightDoubleClick, // WM_RBUTTONDBLCLK
    MiddleDown,  // WM_MBUTTONDOWN
    MiddleUp,    // WM_MBUTTONUP
    Wheel        // WM_MOUSEWHEEL (mouseData = delta)
}

/// <summary>
/// Um evento de mouse gravado/reproduzido.
/// Timestamp usa Environment.TickCount64 (ms relativos ao início da gravação),
/// permitindo calcular os delays entre ações na reprodução.
/// </summary>
public class RecordedMouseEvent
{
    public MouseEventType Type { get; set; }

    /// <summary>Coordenada X absoluta (pixels físicos do monitor primário/desktop virtual).</summary>
    public int X { get; set; }

    /// <summary>Coordenada Y absoluta.</summary>
    public int Y { get; set; }

    /// <summary>Instante em ms (TickCount64 relativo ao início da gravação). Usado para delay.</summary>
    public long TimestampMs { get; set; }

    /// <summary>Delta do scroll (apenas para eventos Wheel).</summary>
    public int WheelDelta { get; set; }

    public override string ToString() =>
        $"[{TimestampMs,7} ms] {Type} ({X}, {Y})" + (Type == MouseEventType.Wheel ? $" Δ={WheelDelta}" : "");
}

/// <summary>
/// Representa uma automação completa: nome + hotkey + sequência de eventos.
/// É a unidade persistida no JSON.
/// </summary>
public class Automation
{
    /// <summary>Id interno estável (GUID curto) para operações de UI/exclusão.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>Hotkey em texto legível, ex.: "Ctrl+Shift+1".</summary>
    public string HotkeyText { get; set; } = string.Empty;

    /// <summary>Flags de modificador MOD_* (uint) para RegisterHotKey.</summary>
    public uint Modifiers { get; set; }

    /// <summary>Virtual Key Code (Keys) para RegisterHotKey.</summary>
    public uint KeyCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<RecordedMouseEvent> Events { get; set; } = new();

    /// <summary>Quantidade de eventos capturados (para exibição na grid).</summary>
    public int EventCount => Events.Count;

    public override string ToString() => $"{Name} ({HotkeyText})";
}

/// <summary>Classe raiz do documento JSON de persistência.</summary>
public class AutomationStore
{
    public List<Automation> Automations { get; set; } = new();
}
