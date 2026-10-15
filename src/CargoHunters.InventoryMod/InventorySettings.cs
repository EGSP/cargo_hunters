using System.Text.Json;
using BepInEx;

namespace CargoHunters.InventoryMod;

/// <summary>Хранит настройки экспорта между запусками игры.</summary>
internal static class InventorySettings
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(Paths.ConfigPath, "cargo-inventory-settings.json");
    private static string? outputPath;

    public static void Load()
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return;
            try
            {
                var saved = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(FilePath));
                outputPath = string.IsNullOrWhiteSpace(saved?.OutputPath) ? null : saved.OutputPath;
            }
            catch (Exception error)
            {
                BepInEx.Logging.Logger.CreateLogSource("CargoInventorySettings")
                    .LogWarning("Cannot load inventory settings: " + error.GetBaseException().Message);
                outputPath = null;
            }
        }
    }

    public static string? OutputPath { get { lock (Gate) return outputPath; } }

    public static object Set(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());
        lock (Gate)
        {
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new SettingsFile { OutputPath = normalized }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
            outputPath = normalized;
        }
        return new { saved = true, outputPath = normalized, settingsFile = FilePath };
    }

    private sealed class SettingsFile
    {
        public string? OutputPath { get; set; }
    }
}
