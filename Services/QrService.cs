using System.Collections.Generic;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace Evania.Services;

/// <summary>
/// Renders QR codes into <see cref="WriteableBitmap"/>s with ZXing. Error correction is set to
/// level H because the OTP tab overlays a live-code badge in the center of the symbol, which a
/// scanner reads as damage - the extra redundancy is what keeps a patched code scannable. Must be
/// called on the UI thread; the WriteableBitmap belongs to it.
/// </summary>
public static class QrService
{
    public static WriteableBitmap Render(string content, int size)
    {
        Dictionary<EncodeHintType, object> hints = new()
        {
            [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.H,
            [EncodeHintType.MARGIN] = 2,
        };

        BitMatrix matrix = new QRCodeWriter().encode(content, BarcodeFormat.QR_CODE, size, size, hints);

        // One module to one pixel, black on white, as B8G8R8A8.
        byte[] pixels = new byte[matrix.Width * matrix.Height * 4];
        int offset = 0;
        for (int y = 0; y < matrix.Height; y++)
        {
            for (int x = 0; x < matrix.Width; x++)
            {
                byte channel = matrix[x, y] ? (byte)0 : (byte)255;
                pixels[offset++] = channel;
                pixels[offset++] = channel;
                pixels[offset++] = channel;
                pixels[offset++] = 255;
            }
        }

        WriteableBitmap bitmap = new(matrix.Width, matrix.Height);
        using SoftwareBitmap software = SoftwareBitmap.CreateCopyFromBuffer(
            CryptographicBuffer.CreateFromByteArray(pixels),
            BitmapPixelFormat.Bgra8,
            matrix.Width,
            matrix.Height);
        software.CopyToBuffer(bitmap.PixelBuffer);
        bitmap.Invalidate();

        return bitmap;
    }
}
