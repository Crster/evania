using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evania.Services;

namespace Evania.ViewModels
{
    /// <summary>One entry in the shortcut dropdown.</summary>
    public sealed record HotkeyOption(ScreenshotHotkey Value, string Display);

    /// <summary>
    /// Backs the Settings page.
    ///
    /// The two kinds of setting are applied differently on purpose. The General and Screenshot
    /// switches take effect the moment they move, because each one has an immediate, observable
    /// answer from the operating system - a startup task state, a registered hotkey, a saved file.
    /// The Database pair waits for Apply: a path and an encryption switch describe one file, and
    /// applying half of that pair - moving the path while the key still matches the old file, say -
    /// is exactly the mistake the Apply button exists to prevent.
    /// </summary>
    public partial class SettingsPageViewModel : ObservableObject
    {
        /// <summary>The live settings instance the page reads through.</summary>
        public AppSettings Settings { get; } = SettingsService.Current;

        public IReadOnlyList<HotkeyOption> HotkeyOptions { get; } =
        [
            new HotkeyOption(ScreenshotHotkey.None, ScreenshotHotkey.None.ToDisplayString()),
            new HotkeyOption(ScreenshotHotkey.PrintScreen, ScreenshotHotkey.PrintScreen.ToDisplayString()),
            new HotkeyOption(ScreenshotHotkey.AltPrintScreen, ScreenshotHotkey.AltPrintScreen.ToDisplayString()),
            new HotkeyOption(ScreenshotHotkey.CtrlShift3, ScreenshotHotkey.CtrlShift3.ToDisplayString()),
            new HotkeyOption(ScreenshotHotkey.AltP, ScreenshotHotkey.AltP.ToDisplayString()),
        ];

        /// <summary>The path typed into the box, which may differ from the live one until Apply.</summary>
        [ObservableProperty]
        public partial string CandidatePath { get; set; }

        /// <summary>The password typed into the box, likewise staged until Apply.</summary>
        [ObservableProperty]
        public partial string CandidatePassword { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsApplying { get; set; }

        [ObservableProperty]
        public partial string StatusText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ErrorMessage { get; set; } = string.Empty;

        /// <summary>The dropdown selection, as the option record the ComboBox actually holds.</summary>
        public HotkeyOption SelectedHotkeyOption
        {
            get => field ?? HotkeyOptions[0];
            set
            {
                if (SetProperty(ref field, value) && value is not null && !stagingHotkey)
                {
                    SetShortcut(value.Value);
                }
            }
        } = null!;

        private bool stagingHotkey;

        public SettingsPageViewModel()
        {
            ResetStagingFromSettings();

            // Both General switches feed one sentence, and either can move from somewhere other than
            // this page - the tray menu, a corrected startup state - so the note follows the settings
            // rather than the switch that happened to be clicked.
            Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AppSettings.CloseToTray) or nameof(AppSettings.StartWithWindows))
                {
                    OnPropertyChanged(nameof(StartsHiddenInTray));
                }
            };

            RefreshStatus();
        }

        /// <summary>
        /// Call every time the Settings page appears. It keeps the staged path, password and dropdown
        /// aligned with the persisted/live settings so reopening the page does not show stale values.
        /// </summary>
        public void ResetStagingFromSettings()
        {
            CandidatePath = Settings.DatabasePath;
            CandidatePassword = DatabaseService.CurrentPassword ?? Settings.DatabasePassword ?? string.Empty;

            // Staged quietly: assigning the record the binding already shows must not read as a
            // request to re-register the hotkey.
            stagingHotkey = true;
            SelectedHotkeyOption = HotkeyOptions.FirstOrDefault(o => o.Value == Settings.ScreenshotShortcut) ?? HotkeyOptions[0];
            stagingHotkey = false;
        }

        public bool HasError => ErrorMessage.Length > 0;

        /// <summary>Apply covers the path and the password, which are staged until it is clicked.</summary>
        public bool NeedsApply =>
            !string.Equals(CandidatePath.Trim(), Settings.DatabasePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(CandidatePassword, DatabaseService.CurrentPassword ?? Settings.DatabasePassword ?? string.Empty, StringComparison.Ordinal);

        /// <summary>
        /// True when a launch will land in the tray with no window - which only happens with both
        /// General switches on, and is worth saying where it comes from.
        /// </summary>
        public bool StartsHiddenInTray => Settings.CloseToTray && Settings.StartWithWindows;

        /// <summary>Where auto-save writes, named so the setting is not a mystery.</summary>
        public string AutoSaveFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

        public string AutoSaveSubtitle => $"Writes the screenshot you end up with straight to {AutoSaveFolder}.";

        partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

        partial void OnCandidatePathChanged(string value) => OnPropertyChanged(nameof(NeedsApply));

        partial void OnCandidatePasswordChanged(string value) => OnPropertyChanged(nameof(NeedsApply));

        [RelayCommand]
        private async Task ApplyDatabaseAsync()
        {
            if (IsApplying)
            {
                return;
            }

            ErrorMessage = string.Empty;
            IsApplying = true;

            // Normalise what was typed before it is compared to anything, so a stray space cannot
            // leave Apply looking needed forever.
            CandidatePath = CandidatePath.Trim();

            try
            {
                string? currentPassword = DatabaseService.CurrentPassword ?? Settings.DatabasePassword;
                bool passwordChanged = !string.Equals(CandidatePassword, currentPassword ?? string.Empty, StringComparison.Ordinal);

                if (passwordChanged)
                {
                    // A changed password cannot open the old file, so the database is rebuilt:
                    // the current file is deleted and an empty one is created under the new password.
                    bool encrypt = !string.IsNullOrEmpty(CandidatePassword);

                    DatabaseStatus result = await DatabaseService.RecreateAsync(CandidatePath, encrypt, encrypt ? CandidatePassword : null);

                    if (!result.Connected)
                    {
                        ErrorMessage = result.Error ?? "The database could not be rebuilt.";
                    }
                }
                else
                {
                    DatabaseStatus result = await DatabaseService.ApplyAsync(CandidatePath, Settings.DatabaseEncryption, currentPassword);

                    if (!result.Connected)
                    {
                        ErrorMessage = result.Error ?? "The database could not be changed.";
                    }
                }
            }
            finally
            {
                IsApplying = false;
                RefreshStatus();
            }
        }

        public async Task SetStartWithWindowsAsync(bool enabled)
        {
            ErrorMessage = string.Empty;

            string? error = await AutoStartService.SetEnabledAsync(enabled);

            if (error is null)
            {
                Settings.StartWithWindows = enabled;

                return;
            }

            ErrorMessage = error;

            // The operating system's answer wins over what was asked for, so the switch cannot end up
            // claiming a state Task Manager disagrees with.
            await AutoStartService.SyncSettingAsync();
        }

        public async Task SetCloseToTrayAsync(bool enabled)
        {
            bool previous = Settings.CloseToTray;

            // Apply the setting in memory first so the rest of the app sees the request, but be
            // prepared to roll it back if the tray icon cannot be created.
            Settings.CloseToTray = enabled;

            await TrayIconService.SetEnabledAsync(enabled);

            // ShowAsync swallows fatal errors to keep startup safe, so inspect the result here.
            if (enabled && !TrayIconService.IsShowing)
            {
                Settings.CloseToTray = previous;
                ErrorMessage = "Evania could not show its tray icon. The setting was turned back off.";
            }
        }

        public void SetAutoSave(bool enabled) => Settings.AutoSaveScreenshot = enabled;

        public void SetShortcut(ScreenshotHotkey hotkey)
        {
            string? error = GlobalHotkeyService.Apply(hotkey);

            stagingHotkey = true;
            SelectedHotkeyOption = HotkeyOptions.FirstOrDefault(
                o => o.Value == (error is null ? hotkey : GlobalHotkeyService.Registered)) ?? HotkeyOptions[0];
            stagingHotkey = false;

            if (error is not null)
            {
                // The dropdown follows SelectedHotkeyOption, so a rejected registration snaps the
                // selection back to what is actually registered.
                ErrorMessage = error;

                return;
            }

            ErrorMessage = string.Empty;
            Settings.ScreenshotShortcut = hotkey;
        }

        public void RefreshStatus()
        {
            DatabaseStatus status = DatabaseService.Status;

            StatusText = status.Description;

            OnPropertyChanged(nameof(NeedsApply));
        }
    }
}
