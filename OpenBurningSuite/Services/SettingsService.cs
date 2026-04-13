// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Services;

/// <summary>
/// Manages loading and saving of application settings to a JSON file
/// in the user's application data directory.
/// </summary>
public static class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenBurningSuite");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static AppSettings? _current;

    /// <summary>Gets the current application settings, loading from disk on first access.</summary>
    public static AppSettings Current => _current ??= Load();

    /// <summary>Loads settings from the JSON file, returning defaults if the file does not exist or is invalid.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null)
                {
                    _current = settings;
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            // If loading fails, fall back to defaults and record the error
            LastError = ex.Message;
        }

        _current = new AppSettings();
        return _current;
    }

    /// <summary>Saves the provided settings to disk. Returns true on success.</summary>
    public static bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
            _current = settings;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>Contains the error message from the last failed operation, if any.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Resets settings to defaults and saves.</summary>
    public static AppSettings ResetToDefaults()
    {
        var defaults = new AppSettings();
        Save(defaults);
        return defaults;
    }

    /// <summary>Returns the path to the settings file.</summary>
    public static string GetSettingsFilePath() => SettingsPath;
}
