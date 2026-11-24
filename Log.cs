using System;
using System.IO;

namespace FolderPeek;

/// <summary>Log mínimo em %LOCALAPPDATA%\FolderPeek\folderpeek.log (rotaciona em ~1 MB).</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderPeek");

    private static string FilePath => Path.Combine(Dir, "folderpeek.log");

    /// <summary>Ligado com --verbose: registra cada passo do hover.</summary>
    public static bool Verbose { get; set; }

    public static void Debug(string message)
    {
        if (Verbose) Write("[dbg] " + message);
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { /* log nunca derruba o app */ }
    }
}
