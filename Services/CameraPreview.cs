using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace Evania.Services
{
    /// <summary>
    /// The live webcam view shown in the recording toolbar.
    ///
    /// A camera can only be opened by one client at a time, so whoever previews it must also be
    /// what feeds it into the recording: the toolbar is captured along with the desktop, and the
    /// camera pixels come from here. That is why this is not a ScreenRecorderLib source — a second
    /// recorder source is tiled next to the monitor and widens the frame, which is what painted the
    /// black borders.
    ///
    /// Frames are pulled on the CPU and copied straight into the WriteableBitmap's pixel buffer.
    /// The image is not mirrored here — the toolbar flips it with a render transform, so what the
    /// user sees is exactly what gets recorded.
    /// </summary>
    public sealed class CameraPreview : IDisposable
    {
        private MediaCapture? _capture;
        private MediaFrameReader? _reader;

        // The reader raises frames on a worker thread, but a WriteableBitmap belongs to the UI
        // thread that owns the XAML tree — creating one off that thread is RPC_E_WRONG_THREAD.
        private readonly DispatcherQueue? _queue = DispatcherQueue.GetForCurrentThread();
        private int _frameInFlight;

        /// <summary>The camera image to display, or null until the first frame arrives.</summary>
        public WriteableBitmap? Bitmap { get; private set; }

        /// <summary>Raised when the frame size changed and <see cref="Bitmap"/> is a new object.</summary>
        public event Action? BitmapChanged;

        /// <summary>Raised with a copied BGRA frame on the camera reader thread.</summary>
        public event Action<byte[], int, int>? FrameReceived;

        public bool IsRunning => _reader is not null;

        /// <summary>Null when the camera is streaming, otherwise the reason it could not start.</summary>
        public async Task<string?> StartAsync()
        {
            if (IsRunning)
            {
                return null;
            }

            MediaCapture capture = new();
            MediaFrameReader? reader = null;
            try
            {
                // FrameSources is only populated when MediaCapture is initialized against a source
                // group, so pick the group that owns a colour video record stream.
                MediaFrameSourceGroup? group = (await MediaFrameSourceGroup.FindAllAsync())
                    .FirstOrDefault(g => g.SourceInfos.Any(VideoRecordSource));
                if (group is null)
                {
                    return "No camera was found.";
                }

                // MemoryPreference.Cpu is what makes VideoMediaFrame.SoftwareBitmap available
                // instead of a GPU-only surface we could not read back here.
                MediaCaptureInitializationSettings settings = new()
                {
                    SourceGroup = group,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                };
                await capture.InitializeAsync(settings);

                MediaFrameSource? source =
                    capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color) ??
                    capture.FrameSources.Values.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord) ??
                    capture.FrameSources.Values.FirstOrDefault();
                if (source is null)
                {
                    return "The camera opened but has no video stream.";
                }

                reader = await capture.CreateFrameReaderAsync(source);
                reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                reader.FrameArrived += Reader_FrameArrived;
                await reader.StartAsync();
            }
            catch (Exception ex)
            {
                if (reader is not null)
                {
                    reader.FrameArrived -= Reader_FrameArrived;
                    reader.Dispose();
                }
                capture.Dispose();
                return ex.Message;
            }

            _capture = capture;
            _reader = reader;
            return null;
        }

        private static bool VideoRecordSource(MediaFrameSourceInfo info) =>
            info.MediaStreamType == MediaStreamType.VideoRecord &&
            info.SourceKind == MediaFrameSourceKind.Color;

        public async Task StopAsync()
        {
            MediaFrameReader? reader = _reader;
            MediaCapture? capture = _capture;
            _reader = null;
            _capture = null;

            if (reader is not null)
            {
                reader.FrameArrived -= Reader_FrameArrived;
                await reader.StopAsync();
                reader.Dispose();
            }

            capture?.Dispose();
        }

        private void Reader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            SoftwareBitmap? bitmap;
            try
            {
                bitmap = ReadFrame(sender);
            }
            catch (Exception)
            {
                return;
            }

            if (bitmap is null)
            {
                return;
            }

            try
            {
                if (FrameReceived is not null)
                {
                    byte[] pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
                    global::Windows.Storage.Streams.Buffer buffer = new((uint)pixels.Length);
                    bitmap.CopyToBuffer(buffer);
                    using DataReader dataReader = DataReader.FromBuffer(buffer);
                    dataReader.ReadBytes(pixels);
                    FrameReceived(pixels, bitmap.PixelWidth, bitmap.PixelHeight);
                }
            }
            catch (Exception)
            {
            }

            DispatcherQueue? queue = _queue;
            if (queue is null)
            {
                bitmap.Dispose();
                return;
            }

            // Drop the frame if the UI has not drawn the previous one yet: a preview should lag,
            // not build a queue of bitmaps.
            if (Interlocked.Exchange(ref _frameInFlight, 1) == 1)
            {
                bitmap.Dispose();
                return;
            }

            if (!queue.TryEnqueue(() => Present(bitmap)))
            {
                Interlocked.Exchange(ref _frameInFlight, 0);
                bitmap.Dispose();
            }
        }

        // Runs on the reader's thread: only format conversion, nothing XAML. The caller owns the
        // result — once accessed, the bitmap outlives the MediaFrameReference it came from.
        private static SoftwareBitmap? ReadFrame(MediaFrameReader sender)
        {
            using MediaFrameReference? reference = sender.TryAcquireLatestFrame();
            SoftwareBitmap? frame = reference?.VideoMediaFrame?.SoftwareBitmap;
            if (frame is null)
            {
                return null;
            }

            // The camera hands over whatever native format it likes; the pixel buffer wants
            // straight BGRA.
            return frame.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
                   frame.BitmapAlphaMode == BitmapAlphaMode.Ignore
                ? frame
                : SoftwareBitmap.Convert(frame, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        }

        private void Present(SoftwareBitmap bitmap)
        {
            try
            {
                EnsureSize(bitmap.PixelWidth, bitmap.PixelHeight);
                bitmap.CopyToBuffer(Bitmap!.PixelBuffer);
                Bitmap.Invalidate();
            }
            finally
            {
                bitmap.Dispose();
                Interlocked.Exchange(ref _frameInFlight, 0);
            }
        }

        private void EnsureSize(int width, int height)
        {
            if (Bitmap is not null && Bitmap.PixelWidth == width && Bitmap.PixelHeight == height)
            {
                return;
            }

            Bitmap = new WriteableBitmap(width, height);
            BitmapChanged?.Invoke();
        }

        // Synchronous teardown for window close: dropping the reader stops delivery immediately,
        // and blocking on StopAsync here would deadlock the UI thread it was started from.
        public void Dispose()
        {
            if (_reader is { } reader)
            {
                reader.FrameArrived -= Reader_FrameArrived;
                reader.Dispose();
            }

            _reader = null;
            _capture?.Dispose();
            _capture = null;
        }
    }
}
