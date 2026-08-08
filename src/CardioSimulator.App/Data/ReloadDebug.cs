using System;
using System.IO;

namespace CardioSimulator.App.Data;

/// <summary>
/// TEMPORARY diagnostic logger for the course-reload investigation. Writes to
/// <c>%LOCALAPPDATA%\CardioSimulator\reload_debug.log</c> and the debugger Output window.
/// Remove once the same-path reload issue is resolved.
/// </summary>
internal static class ReloadDebug
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(AppPaths.Root, "reload_debug.log");

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        System.Diagnostics.Debug.WriteLine("[RELOAD] " + line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Root);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { /* best-effort diagnostics */ }
    }

    /// <summary>A single-line, length-capped preview of text for the log.</summary>
    public static string Snip(string? s, int n = 100)
    {
        if (string.IsNullOrEmpty(s)) return "<empty>";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= n ? s : s[..n] + "…";
    }
}
