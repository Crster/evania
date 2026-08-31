using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Evania.Services;

/// <summary>
/// Owns <c>%APPDATA%\Evania\settings.json</c> and the secret that keys the database.
///
/// Settings cannot live in the database they configure: the file has to be readable before we know
/// where it is or what protects it. So the store is JSON, written through a source-generated
/// context (Release trims, and reflection-based serialization would warn), replaced atomically so a
/// crash mid-write cannot truncate the only copy.
///
/// The database key is the one value never stored as itself. DPAPI (<see cref="ProtectedData"/>
/// with <see cref="DataProtectionScope.CurrentUser"/>) wraps it into a blob that only unwraps for
/// this Windows account on this machine, which is what lets the Settings page offer encryption as a
/// checkbox instead of a password the user has to remember and retype.
/// </summary>
public static partial class SettingsService
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Evania");

    public static string DefaultDatabasePath => Path.Combine(RootDirectory, "Evania.db");

    public static string FilePath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>
    /// True when <see cref="FilePath"/> holds a key blob this Windows account cannot unwrap -
    /// because a roaming profile carried it in from another machine, say. Reported to the user
    /// instead of surfacing later as a bare LiteDB failure.
    /// </summary>
    public static bool DatabaseKeyUnreadable { get; private set; }

    /// <summary>
    /// Extra entropy is not a secret here - it only stops another application under the same user
    /// from unwrapping the blob without naming where it came from.
    /// </summary>
    private static readonly byte[] OptionalEntropy = "Evania.DatabaseKey"u8.ToArray();

    /// <summary>
    /// Reading the settings file unwraps the stored key, which needs <see cref="OptionalEntropy"/>
    /// to already exist. Static field initializers run in declaration order, so loading eagerly in a
    /// field initializer would decrypt with a null entropy that the protecting side never used -
    /// deferred construction is what keeps the two halves of DPAPI agreeing.
    /// </summary>
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
            DatabaseKey = Protect(settings.DatabaseKey),
            PendingDatabaseKey = Protect(settings.PendingDatabaseKey),
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

        DatabaseKeyUnreadable = false;
        settings.DatabaseKey = Unprotect(dto.DatabaseKey);
        settings.PendingDatabaseKey = Unprotect(dto.PendingDatabaseKey);

        // A rotation writes the pending key down before the rebuild that puts it in the file, so a
        // pending key with no live key is what an interrupted first enable leaves behind: that
        // pending key is what opens the file as it is now.
        if (settings.DatabaseKey is null && settings.PendingDatabaseKey is { } promoted)
        {
            settings.DatabaseKey = promoted;
            settings.PendingDatabaseKey = null;
        }
    }

    public static string GenerateDatabaseKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Records the key a rebuild is about to write into the data file, kept separate from the live
    /// key until <see cref="CommitDatabaseKeyRotation"/> so an interrupted rebuild still leaves a
    /// recorded key for whichever state the file is in.
    /// </summary>
    public static void StageDatabaseKey(string newKey)
    {
        Current.PendingDatabaseKey = newKey;
        Save();
    }

    public static void CommitDatabaseKeyRotation()
    {
        if (Current.PendingDatabaseKey is { } newKey)
        {
            Current.DatabaseKey = newKey;
        }

        Current.PendingDatabaseKey = null;
        Save();
    }

    public static void AbandonDatabaseKeyRotation()
    {
        Current.PendingDatabaseKey = null;
        Save();
    }

    /// <summary>Drops both the live and the staged key, leaving the database unencrypted.</summary>
    public static void ClearDatabaseKey()
    {
        Current.DatabaseKey = null;
        Current.PendingDatabaseKey = null;
        Save();
    }

    /// <summary>
    /// Puts both key fields back after a failed apply or rotation. Neither is observable, so the
    /// assignments cannot trigger a save on their own - the single <see cref="Save"/> below is what
    /// makes the restored pair the stored pair.
    /// </summary>
    public static void RestoreDatabaseKeys(string? key, string? pendingKey)
    {
        Current.DatabaseKey = key;
        Current.PendingDatabaseKey = pendingKey;
        Save();
    }

    private static string? Protect(string? plaintext) => plaintext is null
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), OptionalEntropy, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? blob)
    {
        if (string.IsNullOrEmpty(blob))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(blob), OptionalEntropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            // DPAPI throws CryptographicException for "not your account or machine"; a malformed
            // blob arrives as FormatException. Both mean the same thing to the user.
            DatabaseKeyUnreadable = true;
            System.Diagnostics.Debug.WriteLine($"Cannot unwrap the stored database key: {ex.Message}");
            return null;
        }
    }

    private sealed class SettingsDto
    {
        public string? DatabasePath { get; set; }

        public bool DatabaseEncryption { get; set; }

        /// <summary>DPAPI-protected base64 of the LiteDB key, never the key itself.</summary>
        public string? DatabaseKey { get; set; }

        public string? PendingDatabaseKey { get; set; }

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
