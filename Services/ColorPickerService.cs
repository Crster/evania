using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Evania.Services
{
    /// <summary>
    /// Reads the dominant colour of an image region, including its opacity. "Dominant" means the
    /// most frequent colour family, not the per-channel average: averaging a black-and-white region
    /// yields a grey that is present nowhere in the image, so pixels are quantised into buckets and
    /// the winning bucket's real pixels are averaged instead. The reported alpha is that same
    /// average over the winning bucket, so a colour drawn at half opacity reports as half opaque.
    /// </summary>
    public static class ColorPickerService
    {
        /// <summary>
        /// Returns the dominant colour of the bitmap as an uppercase 8-digit hex string
        /// (<c>#RRGGBBAA</c>, alpha last, CSS/Figma order), or null when the bitmap has no opaque
        /// pixels. A fully opaque screenshot therefore ends in <c>FF</c>.
        /// </summary>
        public static string? GetDominantColorHex(Bitmap bitmap)
        {
            if (!TryGetDominantColor(bitmap, out Color color))
            {
                return null;
            }

            return $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";
        }

        private static bool TryGetDominantColor(Bitmap bitmap, out Color color)
        {
            color = Color.Empty;
            if (bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return false;
            }

            Rectangle bounds = new(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int bytes = Math.Abs(data.Stride) * data.Height;
                byte[] pixels = new byte[bytes];
                Marshal.Copy(data.Scan0, pixels, 0, bytes);

                // 4 bits per channel gives 16 levels (4096 buckets): coarse enough to absorb
                // anti-aliasing and compression noise, fine enough to keep distinct colours apart.
                const int levels = 16;
                int bucketCount = levels * levels * levels;
                int[] counts = new int[bucketCount];
                long[] sumR = new long[bucketCount];
                long[] sumG = new long[bucketCount];
                long[] sumB = new long[bucketCount];
                long[] sumA = new long[bucketCount];

                int stride = data.Stride;
                for (int y = 0; y < bitmap.Height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        // Format32bppArgb stores pixels in memory as B, G, R, A.
                        int index = row + (x * 4);
                        byte alpha = pixels[index + 3];
                        if (alpha == 0)
                        {
                            continue;
                        }

                        byte blue = pixels[index];
                        byte green = pixels[index + 1];
                        byte red = pixels[index + 2];

                        int bucket = ((red >> 4) << 8) | ((green >> 4) << 4) | (blue >> 4);
                        counts[bucket]++;
                        sumR[bucket] += red;
                        sumG[bucket] += green;
                        sumB[bucket] += blue;
                        sumA[bucket] += alpha;
                    }
                }

                int best = 0;
                for (int i = 1; i < bucketCount; i++)
                {
                    if (counts[i] > counts[best])
                    {
                        best = i;
                    }
                }

                if (counts[best] == 0)
                {
                    return false;
                }

                int n = counts[best];
                color = Color.FromArgb(
                    (int)(sumA[best] / n),
                    (int)(sumR[best] / n),
                    (int)(sumG[best] / n),
                    (int)(sumB[best] / n));
                return true;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
    }
}
