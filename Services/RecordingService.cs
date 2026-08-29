using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Evania.Services
{
    /// <summary>
    /// File-side helpers for screen recordings: where they live, how they are named,
    /// and how they are listed/postered. The capture itself is driven by
    /// <see cref="RecordingController"/>.
    /// </summary>
    public static class RecordingService
    {
        private static readonly string[] VideoExtensions = { ".mp4" };

        public static async Task<StorageFolder> GetCapturesFolderAsync()
        {
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            string path = Path.Combine(videos, "Captures");
            Directory.CreateDirectory(path);
            return await StorageFolder.GetFolderFromPathAsync(path).AsTask();
        }

        /// <summary>
        /// A not-yet-existing path in the Captures folder for a recording started now. The
        /// recorder writes the file itself, so nothing is created here.
        /// </summary>
        public static async Task<string> NextOutputPathAsync()
        {
            StorageFolder folder = await GetCapturesFolderAsync();
            string stem = $"Recording_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";

            string path = Path.Combine(folder.Path, stem + ".mp4");
            for (int n = 1; File.Exists(path); n++)
            {
                path = Path.Combine(folder.Path, $"{stem} ({n}).mp4");
            }

            return path;
        }

        public static async Task<IReadOnlyList<string>> ListRecordingPathsAsync()
        {
            StorageFolder folder = await GetCapturesFolderAsync();

            var files = new List<StorageFile>();
            foreach (StorageFile file in await folder.GetFilesAsync())
            {
                if (Array.IndexOf(VideoExtensions, file.FileType.ToLowerInvariant()) >= 0)
                {
                    files.Add(file);
                }
            }

            files.Sort((a, b) => b.DateCreated.CompareTo(a.DateCreated));
            return files.ConvertAll(file => file.Path);
        }

        /// <summary>
        /// The shell's poster frame for a video, or null when there is nothing to show yet
        /// (a file that was written moments ago is usually not decodable by the shell yet).
        ///
        /// SingleItem rather than VideosView: VideosView makes the shell grab a frame from
        /// the file, but OneDrive placeholders in a synced folder (reparse-point files)
        /// return null there, so recordings lose their posters once OneDrive picks them up.
        /// SingleItem falls back to the shell's stored/cloud thumbnail, which is a real
        /// JPEG poster for these files. Plain local videos still return their frame.
        /// </summary>
        public static async Task<BitmapImage?> LoadThumbnailAsync(string filePath)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
                using StorageItemThumbnail? thumbnail =
                    await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 260, ThumbnailOptions.ResizeThumbnail).AsTask();

                if (thumbnail is null || thumbnail.Size == 0)
                {
                    return null;
                }

                // The provider hands back a stream it has already walked, and a BitmapImage given
                // anything other than a fresh stream sitting at zero yields a blank card without
                // any error to catch — so copy the bytes out and rewind before decoding.
                using InMemoryRandomAccessStream poster = new();
                await RandomAccessStream.CopyAsync(thumbnail.GetInputStreamAt(0), poster.GetOutputStreamAt(0));
                poster.Seek(0);

                BitmapImage image = new();
                await image.SetSourceAsync(poster);
                return image;
            }
            catch (Exception ex)
            {
                // The shell throws rather than returning nothing for a video it cannot decode
                // yet, which is the normal state for a file recorded seconds ago.
                System.Diagnostics.Debug.WriteLine($"No poster frame for {filePath}: {ex.Message}");
                return null;
            }
        }
    }
}
