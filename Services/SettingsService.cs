using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Evania.Services;

/// <summary>
/// Owns <c>%APPDATA%\Evania\settings.json</c>.
///
/// Settings cannot live in the database they configure: the file has to be readable before we know
/// where it is or whether it is protected by a master password. So the store is JSON, written through
/// a source-generated context (Release trims, and reflection-based serialization would warn), replaced
/// atomically so a crash mid-write cannot truncate the only copy.
/// </summary>
public static partial class SettingsService
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Evania");

    public static string DefaultDatabasePath => Path.Combine(RootDirectory, "Evania.db");

    public static string FilePath => Path.Combine(RootDirectory, "settings.json");

    private static readonly Lazy<AppSettings> lazyCurrent = new(CreateAndLoad);

    public static AppSettings Current => lazyCurrent.Value;

    private static bool loading;

    private static AppSettings CreateAndLoad()
    {
        AppSettings settings = new();

        try
        {
            loading = true;
            Read(settings);
        }
        catch (Exception ex)
        {
            // A settings file we cannot parse is a reason to start from defaults, not a reason not
            // to start at all.
            System.Diagnostics.Debug.WriteLine($"Failed to read {FilePath}: {ex.Message}");
        }
        finally
        {
            loading = false;
        }

        // Every switch on the Settings page applies as it is flipped, which only stays honest if
        // saving hangs off the change itself rather than off each caller remembering to ask.
        settings.PropertyChanged += (_, _) =>
        {
            if (!loading)
            {
                Save(settings);
            }
        };

        return settings;
    }

    /// <summary>Persists the current values. Called for you whenever a setting changes.</summary>
    public static void Save() => Save(Current);

    private static void Save(AppSettings settings)
    {
        SettingsDto dto = new()
        {
            DatabasePath = settings.DatabasePath,
            DatabaseEncryption = settings.DatabaseEncryption,
            StartWithWindows = settings.StartWithWindows,
            CloseToTray = settings.CloseToTray,
            ScreenshotShortcut = settings.ScreenshotShortcut,
            AutoSaveScreenshot = settings.AutoSaveScreenshot,
        };

        try
        {
            Directory.CreateDirectory(RootDirectory);

            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(dto, SettingsJsonContext.Default.SettingsDto));

            if (File.Exists(FilePath))
            {
                File.Replace(tempPath, FilePath, null);
            }
            else
            {
                File.Move(tempPath, FilePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }

    private static void Read(AppSettings settings)
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        // Read bytes, not text: System.Text.Json detects and skips a UTF-8 BOM in a byte buffer, but
        // a BOM that survives into a decoded string is an invalid first token. Notepad and PowerShell
        // both add one when someone edits this file by hand.
        SettingsDto? dto = JsonSerializer.Deserialize(
            File.ReadAllBytes(FilePath), SettingsJsonContext.Default.SettingsDto);

        if (dto is null)
        {
            return;
        }

        settings.DatabasePath = string.IsNullOrWhiteSpace(dto.DatabasePath)
            ? DefaultDatabasePath
            : dto.DatabasePath;
        settings.DatabaseEncryption = dto.DatabaseEncryption;
        settings.StartWithWindows = dto.StartWithWindows;
        settings.CloseToTray = dto.CloseToTray;
        settings.ScreenshotShortcut = dto.ScreenshotShortcut;
        settings.AutoSaveScreenshot = dto.AutoSaveScreenshot;
    }

    private sealed class SettingsDto
    {
        public string? DatabasePath { get; set; }

        public bool DatabaseEncryption { get; set; }

        public bool StartWithWindows { get; set; }

        public bool CloseToTray { get; set; }

        public ScreenshotHotkey ScreenshotShortcut { get; set; }

        public bool AutoSaveScreenshot { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(SettingsDto))]
    private partial class SettingsJsonContext : JsonSerializerContext
    {
    }
}
