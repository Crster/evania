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
                ScreenshotEditResult result = await ScreenshotService.CaptureAndEditAsync(App.MainWindow);

                if (result.SavedFilePath is { } savedPath)
                {
                    // The edit window already wrote the annotated image; show that, not the
                    // raw capture, and treat it as saved.
                    var savedImage = await ScreenshotService.LoadImageFromPathAsync(savedPath);
                    Screenshots.Insert(0, new ScreenshotItem(savedImage, sourcePath: savedPath));
                    LastSavedPath = savedPath;
                }
                else if (result.Bitmap is { } bitmap)
                {
                    var image = await ScreenshotService.ToBitmapImageAsync(bitmap);
                    var item = new ScreenshotItem(bitmap, image);
                    Screenshots.Insert(0, item);
                }
                else
                {
                    return;
                }

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
        private async Task EditScreenshot(ScreenshotItem? item)
        {
            if (item is null)
            {
                return;
            }

            try
            {
                // A disk item's bitmap is ours to release once composited; a freshly captured
                // item keeps its own, so it is only ever read from here.
                Bitmap? loaded = item.Bitmap is null && item.SourcePath is not null
                    ? ScreenshotService.LoadBitmap(item.SourcePath)
                    : null;

                Bitmap? source = loaded ?? item.Bitmap;
                if (source is null)
                {
                    return;
                }

                Bitmap canvas = ScreenshotService.ComposeOntoScreen(source, out Rectangle contentBounds);
                loaded?.Dispose();

                var editWindow = new EditScreenshotWindow(canvas, contentBounds);

                var closeTcs = new TaskCompletionSource<bool>();
                editWindow.Closed += (s, e) => closeTcs.TrySetResult(true);

                editWindow.Activate();

                await closeTcs.Task;

                canvas.Dispose();
                editWindow.EditedBitmap?.Dispose();

                if (editWindow.IsCancelled || editWindow.SavedFilePath is not { } savedPath)
                {
                    return;
                }

                // The annotated image is on disk now: point the item at it so the list shows
                // what was saved and stops offering a save for it.
                var image = await ScreenshotService.LoadImageFromPathAsync(savedPath);
                item.AdoptSavedFile(image, savedPath);
                LastSavedPath = savedPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open edit window: {ex.Message}");
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