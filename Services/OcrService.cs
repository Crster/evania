using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Evania.Services
{
    /// <summary>
    /// On-device text recognition on top of the inbox Windows OCR engine. Recognition needs an
    /// OCR language pack for the script being read (Settings &gt; Time &amp; language &gt; language
    /// &gt; Language options &gt; OCR), so accuracy for non-Latin text depends on what the machine
    /// has installed.
    /// </summary>
    public static class OcrService
    {
        /// <summary>
        /// Recognizes the text in the image, returning one recognized line per line of output.
        /// Returns null when no OCR engine can be created on this machine.
        /// </summary>
        public static async Task<string?> RecognizeTextAsync(Bitmap bitmap)
        {
            OcrEngine? engine = TryCreateEngine();
            if (engine is null)
            {
                return null;
            }

            using SoftwareBitmap? softwareBitmap = await DecodeForOcrAsync(bitmap);
            if (softwareBitmap is null)
            {
                return null;
            }

            OcrResult result = await engine.RecognizeAsync(softwareBitmap);

            // OcrResult.Text joins the lines with spaces, which glues unrelated text blocks
            // into one run-on line.
            return string.Join("\n", result.Lines.Select(line => line.Text));
        }

        private static OcrEngine? TryCreateEngine()
        {
            OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is not null)
            {
                return engine;
            }

            // The user's profile languages may have no OCR pack while some other installed
            // language does; any engine beats none.
            foreach (Language language in OcrEngine.AvailableRecognizerLanguages)
            {
                if (OcrEngine.TryCreateFromLanguage(language) is { } fallback)
                {
                    return fallback;
                }
            }

            return null;
        }

        private static async Task<SoftwareBitmap?> DecodeForOcrAsync(Bitmap bitmap)
        {
            int maxDimension = (int)OcrEngine.MaxImageDimension;
            int longest = Math.Max(bitmap.Width, bitmap.Height);
            double scale = maxDimension > 0 && longest > maxDimension ? (double)maxDimension / longest : 1.0;

            using MemoryStream encoded = new();
            if (scale < 1.0)
            {
                // OcrEngine rejects images past MaxImageDimension, which a full-screen
                // selection on a high-resolution display can exceed.
                using Bitmap scaled = ScaleDown(bitmap, scale);
                scaled.Save(encoded, ImageFormat.Png);
            }
            else
            {
                bitmap.Save(encoded, ImageFormat.Png);
            }
            encoded.Position = 0;

            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(encoded.AsRandomAccessStream());
            using SoftwareBitmap decoded = await decoder.GetSoftwareBitmapAsync();

            // OcrEngine only accepts Bgra8 or Gray8, and PNG can decode into other formats.
            return SoftwareBitmap.Convert(decoded, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        private static Bitmap ScaleDown(Bitmap source, double scale)
        {
            int width = Math.Max(1, (int)(source.Width * scale));
            int height = Math.Max(1, (int)(source.Height * scale));

            Bitmap target = new(width, height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(target))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            }

            return target;
        }
    }
}
