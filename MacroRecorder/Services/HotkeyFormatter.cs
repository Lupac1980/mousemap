using System.Windows.Input;
using MacroRecorder.Native;

namespace MacroRecorder.Services;

/// <summary>
/// Utilitários para conversão de teclas: Virtual Key Code <-> texto legível,
/// detecção de modificadores e construção da string de preview do atalho
/// (ex.: "Ctrl+Shift+1").
/// </summary>
public static class HotkeyFormatter
{
    /// <summary>Converte um ModifierKeys do WPF nas flags MOD_* do RegisterHotKey.</summary>
    public static uint ToModifierFlags(ModifierKeys mods)
    {
        uint flags = 0;
        if (mods.HasFlag(ModifierKeys.Control))    flags |= NativeTypes.MOD_CONTROL;
        if (mods.HasFlag(ModifierKeys.Alt))        flags |= NativeTypes.MOD_ALT;
        if (mods.HasFlag(ModifierKeys.Shift))      flags |= NativeTypes.MOD_SHIFT;
        if (mods.HasFlag(ModifierKeys.Windows))    flags |= NativeTypes.MOD_WIN;
        return flags;
    }

    /// <summary>Extrai apenas os modificadores pressionados do estado atual do teclado.</summary>
    public static ModifierKeys CurrentModifiers()
    {
        var mods = ModifierKeys.None;
        if (Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl))  mods |= ModifierKeys.Control;
        if (Keyboard.IsKeyDown(Key.LeftAlt)   || Keyboard.IsKeyDown(Key.RightAlt))   mods |= ModifierKeys.Alt;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) mods |= ModifierKeys.Shift;
        if (Keyboard.IsKeyDown(Key.LWin)      || Keyboard.IsKeyDown(Key.RWin))       mods |= ModifierKeys.Windows;
        return mods;
    }

    /// <summary>
    /// Monta o texto do atalho a partir dos modificadores + tecla principal.
    /// Ex.: Ctrl+Shift+1, Alt+F4, Win+Space.
    /// </summary>
    public static string Format(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt))     parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift))   parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyToDisplay(key));
        return string.Join("+", parts);
    }

    /// <summary>Nome amigável para cada Key do WPF (cobre as teclas mais comuns).</summary>
    public static string KeyToDisplay(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),                       // A..Z
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(), // 0..9 (fileira superior)
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => key.ToString(),                     // F1..F24
        Key.Space => "Space",
        Key.Enter or Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Tab => "Tab",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
        Key.OemPlus => "+", Key.OemMinus => "-", Key.OemQuestion => "/",
        Key.OemPeriod => ".", Key.OemComma => ",", Key.OemSemicolon => ";",
        Key.OemQuotes => "'", Key.OemTilde => "`",
        Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        _ => key.ToString()
    };

    /// <summary>
    /// Converte uma Key do WPF no Virtual Key Code aceito por RegisterHotKey/SendInput.
    /// Usa a numeração conhecida da enum Keys do WinForms/System.Windows.Forms via
    /// mapeamento manual (o WPF não expõe VK diretamente).
    /// </summary>
    public static uint KeyToVirtualKeyCode(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 0x41 + (uint)(key - Key.A),            // A-Z = 0x41..0x5A
        >= Key.D0 and <= Key.D9 => 0x30 + (uint)(key - Key.D0),         // 0-9 = 0x30..0x39
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (uint)(key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => 0x70 + (uint)(key - Key.F1),        // F1-F24 = 0x70..0x87
        Key.Space => 0x20,
        Key.Enter or Key.Return => 0x0D,
        Key.Escape => 0x1B,
        Key.Tab => 0x09,
        Key.Back => 0x08,
        Key.Delete => 0x2E,
        Key.Insert => 0x2D,
        Key.Home => 0x24,
        Key.End => 0x23,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28,
        Key.OemPlus => 0xBB, Key.OemMinus => 0xBD, Key.OemQuestion => 0xBF,
        Key.OemPeriod => 0xBE, Key.OemComma => 0xBC, Key.OemSemicolon => 0xBA,
        Key.OemQuotes => 0xDE, Key.OemTilde => 0xC0,
        Key.OemOpenBrackets => 0xDB, Key.OemCloseBrackets => 0xDD, Key.OemPipe => 0xDC,
        Key.LWin or Key.RWin => 0x5B,
        Key.LeftCtrl or Key.RightCtrl => 0x11,
        Key.LeftAlt or Key.RightAlt => 0x12,
        Key.LeftShift or Key.RightShift => 0x10,
        _ => 0
    };

    /// <summary>Valida se a combinação tem pelo menos um modificador obrigatório.</summary>
    public static bool HasRequiredModifier(ModifierKeys mods) =>
        mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Alt) || mods.HasFlag(ModifierKeys.Windows);

    /// <summary>Retorna true se a tecla é ela própria um modificador (não pode ser tecla "final").</summary>
    public static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
}
