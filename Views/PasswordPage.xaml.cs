using System;
using System.Threading.Tasks;
using Evania.Models;
using Evania.Services;
using Evania.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using ZXing;
using ZXing.Common;

namespace Evania.Views;

public sealed partial class PasswordPage : Page
{
    /// <summary>
    /// TOTP codes tick once a second while the page is alive; the view model derives each visible
    /// code on demand, so the timer carries no secrets of its own.
    /// </summary>
    private DispatcherTimer? totpTimer;

    /// <summary>Starts masked, and re-masked every time the editor opens so a previous session's
    /// reveal choice never leaks into a different entry.</summary>
    private bool passwordMasked = true;

    public PasswordPageViewModel ViewModel { get; } = new();

    public PasswordPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private async void PasswordPage_Loaded(object sender, RoutedEventArgs e)
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

    private void PasswordPage_Unloaded(object sender, RoutedEventArgs e)
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
        if (e.PropertyName == nameof(PasswordPageViewModel.IsEditing))
        {
            // New and Open both raise IsEditing just after filling the edit fields, so the mask
            // reset always reflects the entry being shown, never the previous one.
            if (ViewModel.IsEditing)
            {
                passwordMasked = true;
                ApplyPasswordMask();
            }

            UpdatePanels();
        }
        else if (e.PropertyName == nameof(PasswordPageViewModel.EditPassword) && passwordMasked)
        {
            PasswordInputMasked.Text = Mask(ViewModel.EditPassword);
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

    private async void EntryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PasswordListItem item)
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
                ? $"Imported {imported} passwords from {file.Name}; skipped {skipped} already in the database."
                : $"Imported {imported} passwords from {file.Name}.";
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
        picker.SuggestedFileName = "evania-passwords";

        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        StorageFile? chosen = await picker.PickSaveFileAsync();
        if (chosen is null)
        {
            return;
        }

        await FileIO.WriteTextAsync(chosen, csv);
        ViewModel.StatusMessage = $"Exported all passwords to {chosen.Name}.";
    }

    private async void Generator_Click(object sender, RoutedEventArgs e) =>
        await ShowGeneratorDialogAsync(fillEditor: false);

    // --- editor ----------------------------------------------------------------------------------

    private void Back_Click(object sender, RoutedEventArgs e)
    {
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
        LiteDB.ObjectId? id = ViewModel.CurrentEntryId;
        if (id is null)
        {
            return;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "Delete password?",
            Content = "This will permanently remove this entry from the database.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.DeleteEntryAsync(id);
        UpdatePanels();
    }

    private async void EditorGenerate_Click(object sender, RoutedEventArgs e) =>
        await ShowGeneratorDialogAsync(fillEditor: true);

    private void EditorCopyPassword_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(ViewModel.EditPassword);

    private void TogglePassword_Click(object sender, RoutedEventArgs e)
    {
        passwordMasked = !passwordMasked;
        ApplyPasswordMask();
    }

    /// <summary>
    /// Swaps the multiline password input for its read-only bullet stand-in and flips the eye icon.
    /// The real TextBox is never rewritten, so masking cannot corrupt the bound value.
    /// </summary>
    private void ApplyPasswordMask()
    {
        PasswordInput.Visibility = passwordMasked ? Visibility.Collapsed : Visibility.Visible;
        PasswordInputMasked.Visibility = passwordMasked ? Visibility.Visible : Visibility.Collapsed;
        PasswordInputMasked.Text = passwordMasked ? Mask(ViewModel.EditPassword) : string.Empty;

        TogglePasswordIcon.Glyph = passwordMasked ? "\uE7B3" : "\uED1A";
        ToolTipService.SetToolTip(TogglePasswordButton, passwordMasked ? "Show password" : "Hide password");
    }

    private static string Mask(string value) => new('●', value.Length);

    private void EditorCopyTotp_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(ViewModel.EditTotpCode);

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

                if (attempt == 1 || attempt % 30 == 0)
                {
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
                bool isOtpAuthUri = text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase);

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

    // --- card quick actions ------------------------------------------------------------------------

    private async void CopyUsername_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PasswordListItem item)
        {
            return;
        }

        PasswordDocument? entry = await PasswordService.GetAsync(item.Id);
        if (entry is not null)
        {
            CopyToClipboard(entry.Username);
        }
    }

    private async void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PasswordListItem item)
        {
            return;
        }

        PasswordDocument? entry = await PasswordService.GetAsync(item.Id);
        if (entry is not null)
        {
            CopyToClipboard(entry.Password);
        }
    }

    private void CopyTotp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PasswordListItem item)
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