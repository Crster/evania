using System;
using System.Threading.Tasks;
using Evania.Services;
using Evania.Windows;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

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
        AppActivationArguments activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (RedirectToOneRunningCopy())
        {
            Environment.Exit(0);

            return;
        }

        // A protocol launch is handled without touching the window; note attachment links
        // just need the file opened in its own app, even if Evania is sitting in the tray.
        if (await TryOpenAttachmentProtocolAsync(activatedArgs))
        {
            return;
        }

        MainWindow = new MainWindow();

        // Windows can disable startup behind the app's back (Task Manager, Settings), so the stored
        // setting is corrected against the operating system before it decides anything else -
        // including whether this launch shows a window at all.
        await AutoStartService.SyncSettingAsync();

        AppSettings settings = SettingsService.Current;
        bool startsHiddenInTray = settings.CloseToTray && settings.StartWithWindows;

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

        // Attachment links clicked while this copy is alive are redirected here by the
        // single-instance handoff above instead of starting a new process.
        AppInstance.GetCurrent().Activated += (_, eventArgs) =>
            _ = TryOpenAttachmentProtocolAsync(eventArgs);

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

    private static void OnHotkeyInvoked() => _ = CaptureFlowService.RunAsync();

    /// <summary>
    /// Handles evania://note/attachment/{id} links: extracts the attachment and opens it
    /// with the default app, same as the note page's Open Attachment button. Returns true
    /// when the activation was a handled protocol link.
    /// </summary>
    private static async Task<bool> TryOpenAttachmentProtocolAsync(AppActivationArguments args)
    {
        if (args.Kind != ExtendedActivationKind.Protocol
            || args.Data is not ProtocolActivatedEventArgs protocol
            || !string.Equals(protocol.Uri.Scheme, "evania", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Uri uri = protocol.Uri;
        bool isAttachmentLink =
            string.Equals(uri.Host, "note", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/attachment/", StringComparison.OrdinalIgnoreCase);
        if (!isAttachmentLink)
        {
            return false;
        }

        string id = Uri.UnescapeDataString(uri.AbsolutePath.Substring("/attachment/".Length));
        try
        {
            DatabaseService.OpenFromSettings();
            byte[] bytes = await NoteService.ReadAttachmentAsync(id);
            string fileName = await NoteService.GetAttachmentFileNameAsync(id);

            StorageFile file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                fileName,
                CreationCollisionOption.GenerateUniqueName);
            await FileIO.WriteBytesAsync(file, bytes);
            await global::Windows.System.Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Attachment protocol open failed: {ex.Message}");
        }

        return true;
    }

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
