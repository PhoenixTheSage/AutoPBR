using System.Text.Json;
using AutoPBR.Core;

namespace AutoPBR.App.Models;

public sealed class UserSettings
{
    public string? OutputDirectory { get; set; }
    public double NormalIntensity { get; set; } = AutoPbrDefaults.DefaultNormalIntensity;
    public double HeightIntensity { get; set; } = AutoPbrDefaults.DefaultHeightIntensity;
    public bool FastSpecular { get; set; }
    public bool IgnorePlants { get; set; }
    public bool ExperimentalSpecular { get; set; }
    public string ColorScheme { get; set; } = "Dark";

    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoPBR");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new UserSettings();

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<UserSettings>(json);
            return settings ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort; ignore persistence errors.
        }
    }
}

