using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// A markdown WYSIWYG editor implemented from scratch on Win2D.
/// </summary>
/// <remarks>
/// The control owns everything a text editor normally gets for free from <c>TextBox</c> or
/// <c>RichEditBox</c>: the buffer (<see cref="MarkdownDocument"/>), the shaping and measurement
/// (<see cref="MarkdownLayoutEngine"/>), the caret, the selection, hit-testing, and the OS text input
/// plumbing. It exists because the note editor needs markdown as its on-disk format while still
/// rendering formatted text, and no stock WinUI control can do both without an embedded browser.
/// <para>
/// The visual tree is deliberately tiny - a <see cref="ScrollViewer"/> wrapping a
/// <see cref="CanvasVirtualControl"/> - so there is no XAML template to keep in sync; everything is
/// drawn in <see cref="OnRegionsInvalidated"/>.
/// </para>
/// </remarks>
public sealed partial class MarkdownEditor : Grid, IDisposable
{
    private readonly EditorTheme theme = new();
    private readonly MarkdownDocument document = new();
    private readonly MarkdownLayoutEngine engine;
    private readonly ScrollViewer scroller;
    private readonly CanvasVirtualControl canvas;
    private readonly DispatcherTimer caretTimer;

    private TextRange selection = TextRange.Caret(0);
    private bool caretOn = true;
    private bool suppressTextPropertyChange;
    private bool disposed;
    private float contentWidth = 600f;

    /// <summary>
    /// Source start of the table currently shown as raw markdown, toggled by the toolbar. -1 when no
    /// table is in edit mode; cleared automatically when the caret leaves the table.
    /// </summary>
    private int editModeTableStart = -1;

    /// <summary>
    /// Remembered x position for Up/Down navigation so walking through short lines does not drag the
    /// caret leftwards, matching every other editor.
    /// </summary>
    private float? desiredCaretX;

    /// <summary>
    /// The block <see cref="SelectAll"/> last narrowed to, so a third Ctrl+A returns to it instead of
    /// whatever block happens to hold the caret once the whole document is highlighted.
    /// </summary>
    private TextRange lastBlockSelection = TextRange.Caret(0);

    public MarkdownEditor()
    {
        engine = new MarkdownLayoutEngine(theme);

        // Blocks are sized during layout, which is synchronous, so the engine asks the control for
        // whatever it already knows about an image rather than waiting on a load.
        engine.ImageNaturalSize = GetImageNaturalSize;

        IsTabStop = true;
        UseSystemFocusVisuals = false;
        Background = new SolidColorBrush(Colors.Transparent);

        canvas = new CanvasVirtualControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Height = 1,
        };
        canvas.RegionsInvalidated += OnRegionsInvalidated;
        canvas.SizeChanged += OnCanvasSizeChanged;
        canvas.CreateResources += OnCanvasCreateResources;

        scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ZoomMode = ZoomMode.Disabled,
            Content = canvas,
        };
        scroller.ViewChanged += (_, _) =>
        {
            canvas.Invalidate();
            UpdateTableToolbarPlacement();

            // The bubble is placed from a pointer sample taken against the previous viewport, so
            // scrolling leaves it pointing at empty space; the next move re-shows it over whatever
            // the cursor is really on.
            HideImageTooltip();
        };

        Children.Add(scroller);

        document.Changed += OnDocumentChanged;

        caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        caretTimer.Tick += OnCaretTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => ApplyThemeColors();

        InitializeTextInput();
        InitializeInput();
        InitializeTableToolbar();
        InitializeImageTooltip();

        MarkdownEditorLog.Write("control constructed");
    }

    /// <summary>Raised after any edit, with the full markdown source.</summary>
    public event EventHandler<string>? MarkdownChanged;

    /// <summary>Raised when the user activates a link or an image.</summary>
    public event EventHandler<string>? LinkInvoked;

    /// <summary>Raised when the user renames a link label.</summary>
    public event EventHandler<LinkRenamedEventArgs>? LinkRenamed;

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownEditor),
        new PropertyMetadata(string.Empty, OnMarkdownPropertyChanged));

    /// <summary>The markdown source. Setting this replaces the document and clears undo history.</summary>
    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly),
        typeof(bool),
        typeof(MarkdownEditor),
        new PropertyMetadata(false));

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText),
        typeof(string),
        typeof(MarkdownEditor),
        new PropertyMetadata("Start writing..."));

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    /// <summary>Plain text with markdown punctuation removed, for previews and search indexes.</summary>
    public string PlainText => MarkdownTextUtilities.ToPlainText(document);

    public double VerticalScrollOffset
    {
        get => scroller.VerticalOffset;
        set => scroller.ChangeView(null, value, null, disableAnimation: true);
    }

    private static void OnMarkdownPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (MarkdownEditor)d;
        if (editor.suppressTextPropertyChange)
        {
            return;
        }

        string text = (string)(e.NewValue ?? string.Empty);
        if (text == editor.document.Text)
        {
            MarkdownEditorLog.Write($"markdown property unchanged, length={text.Length}");
            return;
        }

        MarkdownEditorLog.Write($"markdown property set, length={text.Length} {MarkdownEditorLog.Snippet(text)}");
        editor.document.SetText(text);
        editor.selection = TextRange.Caret(0);
        editor.engine.Invalidate();
        editor.UpdateEditorLayout();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MarkdownEditorLog.Write($"loaded, readyToDraw={canvas.ReadyToDraw} size={ActualWidth:F0}x{ActualHeight:F0}");
        ApplyThemeColors();
        UpdateEditorLayout();
        canvas.Invalidate();
    }

    /// <summary>
    /// Win2D creates its device asynchronously, so the first <see cref="OnLoaded"/> pass usually runs
    /// while <c>ReadyToDraw</c> is still false and <see cref="UpdateEditorLayout"/> bails out. Without
    /// this retry the canvas keeps its zero height, never raises RegionsInvalidated, and the editor
    /// renders nothing at all.
    /// </summary>
    private void OnCanvasCreateResources(CanvasVirtualControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        MarkdownEditorLog.Write($"Win2D resources created ({args.Reason}), docLength={document.Length}");

        // The bitmaps were built on the device that just went away, so they have to be dropped and
        // reloaded against the new one.
        ClearImageCache();
        engine.Invalidate();
        UpdateEditorLayout();
        canvas.Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        MarkdownEditorLog.Write("unloaded, caret blink stopped");
        caretTimer.Stop();
    }

    private void OnDocumentChanged(object? sender, MarkdownDocumentChangedEventArgs e)
    {
        MarkdownEditorLog.Write($"document changed, docLength={document.Length} selection={selection.Start}..{selection.End}");
        UpdateEditorLayout();

        suppressTextPropertyChange = true;
        try
        {
            Markdown = document.Text;
        }
        finally
        {
            suppressTextPropertyChange = false;
        }

        MarkdownChanged?.Invoke(this, document.Text);
        NotifyTextInputOfChange();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
        {
            MarkdownEditorLog.Write($"canvas size changed to {e.NewSize.Width:F0}x{e.NewSize.Height:F0}");
            contentWidth = (float)e.NewSize.Width;
            engine.Invalidate();
            UpdateEditorLayout();
        }
    }

    private void OnCaretTick(object? sender, object e)
    {
        caretOn = !caretOn;
        InvalidateCaret();
    }

    /// <summary>
    /// Re-measures the document and resizes the drawing surface. Cheap when nothing relevant changed,
    /// because the engine caches on (text, width, active table).
    /// </summary>
    private void UpdateEditorLayout()
    {
        if (!canvas.ReadyToDraw)
        {
            MarkdownEditorLog.Exit("canvas not ready to draw");
            return;
        }

        float available = Math.Max(80f, contentWidth - 8f);
        bool rebuilt = engine.Update(canvas, document, available, editModeTableStart);

        // A rebuild produces brand-new BlockLayout objects with no bitmap attached, so the cache is
        // pushed in first, then any target it still lacks is kicked off. Both are cheap no-ops until
        // an image is actually present.
        ApplyCachedImages();
        RequestMissingImages();

        // Always leave a viewport's worth of slack so clicking past the last line still lands in the
        // document instead of missing the surface entirely.
        double desiredHeight = Math.Max(engine.TotalHeight + 40, scroller.ViewportHeight);
        if (Math.Abs(canvas.Height - desiredHeight) > 0.5)
        {
            canvas.Height = desiredHeight;
        }

        if (rebuilt)
        {
            MarkdownEditorLog.Write($"layout rebuilt, blocks={engine.Blocks.Count} totalHeight={engine.TotalHeight:F0} width={available:F0}");
            canvas.Invalidate();
        }

        UpdateTableToolbar();
    }

    private void ApplyThemeColors()
    {
        theme.Foreground = GetThemeColor("TextFillColorPrimary", theme.Foreground);
        theme.SecondaryForeground = GetThemeColor("TextFillColorSecondary", theme.SecondaryForeground);
        theme.SyntaxForeground = GetThemeColor("TextFillColorTertiary", theme.SyntaxForeground);
        theme.LinkForeground = GetThemeColor("AccentTextFillColorPrimary", theme.LinkForeground);
        theme.Caret = theme.Foreground;
        theme.Divider = GetThemeColor("DividerStrokeColorDefault", theme.Divider);
        theme.SelectionFill = WithAlpha(GetThemeColor("AccentFillColorDefault", theme.SelectionFill), 0x55);
        theme.CodeBackground = WithAlpha(theme.SecondaryForeground, 0x22);
        theme.QuoteBar = WithAlpha(theme.SecondaryForeground, 0x80);

        engine.Invalidate();
        UpdateEditorLayout();
        canvas.Invalidate();
        ApplyTableToolbarTheme();
        ApplyImageTooltipTheme();
    }

    private Color GetThemeColor(string resourceKey, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out object? value))
        {
            return value switch
            {
                Color color => color,
                SolidColorBrush brush => brush.Color,
                _ => fallback,
            };
        }

        return fallback;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    // --- rendering ----------------------------------------------------------------------------

    private void OnRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        UpdateEditorLayout();

        foreach (global::Windows.Foundation.Rect region in args.InvalidatedRegions)
        {
            try
            {
                using CanvasDrawingSession session = sender.CreateDrawingSession(region);
                session.Clear(Colors.Transparent);
                DrawRegion(session, region);
            }
            catch (Exception ex)
            {
                // A throw here would tear down the app from a Win2D callback, and the region that
                // caused it is the only useful clue.
                MarkdownEditorLog.Write($"draw failed for region {region.X:F0},{region.Y:F0} {region.Width:F0}x{region.Height:F0} surface={canvas.Size.Width:F0}x{canvas.Size.Height:F0}");
                MarkdownEditorLog.Error(ex);
            }
        }
    }

    private void DrawRegion(CanvasDrawingSession session, global::Windows.Foundation.Rect region)
    {
        if (document.Length == 0)
        {
            DrawPlaceholder(session);
        }

        float top = (float)region.Top;
        float bottom = (float)region.Bottom;

        foreach (BlockLayout block in engine.Blocks)
        {
            if (block.Y > bottom || block.Y + block.Height < top)
            {
                continue;
            }

            BlockRenderers.DrawDecoration(session, theme, block, Math.Max(80f, contentWidth - 8f));
            DrawSelection(session, block);
            BlockRenderers.DrawInlineBackgrounds(session, theme, block);
            DrawBlockText(session, block);
        }

        DrawCaret(session);
    }

    private void DrawBlockText(CanvasDrawingSession session, BlockLayout block)
    {
        if (block.Text is not null)
        {
            session.DrawTextLayout(block.Text.Layout, block.Text.Origin.X, block.Y + block.Text.Origin.Y, theme.Foreground);
        }

        foreach (TableCellLayout cell in block.Cells)
        {
            session.DrawTextLayout(cell.Region.Layout, cell.Region.Origin.X, block.Y + cell.Region.Origin.Y, theme.Foreground);
        }
    }

    /// <summary>
    /// Fills the selection behind the text, clipped to the part of the selection this block owns.
    /// </summary>
    private void DrawSelection(CanvasDrawingSession session, BlockLayout block)
    {
        if (selection.IsEmpty)
        {
            return;
        }

        // An image has no glyphs to highlight, so the selection is drawn as a tint over its whole box.
        // Anything that clips the attachment counts: its markdown is hidden, so a partial drag across
        // it would otherwise show nothing at all.
        if (block.Block.Kind == MarkdownBlockKind.Image
            && selection.Min < block.Block.End
            && selection.Max > block.Block.Start)
        {
            global::Windows.Foundation.Rect box = BlockRenderers.GetImageBounds(block);
            session.FillRoundedRectangle(
                (float)box.X,
                (float)box.Y,
                (float)box.Width,
                (float)box.Height,
                6f,
                6f,
                theme.SelectionFill);
            session.DrawRoundedRectangle(
                (float)box.X + 0.5f,
                (float)box.Y + 0.5f,
                (float)box.Width - 1f,
                (float)box.Height - 1f,
                6f,
                6f,
                theme.LinkForeground);
            return;
        }

        DrawSelectionForRegion(session, block.Text, block.Y);
        foreach (TableCellLayout cell in block.Cells)
        {
            DrawSelectionForRegion(session, cell.Region, block.Y);
        }
    }

    private void DrawSelectionForRegion(CanvasDrawingSession session, TextRegion? region, float yOffset)
    {
        if (region is null)
        {
            return;
        }

        int start = Math.Max(selection.Min, region.DocumentStart);
        int end = Math.Min(selection.Max, region.DocumentEnd);
        if (end <= start)
        {
            return;
        }

        int renderStart = region.ToRenderIndex(start);
        int renderEnd = region.ToRenderIndex(end);
        int count = renderEnd - renderStart;

        if (count <= 0)
        {
            // A selection that only covers hidden syntax still needs a visible sliver so the user can
            // tell the range is selected.
            Vector2 caret = region.GetCaretOrigin(start, out float height);
            session.FillRectangle(caret.X, caret.Y + yOffset, 3f, height, theme.SelectionFill);
            return;
        }

        foreach (CanvasTextLayoutRegion charRegion in region.Layout.GetCharacterRegions(renderStart, count))
        {
            global::Windows.Foundation.Rect bounds = charRegion.LayoutBounds;
            session.FillRectangle(
                (float)bounds.X + region.Origin.X,
                (float)bounds.Y + region.Origin.Y + yOffset,
                (float)bounds.Width,
                (float)bounds.Height,
                theme.SelectionFill);
        }
    }

    private void DrawCaret(CanvasDrawingSession session)
    {
        if (!caretOn || !selection.IsEmpty || !HasFocusInternal || IsReadOnly)
        {
            return;
        }

        if (!TryGetCaretRect(out global::Windows.Foundation.Rect caretRect))
        {
            return;
        }

        session.FillRectangle(
            (float)caretRect.X,
            (float)caretRect.Y,
            (float)caretRect.Width,
            (float)caretRect.Height,
            theme.Caret);
    }

    /// <summary>
    /// Resolves the caret rectangle in canvas coordinates, used for painting, scrolling into view and
    /// for reporting the IME candidate window position.
    /// </summary>
    private bool TryGetCaretRect(out global::Windows.Foundation.Rect rect)
    {
        rect = default;
        BlockLayout? block = engine.FindBlockFor(selection.End);
        if (block is null)
        {
            return false;
        }

        TextRegion? region = block.Text;

        foreach (TableCellLayout cell in block.Cells)
        {
            if (cell.Region.ContainsDocumentPosition(selection.End))
            {
                region = cell.Region;
                break;
            }
        }

        if (region is null && block.Cells.Count > 0)
        {
            // The caret can sit in trimmed padding between cells (e.g. after a trailing space), where
            // no cell claims it. Use the nearest cell instead of falling back to the block, whose
            // rect would park the caret at the table's left edge.
            TableCellLayout nearest = block.Cells[0];
            int nearestDistance = int.MaxValue;
            foreach (TableCellLayout cell in block.Cells)
            {
                int distance = selection.End < cell.Region.DocumentStart
                    ? cell.Region.DocumentStart - selection.End
                    : selection.End > cell.Region.DocumentEnd
                        ? selection.End - cell.Region.DocumentEnd
                        : 0;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = cell;
                }
            }

            region = nearest.Region;
        }

        if (region is null)
        {
            if (block.Block.Kind == MarkdownBlockKind.Image)
            {
                // The picture has no text to sit beside, so the caret stands just outside the edge of
                // its box on the side the offset belongs to, kept to one line's height and centred
                // vertically. Parking it at the block's left edge instead made it look like the caret
                // had not moved to the end of the image at all.
                global::Windows.Foundation.Rect box = BlockRenderers.GetImageBounds(block);
                float imageCaretHeight = Math.Min(block.Height, theme.BodyFontSize * 1.6f);
                float imageCaretX = selection.End >= block.Block.End
                    ? (float)box.Right + 3f
                    : Math.Max(0f, (float)box.X - 3f);

                rect = new global::Windows.Foundation.Rect(
                    imageCaretX,
                    box.Y + (((float)box.Height - imageCaretHeight) / 2f),
                    1.5,
                    imageCaretHeight);
                return true;
            }

            // Only blocks without a text region reach here (thematic breaks), and their Height
            // is the whole box rather than a line. Cap the caret to roughly one text line so it does
            // not tower over the block.
            float caretHeight = Math.Clamp(block.Height, 4f, theme.BodyFontSize * 1.6f);
            rect = new global::Windows.Foundation.Rect(block.ContentX, block.Y, 1.5, caretHeight);
            return true;
        }

        Vector2 origin = region.GetCaretOrigin(selection.End, out float height);
        rect = new global::Windows.Foundation.Rect(origin.X, origin.Y + block.Y, 1.5, Math.Max(4, height));
        return true;
    }

    private void DrawPlaceholder(CanvasDrawingSession session)
    {
        if (string.IsNullOrEmpty(PlaceholderText))
        {
            return;
        }

        using var format = new CanvasTextFormat
        {
            FontFamily = theme.BodyFontFamily,
            FontSize = theme.BodyFontSize,
        };

        session.DrawText(PlaceholderText, 0, 0, WithAlpha(theme.SecondaryForeground, 0x80), format);
    }

    // --- caret / selection state -------------------------------------------------------------

    /// <summary>
    /// The single place selection changes flow through, so the caret blink resets, the active block is
    /// re-laid-out and the OS text stack stays informed.
    /// </summary>
    private void SetSelection(TextRange range, bool resetDesiredX = true)
    {
        int max = document.Length;
        range = new TextRange(Math.Clamp(range.Start, 0, max), Math.Clamp(range.End, 0, max));
        if (range.IsEmpty && TryGetAtomicBlockSyntax(range.End, out TextRange syntax)
            && range.End > syntax.Min && range.End < syntax.Max)
        {
            range = TextRange.Caret(syntax.Max);
        }

        bool blockChanged = engine.FindBlockFor(selection.End)?.Block.Start != engine.FindBlockFor(range.End)?.Block.Start;
        MarkdownEditorLog.Write($"selection {selection.Start}..{selection.End} -> {range.Start}..{range.End} blockChanged={blockChanged}");
        selection = range;

        // Raw table editing ends as soon as the caret leaves that table.
        if (editModeTableStart >= 0
            && engine.FindBlockFor(range.End)?.Block.Start != editModeTableStart)
        {
            editModeTableStart = -1;
            engine.Invalidate();
        }

        if (resetDesiredX)
        {
            desiredCaretX = null;
        }

        RestartCaretBlink();

        if (blockChanged)
        {
            // Moving to another block reveals its markdown syntax and hides the previous one's, so the
            // layout has to be rebuilt rather than merely repainted.
            document.CloseUndoUnit();
            engine.Invalidate();
        }

        UpdateEditorLayout();
        canvas.Invalidate();
        NotifySelectionChanged();
        ScrollCaretIntoView();
    }

    private void RestartCaretBlink()
    {
        caretOn = true;
        caretTimer.Stop();
        if (HasFocusInternal)
        {
            caretTimer.Start();
        }
    }

    /// <summary>
    /// Repaints just the caret. The region is clamped to the drawing surface because Win2D rejects
    /// any rectangle that falls outside it, and padding the caret box pushes X negative at column 0.
    /// </summary>
    private void InvalidateCaret()
    {
        if (!canvas.ReadyToDraw)
        {
            MarkdownEditorLog.Exit("canvas not ready to draw");
            return;
        }

        if (!TryGetCaretRect(out global::Windows.Foundation.Rect caretRect))
        {
            MarkdownEditorLog.Exit($"no caret rect for offset {selection.End}");
            return;
        }

        double surfaceWidth = canvas.Size.Width;
        double surfaceHeight = canvas.Size.Height;
        if (surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            MarkdownEditorLog.Exit($"empty surface {surfaceWidth:F0}x{surfaceHeight:F0}");
            return;
        }

        double left = Math.Max(0, caretRect.X - 2);
        double top = Math.Max(0, caretRect.Y);
        double right = Math.Min(surfaceWidth, caretRect.X + caretRect.Width + 2);
        double bottom = Math.Min(surfaceHeight, caretRect.Y + caretRect.Height);

        if (right <= left || bottom <= top)
        {
            MarkdownEditorLog.Exit($"caret {caretRect.X:F0},{caretRect.Y:F0} outside surface {surfaceWidth:F0}x{surfaceHeight:F0}");
            return;
        }

        canvas.Invalidate(new global::Windows.Foundation.Rect(left, top, right - left, bottom - top));
    }

    private void ScrollCaretIntoView()
    {
        if (!TryGetCaretRect(out global::Windows.Foundation.Rect caretRect))
        {
            return;
        }

        double viewTop = scroller.VerticalOffset;
        double viewBottom = viewTop + scroller.ViewportHeight;
        const double Margin = 24;

        if (caretRect.Top < viewTop + Margin)
        {
            scroller.ChangeView(null, Math.Max(0, caretRect.Top - Margin), null, disableAnimation: true);
        }
        else if (caretRect.Bottom > viewBottom - Margin)
        {
            scroller.ChangeView(null, caretRect.Bottom - scroller.ViewportHeight + Margin, null, disableAnimation: true);
        }
    }

    public void Dispose()
    {
        MarkdownEditorLog.Write("disposing");
        disposed = true;
        caretTimer.Stop();
        ClearImageCache();
        engine.Dispose();
        DisposeTextInput();
        // Win2D controls hold a swap chain; it must be detached explicitly or the GPU resources stay
        // alive after the control goes away.
        canvas.RemoveFromVisualTree();
    }
}

public sealed class LinkRenamedEventArgs : EventArgs
{
    public LinkRenamedEventArgs(string target, string newLabel)
    {
        Target = target;
        NewLabel = newLabel;
    }

    public string Target { get; }

    public string NewLabel { get; }
}
