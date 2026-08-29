using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evania.Services;
using Evania.Windows;
using Microsoft.UI.Windowing;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Evania.ViewModels
{
    public partial class ScreenRecordPageViewModel : ObservableObject
    {
        public ObservableCollection<RecordingItem> Recordings { get; } = new();

        /// <summary>Owned by the page's <c>MediaPlayerElement</c>; the source follows the selection.</summary>
        public MediaPlayer Player { get; } = new();

        [ObservableProperty]
        public partial bool IsRecording { get; set; }

        [ObservableProperty]
        public partial bool IsEmpty { get; set; }

        [ObservableProperty]
        public partial string ErrorMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial RecordingItem? SelectedRecording { get; set; }

        public bool CanRecord => !IsRecording;

        public bool HasError => ErrorMessage.Length > 0;

        public bool HasNoSelection => SelectedRecording is null;

        partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(CanRecord));

        partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

        partial void OnSelectedRecordingChanged(RecordingItem? value)
        {
            // Loaded, not started: picking a recording from the strip should not begin playback
            // before the user wants it.
            Player.Source = value is null ? null : MediaSource.CreateFromUri(new Uri(value.Path));

            OnPropertyChanged(nameof(HasNoSelection));
        }

        [RelayCommand]
        private async Task LoadRecordingsAsync()
        {
            var items = new List<RecordingItem>();
            foreach (string path in await RecordingService.ListRecordingPathsAsync())
            {
                items.Add(new RecordingItem(path, Path.GetFileName(path), await RecordingService.LoadThumbnailAsync(path)));
            }

            RecordingItem? alreadyShown = SelectedRecording;

            Recordings.Clear();
            foreach (RecordingItem item in items)
            {
                Recordings.Add(item);
            }

            IsEmpty = Recordings.Count == 0;

            // Prefer whatever is already loaded in the player so a refresh does not jump the
            // selection back to the newest file.
            if (alreadyShown is null || !File.Exists(alreadyShown.Path))
            {
                SelectedRecording = Recordings.FirstOrDefault();
            }
        }

        [RelayCommand]
        private async Task RecordAsync()
        {
            if (IsRecording)
            {
                return;
            }

            ErrorMessage = string.Empty;

            RecordingController controller;
            try
            {
                controller = new RecordingController();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Recording is unavailable: {ex.Message}";
                return;
            }

            string outputPath = await RecordingService.NextOutputPathAsync();
            var toolbar = new RecordToolbarWindow(controller, outputPath);

            var closeTcs = new TaskCompletionSource<bool>();
            toolbar.Closed += (s, e) => closeTcs.TrySetResult(true);

            IsRecording = true;
            SetMainWindowMinimized(true);
            toolbar.Activate();

            await closeTcs.Task;

            string? savedPath = toolbar.SavedFilePath;
            controller.Dispose();
            IsRecording = false;

            SetMainWindowMinimized(false);

            if (savedPath is null)
            {
                // Stopping while the session was still held is a cancel, not a failure: nothing was
                // captured and the empty file the recorder opened has already been dropped.
                if (controller.CancelledEmptySession)
                {
                    return;
                }

                ErrorMessage = controller.Failure
                    ?? "The recording did not finish. Close any other app that is capturing the screen and try again.";
                return;
            }

            await LoadRecordingsAsync();

            if (FindRecording(savedPath) is { } recorded)
            {
                SelectedRecording = recorded;

                // The shell has usually not decoded a poster for a file that is this new.
                if (recorded.Thumbnail is null)
                {
                    await Task.Delay(1500);
                    recorded.Thumbnail = await RecordingService.LoadThumbnailAsync(recorded.Path);
                }
            }
        }

        [RelayCommand]
        private void ClearError() => ErrorMessage = string.Empty;

        // Minimize/Restore are presenter operations, not AppWindow ones.
        private static void SetMainWindowMinimized(bool minimized)
        {
            MainWindow? main = App.MainWindow;
            if (main is null || main.AppWindow.Presenter is not OverlappedPresenter presenter)
            {
                return;
            }

            if (minimized)
            {
                presenter.Minimize();
            }
            else
            {
                presenter.Restore();
                main.Activate();
            }
        }

        private RecordingItem? FindRecording(string path) =>
            Recordings.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
    }
}
