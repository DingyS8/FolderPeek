using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace FolderPeek;

/// <summary>O que abre o preview.</summary>
internal enum TriggerMode { Hover, Click, Both }

internal sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TriggerMode Trigger { get; set; } = TriggerMode.Both;

    [JsonIgnore] public bool HoverEnabled => Trigger is TriggerMode.Hover or TriggerMode.Both;
    [JsonIgnore] public bool ClickEnabled => Trigger is TriggerMode.Click or TriggerMode.Both;

    public int HoverDelayMs { get; set; } = 1500;
    public bool Paused { get; set; }
    public int MaxItems { get; set; } = 500;
    public bool WelcomeShown { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FolderPeek", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        AppSettings s = new();
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex) { Log.Write($"settings inválido, usando padrão: {ex.Message}"); }

        s.HoverDelayMs = Math.Clamp(s.HoverDelayMs, 200, 10_000);
        s.MaxItems = Math.Clamp(s.MaxItems, 50, 5_000);
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Write($"falha ao salvar settings: {ex.Message}"); }
    }
}

/// <summary>"Iniciar com o Windows" via HKCU\...\Run (não precisa de admin).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FolderPeek";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    private static string? Current
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
    }

    public static bool IsEnabled => Current != null;

    public static void Set(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Se o exe mudou de lugar, atualiza o caminho registrado.</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            var cur = Current;
            if (cur != null && !string.Equals(cur, Command, StringComparison.OrdinalIgnoreCase)) Set(true);
        }
        catch (Exception ex) { Log.Write($"startup refresh: {ex.Message}"); }
    }
}
