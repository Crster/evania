using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Evania.Services
{
    public static class ScreenshotService
    {
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

        private enum SystemMetric
        {
            SM_XVIRTUALSCREEN = 76,
            SM_YVIRTUALSCREEN = 77,
            SM_CXVIRTUALSCREEN = 78,
            SM_CYVIRTUALSCREEN = 79,
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            public static extern int GetSystemMetrics(int nIndex);
        }
    }
}