using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evania.Services;
using Evania.Windows;
using Windows.Storage;

namespace Evania.ViewModels
{
    public partial class ScreenshotPageViewModel : ObservableObject
    {
        public ObservableCollection<ScreenshotItem> Screenshots { get; } = new();

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        [ObservableProperty]
        public partial bool HasScreenshots { get; set; }

        [ObservableProperty]
        public partial bool IsEmpty { get; set; }

        [ObservableProperty]
        public partial string LastSavedPath { get; set; } = string.Empty;

        [RelayCommand]
        private async Task TakeScreenshotAsync()
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            try
            {
                MainWindow? mainWindow = App.MainWindow;
                mainWindow?.AppWindow.Hide();

                await Task.Delay(150);

                Bitmap bitmap = ScreenshotService.CaptureScreenshot();

                mainWindow?.AppWindow.Show();

                var image = await ScreenshotService.ToBitmapImageAsync(bitmap);
                var item = new ScreenshotItem(bitmap, image);
                Screenshots.Insert(0, item);

                HasScreenshots = true;
                IsEmpty = false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task SaveScreenshotAsync(ScreenshotItem? item)
        {
            if (item is null)
            {
                return;
            }

            string path = item.Bitmap is not null
                ? await ScreenshotService.SaveScreenshotAsync(item.Bitmap)
                : await ScreenshotService.SaveScreenshotFileAsync(item.SourcePath!);

            item.IsSaved = true;
            LastSavedPath = path;
        }

        [RelayCommand]
        private void CopyScreenshot(ScreenshotItem? item)
        {
            if (item is null)
            {
                return;
            }

            if (item.Bitmap is not null)
            {
                ScreenshotService.CopyBitmapToClipboard(item.Bitmap);
                return;
            }

            try
            {
                using var bitmap = ScreenshotService.LoadBitmap(item.SourcePath!);
                ScreenshotService.CopyBitmapToClipboard(bitmap);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to copy screenshot: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task LoadScreenshotsAsync()
        {
            StorageFolder folder = await ScreenshotService.GetScreenshotsFolderAsync();

            var files = new List<StorageFile>();
            foreach (StorageFile file in await folder.GetFilesAsync())
            {
                string ext = file.FileType.ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
                {
                    files.Add(file);
                }
            }

            files.Sort((a, b) => b.DateCreated.CompareTo(a.DateCreated));

            Screenshots.Clear();

            foreach (StorageFile file in files)
            {
                var image = await ScreenshotService.LoadImageAsync(file);
                Screenshots.Add(new ScreenshotItem(image, sourcePath: file.Path));
            }

            HasScreenshots = Screenshots.Count > 0;
            IsEmpty = !HasScreenshots;
        }
    }
}