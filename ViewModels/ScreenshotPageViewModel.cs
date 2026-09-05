using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Evania.Services;
using Evania.Windows;
using Windows.Storage;

namespace Evania.ViewModels
{
    public partial class ScreenshotPageViewModel : ObservableObject
    {
        public ObservableCollection<ScreenshotItem> Screenshots { get; } = new();

        public ObservableCollection<ScreenshotItem> FilteredScreenshots { get; } = new();

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        [ObservableProperty]
        public partial bool HasScreenshots { get; set; }

        [ObservableProperty]
        public partial bool IsEmpty { get; set; }

        [ObservableProperty]
        public partial string LastSavedPath { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Filter { get; set; } = string.Empty;

        partial void OnFilterChanged(string value) => ApplyFilter();

        public ScreenshotPageViewModel()
        {
            // Captures reach the list through the messenger rather than the return value of the
            // button below, because the global shortcut and the tray menu trigger the very same flow
            // with no page involved.
            WeakReferenceMessenger.Default.Register<ScreenshotCapturedMessage>(this, (_, message) => Add(message));
        }

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
                await CaptureFlowService.RunAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void Add(ScreenshotCapturedMessage message)
        {
            var item = message.Bitmap is { } bitmap
                ? new ScreenshotItem(bitmap, message.Image)
                : new ScreenshotItem(message.Image, sourcePath: message.SavedPath);

            Screenshots.Insert(0, item);
            ApplyFilter();

            if (message.SavedPath is { } savedPath)
            {
                LastSavedPath = savedPath;
            }

            HasScreenshots = true;
            IsEmpty = false;
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

            ApplyFilter();

            HasScreenshots = Screenshots.Count > 0;
            IsEmpty = !HasScreenshots;
        }

        private void ApplyFilter()
        {
            FilteredScreenshots.Clear();

            foreach (ScreenshotItem item in Screenshots.Where(item =>
                         item.SourcePath is null ||
                         Path.GetFileName(item.SourcePath).Contains(Filter, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredScreenshots.Add(item);
            }
        }
    }
}