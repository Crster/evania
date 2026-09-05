using System;
using System.Threading.Tasks;
using Evania.Services;
using Evania.Windows;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Foundation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Evania;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>
    /// One copy of Evania owns the tray icon and the global shortcut, so a second copy has to hand
    /// its launch over rather than start a rival. That matters most for the two paths that launch the
    /// app without a user looking for a window: the startup task at sign-in, and re-clicking the icon
    /// while the app is already sitting in the tray.
    /// </summary>
    private const string InstanceKey = "Evania.Main";

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        if (RedirectToOneRunningCopy())
        {
            Environment.Exit(0);

            return;
        }

        // Protocol activations can also arrive later, redirected here from a second copy the Copilot
        // key launched; this subscription is what wakes the main instance for those.
        AppInstance.GetCurrent().Activated += OnActivated;

        string? copilotKeyState = CopilotKeyStateFrom(AppInstance.GetCurrent().GetActivatedEventArgs());

        MainWindow = new MainWindow();
        CopilotKeyService.Attach(WinRT.Interop.WindowNative.GetWindowHandle(MainWindow));
        CopilotKeyService.Tapped += HandleCopilotKeyTapped;

        // Windows can disable startup behind the app's back (Task Manager, Settings), so the stored
        // setting is corrected against the operating system before it decides anything else -
        // including whether this launch shows a window at all.
        await AutoStartService.SyncSettingAsync();

        AppSettings settings = SettingsService.Current;

        // A Copilot key press is an explicit request for the app, so it wins over starting hidden.
        bool startsHiddenInTray = settings.CloseToTray && settings.StartWithWindows && copilotKeyState != "Tap";

        GlobalHotkeyService.Attach();

        // Subscribed for the life of the process, whether or not the tray is showing right now: the
        // setting can be switched on later from the Settings page, and a handler that is added once
        // here cannot end up doubled.
        GlobalHotkeyService.Invoked += OnHotkeyInvoked;
        TrayIconService.OpenRequested += HandleTrayOpen;
        TrayIconService.SettingsRequested += HandleTraySettings;
        TrayIconService.ExitRequested += Quit;

        GlobalHotkeyService.Apply(settings.ScreenshotShortcut);

        if (settings.CloseToTray)
        {
            await TrayIconService.ShowAsync();
        }

        // The database is plumbing the settings describe, and opening it takes a disk hit; nothing
        // reads from it yet, so it is not a reason to delay showing the first frame.
        _ = Task.Run(DatabaseService.OpenFromSettings);

        if (startsHiddenInTray)
        {
            // Deliberately never activated: a window that was never shown cannot flicker, which is
            // what makes a sign-in launch quiet.
            TrayIconService.NotifyStartedInTray();

            return;
        }

        MainWindow.Activate();
    }

    private static bool RedirectToOneRunningCopy()
    {
        AppInstance instance = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (instance.IsCurrent)
        {
            return false;
        }

        try
        {
            // Blocking is on purpose: this process is about to end, so there is no message loop
            // worth handing the wait to. The main instance receives the activation and wakes itself.
            instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs())
                .AsTask()
                .GetAwaiter()
                .GetResult();

            return true;
        }
        catch (Exception ex)
        {
            // The copy we were handing over to may have quit mid-handoff. Stay and be the main one
            // rather than leaving the user with no app at all.
            System.Diagnostics.Debug.WriteLine($"Activation redirect failed: {ex.Message}");

            return false;
        }
    }

    /// <summary>
    /// Copilot key states arrive as protocol activations on the <c>evania</c> scheme. Returns the
    /// state declared in Package.appxmanifest (<c>Tap</c>, <c>Down</c>, <c>Up</c>) when this
    /// activation came from the Copilot key, and null for anything else.
    /// </summary>
    private static string? CopilotKeyStateFrom(AppActivationArguments? activationArgs)
    {
        if (activationArgs?.Kind != ExtendedActivationKind.Protocol ||
            activationArgs.Data is not global::Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs protocolArgs ||
            !string.Equals(protocolArgs.Uri.Scheme, "evania", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (IWwwFormUrlDecoderEntry entry in new WwwFormUrlDecoder(protocolArgs.Uri.Query))
        {
            if (entry.Name == "state")
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static void OnActivated(object? sender, AppActivationArguments e)
    {
        if (CopilotKeyStateFrom(e) == "Tap")
        {
            MainWindow?.DispatcherQueue.TryEnqueue(() => MainWindow?.Present());
        }
    }

    private static void HandleCopilotKeyTapped() => MainWindow?.Present();

    private static void OnHotkeyInvoked() => _ = CaptureFlowService.RunAsync();

    private static void HandleTrayOpen() => MainWindow?.Present();

    private static void HandleTraySettings()
    {
        MainWindow?.Present();
        MainWindow?.NavigateToSettings();
    }

    /// <summary>
    /// The only way out once the window's close button just hides it. Tears the listeners down
    /// explicitly rather than letting the process die around them, so the shortcut is released and
    /// the icon leaves the tray with the app instead of lingering until the shell notices.
    /// </summary>
    public static void Quit()
    {
        if (MainWindow is { } window)
        {
            window.IsQuitting = true;
        }

        GlobalHotkeyService.Detach();
        TrayIconService.Hide();
        DatabaseService.Close();

        Environment.Exit(0);
    }
}
