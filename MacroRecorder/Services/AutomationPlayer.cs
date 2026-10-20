using System.Runtime.InteropServices;
using MacroRecorder.Models;
using MacroRecorder.Native;

namespace MacroRecorder.Services;

/// <summary>
/// Reproduzidor de automações: percorre a lista de eventos gravados, respeita
/// os delays (Task.Delay) e injeta os cliques/movimentos via SendInput.
///
/// Executa em Task dedicada com CancellationTokenSource para permitir
/// cancelamento (ex.: nova hotkey pressionada ou saída do app).
/// </summary>
public sealed class AutomationPlayer
{
    // Delay máximo entre eventos na reprodução: evita "travar" o mouse por
    // muito tempo se o usuário ficou parado durante a gravação (cap em 3s).
    private const long MAX_DELAY_MS = 3000;

    // Pequena pausa inicial para o sistema "assentar" após a hotkey.
    private const int STARTUP_GRACE_MS = 150;

    /// <summary>True enquanto há uma reprodução em andamento.</summary>
    public bool IsPlaying { get; private set; }

    private CancellationTokenSource? _cts;

    /// <summary>Início/término da reprodução (para feedback visual no tray).</summary>
    public event Action<Automation>? PlaybackStarted;
    public event Action<Automation>? PlaybackFinished;

    /// <summary>
    /// Reproduz a automação de forma assíncrona em thread separada.
    /// Retorna a Task em execução (aguardável, mas normalmente fire-and-forget).
    /// </summary>
    public Task PlayAsync(Automation automation)
    {
        if (IsPlaying)
        {
            // Não permite reproduções simultâneas: cancela a anterior.
            Cancel();
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        return Task.Run(async () =>
        {
            IsPlaying = true;
            PlaybackStarted?.Invoke(automation);
            try
            {
                await ExecuteSequenceAsync(automation.Events, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelamento é fluxo normal - nada a logar como erro.
            }
            catch (Exception ex)
            {
                Logger.Error($"Erro ao reproduzir '{automation.Name}'", ex);
            }
            finally
            {
                IsPlaying = false;
                PlaybackFinished?.Invoke(automation);
                _cts.Dispose();
                _cts = null;
            }
        }, token);
    }

    /// <summary>Cancela a reprodução em andamento (libera o botão do mouse se preso).</summary>
    public void Cancel()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* já finalizada */ }
    }

    // ==================== NÚCLEO DA REPRODUÇÃO ====================

    private static async Task ExecuteSequenceAsync(IReadOnlyList<RecordedMouseEvent> events, CancellationToken token)
    {
        if (events.Count == 0) return;

        await Task.Delay(STARTUP_GRACE_MS, token).ConfigureAwait(false);

        long previousTimestamp = events[0].TimestampMs;

        foreach (var evt in events)
        {
            token.ThrowIfCancellationRequested();

            // ---- DELAY: diferença de timestamps entre eventos consecutivos ----
            long delay = evt.TimestampMs - previousTimestamp;
            previousTimestamp = evt.TimestampMs;

            if (delay > 0)
                await Task.Delay((int)Math.Min(delay, MAX_DELAY_MS), token).ConfigureAwait(false);

            // ---- MOVE: posiciona o cursor nas coordenadas gravadas ----
            if (evt.Type != MouseEventType.Wheel) // wheel não precisa mover
                MoveCursorTo(evt.X, evt.Y);

            // ---- AÇÃO: injeta o evento equivalente via SendInput ----
            InjectMouseAction(evt);
        }
    }

    /// <summary>Moves o cursor usando SendInput ABSOLUTE (normalizado 0..65535) para precisão multi-monitor.</summary>
    private static void MoveCursorTo(int x, int y)
    {
        // Normalização: pixels -> 0..65535 sobre o desktop virtual inteiro.
        int virtualLeft   = SystemInformation.VirtualScreenLeft;
        int virtualTop    = SystemInformation.VirtualScreenTop;
        int virtualWidth  = SystemInformation.VirtualScreenWidth;
        int virtualHeight = SystemInformation.VirtualScreenHeight;

        int normX = (int)(((x - virtualLeft) * 65535.0) / Math.Max(1, virtualWidth - 1));
        int normY = (int)(((y - virtualTop) * 65535.0) / Math.Max(1, virtualHeight - 1));

        var input = CreateMouseInput(normX, normY,
            NativeTypes.MOUSEEVENTF_MOVE | NativeTypes.MOUSEEVENTF_ABSOLUTE | NativeTypes.MOUSEEVENTF_VIRTUALDESK);

        uint sent = NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeTypes.INPUT>());
        if (sent != 1)
        {
            // Fallback simples caso o envio absoluto falhe (raro): SetCursorPos.
            NativeMethods.SetCursorPos(x, y);
        }
    }

    /// <summary>Injeta down/up/click/wheel conforme o tipo do evento gravado.</summary>
    private static void InjectMouseAction(RecordedMouseEvent evt)
    {
        switch (evt.Type)
        {
            case MouseEventType.Move:
                break; // movimento já feito em MoveCursorTo

            case MouseEventType.LeftDown:
                SendButton(NativeTypes.MOUSEEVENTF_LEFTDOWN);
                break;

            case MouseEventType.LeftUp:
                SendButton(NativeTypes.MOUSEEVENTF_LEFTUP);
                break;

            case MouseEventType.RightDown:
                SendButton(NativeTypes.MOUSEEVENTF_RIGHTDOWN);
                break;

            case MouseEventType.RightUp:
                SendButton(NativeTypes.MOUSEEVENTF_RIGHTUP);
                break;

            case MouseEventType.MiddleDown:
                SendButton(NativeTypes.MOUSEEVENTF_MIDDLEDOWN);
                break;

            case MouseEventType.MiddleUp:
                SendButton(NativeTypes.MOUSEEVENTF_MIDDLEUP);
                break;

            case MouseEventType.LeftClick:
                SendButton(NativeTypes.MOUSEEVENTF_LEFTDOWN);
                SendButton(NativeTypes.MOUSEEVENTF_LEFTUP);
                break;

            case MouseEventType.RightClick:
                SendButton(NativeTypes.MOUSEEVENTF_RIGHTDOWN);
                SendButton(NativeTypes.MOUSEEVENTF_RIGHTUP);
                break;

            case MouseEventType.LeftDoubleClick:
                // Dois downs/ups rápidos simulam o duplo clique (DBLCLK não existe
                // como flag única no SendInput).
                SendButton(NativeTypes.MOUSEEVENTF_LEFTDOWN);
                SendButton(NativeTypes.MOUSEEVENTF_LEFTUP);
                Thread.Sleep(30);
                SendButton(NativeTypes.MOUSEEVENTF_LEFTDOWN);
                SendButton(NativeTypes.MOUSEEVENTF_LEFTUP);
                break;

            case MouseEventType.RightDoubleClick:
                SendButton(NativeTypes.MOUSEEVENTF_RIGHTDOWN);
                SendButton(NativeTypes.MOUSEEVENTF_RIGHTUP);
                Thread.Sleep(30);
                SendButton(NativeTypes.MOUSEEVENTF_RIGHTDOWN);
                SendButton(NativeTypes.MOUSEEVENTF_RIGHTUP);
                break;

            case MouseEventType.Wheel:
                SendWheel(evt.WheelDelta);
                break;
        }
    }

    // ==================== CONSTRUÇÃO DOS INPUTS ====================

    private static void SendButton(uint flag)
    {
        var input = CreateMouseInput(0, 0, flag);
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeTypes.INPUT>());
    }

    private static void SendWheel(int delta)
    {
        var input = CreateMouseInput(0, 0, NativeTypes.MOUSEEVENTF_WHEEL);
        input.u.mi.mouseData = delta;
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeTypes.INPUT>());
    }

    private static NativeTypes.INPUT CreateMouseInput(int dx, int dy, uint flags)
    {
        return new NativeTypes.INPUT
        {
            type = NativeTypes.INPUT_MOUSE,
            u = new NativeTypes.InputUnion
            {
                mi = new NativeTypes.MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    dwFlags = flags,
                    time = 0,
                    mouseData = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }
}

/// <summary>Geometria do desktop virtual (todos os monitores) via APIs nativas.</summary>
internal static class SystemInformation
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    // SM_* para o desktop virtual (multi-monitor)
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    public static int VirtualScreenLeft   => GetSystemMetrics(SM_XVIRTUALSCREEN);
    public static int VirtualScreenTop    => GetSystemMetrics(SM_YVIRTUALSCREEN);
    public static int VirtualScreenWidth  => GetSystemMetrics(SM_CXVIRTUALSCREEN);
    public static int VirtualScreenHeight => GetSystemMetrics(SM_CYVIRTUALSCREEN);
}
