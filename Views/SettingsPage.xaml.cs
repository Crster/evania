using System;
using System.IO;
using System.Threading.Tasks;
using Evania.Services;
using Evania.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Evania.Views;

/// <summary>
/// The Settings page. Everything of substance lives in <see cref="SettingsPageViewModel"/>; this
/// class holds only what needs a window or a control - the file picker, the password boxes, the
/// confirmation dialogs, and the switches that have to tell the operating system rather than just
/// remember.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; } = new();

    /// <summary>
    /// False until the page has finished loading. A <see cref="ToggleSwitch"/> raises
    /// <c>Toggled</c> when a binding first sets its state, and the startup switch in particular must
    /// not answer that by re-registering a startup task nobody asked about.
    /// </summary>
    private bool loaded;

    public SettingsPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetStagingFromSettings();
        InputToStaging();
        SyncSwitchesToSettings();
        ViewModel.RefreshStatus();

        loaded = true;
    }

    /// <summary>
    /// Copies the live settings into the input controls that cannot be safely two-way bound to a
    /// secret (the password box) or that need explicit re-initialisation when the page reappears.
    /// </summary>
    private void InputToStaging()
    {
        PasswordBox.Password = ViewModel.CandidatePassword;
    }

    /// <summary>
    /// Forces the toggle switches and shortcut dropdown back to the authoritative settings values.
    /// This prevents UI drift after a setting is rejected by the operating system or a service.
    /// </summary>
    private void SyncSwitchesToSettings()
    {
        StartWithWindowsToggle.IsOn = ViewModel.Settings.StartWithWindows;
        CloseToTrayToggle.IsOn = ViewModel.Settings.CloseToTray;
        AutoSaveToggle.IsOn = ViewModel.Settings.AutoSaveScreenshot;

        // SelectedItem rather than SelectedValue: an enum value never survives the ComboBox's
        // item-resolution pass (the selection comes back empty), while a record that is one of the
        // ItemsSource items is matched by reference.
        ShortcutCombo.SelectedItem = ViewModel.SelectedHotkeyOption;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null)
        {
            return;
        }

        var picker = new FileSavePicker();

        // A save picker offers named groups of extensions, unlike the open picker's flat filter.
        picker.FileTypeChoices.Add("LiteDB database", new[] { ".db" });
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(ViewModel.CandidatePath);

        // Ask to open where the file already is rather than at some default library.
        try
        {
            if (File.Exists(ViewModel.CandidatePath))
            {
                picker.SuggestedSaveFile = await StorageFile.GetFileFromPathAsync(ViewModel.CandidatePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Picker could not start at the current path: {ex.Message}");
        }

        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        StorageFile? chosen = await picker.PickSaveFileAsync();

        if (chosen is not null)
        {
            ViewModel.CandidatePath = chosen.Path;
        }
    }

    private async void StartWithWindows_Toggled(object sender, RoutedEventArgs e)
    {
        if (!loaded)
        {
            return;
        }

        await ViewModel.SetStartWithWindowsAsync(StartWithWindowsToggle.IsOn);
        StartWithWindowsToggle.IsOn = ViewModel.Settings.StartWithWindows;
    }

    private async void CloseToTray_Toggled(object sender, RoutedEventArgs e)
    {
        if (!loaded)
        {
            return;
        }

        await ViewModel.SetCloseToTrayAsync(CloseToTrayToggle.IsOn);
        CloseToTrayToggle.IsOn = ViewModel.Settings.CloseToTray;
    }

    private void AutoSave_Toggled(object sender, RoutedEventArgs e)
    {
        if (!loaded)
        {
            return;
        }

        ViewModel.SetAutoSave(AutoSaveToggle.IsOn);
        AutoSaveToggle.IsOn = ViewModel.Settings.AutoSaveScreenshot;
    }

    private void Shortcut_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loaded || ShortcutCombo.SelectedItem is not HotkeyOption option)
        {
            return;
        }

        ViewModel.SetShortcut(option.Value);

        // SetShortcut already moved SelectedHotkeyOption to the outcome - the picked key, or the
        // registered one when Windows refused - so the control just follows it.
        if (!ReferenceEquals(ShortcutCombo.SelectedItem, ViewModel.SelectedHotkeyOption))
        {
            ShortcutCombo.SelectedItem = ViewModel.SelectedHotkeyOption;
        }
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.CandidatePassword = PasswordBox.Password;
    }

    private void ErrorBar_Closed(InfoBar sender, object args) => ViewModel.ErrorMessage = string.Empty;
}
