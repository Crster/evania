using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.UI;
using FontStyle = Windows.UI.Text.FontStyle;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// Fonts, sizes and colours the layout engine and renderers use. Populated by the control from the
/// current XAML theme so the editor follows light/dark mode without owning any brushes itself.
/// </summary>
public sealed class EditorTheme
{
    public string BodyFontFamily { get; set; } = "Segoe UI Variable Text";

    public string CodeFontFamily { get; set; } = "Cascadia Mono";

    public float BodyFontSize { get; set; } = 15f;

    public float ParagraphSpacing { get; set; } = 3f;

    public float BlockIndent { get; set; } = 22f;

    public Color Foreground { get; set; } = Colors.Black;

    public Color SecondaryForeground { get; set; } = Color.FromArgb(0xFF, 0x70, 0x70, 0x70);

    /// <summary>Colour for markdown punctuation revealed on the active block.</summary>
    public Color SyntaxForeground { get; set; } = Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A);

    public Color LinkForeground { get; set; } = Color.FromArgb(0xFF, 0x2E, 0x74, 0xD0);

    public Color CodeForeground { get; set; } = Color.FromArgb(0xFF, 0xC0, 0x39, 0x5A);

    public Color CodeBackground { get; set; } = Color.FromArgb(0x20, 0x80, 0x80, 0x80);

    /// <summary>Band drawn behind <see cref="InlineStyle.Highlight"/> text.</summary>
    public Color HighlightBackground { get; set; } = Color.FromArgb(0x66, 0xFF, 0xEB, 0x3B);

    public Color QuoteBar { get; set; } = Color.FromArgb(0x80, 0x80, 0x80, 0x80);

    public Color Divider { get; set; } = Color.FromArgb(0x40, 0x80, 0x80, 0x80);

    public Color SelectionFill { get; set; } = Color.FromArgb(0x50, 0x2E, 0x74, 0xD0);

    public Color Caret { get; set; } = Colors.Black;

    public float GetFontSize(MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => block.Level switch
        {
            1 => BodyFontSize * 1.85f,
            2 => BodyFontSize * 1.5f,
            3 => BodyFontSize * 1.25f,
            4 => BodyFontSize * 1.12f,
            _ => BodyFontSize,
        },
        MarkdownBlockKind.CodeBlock => BodyFontSize * 0.94f,
        _ => BodyFontSize,
    };
}

/// <summary>
/// Maps a run of rendered characters back to the document offsets they came from.
/// </summary>
/// <remarks>
/// Rendered text and document text are not the same length: hidden markdown punctuation is dropped,
/// and list markers are replaced by generated bullets. Segments are the bridge, and they are always
/// emitted in ascending order so lookups can walk them linearly.
/// </remarks>
internal readonly struct LayoutSegment
{
    public LayoutSegment(int renderStart, int documentStart, int length, bool isSynthetic)
    {
        RenderStart = renderStart;
        DocumentStart = documentStart;
        Length = length;
        IsSynthetic = isSynthetic;
    }

    public int RenderStart { get; }

    public int DocumentStart { get; }

    public int Length { get; }

    /// <summary>True for generated glyphs (bullets, checkboxes) that have no document text.</summary>
    public bool IsSynthetic { get; }
}

/// <summary>
/// One positioned <see cref="CanvasTextLayout"/> plus its document mapping. Used both for whole
/// blocks and for individual table cells.
/// </summary>
internal sealed class TextRegion
{
    public TextRegion(
        CanvasTextLayout layout,
        string renderedText,
        Vector2 origin,
        IReadOnlyList<LayoutSegment> segments,
        int documentStart,
        int documentEnd,
        IReadOnlyList<(int Start, int Length, MarkdownInline Inline)>? runs = null)
    {
        Layout = layout;
        RenderedText = renderedText;
        Origin = origin;
        Segments = segments;
        DocumentStart = documentStart;
        DocumentEnd = documentEnd;
        Runs = runs ?? Array.Empty<(int, int, MarkdownInline)>();
    }

    public CanvasTextLayout Layout { get; }

    /// <summary>
    /// The styled runs indexed into <see cref="RenderedText"/>. Only the text runs survive here - the
    /// markdown punctuation they were built from is hidden - which is what lets the painters put a
    /// background behind just the code or highlighted words.
    /// </summary>
    public IReadOnlyList<(int Start, int Length, MarkdownInline Inline)> Runs { get; }

    /// <summary>The exact string handed to <see cref="Layout"/>; needed to clamp caret indices.</summary>
    public string RenderedText { get; }

    public Vector2 Origin { get; set; }

    public IReadOnlyList<LayoutSegment> Segments { get; }

    public int DocumentStart { get; }

    public int DocumentEnd { get; }

    public float Width => (float)Layout.LayoutBounds.Width;

    /// <summary>
    /// The height actually occupied by the drawn lines. <c>DrawBounds</c> is deliberately not used:
    /// it grows with ink overhang from glyphs and decorations, which would inflate block spacing.
    /// </summary>
    public float Height => MarkdownLayoutEngine.SumLineHeights(Layout);

    public bool ContainsDocumentPosition(int position) => position >= DocumentStart && position <= DocumentEnd;

    public int ToRenderIndex(int documentPosition)
    {
        if (Segments.Count == 0)
        {
            return 0;
        }

        // A position in a gap (trimmed padding, hidden syntax) belongs to the segment that precedes
        // it, not the one that follows: snapping forward would throw the caret to the far side of
        // the hidden text, which is how a space typed at a cell's end used to jump to its left edge.
        int previousEnd = 0;
        foreach (LayoutSegment segment in Segments)
        {
            if (segment.IsSynthetic)
            {
                continue;
            }

            if (documentPosition < segment.DocumentStart)
            {
                return previousEnd;
            }

            if (documentPosition <= segment.DocumentStart + segment.Length)
            {
                return segment.RenderStart + (documentPosition - segment.DocumentStart);
            }

            previousEnd = segment.RenderStart + segment.Length;
        }

        LayoutSegment last = Segments[^1];
        return last.RenderStart + last.Length;
    }

    public int ToDocumentIndex(int renderIndex)
    {
        if (Segments.Count == 0)
        {
            // Nothing rendered means the whole block is hidden syntax (an empty list item, or an
            // inactive heading/quote with no body): the only sensible caret position is past the
            // marker, not in front of it, or a click here would insert typed text before the "- ".
            return DocumentEnd;
        }

        foreach (LayoutSegment segment in Segments)
        {
            if (renderIndex < segment.RenderStart)
            {
                return segment.IsSynthetic ? DocumentStart : segment.DocumentStart;
            }

            if (renderIndex <= segment.RenderStart + segment.Length)
            {
                // Landing inside a synthetic run (a bullet glyph) means "the start of the real text".
                if (segment.IsSynthetic)
                {
                    continue;
                }

                return segment.DocumentStart + (renderIndex - segment.RenderStart);
            }
        }

        for (int i = Segments.Count - 1; i >= 0; i--)
        {
            if (!Segments[i].IsSynthetic)
            {
                return Segments[i].DocumentStart + Segments[i].Length;
            }
        }

        return DocumentEnd;
    }

    /// <summary>
    /// Returns the top-left of the caret for a document offset, in region-local-plus-origin space.
    /// </summary>
    /// <remarks>
    /// Win2D can only report a caret relative to an existing character, so a caret sitting past the
    /// last character is expressed as the trailing side of that character instead.
    /// <para>
    /// The reported height is derived from the font size rather than from the character's layout
    /// bounds, because those bounds cover the whole line box including the proportional line spacing.
    /// Using them directly makes the caret as tall as the gap between lines.
    /// </para>
    /// </remarks>
    public Vector2 GetCaretOrigin(int documentPosition, out float height)
    {
        int renderIndex = Math.Clamp(ToRenderIndex(documentPosition), 0, RenderedText.Length);

        if (RenderedText.Length == 0)
        {
            float emptyLineHeight = MarkdownLayoutEngine.SumLineHeights(Layout);
            height = CaretHeightFor(emptyLineHeight);
            return Origin + new Vector2(0, CaretInsetFor(emptyLineHeight));
        }

        bool trailing = false;
        if (renderIndex >= RenderedText.Length)
        {
            renderIndex = RenderedText.Length - 1;
            trailing = true;
        }

        CanvasTextLayoutRegion[] regions = Layout.GetCharacterRegions(renderIndex, 1);
        float lineHeight = regions.Length > 0
            ? (float)regions[0].LayoutBounds.Height
            : MarkdownLayoutEngine.SumLineHeights(Layout);

        height = CaretHeightFor(lineHeight);

        // The caret is centred in the line box so the extra leading sits evenly above and below it.
        return Origin + Layout.GetCaretPosition(renderIndex, trailing) + new Vector2(0, CaretInsetFor(lineHeight));
    }

    /// <summary>The glyph box the caret should span: the em size plus a little padding.</summary>
    private float CaretHeightFor(float lineHeight)
    {
        float glyphHeight = (float)Layout.DefaultFontSize * 1.2f;

        // An empty layout reports no height at all, in which case the font size is the only guide.
        return lineHeight < 1 ? glyphHeight : Math.Min(lineHeight, glyphHeight);
    }

    private float CaretInsetFor(float lineHeight)
        => Math.Max(0, (lineHeight - CaretHeightFor(lineHeight)) / 2f);


    /// <summary>
    /// Maps a point (in the same space as <see cref="Origin"/>) to a document offset.
    /// </summary>
    public int HitTest(Vector2 point)
    {
        Layout.HitTest(point - Origin, out CanvasTextLayoutRegion region, out bool trailing);
        int renderIndex = region.CharacterIndex + (trailing ? region.CharacterCount : 0);
        return ToDocumentIndex(Math.Clamp(renderIndex, 0, RenderedText.Length));
    }
}

/// <summary>
/// A single block positioned in the document, ready to draw and to hit-test.
/// </summary>
internal sealed class BlockLayout
{
    public required MarkdownBlock Block { get; init; }

    public float Y { get; set; }

    public float Height { get; set; }

    /// <summary>Left edge of the block content, after list/quote indentation.</summary>
    public float ContentX { get; set; }

    public float ContentWidth { get; set; }

    /// <summary>Main text region; null for tables and images.</summary>
    public TextRegion? Text { get; set; }

    /// <summary>Positioned cell regions for <see cref="MarkdownBlockKind.Table"/>.</summary>
    public List<TableCellLayout> Cells { get; } = new();

    public List<float> ColumnOffsets { get; } = new();

    public List<float> RowOffsets { get; } = new();

    /// <summary>Marker glyph drawn to the left of list items (bullet, ordinal or checkbox).</summary>
    public string? Marker { get; set; }

    /// <summary>
    /// True when this is the block holding the caret, so its block marker and the caret-hosting
    /// inline group render as text. Consumers use it to tell revealed syntax (editable character by
    /// character) from the hidden kind editing guards still have to protect.
    /// </summary>
    public bool ShowsSyntax { get; init; }

    public CanvasTextLayout? MarkerLayout { get; set; }

    /// <summary>Width of the box reserved for an image block; 0 until an image block is laid out.</summary>
    public float ImageWidth { get; set; }

    public float ImageHeight { get; set; }

    /// <summary>
    /// Decoded bitmap for an image block, filled in by the control after <see cref="BlockRenderers.BuildImage"/>
    /// reserves the box. Null until the bytes are resolved, or permanently when they never were - in which
    /// case the placeholder is drawn instead.
    /// </summary>
    public CanvasBitmap? Image { get; set; }

    /// <summary>True when the block renders its raw markdown for editing (the active table).</summary>
    public bool IsRawEdit { get; init; }

    public bool Contains(int documentPosition) => Block.Contains(documentPosition);
}

internal sealed class TableCellLayout
{
    public required TextRegion Region { get; init; }

    public required int Row { get; init; }

    public required int Column { get; init; }

    public bool IsHeader { get; init; }
}

/// <summary>
/// Turns parsed blocks into positioned Win2D text layouts.
/// </summary>
/// <remarks>
/// The engine is the only place that knows how markdown maps to glyphs. It drops the syntax spans
/// from the drawn text so every block renders formatted, except on the block holding the caret,
/// whose block marker and caret-hosting inline group are rendered muted so they can be edited in
/// place (the caret maps to the nearest visible character when it sits inside punctuation hidden
/// elsewhere). It builds a
/// <see cref="CanvasTextLayout"/> per block with per-span character formatting,
/// and stacks the blocks vertically. Results are cached until the text, width or active block change,
/// because rebuilding layouts is the expensive part of typing.
/// </remarks>
internal sealed class MarkdownLayoutEngine : IDisposable
{
    private readonly EditorTheme theme;
    private readonly List<BlockLayout> blocks = new();

    private string? cachedText;
    private float cachedWidth = -1;
    private int cachedEditModeTableStart = -1;
    private int cachedActiveBlockStart = -1;
    private string? cachedRevealSignature;

    public MarkdownLayoutEngine(EditorTheme theme)
    {
        this.theme = theme;
    }

    public IReadOnlyList<BlockLayout> Blocks => blocks;

    /// <summary>
    /// Lets the control hand a natural pixel size for an image target so <see cref="BlockRenderers.BuildImage"/>
    /// can size the block to the picture instead of always reserving the maximum box. Returns null when
    /// the bitmap is not loaded yet.
    /// </summary>
    public Func<string, global::Windows.Foundation.Size?>? ImageNaturalSize { get; set; }

    public float TotalHeight { get; private set; }

    public void Invalidate()
    {
        cachedText = null;
        cachedWidth = -1;
        cachedEditModeTableStart = -1;
        cachedActiveBlockStart = -1;
        cachedRevealSignature = null;
    }

    /// <summary>
    /// Rebuilds the layout if anything that affects it changed. Returns true when a rebuild happened.
    /// <paramref name="editModeTableStart"/> is the source start of the table being edited as raw
    /// markdown (toggled by the toolbar), or -1 when every table renders as a grid.
    /// <paramref name="activePosition"/> is the caret's document offset, or -1 to keep every block in
    /// its formatted preview; the block containing it reveals its block marker, plus the inline group
    /// (a delimiter pair and what it wraps) the caret sits inside.
    /// </summary>
    public bool Update(
        ICanvasResourceCreator resourceCreator,
        MarkdownDocument document,
        float availableWidth,
        int editModeTableStart,
        int activePosition)
    {
        string text = document.Text;
        IReadOnlyList<MarkdownBlock> parsed = MarkdownParser.Parse(document);
        int activeBlockStart = FindActiveBlockStart(parsed, activePosition);
        string revealSignature = FindRevealSignature(parsed, activeBlockStart, activePosition);

        if (cachedText == text
            && Math.Abs(cachedWidth - availableWidth) < 0.5f
            && cachedEditModeTableStart == editModeTableStart
            && cachedActiveBlockStart == activeBlockStart
            && cachedRevealSignature == revealSignature)
        {
            return false;
        }

        Build(resourceCreator, parsed, availableWidth, editModeTableStart, activeBlockStart, activePosition);

        cachedText = text;
        cachedWidth = availableWidth;
        cachedEditModeTableStart = editModeTableStart;
        cachedActiveBlockStart = activeBlockStart;
        cachedRevealSignature = revealSignature;
        return true;
    }

    /// <summary>
    /// Resolves the start of the block containing <paramref name="position"/> from a fresh parse, so
    /// the reveal follows the caret even while the previous layout's blocks are stale. Positions
    /// inside the line break belong to the block above it, which is where the caret blinks from.
    /// </summary>
    private static int FindActiveBlockStart(IReadOnlyList<MarkdownBlock> parsed, int position)
    {
        if (position < 0)
        {
            return -1;
        }

        foreach (MarkdownBlock block in parsed)
        {
            if (block.Start > position)
            {
                break;
            }

            if (block.Contains(position))
            {
                return block.Start;
            }
        }

        return -1;
    }

    /// <summary>
    /// Canonical description of which inline groups the caret reveals, used as a layout cache key.
    /// Moving the caret within one group or through unmarked text keeps the signature unchanged, so
    /// no rebuild happens; crossing into another group's range produces a different one.
    /// </summary>
    private static string FindRevealSignature(IReadOnlyList<MarkdownBlock> parsed, int activeBlockStart, int position)
    {
        if (activeBlockStart < 0)
        {
            return string.Empty;
        }

        MarkdownBlock? active = null;
        foreach (MarkdownBlock block in parsed)
        {
            if (block.Start == activeBlockStart)
            {
                active = block;
                break;
            }
        }

        if (active is null)
        {
            return string.Empty;
        }

        var revealed = new List<string>();
        IReadOnlyList<MarkdownInline> inlines = active.Inlines;
        for (int i = 0; i < inlines.Count; i++)
        {
            if (!inlines[i].IsSyntax || IsBlockMarker(active, inlines[i]))
            {
                continue;
            }

            (int start, int end) = GetInlineGroupRange(inlines, i);
            if (position >= start && position <= end)
            {
                revealed.Add($"{start}:{end}");
            }
        }

        revealed.Sort(StringComparer.Ordinal);
        return string.Join(";", revealed);
    }

    private void Build(
        ICanvasResourceCreator resourceCreator,
        IReadOnlyList<MarkdownBlock> parsed,
        float availableWidth,
        int editModeTableStart,
        int activeBlockStart,
        int activePosition)
    {
        DisposeBlocks();

        float y = 0;
        foreach (MarkdownBlock block in parsed)
        {
            BlockLayout layout = block.Kind switch
            {
                MarkdownBlockKind.Table => BlockRenderers.BuildTable(resourceCreator, theme, block, availableWidth, block.Start == editModeTableStart),
                MarkdownBlockKind.Image => BlockRenderers.BuildImage(block, availableWidth, ImageNaturalSize),
                MarkdownBlockKind.CodeBlock => BlockRenderers.BuildCodeBlock(resourceCreator, theme, block, availableWidth, block.Start == activeBlockStart),
                MarkdownBlockKind.ThematicBreak => BlockRenderers.BuildThematicBreak(theme, block),
                _ => BuildTextBlock(resourceCreator, block, availableWidth, block.Start == activeBlockStart, activePosition),
            };

            layout.Y = y;

            // Region origins stay relative to their block; every consumer adds BlockLayout.Y itself.
            // Baking the absolute Y in here made the drawing code count it twice, pushing each block
            // an extra line further down the page.
            if (layout.Text is not null)
            {
                layout.Text.Origin = new Vector2(layout.ContentX, layout.Text.Origin.Y);
            }

            y += layout.Height + BlockRenderers.GetSpacingAfter(theme, block);
            blocks.Add(layout);
        }

        TotalHeight = y;
    }

    /// <summary>
    /// Builds a paragraph, heading, quote or list item. On the block holding the caret the block
    /// marker (heading/quote) renders muted for the whole line, and the inline group the caret sits
    /// inside reveals its delimiter pair, so markers can be read, selected and edited in place.
    /// </summary>
    internal BlockLayout BuildTextBlock(
        ICanvasResourceCreator resourceCreator,
        MarkdownBlock block,
        float availableWidth,
        bool revealSyntax,
        int caretPosition)
    {
        float indent = BlockRenderers.GetIndent(theme, block);
        float contentWidth = Math.Max(40f, availableWidth - indent);

        var layout = new BlockLayout
        {
            Block = block,
            ContentX = indent,
            ContentWidth = contentWidth,
            ShowsSyntax = revealSyntax,
        };

        (string rendered, List<LayoutSegment> segments, List<(int Start, int Length, MarkdownInline Inline)> runs) =
            ComposeRuns(block, revealSyntax, caretPosition);

        CanvasTextFormat format = CreateFormat(block);
        var textLayout = new CanvasTextLayout(resourceCreator, rendered, format, contentWidth, 0f)
        {
            WordWrapping = CanvasWordWrapping.WholeWord,
        };

        ApplyRunFormatting(textLayout, runs, block);

        layout.Text = new TextRegion(textLayout, rendered, new Vector2(indent, 0), segments, block.Start, block.End, runs);
        layout.Height = MeasureHeight(textLayout, format);

        if (BlockRenderers.TryGetMarker(block, out string? marker))
        {
            layout.Marker = marker;
            layout.MarkerLayout = new CanvasTextLayout(resourceCreator, marker, CreateFormat(block), theme.BlockIndent, 0f);
        }

        return layout;
    }

    /// <summary>
    /// Flattens a block's inline spans into the string that will actually be drawn, dropping the
    /// markdown punctuation so every block stays formatted. On the block holding the caret the
    /// revealed spans (block marker plus the inline group the caret is inside) are kept so they stay
    /// directly editable.
    /// </summary>
    private static (string Rendered, List<LayoutSegment> Segments, List<(int Start, int Length, MarkdownInline Inline)> Runs) ComposeRuns(
        MarkdownBlock block,
        bool revealSyntax,
        int caretPosition)
    {
        var builder = new System.Text.StringBuilder();
        var segments = new List<LayoutSegment>();
        var runs = new List<(int, int, MarkdownInline)>();

        IReadOnlyList<MarkdownInline> inlines = block.Inlines;
        for (int i = 0; i < inlines.Count; i++)
        {
            MarkdownInline inline = inlines[i];
            if (inline.Length == 0)
            {
                continue;
            }

            if (inline.IsSyntax && !ShouldRenderSyntax(block, inlines, i, revealSyntax, caretPosition))
            {
                continue;
            }

            int renderStart = builder.Length;
            builder.Append(inline.Text);
            segments.Add(new LayoutSegment(renderStart, inline.Start, inline.Length, isSynthetic: false));
            runs.Add((renderStart, inline.Length, inline));
        }

        return (builder.ToString(), segments, runs);
    }

    /// <summary>
    /// Decides whether one syntax span is drawn on the active block. The leading marker of a heading
    /// or quote shows for the whole line's caret; a list marker never shows because the bullet,
    /// ordinal or checkbox in the gutter stands in for it; every other span belongs to an inline
    /// group (a delimiter pair plus everything it wraps) and shows only while the caret is inside
    /// that group.
    /// </summary>
    private static bool ShouldRenderSyntax(
        MarkdownBlock block,
        IReadOnlyList<MarkdownInline> inlines,
        int index,
        bool revealSyntax,
        int caretPosition)
    {
        if (!revealSyntax)
        {
            return false;
        }

        if (IsBlockMarker(block, inlines[index]))
        {
            return block.Kind is MarkdownBlockKind.Heading or MarkdownBlockKind.Quote;
        }

        return IsCaretWithinInlineGroup(inlines, index, caretPosition);
    }

    /// <summary>
    /// The leading syntax span of a block marker: the heading or quote prefix, or the list marker
    /// that the gutter glyph replaces.
    /// </summary>
    private static bool IsBlockMarker(MarkdownBlock block, MarkdownInline inline)
        => inline.Start == block.Start
            && block.Kind is MarkdownBlockKind.Heading
                or MarkdownBlockKind.Quote
                or MarkdownBlockKind.BulletItem
                or MarkdownBlockKind.OrderedItem
                or MarkdownBlockKind.TaskItem;

    /// <summary>
    /// True when <paramref name="caretPosition"/> lies within the group that
    /// <paramref name="index"/>'s syntax span delimits, so the caret inside "…**bo|ld**…" reveals
    /// that pair while the rest of the line stays formatted.
    /// </summary>
    private static bool IsCaretWithinInlineGroup(IReadOnlyList<MarkdownInline> inlines, int index, int caretPosition)
    {
        (int start, int end) = GetInlineGroupRange(inlines, index);
        return caretPosition >= start && caretPosition <= end;
    }

    /// <summary>
    /// The source range a syntax span belongs to: the maximal run of adjacent spans carrying the
    /// span's style, which is the delimiter pair plus its content, nested groups included. Two
    /// groups marked with different styles stay separate even when they touch.
    /// </summary>
    private static (int Start, int End) GetInlineGroupRange(IReadOnlyList<MarkdownInline> inlines, int index)
    {
        InlineStyle style = inlines[index].Style;

        int first = index;
        while (first > 0 && (inlines[first - 1].Style & style) == style)
        {
            first--;
        }

        int last = index;
        while (last + 1 < inlines.Count && (inlines[last + 1].Style & style) == style)
        {
            last++;
        }

        return (inlines[first].Start, inlines[last].End);
    }

    private CanvasTextFormat CreateFormat(MarkdownBlock block) => new()
    {
        FontFamily = block.Kind == MarkdownBlockKind.CodeBlock ? theme.CodeFontFamily : theme.BodyFontFamily,
        FontSize = theme.GetFontSize(block),
        WordWrapping = CanvasWordWrapping.WholeWord,
        Direction = CanvasTextDirection.LeftToRightThenTopToBottom,
        LineSpacingMode = CanvasLineSpacingMode.Default,
        VerticalAlignment = CanvasVerticalAlignment.Top,
    };

    private void ApplyRunFormatting(
        CanvasTextLayout textLayout,
        List<(int Start, int Length, MarkdownInline Inline)> runs,
        MarkdownBlock block)
    {
        float baseSize = theme.GetFontSize(block);
        bool headingBold = block.Kind == MarkdownBlockKind.Heading;

        foreach ((int start, int length, MarkdownInline inline) in runs)
        {
            if (length <= 0)
            {
                continue;
            }

            InlineStyle style = inline.Style;

            if (inline.IsSyntax)
            {
                // Revealed punctuation is muted and carries none of the style it spells out: bold
                // markers that are themselves bold read as formatted text, not as source.
                textLayout.SetColor(start, length, theme.SyntaxForeground);
                continue;
            }

            if (style.HasFlag(InlineStyle.Code))
            {
                textLayout.SetColor(start, length, theme.CodeForeground);
                textLayout.SetFontFamily(start, length, theme.CodeFontFamily);
                textLayout.SetFontSize(start, length, baseSize * 0.94f);
            }
            else if (style.HasFlag(InlineStyle.Link))
            {
                textLayout.SetColor(start, length, theme.LinkForeground);
                textLayout.SetUnderline(start, length, true);
            }
            else if (block.Kind == MarkdownBlockKind.Quote)
            {
                textLayout.SetColor(start, length, theme.SecondaryForeground);
            }
            else
            {
                textLayout.SetColor(start, length, theme.Foreground);
            }

            if (style.HasFlag(InlineStyle.Bold) || headingBold)
            {
                textLayout.SetFontWeight(start, length, FontWeights.ExtraBold);
            }

            if (style.HasFlag(InlineStyle.Italic) || block.Kind == MarkdownBlockKind.Quote)
            {
                textLayout.SetFontStyle(start, length, FontStyle.Italic);
            }

            if (style.HasFlag(InlineStyle.Strikethrough)
                || (block.Kind == MarkdownBlockKind.TaskItem && block.IsChecked && !inline.IsSyntax))
            {
                textLayout.SetStrikethrough(start, length, true);
            }

            if (block.Kind == MarkdownBlockKind.TaskItem && block.IsChecked && !inline.IsSyntax)
            {
                textLayout.SetColor(start, length, theme.SecondaryForeground);
            }
        }
    }

    /// <summary>
    /// An empty layout still needs to occupy a full line so blank paragraphs stay clickable.
    /// </summary>
    /// <remarks>
    /// Height comes from <see cref="CanvasTextLayout.LineMetrics"/> rather than from
    /// <c>LayoutBounds</c>. A layout built with a requested height of zero reports layout bounds that
    /// are padded out to the font's full ascent/descent box plus leading, which is noticeably taller
    /// than the lines actually drawn; summing the per-line metrics gives the real occupied height.
    /// </remarks>
    internal static float MeasureHeight(CanvasTextLayout textLayout, CanvasTextFormat format)
    {
        float height = SumLineHeights(textLayout);

        if (height < 1)
        {
            // Roughly the natural line height of the font, matching CanvasLineSpacingMode.Default.
            height = format.FontSize * 1.33f;
        }

        return height;
    }

    /// <summary>Total height of the laid-out lines, excluding any ink overhang.</summary>
    internal static float SumLineHeights(CanvasTextLayout textLayout)
    {
        float height = 0;

        foreach (CanvasLineMetrics line in textLayout.LineMetrics)
        {
            height += line.Height;
        }

        return height;
    }

    /// <summary>
    /// Finds the block whose vertical band contains <paramref name="y"/>, clamping to the first or
    /// last block when the pointer is outside the content.
    /// </summary>
    public BlockLayout? HitTestBlock(float y)
    {
        if (blocks.Count == 0)
        {
            return null;
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            BlockLayout block = blocks[i];

            // The band runs down to the next block's top rather than to its own height plus
            // ParagraphSpacing. Spacing is per kind (a heading or table sits further from its
            // neighbour than a paragraph does), so a fixed margin left the leftover pixels in no
            // block's band at all and the pointer fell through to the last block in the document.
            float bottom = i + 1 < blocks.Count ? blocks[i + 1].Y : block.Y + block.Height;

            if (y < bottom)
            {
                return block;
            }
        }

        return blocks[^1];
    }

    public BlockLayout? FindBlockFor(int documentPosition)
    {
        foreach (BlockLayout block in blocks)
        {
            if (block.Contains(documentPosition))
            {
                return block;
            }
        }

        return blocks.Count > 0 ? blocks[^1] : null;
    }

    private void DisposeBlocks()
    {
        foreach (BlockLayout block in blocks)
        {
            block.Text?.Layout.Dispose();
            block.MarkerLayout?.Dispose();
            foreach (TableCellLayout cell in block.Cells)
            {
                cell.Region.Layout.Dispose();
            }
        }

        blocks.Clear();
    }

    public void Dispose() => DisposeBlocks();
}
