using System;
using System.Drawing;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Evania.Services;

/// <summary>
/// A capture that produced something: either the image is already on disk at
/// <see cref="SavedPath"/>, or it is still the in-memory <see cref="Bitmap"/> waiting to be saved.
/// The two shapes match the two <see cref="ViewModels.ScreenshotItem"/> constructors, so the page can
/// show whichever it gets without a special case.
/// </summary>
public sealed record ScreenshotCapturedMessage(BitmapImage Image, string? SavedPath, Bitmap? Bitmap);

/// <summary>
/// The one capture sequence: grab the screen, open the editor, apply the auto-save setting, and tell
/// whoever is listening what came out of it.
///
/// The toolbar button, the global shortcut and the tray menu all run through here, which is the only
/// way the shortcut and the auto-save setting mean the same thing as clicking the button. Results go
/// out over the messenger rather than through a return value, because the trigger usually has no
/// page to hand the result back to - the app can be sitting in the tray when the key is pressed - and
/// a weak messenger means a screenshot page that has since been navigated away from simply stops
/// hearing about it instead of pinning itself in memory.
/// </summary>
public static class CaptureFlowService
{
    /// <summary>
    /// True while a capture or its editor is up. The shortcut can be held down or pressed again
    /// while the editor is open, and a second fullscreen editor on top of the first captures
    /// nothing useful.
    /// </summary>
    public static bool IsBusy { get; private set; }

    public static async Task RunAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            ScreenshotEditResult result = await ScreenshotService.CaptureAndEditAsync(App.MainWindow);

            string? savedPath = result.SavedFilePath;

            if (savedPath is null && result.Bitmap is { } pending && SettingsService.Current.AutoSaveScreenshot)
            {
                savedPath = await ScreenshotService.SaveScreenshotAsync(pending);
            }

            // Cancelled: nothing to report.
            if (savedPath is null && result.Bitmap is null)
            {
                return;
            }

            BitmapImage image;

            if (savedPath is { } path)
            {
                // Show the file that now exists rather than the pixels in memory, so an auto-saved
                // capture is the same kind of item as one the user saved from the editor.
                image = await ScreenshotService.LoadImageFromPathAsync(path);
                result.Bitmap?.Dispose();

                WeakReferenceMessenger.Default.Send(new ScreenshotCapturedMessage(image, path, null));
            }
            else
            {
                image = await ScreenshotService.ToBitmapImageAsync(result.Bitmap!);

                WeakReferenceMessenger.Default.Send(new ScreenshotCapturedMessage(image, null, result.Bitmap));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Capture failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
