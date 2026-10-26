namespace TuneUp.Services;

/// <summary>Tiny thread-safe file logger. Logs stay on this PC.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static bool _lockedOut;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            // Several Vanta processes (main window, watchdog, maintenance task) can share one log file,
            // so open it shareable and retry briefly instead of silently dropping the line.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var file = Path.Combine(AppPaths.Logs, $"vanta-{DateTime.Now:yyyyMMdd}{(_lockedOut ? "-2" : "")}.log");
                    using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                    fs.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(15);
                }
                catch (UnauthorizedAccessException) when (!_lockedOut)
                {
                    // Today's file exists but this process can't open it (made by another account or a
                    // sandbox): switch to a second file instead of losing every line for the rest of the day.
                    _lockedOut = true;
                }
                catch
                {
                    return; // Logging must never crash the app.
                }
            }
        }
    }
}
