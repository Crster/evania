using System;
using System.Drawing;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Evania.ViewModels
{
    public partial class ScreenshotItem : ObservableObject
    {
        // Non-null for items just captured in memory; null for items loaded from disk.
        public Bitmap? Bitmap { get; }

        public BitmapImage Image { get; }

        // Non-null for items loaded from disk; null for freshly captured items.
        public string? SourcePath { get; }

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

        partial void OnIsPointerOverChanged(bool value) => OnPropertyChanged(nameof(ShowSaveButton));

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

        public void Dispose() => Bitmap?.Dispose();
    }
}