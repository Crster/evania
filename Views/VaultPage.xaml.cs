using System;
using System.Threading.Tasks;
using Evania.Models;
using Evania.Services;
using Evania.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using ZXing;
using ZXing.Common;

namespace Evania.Views;

public sealed partial class VaultPage : Page
{
    /// <summary>
    /// OTP codes tick once a second while the page is alive; the view model derives each visible
    /// code on demand, so the timer carries no secrets of its own.
    /// </summary>
    private DispatcherTimer? totpTimer;

    /// <summary>
    /// The password field starts masked and is re-masked every time the editor opens, so a previous
    /// session's reveal choice never leaks into a different account.
    /// </summary>
    private bool passwordVisible;

    /// <summary>The otpauth:// URI behind the shareable QR, or null when the edited secret is invalid.</summary>
    private string? otpAuthUri;

    public VaultPageViewModel ViewModel { get; } = new();

    public VaultPage()
    {
        InitializeComponent();

        GroupedEntriesSource.Source = ViewModel.Groups;
        EntryList.ItemsSource = GroupedEntriesSource.View;

        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        EditorTabs.SelectionChanged += EditorTabs_SelectionChanged;
        EditorTabs.SelectedItem = EditorTabs.Items[0];
    }

    private async void VaultPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
        UpdatePanels();

        // The window owns the search box in its chrome; it pushes text changes to us.
        if (App.MainWindow is Evania.Windows.MainWindow mainWindow)
        {
            mainWindow.TitleBarSearchChanged += MainWindow_TitleBarSearchChanged;

            if (!string.IsNullOrEmpty(mainWindow.TitleBarSearchText))
            {
                ViewModel.Filter = mainWindow.TitleBarSearchText;
            }
        }

        totpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        totpTimer.Tick += (_, _) => ViewModel.RefreshTotp();
        totpTimer.Start();
    }

    private void VaultPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is Evania.Windows.MainWindow mainWindow)
        {
            mainWindow.TitleBarSearchChanged -= MainWindow_TitleBarSearchChanged;
        }

        totpTimer?.Stop();
        totpTimer = null;
    }

    private void MainWindow_TitleBarSearchChanged(object? sender, string text)
    {
        if (!ViewModel.IsEditing)
        {
            ViewModel.Filter = text;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VaultPageViewModel.IsEditing))
        {
            // New and Open both raise IsEditing just after filling the edit fields, so the resets
            // here always reflect the account being shown, never the previous one.
            if (ViewModel.IsEditing)
            {
                passwordVisible = false;
                ApplyPasswordMask();
                NoteEditor.Markdown = ViewModel.EditNote;
                EditorTabs.SelectedItem = EditorTabs.Items[0];

                UpdateOtpQr();
            }

            UpdatePanels();
        }
        else if (e.PropertyName is nameof(VaultPageViewModel.EditTotpSecret) or
                 nameof(VaultPageViewModel.EditSite) or
                 nameof(VaultPageViewModel.EditUsername))
        {
            // The QR embeds the issuer and account name alongside the secret, so it follows edits
            // to any of the three.
            UpdateOtpQr();
        }
        else if (e.PropertyName == nameof(VaultPageViewModel.EditPassword))
        {
            if (!passwordVisible && PasswordInput.Visibility == Visibility.Visible
                && !string.IsNullOrEmpty(ViewModel.EditPassword))
            {
                // The empty field renders as plain text so the first character can be typed;
                // once there is a value on screen the eye must switch to the "Hide" state to
                // match what is actually shown.
                passwordVisible = true;
                ApplyPasswordMask();
            }
            else if (!passwordVisible)
            {
                PasswordInputMasked.Text = Mask(ViewModel.EditPassword);
            }
        }
        else if (e.PropertyName == nameof(VaultPageViewModel.Groups))
        {
            // Re-assign the view as well: the source swap must reach the ListView, which holds the
            // view object it was given.
            GroupedEntriesSource.Source = ViewModel.Groups;
            EntryList.ItemsSource = GroupedEntriesSource.View;
        }
    }

    private void UpdatePanels()
    {
        ListPanel.Visibility = ViewModel.IsEditing ? Visibility.Collapsed : Visibility.Visible;
        EditorPanel.Visibility = ViewModel.IsEditing ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- list ------------------------------------------------------------------------------------

    private void New_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NewEntry();
        UpdatePanels();
    }

    private void AddToSite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VaultSiteGroup group)
        {
            return;
        }

        ViewModel.NewEntry(group.DisplayName);
        UpdatePanels();
    }

    private async void EntryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VaultListItem item)
        {
            await ViewModel.OpenEntryAsync(item);
            UpdatePanels();
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        StorageFile? file = await PickFileAsync(new[] { ".csv" });
        if (file is null)
        {
            return;
        }

        string text = await FileIO.ReadTextAsync(file);
        (int imported, int skipped) = await ViewModel.ImportCsvAsync(text);

        if (!ViewModel.HasError)
        {
            ViewModel.StatusMessage = skipped > 0
                ? $"Imported {imported} accounts from {file.Name}; skipped {skipped} already in the vault."
                : $"Imported {imported} accounts from {file.Name}.";
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is null)
        {
            return;
        }

        string? csv = await ViewModel.ExportCsvAsync();
        if (csv is null)
        {
            return;
        }

        FileSavePicker picker = new();
        picker.FileTypeChoices.Add("CSV file", new[] { ".csv" });
        picker.SuggestedFileName = "evania-vault";

        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        StorageFile? chosen = await picker.PickSaveFileAsync();
        if (chosen is null)
        {
            return;
        }

        await FileIO.WriteTextAsync(chosen, csv);
        ViewModel.StatusMessage = $"Exported the vault to {chosen.Name}.";
    }

    // --- editor ----------------------------------------------------------------------------------

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsDirty)
        {
            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = "Discard changes?",
                Content = "This account has unsaved changes.",
                PrimaryButtonText = "Discard",
                CloseButtonText = "Keep editing",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        ViewModel.CloseEditor();
        UpdatePanels();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveEntryAsync();
        UpdatePanels();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsExistingEntry)
        {
            return;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Delete account?",
            Content = "This will permanently remove this account from the vault.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.DeleteAccountAsync();
        UpdatePanels();
    }

    private async void EditorGenerate_Click(object sender, RoutedEventArgs e) =>
        await ShowGeneratorDialogAsync(fillEditor: true);

    private void EditorCopyPassword_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(ViewModel.EditPassword);

    private void TogglePassword_Click(object sender, RoutedEventArgs e)
    {
        passwordVisible = !passwordVisible;
        ApplyPasswordMask();
    }

    /// <summary>
    /// Swaps the single-line password input for its read-only bullet stand-in and flips the eye
    /// icon. The real TextBox is never rewritten, so masking cannot corrupt the bound value. When
    /// the password is empty the mask is skipped so the user can type immediately.
    /// </summary>
    private void ApplyPasswordMask()
    {
        bool showMasked = !passwordVisible && !string.IsNullOrEmpty(ViewModel.EditPassword);

        PasswordInput.Visibility = showMasked ? Visibility.Collapsed : Visibility.Visible;
        PasswordInputMasked.Visibility = showMasked ? Visibility.Visible : Visibility.Collapsed;
        PasswordInputMasked.Text = showMasked ? Mask(ViewModel.EditPassword) : string.Empty;

        TogglePasswordIcon.Glyph = passwordVisible ? "\uED1A" : "\uE7B3";
        ToolTipService.SetToolTip(TogglePasswordButton, passwordVisible ? "Hide password" : "Show password");

        if (!showMasked)
        {
            PasswordInput.Focus(FocusState.Programmatic);
        }
    }

    private static string Mask(string value) => new('●', value.Length);

    private void NoteEditor_MarkdownChanged(object? sender, string markdown) =>
        ViewModel.EditNote = markdown;

    // Basic note formatting, mirroring the subset of the NotePage toolbar that fits here.
    private void NoteBold_Click(object sender, RoutedEventArgs e) => NoteEditor.ToggleBold();
    private void NoteItalic_Click(object sender, RoutedEventArgs e) => NoteEditor.ToggleItalic();
    private void NoteStrikethrough_Click(object sender, RoutedEventArgs e) => NoteEditor.ToggleStrikethrough();
    private void NoteCode_Click(object sender, RoutedEventArgs e) => NoteEditor.ToggleInlineCode();
    private void NoteList_Click(object sender, RoutedEventArgs e) => NoteEditor.ToggleBulletList();
    private void NoteOrderedList_Click(object sender, RoutedEventArgs e) => NoteEditor.ToggleOrderedList();
    private void NoteTaskList_Click(object sender, RoutedEventArgs e) => NoteEditor.ToggleTaskList();
    private void NoteQuote_Click(object sender, RoutedEventArgs e) => NoteEditor.ToggleQuote();

    private void EditorCopyTotp_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(ViewModel.EditTotpCode);

    private void CopyOtpUri_Click(object sender, RoutedEventArgs e)
    {
        if (otpAuthUri is not null)
        {
            CopyToClipboard(otpAuthUri);
        }
    }

    /// <summary>
    /// Renders the shareable QR for the secret currently being edited, or clears it when the secret
    /// is absent or invalid. The QR's own visibility is the view model's - it shows exactly when a
    /// live code does.
    /// </summary>
    private void UpdateOtpQr()
    {
        if (TotpService.TryParseSecret(
                ViewModel.EditTotpSecret,
                out string secret,
                out int digits,
                out int period))
        {
            otpAuthUri = TotpService.BuildOtpAuthUri(secret, ViewModel.EditSite, ViewModel.EditUsername, digits, period);

            try
            {
                // 2x the display size so high-DPI screens get a crisp 1:1 render.
                OtpQrImage.Source = QrService.Render(otpAuthUri, 440);
                return;
            }
            catch (Exception)
            {
            }
        }

        otpAuthUri = null;
        OtpQrImage.Source = null;
    }

    // --- history tab -------------------------------------------------------------------------------

    private void HistoryReveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VaultHistoryItem item)
        {
            return;
        }

        item.IsPasswordVisible = !item.IsPasswordVisible;
        ToolTipService.SetToolTip(
            (FrameworkElement)sender,
            item.IsPasswordVisible ? "Hide password" : "Show password");
    }

    private void HistoryCopyUsername_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is VaultHistoryItem { HasUsername: true } item)
        {
            CopyToClipboard(item.Username);
        }
    }

    private void HistoryCopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is VaultHistoryItem { HasPassword: true } item)
        {
            CopyToClipboard(item.Password);
        }
    }

    private async void ScanTotpQr_Click(object sender, RoutedEventArgs e)
    {
        CameraPreview camera = new();
        Image preview = new()
        {
            Height = 280,
            Stretch = Stretch.Uniform,
        };
        TextBlock status = new()
        {
            Text = "Starting camera...",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Scan authenticator QR code",
            PrimaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 420,
                Children =
                {
                    preview,
                    new TextBlock
                    {
                        Text = "Hold the authenticator QR code steady in front of the webcam.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    status,
                },
            },
        };

        BarcodeReaderGeneric reader = new()
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                PossibleFormats = [BarcodeFormat.QR_CODE],
                TryHarder = true,
            },
        };
        int isDecoding = 0;
        int decodeAttemptCount = 0;
        int scanStatusShown = 0;
        bool codeApplied = false;

        camera.BitmapChanged += () => preview.Source = camera.Bitmap;
        camera.FrameReceived += (pixels, width, height) =>
        {
            if (System.Threading.Interlocked.Exchange(ref isDecoding, 1) == 1)
            {
                return;
            }

            try
            {
                int attempt = System.Threading.Interlocked.Increment(ref decodeAttemptCount);
                if (attempt == 1 && System.Threading.Interlocked.Exchange(ref scanStatusShown, 1) == 0)
                {
                    DispatcherQueue.TryEnqueue(() => status.Text = "Camera ready. Looking for a QR code...");
                }

                Result? result = reader.Decode(new RGBLuminanceSource(
                    pixels,
                    width,
                    height,
                    RGBLuminanceSource.BitmapFormat.BGRA32));
                if (result?.Text is not { } text)
                {
                    return;
                }

                bool isValidTotp = TotpService.TryParseSecret(text, out _, out _, out _);

                if (isValidTotp)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (codeApplied)
                        {
                            return;
                        }

                        codeApplied = true;
                        ViewModel.EditTotpSecret = text;
                        status.Text = "Authenticator QR code detected.";
                        dialog.Hide();
                    });
                }
                else
                {
                    DispatcherQueue.TryEnqueue(() =>
                        status.Text = "QR code found, but it is not a valid TOTP setup code. Try another code.");
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref isDecoding, 0);
            }
        };

        try
        {
            string? cameraError = await camera.StartAsync();
            if (cameraError is not null)
            {
                status.Text = cameraError;
            }
            else
            {
                status.Text = "Camera ready. Looking for a QR code...";
            }

            await dialog.ShowAsync();
        }
        finally
        {
            await camera.StopAsync();
            camera.Dispose();
        }
    }

    // --- card quick actions ----------------------------------------------------------------------

    private async void CopyUsername_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VaultListItem item)
        {
            return;
        }

        (VaultSite Site, VaultAccount Account)? loaded =
            await VaultService.GetAccountAsync(item.SiteId, item.AccountId);
        if (loaded is not null)
        {
            CopyToClipboard(loaded.Value.Account.Username);
        }
    }

    private async void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VaultListItem item)
        {
            return;
        }

        (VaultSite Site, VaultAccount Account)? loaded =
            await VaultService.GetAccountAsync(item.SiteId, item.AccountId);
        if (loaded is not null)
        {
            CopyToClipboard(loaded.Value.Account.Password);
        }
    }

    private void CopyTotp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is VaultListItem item)
        {
            CopyToClipboard(item.TotpCode);
        }
    }

    // --- generator dialog --------------------------------------------------------------------------

    /// <summary>
    /// Builds the generator dialog in code, the house pattern for one-off dialogs. With
    /// <paramref name="fillEditor"/> the primary action drops the password into the editor's field;
    /// otherwise it copies to the clipboard.
    /// </summary>
    private async Task ShowGeneratorDialogAsync(bool fillEditor)
    {
        GeneratorOptions options = new();

        TextBox preview = new()
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            IsTabStop = false,
        };

        TextBlock strength = new()
        {
            FontSize = 11,
            Opacity = 0.55,
            Margin = new Thickness(0, 4, 0, 0),
        };

        TextBlock lengthValue = new()
        {
            FontSize = 12,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        Slider length = new()
        {
            Minimum = PasswordGeneratorService.MinLength,
            Maximum = 64,
            Value = options.Length,
            StepFrequency = 1,
            IsThumbToolTipEnabled = false,
        };

        CheckBox lowercase = new() { Content = "Lowercase (a-z)", IsChecked = options.Lowercase };
        CheckBox uppercase = new() { Content = "Uppercase (A-Z)", IsChecked = options.Uppercase };
        CheckBox digits = new() { Content = "Digits (0-9)", IsChecked = options.Digits };
        CheckBox symbols = new() { Content = "Symbols (!@#$...)", IsChecked = options.Symbols };
        CheckBox avoidAmbiguous = new() { Content = "Avoid look-alike characters (il1Lo0O)", IsChecked = options.AvoidAmbiguous };

        void Regenerate()
        {
            options.Length = (int)length.Value;
            options.Lowercase = lowercase.IsChecked == true;
            options.Uppercase = uppercase.IsChecked == true;
            options.Digits = digits.IsChecked == true;
            options.Symbols = symbols.IsChecked == true;
            options.AvoidAmbiguous = avoidAmbiguous.IsChecked == true;

            lengthValue.Text = $"{options.Length} characters";

            if (!options.Lowercase && !options.Uppercase && !options.Digits && !options.Symbols)
            {
                preview.Text = string.Empty;
                strength.Text = "Pick at least one character class.";

                return;
            }

            preview.Text = PasswordGeneratorService.Generate(options);
            strength.Text = $"~{PasswordGeneratorService.EstimateStrengthBits(options)} bits of entropy";
        }

        length.ValueChanged += (_, _) => Regenerate();
        lowercase.Checked += (_, _) => Regenerate();
        lowercase.Unchecked += (_, _) => Regenerate();
        uppercase.Checked += (_, _) => Regenerate();
        uppercase.Unchecked += (_, _) => Regenerate();
        digits.Checked += (_, _) => Regenerate();
        digits.Unchecked += (_, _) => Regenerate();
        symbols.Checked += (_, _) => Regenerate();
        symbols.Unchecked += (_, _) => Regenerate();
        avoidAmbiguous.Checked += (_, _) => Regenerate();
        avoidAmbiguous.Unchecked += (_, _) => Regenerate();

        Regenerate();

        StackPanel content = new() { Spacing = 10, MinWidth = 360 };
        content.Children.Add(preview);
        content.Children.Add(strength);
        content.Children.Add(new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children =
            {
                new TextBlock { Text = "Length", FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                lengthValue,
            },
        });
        Grid.SetColumn(lengthValue, 1);
        content.Children.Add(length);
        content.Children.Add(lowercase);
        content.Children.Add(uppercase);
        content.Children.Add(digits);
        content.Children.Add(symbols);
        content.Children.Add(avoidAmbiguous);

        Button regenerate = new()
        {
            Content = "Regenerate",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        regenerate.Click += (_, _) => Regenerate();
        content.Children.Add(regenerate);

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Password generator",
            Content = content,
            PrimaryButtonText = fillEditor ? "Use password" : "Copy to clipboard",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && preview.Text.Length > 0)
        {
            if (fillEditor)
            {
                ViewModel.EditPassword = preview.Text;
            }
            else
            {
                CopyToClipboard(preview.Text);
            }
        }
    }

    // --- plumbing ----------------------------------------------------------------------------------

    private void EditorTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (NotePanel is null || OtpPanel is null || HistoryPanel is null)
            return;

        NotePanel.Visibility = sender.SelectedItem == sender.Items[0] ? Visibility.Visible : Visibility.Collapsed;
        OtpPanel.Visibility = sender.SelectedItem == sender.Items[1] ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = sender.SelectedItem == sender.Items[2] ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.ClearError();

    private void StatusBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.ClearStatus();

    private void UnencryptedBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        ViewModel.ShowUnencryptedWarning = false;

    private void OpenSettings_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow?.NavigateToSettings();

    private async Task<StorageFile?> PickFileAsync(string[] extensions)
    {
        if (App.MainWindow is null)
        {
            return null;
        }

        FileOpenPicker picker = new();
        foreach (string extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        return await picker.PickSingleFileAsync();
    }

    private static void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        DataPackage package = new();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
