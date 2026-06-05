using System;
using System.IO;

namespace OpenBurningSuite.Helpers;

public static class Logger
{
    private static readonly string LogDir;
    private static readonly string LogPath;
    private static readonly object _lock = new();

    static Logger()
    {
        LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(LogDir);
        LogPath = Path.Combine(LogDir, $"obs_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(Exception ex, string context = "") => Write("FATAL", $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        lock (_lock)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
                File.AppendAllText(LogPath, line + Environment.NewLine);
                Console.Error.WriteLine(line);
            }
            catch
            {
            }
        }
    }
}
