using System;
using System.IO;
using System.Threading.Tasks;
using Evania.Services;
using Evania.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Evania.Views;

/// <summary>
/// The Settings page. Everything of substance lives in <see cref="SettingsPageViewModel"/>; this
/// class holds only what needs a window or a control - the file picker, the confirmations, the
/// clipboard, and the switches that have to tell the operating system rather than just remember.
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
        loaded = true;
        ViewModel.RefreshStatus();
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

    private void EncryptToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (loaded)
        {
            ViewModel.CandidateEncryption = EncryptToggle.IsOn;
        }
    }

    private async void StartWithWindows_Toggled(object sender, RoutedEventArgs e)
    {
        if (loaded)
        {
            await ViewModel.SetStartWithWindowsAsync(StartWithWindowsToggle.IsOn);
        }
    }

    private async void CloseToTray_Toggled(object sender, RoutedEventArgs e)
    {
        if (loaded)
        {
            await ViewModel.SetCloseToTrayAsync(CloseToTrayToggle.IsOn);
        }
    }

    private void AutoSave_Toggled(object sender, RoutedEventArgs e)
    {
        if (loaded)
        {
            ViewModel.SetAutoSave(AutoSaveToggle.IsOn);
        }
    }

    private void Shortcut_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loaded || ShortcutCombo.SelectedValue is not ScreenshotHotkey hotkey)
        {
            return;
        }

        ViewModel.SetShortcut(hotkey);
    }

    private async void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DatabaseService.KeyForDisplay is not { } key)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(key);
        Clipboard.SetContent(package);
        Clipboard.Flush();

        // Say it where the click happened; a line at the top of a long page is not feedback.
        button.Content = "Copied";
        await Task.Delay(1500);
        button.Content = "Copy key";
    }

    private async void RegenerateKey_Click(object sender, RoutedEventArgs e)
    {
        bool confirmed = await ConfirmAsync(
            "Regenerate the database key?",
            "Evania will rewrite the database with a new key. The key you have copied anywhere stops "
            + "working the moment this finishes, and the old copy of the file LiteDB keeps is left behind "
            + "next to it.",
            "Regenerate");

        if (confirmed)
        {
            await ViewModel.RotateKeyAsync();
        }
    }

    private async void Recreate_Click(object sender, RoutedEventArgs e)
    {
        bool confirmed = await ConfirmAsync(
            "Recreate the database?",
            "This deletes the data file at the path above and starts a new one, in the state the "
            + "switches above ask for. It is the way out of a database whose key is gone, and nothing "
            + "can be recovered from the old file afterwards.",
            "Delete and recreate");

        if (confirmed)
        {
            await ViewModel.RecreateAsync();
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ErrorBar_Closed(InfoBar sender, object args) => ViewModel.ErrorMessage = string.Empty;
}
