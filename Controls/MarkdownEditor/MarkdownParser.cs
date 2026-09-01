using System;
using System.Collections.Generic;

namespace Evania.Controls.MarkdownEditor;

[Flags]
public enum InlineStyle
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Strikethrough = 1 << 2,
    Code = 1 << 3,
    Link = 1 << 4,
}

public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    Quote,
    BulletItem,
    OrderedItem,
    TaskItem,
    CodeBlock,
    Table,
    ThematicBreak,
    Image,
    Blank,
}

/// <summary>
/// A contiguous slice of the markdown source with a resolved visual style.
/// </summary>
/// <remarks>
/// Every character of a block's source belongs to exactly one span. Spans flagged
/// <see cref="IsSyntax"/> are the markdown punctuation (<c>##</c>, <c>**</c>, <c>](url)</c>...); the
/// layout engine hides them unless the caret is on that block, which is what makes the editor feel
/// WYSIWYG while keeping markdown as the single source of truth. Because the spans tile the source
/// with no gaps, mapping a caret offset to a rendered position stays a simple walk.
/// </remarks>
public sealed class MarkdownInline
{
    public MarkdownInline(int start, string text, InlineStyle style, bool isSyntax = false, string? linkTarget = null)
    {
        Start = start;
        Text = text;
        Style = style;
        IsSyntax = isSyntax;
        LinkTarget = linkTarget;
    }

    public int Start { get; }

    public string Text { get; }

    public int Length => Text.Length;

    public int End => Start + Text.Length;

    public InlineStyle Style { get; }

    public bool IsSyntax { get; }

    public string? LinkTarget { get; }
}

public sealed class MarkdownTableCell
{
    public MarkdownTableCell(int start, int end, IReadOnlyList<MarkdownInline> inlines)
    {
        Start = start;
        End = end;
        Inlines = inlines;
    }

    public int Start { get; }

    public int End { get; }

    public IReadOnlyList<MarkdownInline> Inlines { get; }
}

public sealed class MarkdownTableRow
{
    public MarkdownTableRow(bool isHeader, IReadOnlyList<MarkdownTableCell> cells)
    {
        IsHeader = isHeader;
        Cells = cells;
    }

    public bool IsHeader { get; }

    public IReadOnlyList<MarkdownTableCell> Cells { get; }
}

public sealed class MarkdownBlock
{
    public MarkdownBlockKind Kind { get; init; }

    /// <summary>Inclusive start offset in the document.</summary>
    public int Start { get; init; }

    /// <summary>Exclusive end offset in the document, excluding the trailing newline.</summary>
    public int End { get; init; }

    /// <summary>Heading level (1-6) or list nesting depth (0-based).</summary>
    public int Level { get; init; }

    /// <summary>Ordinal shown for <see cref="MarkdownBlockKind.OrderedItem"/>.</summary>
    public int Ordinal { get; init; }

    public bool IsChecked { get; init; }

    /// <summary>Info string of a fenced code block, if any.</summary>
    public string? Language { get; init; }

    /// <summary>Target of an image block, or of the first link in the block.</summary>
    public string? Target { get; init; }

    /// <summary>Alt text of an image block.</summary>
    public string? AltText { get; init; }

    public IReadOnlyList<MarkdownInline> Inlines { get; init; } = Array.Empty<MarkdownInline>();

    public IReadOnlyList<MarkdownTableRow> Rows { get; init; } = Array.Empty<MarkdownTableRow>();

    /// <summary>The table's raw markdown source, kept so the active table can be edited as text.</summary>
    public string? RawSource { get; init; }

    public bool Contains(int position) => position >= Start && position <= End;
}

/// <summary>
/// A line-oriented markdown parser producing blocks whose spans tile the source exactly.
/// </summary>
/// <remarks>
/// This is deliberately not a CommonMark implementation. It covers the feature set the note editor
/// exposes (headings, quotes, lists, task lists, fenced code, tables, images, and the inline styles)
/// and, critically, preserves source offsets for every span so the editor can round-trip between
/// caret positions and rendered glyphs.
/// </remarks>
public static class MarkdownParser
{
    public static IReadOnlyList<MarkdownBlock> Parse(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var blocks = new List<MarkdownBlock>();
        int lineCount = document.LineCount;

        for (int line = 0; line < lineCount; line++)
        {
            int start = document.GetLineStart(line);
            int end = document.GetLineEnd(line);
            string text = document.GetText(start, end - start);

            if (TryParseFencedCode(document, ref line, start, text, out MarkdownBlock? code))
            {
                blocks.Add(code!);
                continue;
            }

            if (TryParseTable(document, ref line, start, text, out MarkdownBlock? table))
            {
                blocks.Add(table!);
                continue;
            }

            blocks.Add(ParseLine(start, text));
        }

        return blocks;
    }

    private static MarkdownBlock ParseLine(int start, string text)
    {
        if (text.Length == 0)
        {
            return new MarkdownBlock { Kind = MarkdownBlockKind.Blank, Start = start, End = start };
        }

        int end = start + text.Length;
        int indent = CountLeadingSpaces(text);
        string body = text[indent..];

        if (IsThematicBreak(body))
        {
            return new MarkdownBlock
            {
                Kind = MarkdownBlockKind.ThematicBreak,
                Start = start,
                End = end,
                Inlines = new[] { new MarkdownInline(start, text, InlineStyle.None, isSyntax: true) },
            };
        }

        if (TryMatchHeading(body, out int level, out int headingMarkerLength))
        {
            int contentStart = start + indent + headingMarkerLength;
            var inlines = new List<MarkdownInline>
            {
                new(start, text[..(indent + headingMarkerLength)], InlineStyle.None, isSyntax: true),
            };
            inlines.AddRange(ParseInlines(contentStart, text[(indent + headingMarkerLength)..]));
            return new MarkdownBlock
            {
                Kind = MarkdownBlockKind.Heading,
                Start = start,
                End = end,
                Level = level,
                Inlines = inlines,
            };
        }

        if (body.StartsWith("> ", StringComparison.Ordinal) || body == ">")
        {
            int markerLength = body.StartsWith("> ", StringComparison.Ordinal) ? 2 : 1;
            var inlines = new List<MarkdownInline>
            {
                new(start, text[..(indent + markerLength)], InlineStyle.None, isSyntax: true),
            };
            inlines.AddRange(ParseInlines(start + indent + markerLength, text[(indent + markerLength)..]));
            return new MarkdownBlock
            {
                Kind = MarkdownBlockKind.Quote,
                Start = start,
                End = end,
                Inlines = inlines,
            };
        }

        if (TryMatchListMarker(body, out bool ordered, out int ordinal, out int markerLen))
        {
            string afterMarker = body[markerLen..];
            bool isTask = TryMatchTask(afterMarker, out bool isChecked, out int taskLen);
            int totalMarker = indent + markerLen + (isTask ? taskLen : 0);

            var inlines = new List<MarkdownInline>
            {
                new(start, text[..totalMarker], InlineStyle.None, isSyntax: true),
            };
            inlines.AddRange(ParseInlines(start + totalMarker, text[totalMarker..]));

            MarkdownBlockKind kind = isTask
                ? MarkdownBlockKind.TaskItem
                : ordered ? MarkdownBlockKind.OrderedItem : MarkdownBlockKind.BulletItem;

            return new MarkdownBlock
            {
                Kind = kind,
                Start = start,
                End = end,
                // Four spaces (or a tab, normalized to four) per nesting level.
                Level = indent / 4,
                Ordinal = ordinal,
                IsChecked = isChecked,
                Inlines = inlines,
            };
        }

        if (TryMatchStandaloneImage(body, out string? alt, out string? target))
        {
            return new MarkdownBlock
            {
                Kind = MarkdownBlockKind.Image,
                Start = start,
                End = end,
                AltText = alt,
                Target = target,
                Inlines = new[] { new MarkdownInline(start, text, InlineStyle.None, isSyntax: true) },
            };
        }

        return new MarkdownBlock
        {
            Kind = MarkdownBlockKind.Paragraph,
            Start = start,
            End = end,
            Inlines = ParseInlines(start, text),
        };
    }

    // --- block probes -------------------------------------------------------------------------

    private static bool TryParseFencedCode(MarkdownDocument document, ref int line, int start, string text, out MarkdownBlock? block)
    {
        block = null;
        string trimmed = text.TrimStart();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return false;
        }

        string language = trimmed[3..].Trim();
        int lastLine = line;
        int lineCount = document.LineCount;

        // Scan for the closing fence; an unterminated fence runs to the end of the document, which is
        // what the user sees while they are still typing it.
        for (int probe = line + 1; probe < lineCount; probe++)
        {
            lastLine = probe;
            if (document.GetLineText(probe).TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                break;
            }

            if (probe == lineCount - 1)
            {
                lastLine = probe;
            }
        }

        if (lastLine == line)
        {
            lastLine = Math.Min(line, lineCount - 1);
        }

        int end = document.GetLineEnd(lastLine);
        string source = document.GetText(start, end - start);

        block = new MarkdownBlock
        {
            Kind = MarkdownBlockKind.CodeBlock,
            Start = start,
            End = end,
            Language = string.IsNullOrEmpty(language) ? null : language,
            Inlines = new[] { new MarkdownInline(start, source, InlineStyle.Code) },
        };

        line = lastLine;
        return true;
    }

    private static bool TryParseTable(MarkdownDocument document, ref int line, int start, string text, out MarkdownBlock? block)
    {
        block = null;
        if (!LooksLikeTableRow(text) || line + 1 >= document.LineCount)
        {
            return false;
        }

        // A table only exists if the second line is the delimiter row; otherwise the pipes are just
        // ordinary text and must keep rendering as a paragraph.
        if (!IsTableDelimiterRow(document.GetLineText(line + 1)))
        {
            return false;
        }

        var rows = new List<MarkdownTableRow>
        {
            new(isHeader: true, ParseTableCells(start, text)),
        };

        int lastLine = line + 1;
        for (int probe = line + 2; probe < document.LineCount; probe++)
        {
            string rowText = document.GetLineText(probe);
            if (!LooksLikeTableRow(rowText))
            {
                break;
            }

            rows.Add(new MarkdownTableRow(isHeader: false, ParseTableCells(document.GetLineStart(probe), rowText)));
            lastLine = probe;
        }

        block = new MarkdownBlock
        {
            Kind = MarkdownBlockKind.Table,
            Start = start,
            End = document.GetLineEnd(lastLine),
            Rows = rows,
            RawSource = document.GetText(start, document.GetLineEnd(lastLine) - start),
        };

        line = lastLine;
        return true;
    }

    private static IReadOnlyList<MarkdownTableCell> ParseTableCells(int lineStart, string text)
    {
        var cells = new List<MarkdownTableCell>();
        int index = 0;
        if (index < text.Length && text[index] == '|')
        {
            index++;
        }

        int cellStart = index;
        for (; index <= text.Length; index++)
        {
            bool atEnd = index == text.Length;
            if (!atEnd && text[index] != '|')
            {
                continue;
            }

            string raw = text[cellStart..index];
            // A trailing pipe produces an empty final cell that no renderer should show.
            if (!(atEnd && raw.Trim().Length == 0 && cells.Count > 0))
            {
                // Only the leading padding is trimmed. Keeping the trailing padding inside the
                // cell's range and its inlines lets the caret sit past the last visible character
                // (after a space) instead of snapping back onto it.
                int trimOffset = raw.Length - raw.TrimStart().Length;
                string content = raw[trimOffset..];
                int contentStart = lineStart + cellStart + trimOffset;
                cells.Add(new MarkdownTableCell(contentStart, contentStart + content.Length, ParseInlines(contentStart, content)));
            }

            cellStart = index + 1;
        }

        return cells;
    }

    private static bool LooksLikeTableRow(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length > 0 && trimmed.Contains('|');
    }

    private static bool IsTableDelimiterRow(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || !trimmed.Contains('-'))
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (c is not ('|' or '-' or ':' or ' '))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsThematicBreak(string body)
    {
        string trimmed = body.Replace(" ", string.Empty);
        return trimmed.Length >= 3
            && (AllSame(trimmed, '-') || AllSame(trimmed, '*') || AllSame(trimmed, '_'));
    }

    private static bool AllSame(string value, char c)
    {
        foreach (char ch in value)
        {
            if (ch != c)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryMatchHeading(string body, out int level, out int markerLength)
    {
        level = 0;
        markerLength = 0;
        int hashes = 0;
        while (hashes < body.Length && body[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is < 1 or > 6)
        {
            return false;
        }

        if (hashes >= body.Length || body[hashes] != ' ')
        {
            return false;
        }

        level = hashes;
        markerLength = hashes + 1;
        return true;
    }

    private static bool TryMatchListMarker(string body, out bool ordered, out int ordinal, out int markerLength)
    {
        ordered = false;
        ordinal = 0;
        markerLength = 0;

        if (body.Length >= 2 && body[0] is '-' or '*' or '+' && body[1] == ' ')
        {
            markerLength = 2;
            return true;
        }

        int digits = 0;
        while (digits < body.Length && char.IsAsciiDigit(body[digits]))
        {
            digits++;
        }

        if (digits == 0 || digits + 1 >= body.Length)
        {
            return false;
        }

        if (body[digits] is not ('.' or ')') || body[digits + 1] != ' ')
        {
            return false;
        }

        ordered = true;
        ordinal = int.Parse(body[..digits]);
        markerLength = digits + 2;
        return true;
    }

    private static bool TryMatchTask(string afterMarker, out bool isChecked, out int length)
    {
        isChecked = false;
        length = 0;
        if (afterMarker.Length < 3 || afterMarker[0] != '[' || afterMarker[2] != ']')
        {
            return false;
        }

        char state = afterMarker[1];
        if (state is not (' ' or 'x' or 'X'))
        {
            return false;
        }

        isChecked = state is 'x' or 'X';
        length = afterMarker.Length > 3 && afterMarker[3] == ' ' ? 4 : 3;
        return true;
    }

    private static bool TryMatchStandaloneImage(string body, out string? alt, out string? target)
    {
        alt = null;
        target = null;
        if (!body.StartsWith("![", StringComparison.Ordinal) || !body.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        int closeBracket = body.IndexOf("](", StringComparison.Ordinal);
        if (closeBracket < 0)
        {
            return false;
        }

        alt = body[2..closeBracket];
        target = body[(closeBracket + 2)..^1];
        return true;
    }

    private static int CountLeadingSpaces(string text)
    {
        int i = 0;
        while (i < text.Length && text[i] == ' ')
        {
            i++;
        }

        return i;
    }

    // --- inline scanning ----------------------------------------------------------------------

    /// <summary>
    /// Splits a line of markdown into styled spans that tile <paramref name="text"/> exactly.
    /// </summary>
    public static IReadOnlyList<MarkdownInline> ParseInlines(int offset, string text)
    {
        var result = new List<MarkdownInline>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        int plainStart = 0;
        int i = 0;

        void FlushPlain(int upTo)
        {
            if (upTo > plainStart)
            {
                result.Add(new MarkdownInline(offset + plainStart, text[plainStart..upTo], InlineStyle.None));
            }
        }

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '`')
            {
                // A doubled backtick is the highlight delimiter, handled further below.
                bool doubled = (i + 1 < text.Length && text[i + 1] == '`')
                    || (i > plainStart && text[i - 1] == '`');
                int close = doubled ? -1 : text.IndexOf('`', i + 1);
                if (close > i)
                {
                    FlushPlain(i);
                    result.Add(new MarkdownInline(offset + i, "`", InlineStyle.Code, isSyntax: true));
                    result.Add(new MarkdownInline(offset + i + 1, text[(i + 1)..close], InlineStyle.Code));
                    result.Add(new MarkdownInline(offset + close, "`", InlineStyle.Code, isSyntax: true));
                    i = close + 1;
                    plainStart = i;
                    continue;
                }
            }
            else if ((c == '!' && i + 1 < text.Length && text[i + 1] == '[') || c == '[')
            {
                if (TryReadLink(text, i, out int altStart, out int altEnd, out int targetStart, out int targetEnd, out int consumedEnd))
                {
                    FlushPlain(i);
                    string target = text[targetStart..targetEnd];
                    result.Add(new MarkdownInline(offset + i, text[i..altStart], InlineStyle.Link, isSyntax: true));
                    result.AddRange(ApplyStyle(ParseInlines(offset + altStart, text[altStart..altEnd]), InlineStyle.Link, target));
                    result.Add(new MarkdownInline(offset + altEnd, text[altEnd..consumedEnd], InlineStyle.Link, isSyntax: true, linkTarget: target));
                    i = consumedEnd;
                    plainStart = i;
                    continue;
                }
            }
            else if (TryReadDelimited(text, i, "***", out int inner1Start, out int inner1End, out int end1))
            {
                FlushPlain(i);
                AddDelimited(result, offset, text, i, inner1Start, inner1End, end1, InlineStyle.Bold | InlineStyle.Italic);
                i = end1;
                plainStart = i;
                continue;
            }
            else if (TryReadDelimited(text, i, "**", out int inner2Start, out int inner2End, out int end2)
                || TryReadDelimited(text, i, "__", out inner2Start, out inner2End, out end2))
            {
                FlushPlain(i);
                AddDelimited(result, offset, text, i, inner2Start, inner2End, end2, InlineStyle.Bold);
                i = end2;
                plainStart = i;
                continue;
            }
            else if (TryReadDelimited(text, i, "~~", out int inner3Start, out int inner3End, out int end3))
            {
                FlushPlain(i);
                AddDelimited(result, offset, text, i, inner3Start, inner3End, end3, InlineStyle.Strikethrough);
                i = end3;
                plainStart = i;
                continue;
            }
            else if (TryReadDelimited(text, i, "``", out int inner5Start, out int inner5End, out int end5))
            {
                // Highlight (``...``) renders as inline code until it gets its own style.
                FlushPlain(i);
                AddDelimited(result, offset, text, i, inner5Start, inner5End, end5, InlineStyle.Code);
                i = end5;
                plainStart = i;
                continue;
            }
            else if (TryReadDelimited(text, i, "*", out int inner4Start, out int inner4End, out int end4)
                || TryReadDelimited(text, i, "_", out inner4Start, out inner4End, out end4))
            {
                FlushPlain(i);
                AddDelimited(result, offset, text, i, inner4Start, inner4End, end4, InlineStyle.Italic);
                i = end4;
                plainStart = i;
                continue;
            }

            i++;
        }

        FlushPlain(text.Length);
        return result;
    }

    private static void AddDelimited(
        List<MarkdownInline> result,
        int offset,
        string text,
        int start,
        int innerStart,
        int innerEnd,
        int end,
        InlineStyle style)
    {
        result.Add(new MarkdownInline(offset + start, text[start..innerStart], style, isSyntax: true));
        result.AddRange(ApplyStyle(ParseInlines(offset + innerStart, text[innerStart..innerEnd]), style, linkTarget: null));
        result.Add(new MarkdownInline(offset + innerEnd, text[innerEnd..end], style, isSyntax: true));
    }

    private static IEnumerable<MarkdownInline> ApplyStyle(IReadOnlyList<MarkdownInline> inlines, InlineStyle style, string? linkTarget)
    {
        foreach (MarkdownInline inline in inlines)
        {
            yield return new MarkdownInline(
                inline.Start,
                inline.Text,
                inline.Style | style,
                inline.IsSyntax,
                inline.LinkTarget ?? linkTarget);
        }
    }

    private static bool TryReadDelimited(string text, int index, string delimiter, out int innerStart, out int innerEnd, out int end)
    {
        innerStart = 0;
        innerEnd = 0;
        end = 0;

        if (index + delimiter.Length >= text.Length
            || string.CompareOrdinal(text, index, delimiter, 0, delimiter.Length) != 0)
        {
            return false;
        }

        innerStart = index + delimiter.Length;

        // An emphasis run may not open on whitespace, otherwise "a * b * c" would style as italic.
        if (innerStart < text.Length && text[innerStart] == ' ')
        {
            return false;
        }

        int close = text.IndexOf(delimiter, innerStart, StringComparison.Ordinal);
        if (close <= innerStart)
        {
            return false;
        }

        innerEnd = close;
        end = close + delimiter.Length;
        return true;
    }

    private static bool TryReadLink(
        string text,
        int index,
        out int altStart,
        out int altEnd,
        out int targetStart,
        out int targetEnd,
        out int end)
    {
        altStart = altEnd = targetStart = targetEnd = end = 0;

        int bracket = text[index] == '!' ? index + 1 : index;
        if (bracket >= text.Length || text[bracket] != '[')
        {
            return false;
        }

        int closeBracket = text.IndexOf(']', bracket + 1);
        if (closeBracket < 0 || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(')
        {
            return false;
        }

        int closeParen = text.IndexOf(')', closeBracket + 2);
        if (closeParen < 0)
        {
            return false;
        }

        altStart = bracket + 1;
        altEnd = closeBracket;
        targetStart = closeBracket + 2;
        targetEnd = closeParen;
        end = closeParen + 1;
        return true;
    }
}
