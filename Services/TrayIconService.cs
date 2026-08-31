using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

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

        ImageSource image = await LoadIconAsync();

        var taskbar = new TaskbarIcon
        {
            IconSource = image,
            ToolTipText = "Evania",
            // TaskbarIcon carries no menu property of its own - the menu it renders is the flyout it
            // inherits from FrameworkElement, which the library turns into a Win32 popup.
            ContextFlyout = BuildMenu(),
            // Double-clicking the icon is the way back to a window that was hidden, so it does what
            // the first menu entry says rather than whatever the library's default would be.
            DoubleClickCommand = new RelayCommand(() => OpenRequested?.Invoke()),
        };

        icon = taskbar;

        // TaskbarIcon only builds the real Win32 tray icon from its Loaded handler, and this instance
        // is deliberately kept out of the visual tree because its lifetime follows the setting rather
        // than any window. Without this call the object exists, IsShowing reads true, and nothing ever
        // appears in the notification area - which strands the app the moment the window is hidden.
        // Efficiency mode stays off so the icon is present rather than summoned on demand.
        taskbar.ForceCreate(enablesEfficiencyMode: false);

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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Balloon tip failed: {ex.Message}");
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
    /// Decodes the tray icon from the build output, the way <see cref="WindowIconService"/> loads it
    /// for the title bar: this deployment puts its assets next to the manifest rather than in a
    /// package resource index, so <c>ms-appx</c> is not a dependable address here. The ICO is what
    /// the title bar uses; a PNG is the fallback because it is the mark at a fixed 32px.
    /// </summary>
    private static async Task<ImageSource> LoadIconAsync()
    {
        foreach (string candidate in new[] { WindowIconService.IconPath, FallbackIconPath })
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                using FileStream stream = File.OpenRead(candidate);

                BitmapImage image = new();
                await image.SetSourceAsync(stream.AsRandomAccessStream());

                if (image.PixelWidth > 0)
                {
                    return image;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tray icon {candidate} did not decode: {ex.Message}");
            }
        }

        return new BitmapImage();
    }

    private static string FallbackIconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.targetsize-32_altform-unplated.png");
}
