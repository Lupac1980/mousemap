using System.IO;

namespace MacroRecorder.Services;

/// <summary>
/// Logger simples em arquivo (macrorecorder.log ao lado do executável).
/// Requisito: "Logging de erros" - sem dependências externas.
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static string LogPath => Path.Combine(AppContext.BaseDirectory, "macrorecorder.log");

    public static void Info(string message)  => Write("INFO ", message);
    public static void Warn(string message)  => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
    {
        string full = ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        Write("ERROR", full);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging nunca pode derrubar o aplicativo.
        }
    }
}
