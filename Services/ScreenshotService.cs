using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Evania.Windows;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Evania.Services
{
    /// <summary>
    /// Outcome of an interactive capture + edit session. When the user saved, only
    /// <see cref="SavedFilePath"/> is set and the image already lives on disk; otherwise
    /// <see cref="Bitmap"/> carries the image still waiting to be saved. Both are null when
    /// the session was cancelled. The caller owns whatever is non-null.
    /// </summary>
    public sealed record ScreenshotEditResult(string? SavedFilePath, Bitmap? Bitmap);

    public static class ScreenshotService
    {
        /// <summary>
        /// Captures a screenshot, shows the fullscreen EditScreenshotWindow for the user to
        /// annotate/copy/save/cancel, and returns the result.
        ///
        /// When <paramref name="mainWindow"/> is on screen it is hidden during capture and put back
        /// afterwards, so the shot does not contain the app. When it is already out of the way - the
        /// app is sitting in the tray, or was never shown - it is left exactly where it is: a capture
        /// triggered by the global shortcut must not be the thing that pops the main window open.
        /// </summary>
        public static async Task<ScreenshotEditResult> CaptureAndEditAsync(Microsoft.UI.Xaml.Window? mainWindow = null)
        {
            bool restoresWindow = mainWindow?.AppWindow.IsVisible ?? false;

            if (restoresWindow)
            {
                mainWindow!.AppWindow.Hide();

                await Task.Delay(150);
            }

            Bitmap bitmap = CaptureScreenshot();

            var editWindow = new EditScreenshotWindow(bitmap);

            var closeTcs = new TaskCompletionSource<bool>();
            editWindow.Closed += (s, e) => closeTcs.TrySetResult(true);

            editWindow.Activate();
            PromoteToForeground(editWindow);

            await closeTcs.Task;

            if (restoresWindow)
            {
                mainWindow!.AppWindow.Show();
            }

            if (editWindow.IsCancelled)
            {
                bitmap.Dispose();
                return new ScreenshotEditResult(null, null);
            }

            // A save wrote the edited image to disk — that file is the result, the capture
            // it was derived from is spent.
            if (editWindow.SavedFilePath is not null)
            {
                bitmap.Dispose();
                editWindow.EditedBitmap?.Dispose();
                return new ScreenshotEditResult(editWindow.SavedFilePath, null);
            }

            if (editWindow.EditedBitmap is { } edited)
            {
                bitmap.Dispose();
                return new ScreenshotEditResult(null, edited);
            }

            return new ScreenshotEditResult(null, bitmap);
        }

        /// <summary>
        /// Captures the entire virtual desktop (union of all displays) and returns it as a Bitmap.
        /// The caller is responsible for disposing and optionally saving the image.
        /// </summary>
        public static Bitmap CaptureScreenshot()
        {
            int left = GetSystemMetrics(SystemMetric.SM_XVIRTUALSCREEN);
            int top = GetSystemMetrics(SystemMetric.SM_YVIRTUALSCREEN);
            int width = GetSystemMetrics(SystemMetric.SM_CXVIRTUALSCREEN);
            int height = GetSystemMetrics(SystemMetric.SM_CYVIRTUALSCREEN);

            Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height));
            }

            return bitmap;
        }

        /// <summary>
        /// Converts a Bitmap to a BitmapImage for display in XAML.
        /// </summary>
        public static async Task<BitmapImage> ToBitmapImageAsync(Bitmap bitmap)
        {
            using MemoryStream stream = new();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;

            BitmapImage image = new();
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            return image;
        }

        /// <summary>
        /// Saves the given Bitmap as a PNG in the user's Pictures\Screenshots folder.
        /// Returns the saved file path.
        /// </summary>
        public static async Task<string> SaveScreenshotAsync(Bitmap bitmap)
        {
            StorageFolder folder = await GetScreenshotsFolderAsync();
            string fileName = $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
            StorageFile file = await folder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);

            using (MemoryStream stream = new())
            {
                bitmap.Save(stream, ImageFormat.Png);
                stream.Position = 0;
                await FileIO.WriteBytesAsync(file, stream.ToArray());
            }

            return file.Path;
        }

        /// <summary>
        /// Decodes an image file directly into a BitmapImage, avoiding System.Drawing
        /// and its requirement that the source stream outlive the image.
        /// </summary>
        public static async Task<BitmapImage> LoadImageAsync(StorageFile file)
        {
            using var stream = await file.OpenStreamForReadAsync();
            BitmapImage image = new();
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            return image;
        }

        /// <summary>
        /// Same as <see cref="LoadImageAsync(StorageFile)"/> for callers that only have a
        /// path and should not depend on the WinRT storage broker.
        /// </summary>
        public static async Task<BitmapImage> LoadImageFromPathAsync(string filePath)
        {
            using var fs = File.OpenRead(filePath);
            BitmapImage image = new();
            await image.SetSourceAsync(fs.AsRandomAccessStream());
            return image;
        }

        /// <summary>
        /// Composes the given bitmap onto a new screen-sized bitmap with the image centered.
        /// This ensures EditScreenshotWindow receives a bitmap that matches the fullscreen
        /// dimensions, so drawing/selection coordinates align correctly.
        /// </summary>
        /// <param name="contentBounds">Where the source image landed on the canvas, so the
        /// edited result can be trimmed back to it instead of saving the padding.</param>
        public static Bitmap ComposeOntoScreen(Bitmap source, out Rectangle contentBounds)
        {
            int screenWidth = GetSystemMetrics(SystemMetric.SM_CXVIRTUALSCREEN);
            int screenHeight = GetSystemMetrics(SystemMetric.SM_CYVIRTUALSCREEN);

            if (source.Width >= screenWidth && source.Height >= screenHeight)
            {
                contentBounds = new Rectangle(0, 0, source.Width, source.Height);
                return (Bitmap)source.Clone();
            }

            var canvas = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppArgb);
            int x = (screenWidth - source.Width) / 2;
            int y = (screenHeight - source.Height) / 2;
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.Black);
                g.DrawImage(source, x, y);
            }

            contentBounds = new Rectangle(x, y, source.Width, source.Height);
            contentBounds.Intersect(new Rectangle(0, 0, screenWidth, screenHeight));
            return canvas;
        }

        /// <summary>
        /// Loads a Bitmap from disk so it can be reused (e.g. for clipboard copy).
        /// </summary>
        public static Bitmap LoadBitmap(string filePath)
        {
            MemoryStream stream = new();
            using (FileStream fs = File.OpenRead(filePath))
            {
                fs.CopyTo(stream);
            }
            stream.Position = 0;
            return new Bitmap(stream);
        }

        /// <summary>
        /// Copies the given Bitmap to the Windows clipboard so the user can paste it
        /// directly into apps such as Paint. The bitmap is disposed by this method.
        /// </summary>
        public static void CopyBitmapToClipboard(Bitmap bitmap)
        {
            using MemoryStream stream = new();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;

            DataPackage dataPackage = new();
            dataPackage.SetData("image/png", stream.AsRandomAccessStream());
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
        }

        /// <summary>
        /// Copies plain text to the Windows clipboard so the user can paste it into any app.
        /// </summary>
        public static void CopyTextToClipboard(string text)
        {
            DataPackage dataPackage = new();
            dataPackage.SetText(text);
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
        }

        /// <summary>
        /// Copies an existing screenshot file into the Pictures\Screenshots folder
        /// with a new timestamped name. Used to "save" items that were loaded from
        /// disk (which have no in-memory Bitmap).
        /// </summary>
        public static async Task<string> SaveScreenshotFileAsync(string sourcePath)
        {
            StorageFolder folder = await GetScreenshotsFolderAsync();
            string fileName = $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
            StorageFile target = await folder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
            StorageFile source = await StorageFile.GetFileFromPathAsync(sourcePath);
            await source.CopyAndReplaceAsync(target);
            return target.Path;
        }

        public static Task<StorageFolder> GetScreenshotsFolderAsync()
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string path = Path.Combine(pictures, "Screenshots");
            Directory.CreateDirectory(path);
            return StorageFolder.GetFolderFromPathAsync(path).AsTask();
        }

        private static int GetSystemMetrics(SystemMetric metric)
        {
            return NativeMethods.GetSystemMetrics((int)metric);
        }

        private static void PromoteToForeground(Microsoft.UI.Xaml.Window window)
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOWNORMAL);
            NativeMethods.SetWindowPos(
                hWnd,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            NativeMethods.SetWindowPos(
                hWnd,
                NativeMethods.HWND_NOTOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.SetForegroundWindow(hWnd);
        }

        private enum SystemMetric
        {
            SM_XVIRTUALSCREEN = 76,
            SM_YVIRTUALSCREEN = 77,
            SM_CXVIRTUALSCREEN = 78,
            SM_CYVIRTUALSCREEN = 79,
        }

        private static class NativeMethods
        {
            public const int SW_SHOWNORMAL = 1;
            public const uint SWP_NOSIZE = 0x0001;
            public const uint SWP_NOMOVE = 0x0002;
            public const uint SWP_NOACTIVATE = 0x0010;
            public const uint SWP_SHOWWINDOW = 0x0040;
            public static readonly IntPtr HWND_TOPMOST = new(-1);
            public static readonly IntPtr HWND_NOTOPMOST = new(-2);

            [DllImport("user32.dll")]
            public static extern int GetSystemMetrics(int nIndex);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetForegroundWindow(IntPtr hWnd);
        }
    }
}
