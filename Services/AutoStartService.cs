using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace Evania.Services;

/// <summary>
/// Reads and writes the "Start with Windows" state through the app's own startup task, declared in
/// <c>Package.appxmanifest</c>.
///
/// Going through the startup task rather than an HKCU Run key keeps the switch honest: Task Manager
/// and Settings show the same entry the app writes, so the user can always see and override it.
/// That also means the operating system is the source of truth - it can disable startup behind our
/// back - so the state is read at launch and copied into the settings the UI binds to.
/// </summary>
public static class AutoStartService
{
    public const string TaskId = "EvaniaStartupTask";

    /// <summary>
    /// The state Windows currently reports, or null when the task cannot be resolved (an
    /// unpackaged build, or a manifest without the extension).
    /// </summary>
    public static async Task<StartupTaskState?> ReadAsync()
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);

            return task.State;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns startup on or off. Returns null on success, or the sentence to show the user.
    /// </summary>
    public static async Task<string?> SetEnabledAsync(bool enabled)
    {
        StartupTask task;

        try
        {
            task = await StartupTask.GetAsync(TaskId);
        }
        catch (Exception ex)
        {
            return $"Windows did not accept the startup registration ({ex.Message}). Reinstall Evania with run-app.ps1.";
        }

        try
        {
            // Enabling is a request Windows can refuse; disabling is a call it takes. Both are
            // answered by reading the state back, so the switch reports what the operating system
            // agreed to rather than what was asked for.
            if (enabled)
            {
                await task.RequestEnableAsync();
            }
            else
            {
                task.Disable();
            }

            return task.State switch
            {
                StartupTaskState.Enabled when enabled => null,
                StartupTaskState.Disabled when !enabled => null,
                StartupTaskState.EnabledByPolicy when enabled => null,
                // The user switched Evania off in Task Manager or Settings > Startup. That verdict
                // sticks: only they can put it back, so say who has to act rather than retrying.
                StartupTaskState.DisabledByUser when enabled =>
                    "Startup is blocked for Evania in Windows. Enable it under Task Manager > Startup apps, or Settings > Apps > Evania > Startup.",
                StartupTaskState.DisabledByPolicy =>
                    "Startup is managed by your organisation's policy, so Evania cannot change it.",
                _ => $"Windows reports startup as \"{task.State}\" rather than {(enabled ? "on" : "off")}.",
            };
        }
        catch (Exception ex)
        {
            return $"Windows refused the startup change: {ex.Message}";
        }
    }

    /// <summary>
    /// Aligns the stored setting with what Windows actually does, so the switch on the Settings page
    /// shows the operating system's state rather than what was last asked for.
    /// </summary>
    public static async Task SyncSettingAsync()
    {
        StartupTaskState? state = await ReadAsync();

        if (state is null)
        {
            return;
        }

        bool enabled = state == StartupTaskState.Enabled;

        if (SettingsService.Current.StartWithWindows != enabled)
        {
            SettingsService.Current.StartWithWindows = enabled;
        }
    }
}
