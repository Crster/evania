using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Streams;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// Asynchronous image loading for <see cref="MarkdownEditor"/>.
/// </summary>
/// <remarks>
/// The layout only reserves a fixed box for an image (<see cref="BlockRenderers.BuildImage"/>); the
/// bytes arrive later and out of order. The control owns a target-keyed bitmap cache that survives
/// layout rebuilds - <see cref="ApplyCachedImages"/> pushes it into the freshly built blocks, and
/// <see cref="RequestMissingImages"/> kicks off a load for any target it has never seen. A null value
/// is cached for a target that failed to resolve, so a broken or non-image link is not retried on
/// every keystroke. Loads begin from the layout pass on the UI thread, so their continuations - the
/// decode that touches the device included - resume there too.
/// </remarks>
public sealed partial class MarkdownEditor
{
    private readonly Dictionary<string, CanvasBitmap?> images = new(StringComparer.Ordinal);
    private readonly HashSet<string> imageRequests = new(StringComparer.Ordinal);

    /// <summary>
    /// Bumped whenever the cache is dropped. A load that finished across a device recreation is
    /// discarded rather than cached against the wrong device.
    /// </summary>
    private int imageGeneration;

    /// <summary>
    /// Maps an image target (the part inside <c>![alt](<b>target</b>)</c>) to its raw bytes, or null
    /// when the target is not something the host can load. The control never interprets the target -
    /// it hands over whatever the document held and draws what comes back - so attachment URIs, paths
    /// and URLs are all the host's to resolve. Left null, images simply keep their placeholder.
    /// </summary>
    public Func<string, Task<byte[]?>>? ImageResolver { get; set; }

    /// <summary>
    /// Releases every cached bitmap. Called when the Win2D device is recreated (the bitmaps belong to
    /// the old device) and when the control is disposed.
    /// </summary>
    private void ClearImageCache()
    {
        foreach (CanvasBitmap? bitmap in images.Values)
        {
            bitmap?.Dispose();
        }

        images.Clear();
        imageRequests.Clear();
        imageGeneration++;
    }

    /// <summary>
    /// Points every image block at its cached bitmap, if one exists. Called after each layout rebuild,
    /// because building blocks is synchronous and cannot wait for a load that already finished.
    /// </summary>
    private void ApplyCachedImages()
    {
        if (images.Count == 0)
        {
            return;
        }

        foreach (BlockLayout block in engine.Blocks)
        {
            if (block.Block.Kind == MarkdownBlockKind.Image
                && block.Block.Target is string target
                && images.TryGetValue(target, out CanvasBitmap? cached))
            {
                block.Image = cached;
            }
        }
    }

    /// <summary>
    /// Starts a load for each image target that is neither cached nor already in flight.
    /// </summary>
    private void RequestMissingImages()
    {
        if (ImageResolver is null)
        {
            return;
        }

        foreach (BlockLayout block in engine.Blocks)
        {
            if (block.Block.Kind == MarkdownBlockKind.Image
                && block.Block.Target is string target
                && !images.ContainsKey(target)
                && imageRequests.Add(target))
            {
                _ = LoadImageAsync(target);
            }
        }
    }

    /// <summary>
    /// Resolves one target to a bitmap. The byte fetch runs off the UI thread; the decode and the
    /// repaint happen once it resumes back here, because a device texture can only be built there.
    /// </summary>
    private async Task LoadImageAsync(string target)
    {
        int generation = imageGeneration;
        Func<string, Task<byte[]?>>? resolver = ImageResolver;
        if (resolver is null)
        {
            imageRequests.Remove(target);
            return;
        }

        byte[]? bytes = null;
        try
        {
            bytes = await resolver(target);
        }
        catch (Exception)
        {
        }

        CanvasBitmap? bitmap = null;
        if (bytes is { Length: > 0 })
        {
            try
            {
                bitmap = await DecodeAsync(bytes);
            }
            catch (Exception)
            {
                // A truncated or non-image attachment throws here; treat it like an unresolvable
                // target so the block keeps its placeholder instead of failing the document's layout.
            }
        }

        // A device recreation (or disposal) mid-flight leaves this texture on the wrong device.
        if (disposed || generation != imageGeneration)
        {
            bitmap?.Dispose();
            return;
        }

        imageRequests.Remove(target);
        images[target] = bitmap;
        ApplyCachedImages();

        // The box was reserved at the maximum size while the bytes were in flight; rebuild so it hugs
        // the picture that actually loaded, otherwise a small attachment would sit in a column of
        // placeholder with the caret stranded far from its edge.
        engine.Invalidate();
        UpdateEditorLayout();
        canvas.Invalidate();
    }

    /// <summary>
    /// The pixel size already known for a target, used by layout to size an image block to its picture.
    /// Null while the bytes are still in flight (or when they never resolved), in which case the block
    /// keeps the maximum box as a placeholder.
    /// </summary>
    private global::Windows.Foundation.Size? GetImageNaturalSize(string target)
        => images.TryGetValue(target, out CanvasBitmap? bitmap) && bitmap is not null
            ? new global::Windows.Foundation.Size(bitmap.Size.Width, bitmap.Size.Height)
            : null;

    /// <summary>
    /// Wraps the bytes in an in-memory stream and builds a bitmap on the live device.
    /// </summary>
    private async Task<CanvasBitmap> DecodeAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (DataWriter writer = new(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();

            // Disposing the writer would otherwise close the stream it was handed; detaching leaves it
            // open so LoadAsync below can still read it.
            writer.DetachStream();
        }

        stream.Seek(0);
        return await CanvasBitmap.LoadAsync(canvas, stream);
    }

    // --- hover tooltip ------------------------------------------------------------------------

    /// <summary>
    /// Floats the image's name over the canvas while the pointer is on it. The name used to be painted
    /// as a permanent caption under every image, which spent a line of the note on a label the user
    /// rarely needs; a bubble that only appears on hover keeps the same information reachable without
    /// the clutter.
    /// </summary>
    private Border? imageTooltip;
    private TextBlock? imageTooltipText;

    /// <summary>The canvas-space point the bubble is currently anchored to.</summary>
    private global::Windows.Foundation.Point lastImageTooltipPoint;

    private void InitializeImageTooltip()
    {
        imageTooltipText = new TextBlock
        {
            FontSize = 12,
            MaxWidth = 320,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        imageTooltip = new Border
        {
            Visibility = Visibility.Collapsed,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 3, 7, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,

            // The bubble sits on top of the canvas, so it must not take pointer input: the moment the
            // cursor reached the bubble it would stop being over the image and dismiss itself.
            IsHitTestVisible = false,
            Child = imageTooltipText,
        };

        // Resolved from theme resources rather than from the editor's own palette: the bubble needs an
        // opaque fill because it floats over the canvas. See ApplyImageTooltipTheme.
        Children.Add(imageTooltip);

        // The bubble has no size until it has been arranged, so the first hover positions it with a
        // zero width and the edge clamp does nothing. Reposition once the real size arrives.
        imageTooltip.SizeChanged += (_, _) => RepositionImageTooltip();

        ApplyImageTooltipTheme();
    }

    /// <summary>
    /// Gives the bubble an opaque, theme-correct fill. The brush the previous version asked for,
    /// <c>CardBackgroundFillColorSecondary</c>, is itself almost transparent by design (it assumes Mica
    /// behind it), so floating over the canvas left the tooltip reading as text on nothing. A
    /// <c>Solid*</c> background resource is the opaque equivalent, and the alpha is forced back to full
    /// so <c>TextFillColor*</c> - also defined with alpha - cannot reintroduce the same problem.
    /// </summary>
    private void ApplyImageTooltipTheme()
    {
        if (imageTooltip is null || imageTooltipText is null)
        {
            return;
        }

        global::Windows.UI.Color fallbackFill = ActualTheme == ElementTheme.Dark
            ? global::Windows.UI.Color.FromArgb(0xFF, 0x27, 0x27, 0x27)
            : global::Windows.UI.Color.FromArgb(0xFF, 0xF9, 0xF9, 0xF9);

        imageTooltip.Background = new SolidColorBrush(WithAlpha(GetThemeColor("SolidBackgroundFillColorSecondary", fallbackFill), 0xFF));

        // Only the fill is forced opaque. The stroke is meant to stay a translucent hairline over it,
        // and pushing its alpha to full would turn a subtle edge into a hard dark outline.
        imageTooltip.BorderBrush = new SolidColorBrush(GetThemeColor("ControlStrokeColorDefault", theme.Divider));
        imageTooltipText.Foreground = new SolidColorBrush(WithAlpha(GetThemeColor("TextFillColorPrimary", theme.Foreground), 0xFF));
    }

    /// <summary>
    /// Shows the name of the image under <paramref name="point"/> (canvas coordinates), or hides the
    /// bubble when the pointer is not over one.
    /// </summary>
    private void UpdateImageTooltip(global::Windows.Foundation.Point point)
    {
        if (imageTooltip is null || imageTooltipText is null)
        {
            return;
        }

        string? name = FindImageNameAt(point);
        if (name is null)
        {
            HideImageTooltip();
            return;
        }

        imageTooltipText.Text = name;
        imageTooltip.Visibility = Visibility.Visible;
        lastImageTooltipPoint = point;
        RepositionImageTooltip();
    }

    /// <summary>
    /// Places the bubble beside the cursor. Split out so it can be re-run once the bubble has been
    /// measured: on the first hover its size is still zero, so the clamp below would be a no-op.
    /// </summary>
    private void RepositionImageTooltip()
    {
        if (imageTooltip is null || imageTooltipText is null || imageTooltip.Visibility != Visibility.Visible)
        {
            return;
        }

        // The canvas is the scroll content, so its y is document space: drop the current offset to get
        // the control-relative position the overlay is measured from. Hanging the bubble below and to
        // the right of the cursor keeps it clear of the pointer glyph.
        double left = lastImageTooltipPoint.X + 14;
        double top = lastImageTooltipPoint.Y - scroller.VerticalOffset + 16;

        double maxLeft = Math.Max(0, ActualWidth - imageTooltip.ActualWidth);
        double maxTop = Math.Max(0, ActualHeight - imageTooltip.ActualHeight);

        imageTooltip.Margin = new Thickness(Math.Clamp(left, 0, maxLeft), Math.Clamp(top, 0, maxTop), 0, 0);
    }

    private void HideImageTooltip()
    {
        if (imageTooltip is { Visibility: Visibility.Visible })
        {
            imageTooltip.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// The name of the image whose reserved box contains <paramref name="point"/>, or null. Alt text is
    /// where the host records the file name; an image without one has nothing worth showing.
    /// </summary>
    private string? FindImageNameAt(global::Windows.Foundation.Point point)
    {
        BlockLayout? block = engine.HitTestBlock((float)point.Y);
        if (block is null || block.Block.Kind != MarkdownBlockKind.Image)
        {
            return null;
        }

        // HitTestBlock clamps to the nearest block rather than reporting a miss, so a pointer well
        // clear of the image still resolves to it. Only the box the picture is drawn in counts.
        if (!BlockRenderers.GetImageBounds(block).Contains(point))
        {
            return null;
        }

        string name = (block.Block.AltText ?? string.Empty).Trim();
        return name.Length == 0 ? null : name;
    }
}
