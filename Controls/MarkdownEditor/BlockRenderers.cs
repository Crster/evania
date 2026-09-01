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

    public static BlockLayout BuildCodeBlock(ICanvasResourceCreator resourceCreator, EditorTheme theme, MarkdownBlock block, float availableWidth)
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

        // When there is real content between the fences, neither fence line is drawn; the caret
        // snaps from the hidden opening fence onto the first content line. An empty block keeps both
        // fences visible, otherwise there would be nothing left to click or edit.
        int firstBreak = source.IndexOf('\n');
        bool hasContent = firstBreak >= 0 && firstBreak + 1 < source.Length && source[..firstBreak].TrimStart().StartsWith("```");

        string rendered = source;
        var segments = new List<LayoutSegment>();

        if (hasContent)
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
        else
        {
            segments.Add(new LayoutSegment(0, block.Start, rendered.Length, isSynthetic: false));
        }

        var textLayout = new CanvasTextLayout(resourceCreator, rendered, format, contentWidth, 0f);
        textLayout.SetColor(0, rendered.Length, theme.Foreground);

        return new BlockLayout
        {
            Block = block,
            ContentX = CodePadding,
            ContentWidth = contentWidth,
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
    /// Reserves a fixed box for an image. The bitmap itself is resolved asynchronously by the control,
    /// so the layout only needs a stable placeholder size.
    /// </summary>
    public static BlockLayout BuildImage(
        ICanvasResourceCreator resourceCreator,
        EditorTheme theme,
        MarkdownBlock block,
        float availableWidth)
    {
        var caption = new CanvasTextFormat
        {
            FontFamily = theme.BodyFontFamily,
            FontSize = theme.BodyFontSize * 0.85f,
            WordWrapping = CanvasWordWrapping.Wrap,
        };

        string alt = string.IsNullOrWhiteSpace(block.AltText) ? "Image" : block.AltText!;
        var captionLayout = new CanvasTextLayout(resourceCreator, alt, caption, Math.Max(40f, availableWidth), 0f);
        captionLayout.SetColor(0, alt.Length, theme.SecondaryForeground);

        float imageHeight = Math.Min(320f, availableWidth * 0.5f);
        return new BlockLayout
        {
            Block = block,
            ContentWidth = availableWidth,
            ImageHeight = imageHeight,
            Text = new TextRegion(captionLayout, alt, new Vector2(0, imageHeight + 4f), new List<LayoutSegment> { new(0, block.Start, 0, true) }, block.Start, block.End),
            Height = imageHeight + 4f + MarkdownLayoutEngine.MeasureHeight(captionLayout, caption),
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
                    cell.End);

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
                session.FillRectangle(0, y + (RuleHeight / 2f), contentWidth, 1f, theme.Divider);
                break;

            case MarkdownBlockKind.Image:
                if (layout.ImageHeight > 0)
                {
                    session.FillRoundedRectangle(0, y, contentWidth, layout.ImageHeight, 6f, 6f, theme.CodeBackground);
                    session.DrawRoundedRectangle(0.5f, y + 0.5f, contentWidth - 1f, layout.ImageHeight - 1f, 6f, 6f, theme.Divider);
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
