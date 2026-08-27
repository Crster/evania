using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Evania.ViewModels
{
    /// <summary>
    /// One recording in the horizontal list. Unlike a screenshot item it is always a file on
    /// disk — the recorder writes straight into Videos\Captures — so the player and the list
    /// both address it by path.
    /// </summary>
    public partial class RecordingItem : ObservableObject
    {
        public string Path { get; }

        public string Name { get; }

        // Null until the shell can decode a poster frame for the file.
        [ObservableProperty]
        public partial BitmapImage? Thumbnail { get; set; }

        public bool HasNoThumbnail => Thumbnail is null;

        partial void OnThumbnailChanged(BitmapImage? value) => OnPropertyChanged(nameof(HasNoThumbnail));

        public RecordingItem(string path, string name, BitmapImage? thumbnail = null)
        {
            Path = path;
            Name = name;
            Thumbnail = thumbnail;
        }
    }
}
