using System.IO;
using System.Text.Json;
using SpiritNest.Models;

namespace SpiritNest.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(AppPaths.SettingsFile))
        {
            Current = new AppSettings();
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(AppPaths.SettingsFile);
            Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            EnsureSearchEngines(Current);
            if (Migrate(Current))
                Save();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    private static bool Migrate(AppSettings s)
    {
        var changed = false;
        if (s.SettingsSchema < 2)
        {
            s.SettingsSchema = 2;
            changed = true;
        }

        if (s.SettingsSchema < 3)
        {
            s.CollapseDelayMs = 200;
            s.SettingsSchema = 3;
            changed = true;
        }

        if (s.SettingsSchema < 4)
        {
            // Hover open 240ms; drag within that window does not open
            s.ExpandDelayMs = 240;
            s.CollapseDelayMs = 200;
            s.SettingsSchema = 4;
            changed = true;
        }

        if (s.SettingsSchema < 5)
        {
            if (s.SearchWeatherIdleSeconds <= 0)
                s.SearchWeatherIdleSeconds = 10;
            s.SettingsSchema = 5;
            changed = true;
        }

        if (s.SettingsSchema < 6)
        {
            s.SettingsSchema = 6;
            changed = true;
        }

        if (s.SettingsSchema < 7)
        {
            if (s.AnimationDurationMs < 100)
                s.AnimationDurationMs = 360;
            s.SettingsSchema = 7;
            changed = true;
        }

        if (s.SettingsSchema < 8)
        {
            s.EnablePanelAnimation = true;
            s.SettingsSchema = 8;
            changed = true;
        }

        return changed;
    }

    private static void EnsureSearchEngines(AppSettings s)
    {
        if (s.SearchEngines is null || s.SearchEngines.Count == 0)
            s.SearchEngines = SearchEngineInfo.CreateBuiltIns();
        if (string.IsNullOrWhiteSpace(s.DefaultSearchEngineId))
            s.DefaultSearchEngineId = "bing";
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Current, JsonOptions));
    }
}
