using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MarkdownEditor = Evania.Controls.MarkdownEditor.MarkdownEditor;
using Evania.Services;
using Evania.ViewModels;
using Evania.Windows;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
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

    private readonly string[] settingLabelNames =
    [
        // Section headers
        "DatabaseHeader",
        "GeneralHeader",
        "ScreenshotHeader",
        "ChatHeader",
        "WebBookmarksHeader",

        // Setting titles
        "PathTitle",
        "PasswordTitle",
        "StartWithWindowsTitle",
        "CloseToTrayTitle",
        "ShortcutTitle",
        "AutoSaveTitle",
        "ProviderTitle",
        "BookmarkUrlLabel",
        "SavedBookmarksTitle",

        // Subtitles
        "PathSubtitle",
        "PasswordSubtitle",
        "StartWithWindowsSubtitle",
        "CloseToTraySubtitle",
        "ShortcutSubtitle",
        "AutoSaveSubtitle",
        "ProviderSubtitle",
        "BookmarkUrlSubtitle"
    ];

    private TextBlock[] settingLabels = [];

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
        ViewModel.RequestDatabasePasswordAsync = PromptForDatabasePasswordAsync;

        // The password box cannot be two-way bound to a secret, so a password the view model picked
        // up elsewhere - the prompt dialog, most of all - has to be pushed back into the control.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsPageViewModel.CandidatePassword) &&
                PasswordBox.Password != ViewModel.CandidatePassword)
            {
                PasswordBox.Password = ViewModel.CandidatePassword;
            }
        };
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetStagingFromSettings();
        InputToStaging();
        SyncSwitchesToSettings();
        ViewModel.RefreshStatus();

        settingLabels = settingLabelNames.Select(n => (TextBlock)FindName(n)).ToArray();

        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.TitleBarSearchChanged += OnTitleBarSearchChanged;
            ApplySearchHighlight(mainWindow.TitleBarSearchText);
        }

        AboutVersionText.Text = $"Evania {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}";
        loaded = true;
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.TitleBarSearchChanged -= OnTitleBarSearchChanged;
        }
    }

    private static readonly SolidColorBrush HighlightBackground = new(Colors.DodgerBlue);

        private void OnTitleBarSearchChanged(object? sender, string text)
        {
            ApplySearchHighlight(text);
        }

        private void ApplySearchHighlight(string text)
        {
            foreach (TextBlock label in settingLabels)
            {
                label.TextHighlighters.Clear();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    int index = label.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase);

                    if (index >= 0)
                    {
                        var range = new TextRange { StartIndex = index, Length = text.Length };
                        var highlighter = new TextHighlighter
                        {
                            Background = HighlightBackground,
                            Ranges = { range }
                        };

                        label.TextHighlighters.Add(highlighter);
                    }
                }
            }
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
        ChatProviderBox.Text = ViewModel.Settings.ChatProviderUrl;
        ChatProviderSetButton.IsEnabled = ViewModel.IsChatProviderUrlStaged(ChatProviderBox.Text);
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null)
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".db");

        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        // Start the picker where the current path lives, not at some default library.
        string? directory = Path.GetDirectoryName(ViewModel.CandidatePath);

        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                SetPickerFolder(picker, directory);
            }
            catch (Exception)
            {
            }
        }

        StorageFile? chosen = await picker.PickSingleFileAsync();

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

    /// <summary>
    /// The address is committed only by the Set button: a half-typed address must not steer the
    /// Chat page, so the text is validated here before it reaches the settings and, on success,
    /// the normalized value is shown back in the box.
    /// </summary>
    private void ChatProviderSet_Click(object sender, RoutedEventArgs e)
    {
        if (!loaded)
        {
            return;
        }

        if (ViewModel.SetChatProviderUrl(ChatProviderBox.Text))
        {
            ChatProviderBox.Text = ViewModel.Settings.ChatProviderUrl;
        }
    }

    private void ChatProviderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ChatProviderSetButton.IsEnabled = ViewModel.IsChatProviderUrlStaged(ChatProviderBox.Text);
    }

    private async void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.AddBookmarkAsync();
    }

    private void RemoveBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WebBookmark bookmark })
        {
            ViewModel.RemoveBookmark(bookmark);
        }
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.CandidatePassword = PasswordBox.Password;
    }

    private async Task<string?> PromptForDatabasePasswordAsync(string path, bool previousWasRejected)
    {
        PasswordBox input = new()
        {
            PlaceholderText = "Password",
            PasswordRevealMode = PasswordRevealMode.Peek
        };

        ContentDialog dialog = new()
        {
            Title = previousWasRejected ? "Incorrect password" : "Password required",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = previousWasRejected
                            ? "That password did not open this database. Try again, or cancel to leave the current one in place."
                            : "This database is encrypted. Enter its password to open it.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock { Text = path, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                    input
                }
            },
            PrimaryButtonText = previousWasRejected ? "Try again" : "Use password",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(input.Password) ? null : input.Password;
    }

    private async void ClearBrowsingData_Click(object sender, RoutedEventArgs e)
    {
        ContentDialog confirm = new()
        {
            Title = "Clear browsing data?",
            Content = "This signs you out of Chat and Web Bookmarks and removes their cookies, cache and history.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await ProfileWebView.EnsureCoreWebView2Async(await SettingsService.GetChatWebViewEnvironmentAsync());
            await ProfileWebView.CoreWebView2.Profile.ClearBrowsingDataAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = ex.Message;
        }
    }

    private async void ShowLegal_Click(object sender, RoutedEventArgs e)
    {
        string file = (string)((FrameworkElement)sender).Tag;
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Legal", file);
        string text = File.Exists(path) ? await File.ReadAllTextAsync(path) : "This document is not available.";

        LegalEditor.Markdown = text;
        LegalOverlay.Visibility = Visibility.Visible;
        LegalEditor.Redraw();
    }

    private void LegalClose_Click(object sender, RoutedEventArgs e) => LegalOverlay.Visibility = Visibility.Collapsed;

    private void ErrorBar_Closed(InfoBar sender, object args) => ViewModel.ErrorMessage = string.Empty;

    private static void SetPickerFolder(object picker, string folderPath)
    {
        Directory.CreateDirectory(folderPath);

        nint fileDialog = Marshal.GetComInterfaceForObject(picker, typeof(IFileDialog));

        if (fileDialog == nint.Zero)
        {
            return;
        }

        try
        {
            Guid shellItemGuid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(folderPath, nint.Zero, ref shellItemGuid, out nint shellItem);

            if (shellItem != nint.Zero)
            {
                try
                {
                    ((IFileDialog)Marshal.GetObjectForIUnknown(fileDialog)).SetFolder(shellItem);
                }
                finally
                {
                    Marshal.Release(shellItem);
                }
            }
        }
        finally
        {
            Marshal.Release(fileDialog);
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    private interface IFileDialog
    {
        [PreserveSig]
        nint Show(nint parent);

        [PreserveSig]
        nint SetFileTypes(uint count, nint filters);

        [PreserveSig]
        nint SetFileTypeIndex(uint id);

        [PreserveSig]
        nint GetFileTypeIndex(out uint id);

        [PreserveSig]
        nint Advise(nint sink, out uint cookie);

        [PreserveSig]
        nint Unadvise(uint cookie);

        [PreserveSig]
        nint SetOptions(uint options);

        [PreserveSig]
        nint GetOptions(out uint options);

        [PreserveSig]
        nint SetDefaultFolder(nint folder);

        [PreserveSig]
        nint SetFolder(nint folder);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    private interface IShellItem
    {
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, nint bindContext, ref Guid riid, out nint shellItem);
}
