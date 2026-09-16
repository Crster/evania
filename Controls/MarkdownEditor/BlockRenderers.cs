using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.UI;
using FontStyle = Windows.UI.Text.FontStyle;
using FontWeights = Microsoft.UI.Text.FontWeights;
using Rect = Windows.Foundation.Rect;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// Layout and painting for the blocks that are not a single run of wrapped text.
/// </summary>
/// <remarks>
/// Paragraphs, headings, quotes and list items all reduce to one <see cref="CanvasTextLayout"/> and
/// are handled by <see cref="MarkdownLayoutEngine"/>. Code blocks, tables, images and rules need
/// their own geometry (backgrounds, grid lines, fixed boxes), so they are built and drawn here while
/// still producing the same <see cref="BlockLayout"/>/<see cref="TextRegion"/> shape, which keeps
/// caret mapping and hit-testing uniform across every block kind.
/// </remarks>
internal static class BlockRenderers
{
    private const float CodePadding = 10f;
    private const float CellPaddingX = 10f;
    private const float CellPaddingY = 6f;
    private const float RuleHeight = 13f;

    /// <summary>
    /// The largest an image is ever drawn. A note is mostly text, so attachments stay thumbnails
    /// rather than banners: 200x200 keeps the block close to the size of a tall paragraph.
    /// </summary>
    public const float MaxImageSize = 200f;

    /// <summary>
    /// The smallest edge an image box ever reserves. A very wide or very tall picture scales down to a
    /// sliver, and a sliver is neither clickable nor selectable, so the box keeps a floor.
    /// </summary>
    public const float MinImageSize = 24f;

    /// <summary>
    /// The rectangle an image block occupies: its own box (see <see cref="BuildImage"/>) flush with
    /// the left edge of the content. Shared by painting, hit-testing and the caret so every consumer
    /// agrees on where the picture actually is.
    /// </summary>
    public static Rect GetImageBounds(BlockLayout layout)
    {
        float boxWidth = layout.ImageWidth > 0 ? layout.ImageWidth : Math.Min(MaxImageSize, layout.ContentWidth);
        return new Rect(0, layout.Y, boxWidth, layout.ImageHeight);
    }

    // --- layout -------------------------------------------------------------------------------

    /// <summary>
    /// Horizontal offset of a block's text: list nesting and quotes both push content right so their
    /// marker or bar has room.
    /// </summary>
    public static float GetIndent(EditorTheme theme, MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Quote => theme.BlockIndent,
        MarkdownBlockKind.BulletItem or MarkdownBlockKind.OrderedItem or MarkdownBlockKind.TaskItem
            => theme.BlockIndent * (block.Level + 1),
        _ => 0f,
    };

    public static float GetSpacingAfter(EditorTheme theme, MarkdownBlock block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => theme.ParagraphSpacing * 1.5f,
        MarkdownBlockKind.BulletItem or MarkdownBlockKind.OrderedItem or MarkdownBlockKind.TaskItem => 2f,
        MarkdownBlockKind.CodeBlock or MarkdownBlockKind.Table or MarkdownBlockKind.Image => theme.ParagraphSpacing * 2f,
        _ => theme.ParagraphSpacing,
    };

    /// <summary>
    /// The glyph drawn in the gutter of a list item. Checkboxes are drawn as geometry, so task items
    /// report no text marker.
    /// </summary>
    public static bool TryGetMarker(MarkdownBlock block, out string? marker)
    {
        marker = block.Kind switch
        {
            MarkdownBlockKind.BulletItem => block.Level % 2 == 0 ? "\u2022" : "\u25E6",
            MarkdownBlockKind.OrderedItem => $"{block.Ordinal}.",
            _ => null,
        };

        return marker is not null;
    }

    /// <param name="revealSyntax">
    /// True for the block holding the caret: the fences and language tag join the body as ordinary
    /// editable source, so the markers can be read, selected or deleted in place.
    /// </param>
    public static BlockLayout BuildCodeBlock(
        ICanvasResourceCreator resourceCreator,
        EditorTheme theme,
        MarkdownBlock block,
        float availableWidth,
        bool revealSyntax)
    {
        float contentWidth = Math.Max(40f, availableWidth - (CodePadding * 2));
        string source = block.Inlines.Count > 0 ? block.Inlines[0].Text : string.Empty;

        var format = new CanvasTextFormat
        {
            FontFamily = theme.CodeFontFamily,
            FontSize = theme.GetFontSize(block),
            WordWrapping = CanvasWordWrapping.Wrap,
            LineSpacingMode = CanvasLineSpacingMode.Default,
        };

        // When there is real content between the fences and the block is not active, neither fence
        // line is drawn; the caret snaps from the hidden opening fence onto the first content line.
        // An empty block keeps both fences visible, otherwise there would be nothing left to click
        // or edit.
        int firstBreak = source.IndexOf('\n');
        bool hasContent = firstBreak >= 0 && firstBreak + 1 < source.Length && source[..firstBreak].TrimStart().StartsWith("```");

        string rendered = source;
        var segments = new List<LayoutSegment>();

        if (revealSyntax || !hasContent)
        {
            segments.Add(new LayoutSegment(0, block.Start, rendered.Length, isSynthetic: false));
        }
        else
        {
            rendered = rendered[(firstBreak + 1)..];
            segments.Add(new LayoutSegment(0, block.Start + firstBreak + 1, rendered.Length, isSynthetic: false));

            int lastBreak = rendered.LastIndexOf('\n');
            if (lastBreak >= 0)
            {
                LayoutSegment last = segments[^1];
                int tailLength = last.Length - (rendered.Length - lastBreak);
                rendered = rendered[..lastBreak];
                segments[^1] = new LayoutSegment(last.RenderStart, last.DocumentStart, tailLength, isSynthetic: false);
            }
        }

        var textLayout = new CanvasTextLayout(resourceCreator, rendered, format, contentWidth, 0f);
        textLayout.SetColor(0, rendered.Length, theme.Foreground);

        return new BlockLayout
        {
            Block = block,
            ContentX = CodePadding,
            ContentWidth = contentWidth,
            ShowsSyntax = revealSyntax,
            Text = new TextRegion(textLayout, rendered, new Vector2(CodePadding, CodePadding), segments, block.Start, block.End),
            Height = MarkdownLayoutEngine.MeasureHeight(textLayout, format) + (CodePadding * 2),
        };
    }

    public static BlockLayout BuildThematicBreak(EditorTheme theme, MarkdownBlock block) => new()
    {
        Block = block,
        ContentX = 0,
        ContentWidth = 0,
        Height = RuleHeight,
    };

    /// <summary>
    /// Reserves the box an image is drawn in: the picture's own size, never wider than
    /// <see cref="MaxImageSize"/> or the content width and never taller than <see cref="MaxImageSize"/>.
    /// The bitmap itself is resolved asynchronously by the control, so <paramref name="naturalSize"/>
    /// only answers once the bytes have arrived; until then the full capped square stands in as the
    /// placeholder. Nothing is drawn as a caption; the file name surfaces as a hover tooltip instead
    /// (see <c>MarkdownEditor.Images.cs</c>).
    /// </summary>
    public static BlockLayout BuildImage(
        MarkdownBlock block,
        float availableWidth,
        Func<string, global::Windows.Foundation.Size?>? naturalSize)
    {
        float capWidth = Math.Min(MaxImageSize, availableWidth);
        float boxWidth = capWidth;
        float boxHeight = MaxImageSize;

        if (block.Target is string target
            && naturalSize?.Invoke(target) is global::Windows.Foundation.Size size
            && size.Width > 0
            && size.Height > 0)
        {
            // Contain without upscaling, so a small attachment does not reserve a column of empty
            // space around itself and a wide banner is scaled down rather than cropped. The floor
            // keeps a 4000x40 panorama from collapsing into a sliver nothing can be clicked on.
            float scale = Math.Min(1f, Math.Min(capWidth / (float)size.Width, MaxImageSize / (float)size.Height));
            boxWidth = Math.Max(Math.Min(MinImageSize, capWidth), (float)size.Width * scale);
            boxHeight = Math.Max(MinImageSize, (float)size.Height * scale);
        }

        return new BlockLayout
        {
            Block = block,
            ContentWidth = availableWidth,
            ImageWidth = boxWidth,
            ImageHeight = boxHeight,
            Height = boxHeight,
        };
    }

    /// <summary>
    /// Lays a table out as an evenly divided grid, sized to the tallest cell in each row. While the
    /// caret is inside the table it instead renders the raw markdown in a monospaced box (like a
    /// code block) so rows and columns can be edited directly as text.
    /// </summary>
    public static BlockLayout BuildTable(ICanvasResourceCreator resourceCreator, EditorTheme theme, MarkdownBlock block, float availableWidth, bool editMode)
    {
        if (editMode)
        {
            return BuildTableRawEdit(resourceCreator, theme, block, availableWidth);
        }

        var layout = new BlockLayout
        {
            Block = block,
            ContentX = 0,
            ContentWidth = availableWidth,
        };

        int columnCount = 1;
        foreach (MarkdownTableRow row in block.Rows)
        {
            columnCount = Math.Max(columnCount, row.Cells.Count);
        }

        float columnWidth = Math.Max(60f, availableWidth / columnCount);
        for (int c = 0; c <= columnCount; c++)
        {
            layout.ColumnOffsets.Add(c * columnWidth);
        }

        float y = 0;
        for (int r = 0; r < block.Rows.Count; r++)
        {
            MarkdownTableRow row = block.Rows[r];
            layout.RowOffsets.Add(y);
            float rowHeight = 0;

            for (int c = 0; c < row.Cells.Count && c < columnCount; c++)
            {
                MarkdownTableCell cell = row.Cells[c];
                var format = new CanvasTextFormat
                {
                    FontFamily = theme.BodyFontFamily,
                    FontSize = theme.BodyFontSize,
                    WordWrapping = CanvasWordWrapping.WholeWord,
                };

                var (rendered, segments, runs) = ComposeCell(cell);
                var cellLayout = new CanvasTextLayout(resourceCreator, rendered, format, columnWidth - (CellPaddingX * 2), 0f);
                if (rendered.Length > 0)
                {
                    cellLayout.SetColor(0, rendered.Length, theme.Foreground);
                    ApplyCellFormatting(cellLayout, runs, theme, row.IsHeader);
                }

                var region = new TextRegion(
                    cellLayout,
                    rendered,
                    new Vector2(layout.ColumnOffsets[c] + CellPaddingX, y + CellPaddingY),
                    segments,
                    cell.Start,
                    cell.End,
                    runs);

                layout.Cells.Add(new TableCellLayout
                {
                    Region = region,
                    Row = r,
                    Column = c,
                    IsHeader = row.IsHeader,
                });

                rowHeight = Math.Max(rowHeight, MarkdownLayoutEngine.MeasureHeight(cellLayout, format));
            }

            y += rowHeight + (CellPaddingY * 2);
        }

        layout.RowOffsets.Add(y);
        layout.Height = y;
        return layout;
    }

    /// <summary>
    /// The active table rendered as raw markdown: monospaced source in a padded box, one segment
    /// covering the whole range so the caret moves freely across pipes, rows and the delimiter.
    /// </summary>
    private static BlockLayout BuildTableRawEdit(ICanvasResourceCreator resourceCreator, EditorTheme theme, MarkdownBlock block, float availableWidth)
    {
        float contentWidth = Math.Max(40f, availableWidth - (CodePadding * 2));
        string source = block.RawSource ?? string.Empty;

        var format = new CanvasTextFormat
        {
            FontFamily = theme.CodeFontFamily,
            FontSize = theme.GetFontSize(block),
            WordWrapping = CanvasWordWrapping.Wrap,
            LineSpacingMode = CanvasLineSpacingMode.Default,
        };

        var textLayout = new CanvasTextLayout(resourceCreator, source, format, contentWidth, 0f);
        textLayout.SetColor(0, source.Length, theme.Foreground);

        var segments = new List<LayoutSegment> { new(0, block.Start, source.Length, isSynthetic: false) };

        return new BlockLayout
        {
            Block = block,
            ContentX = CodePadding,
            ContentWidth = contentWidth,
            IsRawEdit = true,
            Text = new TextRegion(textLayout, source, new Vector2(CodePadding, CodePadding), segments, block.Start, block.End),
            Height = MarkdownLayoutEngine.MeasureHeight(textLayout, format) + (CodePadding * 2),
        };
    }

    /// <summary>
    /// Table cells always render formatted; their markdown punctuation is never revealed because the
    /// caret position inside a grid is already unambiguous.
    /// </summary>
    private static (string Rendered, List<LayoutSegment> Segments, List<(int Start, int Length, MarkdownInline Inline)> Runs) ComposeCell(
        MarkdownTableCell cell)
    {
        var builder = new System.Text.StringBuilder();
        var segments = new List<LayoutSegment>();
        var runs = new List<(int, int, MarkdownInline)>();

        foreach (MarkdownInline inline in cell.Inlines)
        {
            if (inline.IsSyntax || inline.Length == 0)
            {
                continue;
            }

            segments.Add(new LayoutSegment(builder.Length, inline.Start, inline.Length, isSynthetic: false));
            runs.Add((builder.Length, inline.Length, inline));
            builder.Append(inline.Text);
        }

        return (builder.ToString(), segments, runs);
    }

    /// <summary>
    /// Paints the per-span inline styles (bold, italic, code, link...) the parser resolved for the
    /// cell. Header cells are already bold, so the weight is left alone there.
    /// </summary>
    private static void ApplyCellFormatting(
        CanvasTextLayout cellLayout,
        List<(int Start, int Length, MarkdownInline Inline)> runs,
        EditorTheme theme,
        bool isHeader)
    {
        foreach ((int start, int length, MarkdownInline inline) in runs)
        {
            if (length <= 0)
            {
                continue;
            }

            InlineStyle style = inline.Style;

            if (style.HasFlag(InlineStyle.Code))
            {
                cellLayout.SetColor(start, length, theme.CodeForeground);
                cellLayout.SetFontFamily(start, length, theme.CodeFontFamily);
                cellLayout.SetFontSize(start, length, theme.BodyFontSize * 0.94f);
            }
            else if (style.HasFlag(InlineStyle.Link))
            {
                cellLayout.SetColor(start, length, theme.LinkForeground);
            }

            if (style.HasFlag(InlineStyle.Bold) && !isHeader)
            {
                cellLayout.SetFontWeight(start, length, FontWeights.ExtraBold);
            }

            if (style.HasFlag(InlineStyle.Italic))
            {
                cellLayout.SetFontStyle(start, length, FontStyle.Italic);
            }

            if (style.HasFlag(InlineStyle.Strikethrough))
            {
                cellLayout.SetStrikethrough(start, length, true);
            }
        }
    }

    // --- painting -----------------------------------------------------------------------------

    /// <summary>
    /// Paints the marker band behind highlighted spans. This runs after the selection fill but before
    /// the glyphs so the band sits under the text yet over the selection.
    /// </summary>
    /// <remarks>
    /// Inline code is deliberately absent: it is distinguished by colour and a monospaced family, not
    /// by a background. Only the rendered text runs reach this method - the regions built for code
    /// blocks and raw table editing carry no runs at all. A run's characters are merged into one box
    /// per visual line first, because the fill is translucent and overlapping per-character rectangles
    /// would compound into a darker patch.
    /// </remarks>
    public static void DrawInlineBackgrounds(CanvasDrawingSession session, EditorTheme theme, BlockLayout layout)
    {
        DrawRegionBackgrounds(session, theme, layout.Text, layout.Y);
        foreach (TableCellLayout cell in layout.Cells)
        {
            DrawRegionBackgrounds(session, theme, cell.Region, layout.Y);
        }
    }

    private static void DrawRegionBackgrounds(CanvasDrawingSession session, EditorTheme theme, TextRegion? region, float yOffset)
    {
        if (region is null || region.Runs.Count == 0 || region.RenderedText.Length == 0)
        {
            return;
        }

        foreach ((int start, int length, MarkdownInline inline) in region.Runs)
        {
            if (inline.IsSyntax || length <= 0 || !inline.Style.HasFlag(InlineStyle.Highlight))
            {
                continue;
            }

            int clampedStart = Math.Clamp(start, 0, region.RenderedText.Length);
            int clampedLength = Math.Min(length, region.RenderedText.Length - clampedStart);
            if (clampedLength <= 0)
            {
                continue;
            }

            // Merge consecutive character boxes that share a line into one run rectangle.
            var boxes = new List<Rect>();
            foreach (CanvasTextLayoutRegion charRegion in region.Layout.GetCharacterRegions(clampedStart, clampedLength))
            {
                Rect box = charRegion.LayoutBounds;
                if (box.Width <= 0 && box.Height <= 0)
                {
                    continue;
                }

                if (boxes.Count > 0 && Math.Abs(box.Y - boxes[^1].Y) < 1)
                {
                    Rect last = boxes[^1];
                    double left = Math.Min(last.X, box.X);
                    double top = Math.Min(last.Y, box.Y);
                    double right = Math.Max(last.X + last.Width, box.X + box.Width);
                    double bottom = Math.Max(last.Y + last.Height, box.Y + box.Height);
                    boxes[^1] = new Rect(left, top, right - left, bottom - top);
                }
                else
                {
                    boxes.Add(box);
                }
            }

            foreach (Rect box in boxes)
            {
                float x = (float)box.X + region.Origin.X;
                float y = (float)box.Y + region.Origin.Y + yOffset;
                session.FillRectangle(x, y, (float)box.Width, (float)box.Height, theme.HighlightBackground);
            }
        }
    }

    /// <summary>
    /// Draws the chrome behind a block (quote bar, code background, table grid, checkbox, rule). The
    /// text itself is drawn by the control afterwards so selection highlighting can sit between them.
    /// </summary>
    public static void DrawDecoration(CanvasDrawingSession session, EditorTheme theme, BlockLayout layout, float contentWidth)
    {
        MarkdownBlock block = layout.Block;
        float y = layout.Y;

        switch (block.Kind)
        {
            case MarkdownBlockKind.Quote:
                session.FillRectangle(2f, y, 3f, layout.Height, theme.QuoteBar);
                break;

            case MarkdownBlockKind.CodeBlock:
                session.FillRoundedRectangle(0, y, contentWidth, layout.Height, 6f, 6f, theme.CodeBackground);
                break;

            case MarkdownBlockKind.ThematicBreak:
                session.FillRectangle(0, y + ((RuleHeight - 3f) / 2f), contentWidth, 3f, theme.Divider);
                break;

            case MarkdownBlockKind.Image:
                if (layout.ImageHeight > 0)
                {
                    DrawImage(session, theme, layout);
                }

                break;

            case MarkdownBlockKind.Table:
                if (layout.IsRawEdit)
                {
                    session.FillRoundedRectangle(0, y, contentWidth, layout.Height, 6f, 6f, theme.CodeBackground);
                }
                else
                {
                    DrawTableGrid(session, theme, layout);
                }

                break;

            case MarkdownBlockKind.TaskItem:
                DrawCheckbox(session, theme, layout);
                break;
        }

        if (layout.MarkerLayout is not null)
        {
            float markerX = Math.Max(0, layout.ContentX - theme.BlockIndent + 4f);
            session.DrawTextLayout(layout.MarkerLayout, markerX, y, theme.SecondaryForeground);
        }
    }

    /// <summary>
    /// Draws an image block: the decoded bitmap scaled to fit inside the reserved box and centred, or
    /// the grey placeholder when the bytes have not arrived (or never will).
    /// </summary>
    private static void DrawImage(CanvasDrawingSession session, EditorTheme theme, BlockLayout layout)
    {
        Rect box = GetImageBounds(layout);

        if (layout.Image is null)
        {
            session.FillRoundedRectangle((float)box.X, (float)box.Y, (float)box.Width, (float)box.Height, 6f, 6f, theme.CodeBackground);
            session.DrawRoundedRectangle((float)box.X + 0.5f, (float)box.Y + 0.5f, (float)box.Width - 1f, (float)box.Height - 1f, 6f, 6f, theme.Divider);
            return;
        }

        // CanvasBitmap.Size is a Windows.Foundation.Size whose members are doubles.
        float pixelWidth = (float)layout.Image.Size.Width;
        float pixelHeight = (float)layout.Image.Size.Height;
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }

        // Contain, never upscale: a small attachment enlarged to fill the box would only blur.
        float scale = Math.Min(1f, Math.Min((float)box.Width / pixelWidth, (float)box.Height / pixelHeight));
        float width = pixelWidth * scale;
        float height = pixelHeight * scale;
        float left = (float)box.X + ((float)box.Width - width) / 2f;
        float top = (float)box.Y + ((float)box.Height - height) / 2f;

        session.DrawImage(layout.Image, new Rect(left, top, width, height));
        session.DrawRoundedRectangle(left + 0.5f, top + 0.5f, width - 1f, height - 1f, 4f, 4f, theme.Divider);
    }

    private static void DrawTableGrid(CanvasDrawingSession session, EditorTheme theme, BlockLayout layout)
    {
        float top = layout.Y;
        float bottom = layout.Y + layout.Height;
        float right = layout.ColumnOffsets.Count > 0 ? layout.ColumnOffsets[^1] : 0;

        if (layout.RowOffsets.Count > 1)
        {
            // Shade the header row so the table reads as a table even without borders.
            session.FillRectangle(0, top, right, layout.RowOffsets[1] - layout.RowOffsets[0], theme.CodeBackground);
        }

        foreach (float x in layout.ColumnOffsets)
        {
            session.DrawLine(x, top, x, bottom, theme.Divider);
        }

        foreach (float rowOffset in layout.RowOffsets)
        {
            session.DrawLine(0, top + rowOffset, right, top + rowOffset, theme.Divider);
        }
    }

    private static void DrawCheckbox(CanvasDrawingSession session, EditorTheme theme, BlockLayout layout)
    {
        const float Size = 13f;
        float x = Math.Max(0, layout.ContentX - theme.BlockIndent + 4f);
        float y = layout.Y + 4f;

        if (layout.Block.IsChecked)
        {
            session.FillRoundedRectangle(x, y, Size, Size, 3f, 3f, theme.LinkForeground);
            using var check = new CanvasPathBuilder(session);
            check.BeginFigure(x + 3f, y + 6.5f);
            check.AddLine(x + 5.5f, y + 9.5f);
            check.AddLine(x + 10f, y + 3.5f);
            check.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(check);
            session.DrawGeometry(geometry, Colors.White, 1.6f);
        }
        else
        {
            session.DrawRoundedRectangle(x + 0.5f, y + 0.5f, Size - 1f, Size - 1f, 3f, 3f, theme.SecondaryForeground);
        }
    }

    /// <summary>
    /// Bounds of the clickable checkbox, used to toggle a task item by pointer.
    /// </summary>
    public static Rect GetCheckboxBounds(EditorTheme theme, BlockLayout layout)
    {
        float x = Math.Max(0, layout.ContentX - theme.BlockIndent + 4f);
        return new Rect(x - 2, layout.Y + 2, 17, 17);
    }
}
