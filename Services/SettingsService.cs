using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Evania.Models;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace Evania.Services;

/// <summary>
/// Owns <c>%APPDATA%\Evania\settings.json</c>, outside the MSIX container so data survives
/// uninstall and reinstall.
///
/// Settings cannot live in the database they configure: the file has to be readable before we know
/// where it is or whether it is protected by a master password. So the store is JSON, written through
/// a source-generated context (Release trims, and reflection-based serialization would warn), replaced
/// atomically so a crash mid-write cannot truncate the only copy.
/// </summary>
public static partial class SettingsService
{
    // MSIX virtualizes every user-level path (APPDATA, USERPROFILE, KnownFolders, env vars).
    // The machine-level registry is not virtualized for full-trust packages, so we read the
    // real profiles directory from HKLM and combine it with the username to build the real path.
    public static string RootDirectory { get; } = Path.Combine(GetRealAppDataPath(), "Evania");

    private static string GetRealAppDataPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            string profilesDir = key?.GetValue("ProfilesDirectory") as string ?? "";
            string userName = Environment.GetEnvironmentVariable("USERNAME")
                ?? Environment.UserName;
            if (!string.IsNullOrEmpty(profilesDir))
            {
                return Path.Combine(profilesDir, userName, "AppData", "Roaming");
            }
        }
        catch { }
        return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    public static string DefaultDatabasePath => Path.Combine(RootDirectory, "Evania.db");

    public static string FilePath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>
    /// The WebView2 user data folder the Chat page browses with. Explicit rather than the runtime
    /// default so cookies - and with them the sign-in to each provider - live outside the package
    /// and survive both loose-file and staged runs.
    /// </summary>
    public static string ChatDataFolder => Path.Combine(RootDirectory, "chat-webview");

    private static readonly Lazy<Task<CoreWebView2Environment>> lazyChatEnvironment =
        new(CreateChatWebViewEnvironmentAsync);

    private static readonly Lazy<AppSettings> lazyCurrent = new(CreateAndLoad);

    public static Task<CoreWebView2Environment> GetChatWebViewEnvironmentAsync() => lazyChatEnvironment.Value;

    private static Task<CoreWebView2Environment> CreateChatWebViewEnvironmentAsync() =>
        CoreWebView2Environment.CreateWithOptionsAsync(null, ChatDataFolder, null).AsTask();

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
        catch (Exception)
        {
            // A settings file we cannot parse is a reason to start from defaults, not a reason not
            // to start at all.
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

        // On a fresh install the file does not exist and no property has changed yet, so the
        // change handler above has never fired. Persist the defaults so the file - and the
        // directory it lives in - always exist after the app has run once.
        if (!File.Exists(FilePath))
        {
            Save(settings);
        }

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
            ChatProvider = settings.ChatProvider,
            NoteSort = settings.NoteSort,

            // Bookmarks are content, not configuration, so BookmarkService keeps them in the
            // database, where they outlive an uninstall. The JSON copy is written only until that
            // has actually happened - a session whose database never opens must not lose them -
            // and leaving it null afterwards drops the key from the file.
            Bookmarks = BookmarkService.PersistedToDatabase
                ? null
                : settings.Bookmarks
                    .Where(b => !string.IsNullOrWhiteSpace(b.Url))
                    .Select(b => new BookmarkDto
                    {
                        Title = b.Title,
                        Url = b.Url,
                    })
                    .ToList(),
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
        catch (Exception)
        {
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
        settings.ChatProvider = dto.ChatProvider;
        settings.NoteSort = dto.NoteSort;
        // Legacy only. A file written before bookmarks moved into the database still carries them,
        // and BookmarkService hands that list to the data file the first time one opens.
        settings.Bookmarks = dto.Bookmarks?
            .Where(b => !string.IsNullOrWhiteSpace(b.Url))
            .Select(b => new WebBookmark
            {
                Title = b.Title?.Trim() ?? string.Empty,
                Url = b.Url.Trim(),
            })
            .ToList() ?? [];
    }

    private sealed class SettingsDto
    {
        public string? DatabasePath { get; set; }

        public bool DatabaseEncryption { get; set; }

        public bool StartWithWindows { get; set; }

        public bool CloseToTray { get; set; }

        public ScreenshotHotkey ScreenshotShortcut { get; set; }

        public bool AutoSaveScreenshot { get; set; }

        public ChatProvider ChatProvider { get; set; }

        public NoteSortOrder NoteSort { get; set; }

        /// <summary>Read for the one-time migration into the database; never written again.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<BookmarkDto>? Bookmarks { get; set; }
    }

    private sealed class BookmarkDto
    {
        public string? Title { get; set; }

        public string Url { get; set; } = string.Empty;
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(SettingsDto))]
    private partial class SettingsJsonContext : JsonSerializerContext
    {
    }
}
