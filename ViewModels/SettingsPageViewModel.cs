using System;
using System.Collections.Generic;
using System.IO;
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

        /// <summary>The encryption switch, likewise staged until Apply.</summary>
        [ObservableProperty]
        public partial bool CandidateEncryption { get; set; }

        [ObservableProperty]
        public partial bool IsApplying { get; set; }

        [ObservableProperty]
        public partial string StatusText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string ErrorMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial ScreenshotHotkey SelectedHotkey { get; set; }

        public SettingsPageViewModel()
        {
            CandidatePath = Settings.DatabasePath;
            CandidateEncryption = Settings.DatabaseEncryption;
            SelectedHotkey = Settings.ScreenshotShortcut;

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

        public bool HasError => ErrorMessage.Length > 0;

        /// <summary>
        /// Whether the key can be copied or rotated: only while the file on disk is actually open and
        /// encrypted, which the live connection decides - not the pending switch.
        /// </summary>
        public bool CanManageKey => DatabaseService.Status.Connected && DatabaseService.Status.Encrypted;

        public bool NeedsApply =>
            !string.Equals(CandidatePath.Trim(), Settings.DatabasePath, StringComparison.OrdinalIgnoreCase) ||
            CandidateEncryption != Settings.DatabaseEncryption;

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

        partial void OnCandidateEncryptionChanged(bool value) => OnPropertyChanged(nameof(NeedsApply));

        [RelayCommand]
        private async Task ApplyDatabaseAsync()
        {
            if (IsApplying)
            {
                return;
            }

            IsApplying = true;
            ErrorMessage = string.Empty;

            // Normalise what was typed before it is compared to anything, so a stray space cannot
            // leave Apply looking needed forever.
            CandidatePath = CandidatePath.Trim();

            try
            {
                DatabaseStatus result = await DatabaseService.ApplyAsync(CandidatePath, CandidateEncryption);

                if (!result.Connected)
                {
                    ErrorMessage = result.Error ?? "The database could not be changed.";
                }
            }
            finally
            {
                IsApplying = false;
                RefreshStatus();
            }
        }

        /// <summary>
        /// Writes a new key into the data file. Reached only from the page, which has just asked the
        /// user to confirm that the old key stops working the moment this succeeds.
        /// </summary>
        public async Task RotateKeyAsync()
        {
            if (IsApplying)
            {
                return;
            }

            IsApplying = true;
            ErrorMessage = string.Empty;

            try
            {
                DatabaseStatus result = await DatabaseService.RotateKeyAsync();

                if (!result.Connected)
                {
                    ErrorMessage = result.Error ?? "The key could not be replaced.";
                }
            }
            finally
            {
                IsApplying = false;
                RefreshStatus();
            }
        }

        /// <summary>
        /// Starts over from an empty file in the requested state - the only way out of a database
        /// whose key is gone. The page confirms this before calling it.
        /// </summary>
        public async Task RecreateAsync()
        {
            if (IsApplying)
            {
                return;
            }

            IsApplying = true;
            ErrorMessage = string.Empty;

            try
            {
                DatabaseStatus result = await DatabaseService.RecreateAsync(CandidatePath.Trim(), CandidateEncryption);

                if (!result.Connected)
                {
                    ErrorMessage = result.Error ?? "The database could not be recreated.";
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

        public Task SetCloseToTrayAsync(bool enabled)
        {
            Settings.CloseToTray = enabled;

            return TrayIconService.SetEnabledAsync(enabled);
        }

        public void SetAutoSave(bool enabled) => Settings.AutoSaveScreenshot = enabled;

        public void SetShortcut(ScreenshotHotkey hotkey)
        {
            string? error = GlobalHotkeyService.Apply(hotkey);

            if (error is not null)
            {
                ErrorMessage = error;

                // Put the dropdown back on what is actually registered, and say so by raising the
                // change explicitly: the value never moved away from it in the settings.
                OnPropertyChanged(nameof(SelectedHotkey));

                return;
            }

            ErrorMessage = string.Empty;
            Settings.ScreenshotShortcut = hotkey;
        }

        public void RefreshStatus()
        {
            DatabaseStatus status = DatabaseService.Status;

            StatusText = !status.Connected && SettingsService.DatabaseKeyUnreadable
                ? "Not connected - the stored key was protected on another Windows account or machine."
                : status.Description;

            OnPropertyChanged(nameof(CanManageKey));
            OnPropertyChanged(nameof(NeedsApply));
        }
    }
}
