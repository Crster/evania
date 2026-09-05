using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;

namespace Evania.Services;

/// <summary>
/// The tray icon that stands in for the window once "Close to system tray" hides it.
///
/// WinUI 3 has no tray API, so this is H.NotifyIcon's <see cref="TaskbarIcon"/> driven from code
/// rather than XAML: the icon has to appear and disappear with the setting, which is not the
/// lifetime of a window or a page. Its context menu is built as a <see cref="MenuFlyout"/> whose
/// items carry commands, because the default menu mode renders a real Win32 popup from that
/// flyout and only its commands get invoked - a Click handler would never run.
/// </summary>
public static class TrayIconService
{
    public static bool IsShowing { get; private set; }

    public static event Action? OpenRequested;
    public static event Action? SettingsRequested;
    public static event Action? ExitRequested;

    private static TaskbarIcon? icon;

    /// <summary>Puts the icon in the tray, creating it on first use. Safe to call repeatedly.</summary>
    public static async Task ShowAsync()
    {
        if (IsShowing)
        {
            return;
        }

        // IconSource has to be a real Icon, not a BitmapImage: the library resolves a BitmapImage
        // through its UriSource via StorageFile.GetFileFromApplicationUriAsync, and a stream-decoded
        // image has no URI to hand it - which crashes the call on the dispatcher and kills the app.
        System.Drawing.Icon? iconImage = await Task.Run(LoadIcon);

        if (iconImage is null)
        {
            // A missing icon is a deployment issue, not a reason to kill the app at startup. Let the
            // Settings page report the toggle failure through IsShowing while the window stays usable.
            return;
        }

        var taskbar = new TaskbarIcon
        {
            // Icon, not IconSource: IconSource only accepts an ImageSource, and the library turns a
            // BitmapImage back into a file through its UriSource - which a stream-loaded image does
            // not have, crashing startup inside GetFileFromApplicationUriAsync.
            Icon = iconImage,
            ToolTipText = "Evania",
            // TaskbarIcon carries no menu property of its own - the menu it renders is the flyout it
            // inherits from FrameworkElement, which the library turns into a Win32 popup.
            ContextFlyout = BuildMenu(),
            // Double-clicking the icon is the way back to a window that was hidden, so it does what
            // the first menu entry says rather than whatever the library's default would be.
            DoubleClickCommand = new RelayCommand(() => OpenRequested?.Invoke()),
        };

        try
        {
            // TaskbarIcon only builds the real Win32 tray icon from its Loaded handler, and this instance
            // is deliberately kept out of the visual tree because its lifetime follows the setting rather
            // than any window. Without this call the object exists, IsShowing reads true, and nothing ever
            // appears in the notification area - which strands the app the moment the window is hidden.
            // Efficiency mode stays off so the icon is present rather than summoned on demand.
            taskbar.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception)
        {
            taskbar.Dispose();
            return;
        }

        icon = taskbar;
        IsShowing = true;
    }

    public static void Hide()
    {
        if (icon is null)
        {
            return;
        }

        icon.Dispose();
        icon = null;
        IsShowing = false;
    }

    /// <summary>
    /// Follows the "Close to system tray" switch. Turning it off has to put the window back if it is
    /// currently hidden behind the icon being removed - otherwise the app is left running with no way
    /// in and no way out.
    /// </summary>
    public static async Task SetEnabledAsync(bool enabled)
    {
        if (!enabled)
        {
            Hide();

            if (App.MainWindow is { } window && !window.AppWindow.IsVisible)
            {
                window.Present();
            }

            return;
        }

        await ShowAsync();
    }

    /// <summary>
    /// Tells the user Evania is running when the app deliberately starts without a window. Without
    /// this the only evidence of a successful launch is an icon in a tray overflow menu.
    /// </summary>
    public static void NotifyStartedInTray()
    {
        try
        {
            icon?.ShowNotification(
                "Evania",
                "Evania is running in the tray. Double-click the icon to open it.",
                H.NotifyIcon.Core.NotificationIcon.None,
                null,
                true,
                false,
                false,
                false,
                null);
        }
        catch (Exception)
        {
        }
    }

    private static MenuFlyout BuildMenu()
    {
        MenuFlyout menu = new();

        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Open Evania",
            Command = new RelayCommand(() => OpenRequested?.Invoke()),
        });

        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Settings",
            Command = new RelayCommand(() => SettingsRequested?.Invoke()),
        });

        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Exit Evania",
            Command = new RelayCommand(() => ExitRequested?.Invoke()),
        });

        return menu;
    }

    /// <summary>
    /// Loads the tray icon from the ICO next to the executable - the same file the title bar uses -
    /// or null when it is missing or unreadable. This deployment puts its assets next to the manifest
    /// rather than in a package resource index, so <c>ms-appx</c> is not a dependable address here.
    /// </summary>
    private static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            return File.Exists(WindowIconService.IconPath)
                ? new System.Drawing.Icon(WindowIconService.IconPath, 32, 32)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
