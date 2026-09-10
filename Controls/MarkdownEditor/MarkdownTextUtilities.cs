using System.Text;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// Text projections derived from the markdown source.
/// </summary>
internal static class MarkdownTextUtilities
{
    /// <summary>
    /// Strips markdown punctuation to produce readable plain text.
    /// </summary>
    /// <remarks>
    /// Reuses the parser rather than a regex pass so the result always matches what the editor draws:
    /// every span the renderer would hide is exactly the span dropped here.
    /// </remarks>
    public static string ToPlainText(MarkdownDocument document)
        => ToPlainText(document, 0);

    /// <summary>
    /// Projects the markdown from <paramref name="sourcePosition"/> onward to plain text.
    /// </summary>
    /// <remarks>
    /// The cut is exact rather than block-aligned: text before the position is dropped even when it
    /// shares a paragraph with text after it, so a caller handed the offset at the top of a scrolled
    /// viewport gets a projection that starts on that very line.
    /// </remarks>
    public static string ToPlainText(string markdown, int sourcePosition)
    {
        var document = new MarkdownDocument();
        document.SetText(markdown);
        return ToPlainText(document, sourcePosition);
    }

    private static string ToPlainText(MarkdownDocument document, int sourcePosition)
    {
        var builder = new StringBuilder();

        foreach (MarkdownBlock block in MarkdownParser.Parse(document))
        {
            if (block.End < sourcePosition)
            {
                continue;
            }

            if (block.Kind == MarkdownBlockKind.Table)
            {
                foreach (MarkdownTableRow row in block.Rows)
                {
                    for (int i = 0; i < row.Cells.Count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append('\t');
                        }

                        AppendInlines(builder, row.Cells[i].Inlines, sourcePosition);
                    }

                    builder.Append('\n');
                }

                continue;
            }

            AppendInlines(builder, block.Inlines, sourcePosition);
            builder.Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static void AppendInlines(
        StringBuilder builder,
        System.Collections.Generic.IReadOnlyList<MarkdownInline> inlines,
        int sourcePosition)
    {
        foreach (MarkdownInline inline in inlines)
        {
            if (inline.IsSyntax || inline.End <= sourcePosition)
            {
                continue;
            }

            // The straddling inline is the only one that needs slicing; everything after it starts
            // past the cut and is appended whole.
            builder.Append(inline.Start >= sourcePosition
                ? inline.Text
                : inline.Text[(sourcePosition - inline.Start)..]);
        }
    }
}
