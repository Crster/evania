using System;
using System.Drawing;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Evania.ViewModels
{
    public partial class ScreenshotItem : ObservableObject
    {
        // Non-null for items just captured in memory; null for items loaded from disk.
        public Bitmap? Bitmap { get; private set; }

        [ObservableProperty]
        public partial BitmapImage Image { get; set; }

        // Non-null for items loaded from disk; null for freshly captured items.
        public string? SourcePath { get; private set; }

        public bool HasBitmapSource => Bitmap is not null;

        [ObservableProperty]
        public partial bool IsSaved { get; set; }

        [ObservableProperty]
        public partial bool IsPointerOver { get; set; }

        /// <summary>
        /// True when the Save button should be shown: this item has not been saved yet
        /// and the pointer is currently over it.
        /// </summary>
        public bool ShowSaveButton => !IsSaved && IsPointerOver;

        public bool ShowEditButton => IsPointerOver;

        partial void OnIsPointerOverChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowSaveButton));
            OnPropertyChanged(nameof(ShowEditButton));
        }

        partial void OnIsSavedChanged(bool value) => OnPropertyChanged(nameof(ShowSaveButton));

        public ScreenshotItem(BitmapImage image, string? sourcePath = null)
        {
            Image = image;
            SourcePath = sourcePath;
            IsSaved = true;
        }

        public ScreenshotItem(Bitmap bitmap, BitmapImage image)
        {
            Bitmap = bitmap;
            Image = image;
        }

        /// <summary>
        /// Points this item at <paramref name="savedPath"/> — the file the edited image was
        /// written to — and shows that image instead of the in-memory one. Used when an edit
        /// session saves, so the list reflects what is on disk and stops offering a save button.
        /// </summary>
        public void AdoptSavedFile(BitmapImage image, string savedPath)
        {
            Bitmap?.Dispose();
            Bitmap = null;
            Image = image;
            SourcePath = savedPath;
            IsSaved = true;
            OnPropertyChanged(nameof(HasBitmapSource));
        }

        public void Dispose() => Bitmap?.Dispose();
    }
}