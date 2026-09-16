using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Evania.Models;

namespace Evania.Services;

/// <summary>The global capture shortcut the user picked, or none at all.</summary>
public enum ScreenshotHotkey
{
    None,
    PrintScreen,
    AltPrintScreen,
    CtrlShift3,
    AltP,
}

public static class ScreenshotHotkeyExtensions
{
    public static string ToDisplayString(this ScreenshotHotkey hotkey) => hotkey switch
    {
        ScreenshotHotkey.None => "None",
        ScreenshotHotkey.PrintScreen => "Print Screen",
        ScreenshotHotkey.AltPrintScreen => "Alt + Print Screen",
        ScreenshotHotkey.CtrlShift3 => "Ctrl + Shift + 3",
        ScreenshotHotkey.AltP => "Alt + P",
        _ => "None",
    };
}

/// <summary>The address the Chat page shows when the user has not typed one.</summary>
public static class ChatProviderDefaults
{
    public const string Url = "https://copilot.microsoft.com";
}

/// <summary>
/// One webpage bookmark the user pinned from Settings.
/// </summary>
public sealed class WebBookmark
{
    public string Title { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Everything the Settings page edits, live.
///
/// The view model binds straight to this instance rather than mirroring the values, so a change
/// made anywhere - the page, a tray menu, startup - is the same object the rest of the app reads,
/// and <see cref="SettingsService"/> can save on the property-changed event instead of needing an
/// explicit commit from every caller.
/// </summary>
public partial class AppSettings : ObservableObject
{
    [ObservableProperty]
    public partial string DatabasePath { get; set; }

    /// <summary>
    /// Whether the data file is AES-encrypted by LiteDB with a master password.
    /// </summary>
    [ObservableProperty]
    public partial bool DatabaseEncryption { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial ScreenshotHotkey ScreenshotShortcut { get; set; } = ScreenshotHotkey.PrintScreen;

    [ObservableProperty]
    public partial bool AutoSaveScreenshot { get; set; }

    /// <summary>
    /// The address the Chat page hosts, typed freely in Settings. An unreadable value falls back
    /// to <see cref="ChatProviderDefaults.Url"/> when the page resolves it. The page subscribes to
    /// this property so editing it in Settings navigates a loaded Chat page immediately.
    /// </summary>
    [ObservableProperty]
    public partial string ChatProviderUrl { get; set; } = ChatProviderDefaults.Url;

    /// <summary>
    /// The notes list sort the user last chose; the Notes page restores it on load.
    /// </summary>
    [ObservableProperty]
    public partial NoteSortOrder NoteSort { get; set; } = NoteSortOrder.UpdatedDescending;

    /// <summary>
    /// Webpages pinned from Settings and surfaced in the sidebar.
    /// </summary>
    [ObservableProperty]
    public partial List<WebBookmark> Bookmarks { get; set; } = [];

    /// <summary>
    /// The master password loaded from Windows Credential Manager for this session, or null while
    /// encryption is off or locked.
    /// Plainly observable and deliberately not an <see cref="ObservableProperty"/>: it is a secret,
    /// so it must never end up on the binding trail of a XAML element or in settings.json.
    /// </summary>
    public string? DatabasePassword { get; set; }

    /// <summary>
    /// The password a rotation / change is moving to, kept alongside <see cref="DatabasePassword"/>
    /// until the rebuild that writes it into the data file has succeeded.
    /// </summary>
    public string? PendingDatabasePassword { get; set; }

    public AppSettings()
    {
        DatabasePath = SettingsService.DefaultDatabasePath;
    }
}
