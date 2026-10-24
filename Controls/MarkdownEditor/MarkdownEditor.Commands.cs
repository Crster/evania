using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// The public formatting surface and clipboard support for <see cref="MarkdownEditor"/>.
/// </summary>
/// <remarks>
/// Every command is expressed as a markdown source edit rather than as a styling operation, which is
/// what keeps the document the single source of truth: toggling bold really does insert or remove
/// <c>**</c>. Inline commands wrap or unwrap the selection; block commands rewrite the line prefix of
/// each selected line.
/// </remarks>
public sealed partial class MarkdownEditor
{
    // --- inline commands ----------------------------------------------------------------------

    public void ToggleBold() => ToggleWrap("**");

    public void ToggleItalic() => ToggleWrap("*");

    public void ToggleStrikethrough() => ToggleWrap("~~");

    /// <summary>Inline code, wrapped in double backticks and drawn in a monospaced face.</summary>
    public void ToggleInlineCode() => ToggleWrap("``");

    /// <summary>Highlight, wrapped in equals signs and drawn with a marker band behind the text.</summary>
    public void ToggleHighlight() => ToggleWrap("==");

    /// <summary>
    /// Wraps the selection in <paramref name="delimiter"/>, or unwraps it when it is already wrapped.
    /// </summary>
    /// <remarks>
    /// With an empty selection the delimiters are inserted and the caret parked between them, so the
    /// user can hit Ctrl+B and keep typing.
    /// </remarks>
    private void ToggleWrap(string delimiter)
    {
        if (IsReadOnly)
        {
            return;
        }


        int min = selection.Min;
        int max = selection.Max;
        int length = delimiter.Length;

        bool insideDelimiters = min >= length
            && max + length <= document.Length
            && document.GetText(min - length, length) == delimiter
            && document.GetText(max, length) == delimiter;

        if (insideDelimiters)
        {
            string inner = document.GetText(min, max - min);
            document.Replace(min - length, (max - min) + (length * 2), inner, MarkdownEditKind.Structural, selection);
            SetSelection(new TextRange(min - length, (min - length) + inner.Length));
            return;
        }

        string selected = document.GetText(min, max - min);
        if (selected.StartsWith(delimiter, StringComparison.Ordinal)
            && selected.EndsWith(delimiter, StringComparison.Ordinal)
            && selected.Length >= length * 2)
        {
            string inner = selected[length..^length];
            document.Replace(min, selected.Length, inner, MarkdownEditKind.Structural, selection);
            SetSelection(new TextRange(min, min + inner.Length));
            return;
        }

        document.Replace(min, selected.Length, delimiter + selected + delimiter, MarkdownEditKind.Structural, selection);
        SetSelection(selected.Length == 0
            ? TextRange.Caret(min + length)
            : new TextRange(min + length, min + length + selected.Length));
    }

    public void InsertLink(string? target = null, string? text = null)
    {
        if (IsReadOnly)
        {
            return;
        }

        string label = text ?? document.GetText(selection);
        string url = target ?? "https://";
        string markdown = $"[{label}]({url})";

        int min = selection.Min;
        document.Replace(min, selection.Length, markdown, MarkdownEditKind.Structural, selection);

        // Select the URL so it can be replaced by typing straight away.
        int urlStart = min + label.Length + 3;
        SetSelection(new TextRange(urlStart, urlStart + url.Length));
    }

    public void InsertImage(string target, string? altText = null)
    {
        if (IsReadOnly)
        {
            return;
        }

        string markdown = $"![{altText ?? "image"}]({target})";
        InsertBlock(markdown);
    }

    /// <summary>
    /// Inserts a table. When the caret is already inside one, the button becomes a toggle between
    /// the rendered grid and the raw markdown source, so rows and columns can be edited as text.
    /// </summary>
    public void InsertTable(int columns = 3, int rows = 2)
    {
        if (IsReadOnly)
        {
            return;
        }

        MarkdownBlock? table = FindTableBlock(selection.Min);
        if (table is not null)
        {
            editModeTableStart = editModeTableStart == table.Start ? -1 : table.Start;
            engine.Invalidate();
            UpdateEditorLayout();
            return;
        }

        var builder = new StringBuilder();
        builder.Append('|');
        for (int c = 0; c < columns; c++)
        {
            builder.Append(" Column ").Append(c + 1).Append(" |");
        }

        builder.Append('\n').Append('|');
        for (int c = 0; c < columns; c++)
        {
            builder.Append(" --- |");
        }

        for (int r = 0; r < rows; r++)
        {
            builder.Append('\n').Append('|');
            for (int c = 0; c < columns; c++)
            {
                builder.Append("   |");
            }
        }

        InsertBlock(builder.ToString());
    }

    public void InsertHorizontalRule() => InsertBlock("---");

    public void InsertDateTime() => InsertText(DateTime.Now.ToString("g"));

    public void InsertUuid7() => InsertText(Guid.CreateVersion7().ToString());

    /// <summary>
    /// Wraps the selection in a fenced code block, or removes the surrounding fences when the
    /// selection or caret is already inside one.
    /// </summary>
    public void ToggleCodeBlock(string? language = null)
    {
        if (IsReadOnly)
        {
            return;
        }

        if (TryRemoveCodeBlock())
        {
            return;
        }

        string selected = document.GetText(selection);
        string fence = $"```{language ?? string.Empty}\n{selected}\n```";
        int min = selection.Min;
        document.Replace(min, selection.Length, fence, MarkdownEditKind.Structural, selection);

        int contentStart = min + 4 + (language?.Length ?? 0);
        SetSelection(new TextRange(contentStart, contentStart + selected.Length));
    }

    /// <summary>
    /// Finds the fenced code block overlapping the selection and strips its fence lines, keeping the
    /// content selected. Returns false when the selection is not in a code block.
    /// </summary>
    private bool TryRemoveCodeBlock()
    {
        MarkdownBlock? block = MarkdownParser.Parse(document)
            .FirstOrDefault(b => b.Kind == MarkdownBlockKind.CodeBlock
                && selection.Max > b.Start && selection.Min < b.End);

        if (block is null)
        {
            return false;
        }

        int openBreak = document.Text.IndexOf('\n', block.Start);
        if (openBreak < 0 || openBreak >= block.End)
        {
            // Nothing but a lone fence line: delete the whole block.
            document.Replace(block.Start, block.End - block.Start, string.Empty, MarkdownEditKind.Structural, selection);
            SetSelection(TextRange.Caret(block.Start));
            return true;
        }

        string inner = document.GetText(openBreak + 1, block.End - openBreak - 1);
        int closeBreak = inner.LastIndexOf('\n');
        if (closeBreak >= 0 && inner[(closeBreak + 1)..].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            inner = inner[..closeBreak];
        }

        document.Replace(block.Start, block.End - block.Start, inner, MarkdownEditKind.Structural, selection);
        SetSelection(new TextRange(block.Start, block.Start + inner.Length));
        return true;
    }

    /// <summary>
    /// Inserts standalone block markdown on its own line, adding separators only where needed.
    /// </summary>
    private void InsertBlock(string markdown)
    {
        EscapeImageBlock();

        int min = selection.Min;
        bool needsLeading = min > 0 && document[min - 1] != '\n';
        bool needsTrailing = selection.Max >= document.Length || document[selection.Max] != '\n';

        string payload = (needsLeading ? "\n" : string.Empty) + markdown + (needsTrailing ? "\n" : string.Empty);
        document.Replace(min, selection.Length, payload, MarkdownEditKind.Structural, selection);
        SetSelection(TextRange.Caret(min + payload.Length));
    }

    /// <summary>
    /// Moves the caret past the image block or inline link when it is inside one, so inserting a new
    /// block or typing does not shred the hidden link/attachment markdown syntax.
    /// </summary>
    private void EscapeImageBlock()
    {
        BlockLayout? layout = engine.FindBlockFor(selection.Min);
        if (layout is null)
        {
            return;
        }

        if (layout.Block.Kind == MarkdownBlockKind.Image)
        {
            SetSelection(TextRange.Caret(layout.Block.End));
            return;
        }

        // On the block holding the caret, every syntax character the caret could touch is revealed
        // (the group under the caret always is), so an insertion there cannot split hidden markdown.
        if (layout.ShowsSyntax)
        {
            return;
        }

        // If the caret is inside any link inline, move past the entire link group
        // so the link syntax is not split.
        IReadOnlyList<MarkdownInline> inlines = layout.Block.Inlines;
        for (int i = 0; i < inlines.Count; i++)
        {
            MarkdownInline inline = inlines[i];
            if ((inline.Style & InlineStyle.Link) != 0)
            {
                bool isFirstInGroup = i == 0 || (inlines[i - 1].Style & InlineStyle.Link) == 0;
                if (isFirstInGroup && selection.Min == inline.Start)
                {
                    continue;
                }

                if (selection.Min >= inline.Start && selection.Min <= inline.End)
                {
                    SetSelection(TextRange.Caret(FindLinkEnd(inlines, inline)));
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Returns the exclusive end of the last inline in a consecutive run of link-styled inlines
    /// that includes <paramref name="linkInline"/>.
    /// </summary>
    private static int FindLinkEnd(IReadOnlyList<MarkdownInline> inlines, MarkdownInline linkInline)
    {
        int end = linkInline.End;
        bool found = false;
        foreach (MarkdownInline inline in inlines)
        {
            if (inline == linkInline)
            {
                found = true;
                end = inline.End;
            }
            else if (found)
            {
                if ((inline.Style & InlineStyle.Link) != 0)
                {
                    end = inline.End;
                }
                else
                {
                    break;
                }
            }
        }

        return end;
    }

    // --- block commands -----------------------------------------------------------------------

    public void ApplyHeading(int level) => ApplyLinePrefix(line =>
    {
        string body = StripBlockPrefix(line, out string existing);
        string marker = new('#', Math.Clamp(level, 1, 6));

        // Re-applying the same heading level toggles it back off, matching the toolbar's pressed state.
        return existing == marker + " " ? body : $"{marker} {body}";
    });

    public void ToggleQuote() => ApplyLinePrefix(line =>
    {
        string body = StripBlockPrefix(line, out string existing);
        return existing == "> " ? body : $"> {body}";
    });

    public void ToggleBulletList() => ApplyLinePrefix(line =>
    {
        string body = StripBlockPrefix(line, out string existing);
        return existing == "- " ? body : $"- {body}";
    });

    public void ToggleOrderedList()
    {
        int ordinal = 0;
        ApplyLinePrefix(line =>
        {
            string body = StripBlockPrefix(line, out string existing);
            ordinal++;
            return existing.EndsWith(". ", StringComparison.Ordinal) ? body : $"{ordinal}. {body}";
        });
    }

    public void ToggleTaskList() => ApplyLinePrefix(line =>
    {
        string body = StripBlockPrefix(line, out string existing);
        return existing.StartsWith("- [", StringComparison.Ordinal) ? body : $"- [ ] {body}";
    });

    /// <summary>
    /// Flips a single task item's checkbox in place, leaving the caret and selection untouched.
    /// </summary>
    private void ToggleTask(MarkdownBlock block)
    {
        string line = document.GetText(block.Start, block.End - block.Start);
        int bracket = line.IndexOf("[", StringComparison.Ordinal);
        if (bracket < 0 || bracket + 2 >= line.Length)
        {
            return;
        }

        TextRange previous = selection;
        document.Replace(block.Start + bracket + 1, 1, block.IsChecked ? " " : "x", MarkdownEditKind.Structural, previous);
        engine.Invalidate();
        SetSelection(previous);
    }

    /// <summary>
    /// Rewrites every line touched by the selection, preserving the selection across the edit.
    /// </summary>
    private void ApplyLinePrefix(Func<string, string> transform)
    {
        if (IsReadOnly)
        {
            return;
        }

        TextRange span = document.GetLineSpan(selection);
        string original = document.GetText(span);
        string[] lines = original.Split('\n');

        var builder = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(transform(lines[i]));
        }

        string replacement = builder.ToString();
        document.Replace(span.Min, span.Length, replacement, MarkdownEditKind.Structural, selection);
        engine.Invalidate();

        int delta = replacement.Length - original.Length;

        // Everything on the rewritten lines moves with the prefix change, and a caret stays a caret:
        // keeping the old anchor while shifting the caret used to select the freshly inserted
        // marker, so the next typed character replaced "- [ ] " instead of following it. Clamping to
        // span.Min keeps a caret that sat at the line's start on the content when a prefix is
        // removed instead of walking it back into the previous line.
        int Shift(int position) => position >= span.Min ? Math.Max(span.Min, position + delta) : position;
        SetSelection(selection.IsEmpty
            ? TextRange.Caret(Shift(selection.End))
            : new TextRange(Shift(selection.Start), Shift(selection.End)));
    }

    /// <summary>
    /// Removes any existing block marker from a line and reports what it was, so commands can decide
    /// between replacing and toggling.
    /// </summary>
    private static string StripBlockPrefix(string line, out string existingPrefix)
    {
        existingPrefix = string.Empty;
        string trimmed = line.TrimStart(' ');

        int hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is >= 1 and <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' ')
        {
            existingPrefix = trimmed[..(hashes + 1)];
            return trimmed[(hashes + 1)..];
        }

        if (trimmed.StartsWith("> ", StringComparison.Ordinal))
        {
            existingPrefix = "> ";
            return trimmed[2..];
        }

        if (trimmed.StartsWith("- [", StringComparison.Ordinal) && trimmed.Length > 5 && trimmed[4] == ']')
        {
            existingPrefix = trimmed[..Math.Min(6, trimmed.Length)];
            return trimmed[existingPrefix.Length..];
        }

        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            existingPrefix = trimmed[..2];
            return trimmed[2..];
        }

        int digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] == '.' && trimmed[digits + 1] == ' ')
        {
            existingPrefix = trimmed[..(digits + 2)];
            return trimmed[(digits + 2)..];
        }

        return trimmed;
    }

    // --- Enter / Tab behaviour ----------------------------------------------------------------

    /// <summary>
    /// Inserts a newline, continuing the current list item where appropriate. Inside a table a
    /// newline would split the row and destroy the grid, so Enter inserts a fresh row instead.
    /// </summary>
    private void HandleEnter()
    {
        if (selection.IsEmpty && TryGetAtomicBlockSyntax(selection.End, out TextRange syntax)
            && selection.End > syntax.Min && selection.End <= syntax.Max)
        {
            SetSelection(TextRange.Caret(syntax.Min));
        }

        if (TryInsertTableRowBelow())
        {
            return;
        }

        // Enter with a picture still selected keeps the attachment: the new line goes under it, which
        // is what "add content below the image" means. Collapsing to the end also matches how a
        // selection behaves in every other editor.
        if (!selection.IsEmpty && TryGetSelectedImage(out MarkdownBlock? image))
        {
            SetSelection(TextRange.Caret(image!.End));
        }

        EscapeImageBlock();

        int line = document.GetLineIndex(selection.Min);
        string text = document.GetLineText(line);
        string continuation = GetListContinuation(text, out bool emptyItem);

        if (emptyItem)
        {
            // Enter on an empty list item ends the list rather than adding another bullet.
            int start = document.GetLineStart(line);
            document.Replace(start, text.Length, string.Empty, MarkdownEditKind.Structural, selection);
            SetSelection(TextRange.Caret(start));
            return;
        }

        string payload = "\n" + continuation;
        document.Replace(selection.Min, selection.Length, payload, MarkdownEditKind.Structural, selection);
        SetSelection(TextRange.Caret(selection.Min + payload.Length));
    }

    /// <summary>
    /// Appends an empty row under the one holding the caret and moves into its first cell. Returns
    /// false when the caret is not in a table.
    /// </summary>
    private bool TryInsertTableRowBelow()
    {
        MarkdownBlock? block = FindTableBlock(selection.Min);
        if (block is null)
        {
            return false;
        }

        int rowLine = document.GetLineIndex(selection.Min);
        int columnCount = Math.Max(1, block.Rows[0].Cells.Count);
        string rowText = "|" + string.Concat(Enumerable.Repeat("   |", columnCount));

        // Enter in the header inserts after the delimiter row so the header stays on top; anywhere
        // else the row goes right under the caret's own line, with no blank line in between.
        int anchorLine = rowLine == document.GetLineIndex(block.Start) ? rowLine + 1 : rowLine;
        int insertAt = document.GetLineEnd(Math.Min(anchorLine, document.LineCount - 1));

        document.Insert(insertAt, "\n" + rowText, MarkdownEditKind.Structural);
        engine.Invalidate();
        SetSelection(TextRange.Caret(insertAt + 3));
        return true;
    }

    /// <summary>
    /// The table block whose source range covers <paramref name="position"/>, or null. Parsed fresh
    /// because the engine's cached blocks only cover what was last laid out.
    /// </summary>
    private MarkdownBlock? FindTableBlock(int position)
        => MarkdownParser.Parse(document).FirstOrDefault(b =>
            b.Kind == MarkdownBlockKind.Table && position >= b.Start && position <= b.End);

    /// <summary>
    /// Returns the prefix a new line should start with to stay inside the current list.
    /// </summary>
    private static string GetListContinuation(string line, out bool emptyItem)
    {
        emptyItem = false;
        int indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        string body = line[indent..];
        string pad = new(' ', indent);

        if (body.StartsWith("- [", StringComparison.Ordinal) && body.Length > 4 && body[4] == ']')
        {
            emptyItem = body.Length <= 6 || body[6..].Trim().Length == 0;
            return emptyItem ? string.Empty : pad + "- [ ] ";
        }

        if (body.StartsWith("- ", StringComparison.Ordinal) || body.StartsWith("* ", StringComparison.Ordinal))
        {
            emptyItem = body[2..].Trim().Length == 0;
            return emptyItem ? string.Empty : pad + body[..2];
        }

        int digits = 0;
        while (digits < body.Length && char.IsAsciiDigit(body[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits + 1 < body.Length && body[digits] == '.' && body[digits + 1] == ' ')
        {
            emptyItem = body[(digits + 2)..].Trim().Length == 0;
            if (emptyItem)
            {
                return string.Empty;
            }

            int next = int.Parse(body[..digits]) + 1;
            return $"{pad}{next}. ";
        }

        if (body.StartsWith("> ", StringComparison.Ordinal))
        {
            emptyItem = body[2..].Trim().Length == 0;
            return emptyItem ? string.Empty : pad + "> ";
        }

        return pad;
    }

    /// <summary>
    /// Tab indents list items by one level; elsewhere it inserts spaces. Shift+Tab always outdents.
    /// Inside a table, Tab moves to the next cell (wrapping rows) and Shift+Tab moves back.
    /// </summary>
    private void HandleTab(bool shift)
    {
        if (TryMoveTableCell(shift))
        {
            return;
        }

        int line = document.GetLineIndex(selection.Min);
        string text = document.GetLineText(line);
        bool isListItem = StripBlockPrefix(text, out string prefix).Length >= 0 && prefix.Length > 0;

        if (!isListItem && !shift)
        {
            document.Replace(selection.Min, selection.Length, "    ", MarkdownEditKind.Typing, selection);
            SetSelection(TextRange.Caret(selection.Min + 4));
            return;
        }

        int start = document.GetLineStart(line);
        if (shift)
        {
            int remove = 0;
            while (remove < 4 && remove < text.Length && text[remove] == ' ')
            {
                remove++;
            }

            if (remove == 0)
            {
                return;
            }

            TextRange previous = selection;
            document.Replace(start, remove, string.Empty, MarkdownEditKind.Structural, previous);
            SetSelection(TextRange.Caret(Math.Max(start, previous.End - remove)));
        }
        else
        {
            TextRange previous = selection;
            document.Insert(start, "    ", MarkdownEditKind.Structural);
            SetSelection(TextRange.Caret(previous.End + 4));
        }
    }

    /// <summary>
    /// Moves the caret between table cells, landing after the cell's last character. The end offset
    /// includes any trailing hidden syntax, so it stays inside this cell's span instead of snapping
    /// to the next one. Returns false when the caret is not in a table.
    /// </summary>
    private bool TryMoveTableCell(bool shift)
    {
        MarkdownBlock? block = FindTableBlock(selection.Min);
        if (block is null)
        {
            return false;
        }

        var cells = new List<MarkdownTableCell>();
        foreach (MarkdownTableRow row in block.Rows)
        {
            cells.AddRange(row.Cells);
        }

        if (cells.Count == 0)
        {
            return false;
        }

        // The header row is followed by the delimiter row in the source, so cells are already in
        // document order.
        int current = -1;
        for (int i = 0; i < cells.Count; i++)
        {
            if (selection.Min >= cells[i].Start && selection.Min <= cells[i].End)
            {
                current = i;
                break;
            }

            if (cells[i].Start > selection.Min)
            {
                // Between cells (on a pipe or padding): pick that side of the gap.
                current = shift ? i - 1 : i;
                break;
            }
        }

        if (current < 0)
        {
            current = cells.Count - 1;
        }

        int next = shift
            ? (current - 1 + cells.Count) % cells.Count
            : (current + 1) % cells.Count;

        MarkdownTableCell target = cells[next];
        // Land after the cell's last character. The end offset is inclusive of trailing hidden
        // syntax, so it stays inside this cell's span; only fall back when the cell is empty.
        int caret = target.End > target.Start ? target.End : target.Start;
        SetSelection(TextRange.Caret(caret));
        return true;
    }

    /// <summary>
    /// After a plain newline typed through the OS text stack, re-applies list continuation.
    /// </summary>
    /// <remarks>
    /// Enter normally arrives through <see cref="HandleEnter"/>, but IMEs and the touch keyboard can
    /// commit a newline as ordinary text, which would otherwise break out of a list silently.
    /// </remarks>
    private void AutoContinueList(int editStart, string insertedText)
    {
        if (insertedText != "\n" || IsReadOnly)
        {
            return;
        }

        // A raw newline inside a table row splits it; strip it and let Enter's row insertion win.
        if (FindTableBlock(editStart) is not null)
        {
            document.Replace(editStart, 1, string.Empty, MarkdownEditKind.Structural, selection);
            SetSelection(TextRange.Caret(editStart));
            return;
        }

        int previousLine = document.GetLineIndex(Math.Max(0, editStart - 1));
        string continuation = GetListContinuation(document.GetLineText(previousLine), out bool emptyItem);
        if (emptyItem || continuation.Length == 0)
        {
            return;
        }

        int caret = editStart + 1;
        document.Insert(caret, continuation, MarkdownEditKind.Structural);
        SetSelection(TextRange.Caret(caret + continuation.Length));
    }

    // --- selection / history ------------------------------------------------------------------

    /// <summary>
    /// Ctrl+A: selects the block the caret sits in, and only on the next press the whole document. A
    /// third press narrows back to the block, so the shortcut cycles block &lt;-&gt; document.
    /// </summary>
    /// <remarks>
    /// In a table the unit is the cell under the caret rather than the whole grid, because the cell is
    /// what the user is actually editing; raw table editing has no cells, so its fenced source is the
    /// unit. Code blocks include their fences so a copy or delete takes the block with it instead of
    /// leaving an orphaned fence behind.
    /// </remarks>
    public void SelectAll()
    {
        if (selection.Min == 0
            && selection.Max == document.Length
            && document.Length > 0
            && !lastBlockSelection.IsEmpty)
        {
            SetSelection(lastBlockSelection);
            return;
        }

        TextRange block = GetBlockRange(selection.End);

        bool blockAlreadySelected = !block.IsEmpty
            && selection.Min == block.Min
            && selection.Max == block.Max;

        if (block.IsEmpty || blockAlreadySelected)
        {
            lastBlockSelection = block;
            SetSelection(new TextRange(0, document.Length));
            return;
        }

        lastBlockSelection = block;
        SetSelection(new TextRange(block.Min, block.Max));
    }

    /// <summary>
    /// The source range of the block containing <paramref name="position"/> - the grid cell when the
    /// position is inside a rendered table, otherwise the whole block. Empty when no block claims it.
    /// </summary>
    private TextRange GetBlockRange(int position)
    {
        BlockLayout? block = engine.FindBlockFor(position);

        // FindBlockFor clamps to the last block when nothing contains the position, so containment has
        // to be verified rather than trusted.
        if (block is null || !block.Contains(position))
        {
            return TextRange.Caret(0);
        }

        if (block.Block.Kind == MarkdownBlockKind.Table && !block.IsRawEdit)
        {
            foreach (TableCellLayout cell in block.Cells)
            {
                if (cell.Region.ContainsDocumentPosition(position))
                {
                    return new TextRange(cell.Region.DocumentStart, cell.Region.DocumentEnd);
                }
            }
        }

        return new TextRange(block.Block.Start, block.Block.End);
    }

    public void Undo()
    {
        if (document.Undo(out TextRange restored))
        {
            engine.Invalidate();
            SetSelection(restored);
        }
        else
        {
        }
    }

    public void Redo()
    {
        if (document.Redo(out TextRange restored))
        {
            engine.Invalidate();
            SetSelection(restored);
        }
        else
        {
        }
    }

    // --- clipboard ----------------------------------------------------------------------------

    public async Task CopyAsync()
    {
        if (selection.IsEmpty)
        {
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        string copied = document.GetText(selection);
        package.SetText(copied);
        Clipboard.SetContent(package);
        await Task.CompletedTask;
    }

    public async Task CutAsync()
    {
        if (selection.IsEmpty || IsReadOnly)
        {
            return;
        }

        await CopyAsync();
        DeleteSelectionPreservingTable();
    }

    /// <summary>
    /// Asks the host to turn clipboard content that is not text - a bitmap pasted straight from the
    /// clipboard (screenshot or "copy image") or files copied in the shell - into the markdown to
    /// insert at the caret. The host owns storage, so it persists the payload as attachments.
    /// Return null to fall back to plain-text pasting. Left null, non-text clipboard content is
    /// ignored, exactly as before.
    /// </summary>
    public Func<ClipboardPasteContent, Task<string?>>? ClipboardPasteHandler { get; set; }

    /// <summary>
    /// Pastes clipboard content as markdown.
    /// </summary>
    /// <remarks>
    /// Plain text is inserted verbatim because it may already be markdown. A bare URL dropped onto a
    /// non-empty selection becomes a link around that selection, which is the one transformation that
    /// is always what the user meant. Clipboard images and copied files are offered to
    /// <see cref="ClipboardPasteHandler"/> first, so a picture lands as an attachment block and a
    /// copied file as an attachment link.
    /// </remarks>
    public async Task PasteAsync()
    {
        if (IsReadOnly)
        {
            return;
        }

        if (await TryPasteAttachmentAsync())
        {
            return;
        }

        DataPackageView view = Clipboard.GetContent();
        if (!view.Contains(StandardDataFormats.Text))
        {
            return;
        }

        string text = await view.GetTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }


        string trimmed = text.Trim();
        if (IsAbsoluteUrl(trimmed))
        {
            if (!selection.IsEmpty)
            {
                InsertLink(trimmed, document.GetText(selection));
                return;
            }

            string title = await TryExtractTitleFromClipboardHtmlAsync(view, trimmed)
                ?? await TryFetchWebPageTitleAsync(trimmed)
                ?? trimmed;

            string markdownLink = $"[{title}]({trimmed})";
            TableGuardResult linkGuard = GetTableGuard(selection, markdownLink);
            if (linkGuard.Blocked)
            {
                return;
            }

            string insert = linkGuard.Replacement;
            SetSelection(document.Replace(selection.Min, selection.Length, insert, MarkdownEditKind.Structural, selection));
            return;
        }

        TableGuardResult guard = GetTableGuard(selection, text);
        if (guard.Blocked)
        {
            return;
        }

        if (guard.Replacement != text)
        {
            text = guard.Replacement;
        }

        SetSelection(document.Replace(selection.Min, selection.Length, text, MarkdownEditKind.Structural, selection));
    }

    /// <summary>
    /// Offers the clipboard's non-text content to <see cref="ClipboardPasteHandler"/> and inserts
    /// whatever markdown comes back as a standalone block at the caret. Returns false when the
    /// clipboard holds nothing attachable, so the plain-text paste path takes over.
    /// </summary>
    private async Task<bool> TryPasteAttachmentAsync()
    {
        Func<ClipboardPasteContent, Task<string?>>? handler = ClipboardPasteHandler;
        if (handler is null)
        {
            return false;
        }

        ClipboardPasteContent? content = await CaptureClipboardAttachmentAsync();
        if (content is null)
        {
            return false;
        }

        string? markdown = await handler(content);
        if (string.IsNullOrEmpty(markdown))
        {
            return false;
        }

        InsertBlock(markdown);
        return true;
    }

    /// <summary>
    /// Reads the clipboard's attachment-shaped content: files copied in the shell first (they carry
    /// their names), then a bare bitmap from a screenshot or "copy image". Null when neither is
    /// present or readable, which is the normal case for a text clipboard.
    /// </summary>
    private static async Task<ClipboardPasteContent?> CaptureClipboardAttachmentAsync()
    {
        try
        {
            DataPackageView view = Clipboard.GetContent();

            if (view.Contains(StandardDataFormats.StorageItems))
            {
                IReadOnlyList<IStorageItem> items = await view.GetStorageItemsAsync();
                List<StorageFile> files = new();
                foreach (IStorageItem item in items)
                {
                    if (item is StorageFile file)
                    {
                        files.Add(file);
                    }
                }

                if (files.Count > 0)
                {
                    return new ClipboardPasteContent(files);
                }
            }

            if (view.Contains(StandardDataFormats.Bitmap))
            {
                RandomAccessStreamReference reference = await view.GetBitmapAsync();
                using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
                byte[] bytes = await ReadStreamAsync(stream);
                if (bytes.Length > 0)
                {
                    return new ClipboardPasteContent(bytes, stream.ContentType);
                }
            }
        }
        catch (Exception ex)
        {
            // Some sources hand out formats that cannot be opened. That must never break pasting:
            // the plain-text path below still gets its chance.
        }

        return null;
    }

    /// <summary>
    /// Reads a WinRT stream to the end. The buffer is sized from what actually arrived, since a
    /// stream may deliver fewer bytes than its reported size claims.
    /// </summary>
    private static async Task<byte[]> ReadStreamAsync(IRandomAccessStream stream)
    {
        using DataReader reader = new(stream);
        await reader.LoadAsync((uint)Math.Min(stream.Size, int.MaxValue));
        byte[] bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static readonly HttpClient HttpTitleClient = CreateHttpTitleClient();

    private static HttpClient CreateHttpTitleClient()
    {
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        HttpClient client = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return client;
    }

    private static async Task<string?> TryExtractTitleFromClipboardHtmlAsync(DataPackageView view, string targetUrl)
    {
        try
        {
            if (!view.Contains(StandardDataFormats.Html))
            {
                return null;
            }

            string html = await view.GetHtmlFormatAsync();
            if (string.IsNullOrEmpty(html))
            {
                return null;
            }

            Match aMatch = Regex.Match(html, @"<a\s+[^>]*href=[""']([^""']+)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (aMatch.Success)
            {
                string href = aMatch.Groups[1].Value;
                if (href.Equals(targetUrl, StringComparison.OrdinalIgnoreCase) || targetUrl.Contains(href, StringComparison.OrdinalIgnoreCase) || href.Contains(targetUrl, StringComparison.OrdinalIgnoreCase))
                {
                    string text = Regex.Replace(aMatch.Groups[2].Value, @"<[^>]+>", string.Empty);
                    text = WebUtility.HtmlDecode(text);
                    text = Regex.Replace(text, @"\s+", " ").Trim();
                    if (!string.IsNullOrWhiteSpace(text) && !IsAbsoluteUrl(text))
                    {
                        return text.Replace("[", "\\[").Replace("]", "\\]");
                    }
                }
            }
        }
        catch
        {
            // Ignore clipboard html parsing errors
        }

        return null;
    }

    private static async Task<string?> TryFetchWebPageTitleAsync(string url)
    {
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2.5));
            using HttpResponseMessage response = await HttpTitleClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType != null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) && !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(cts.Token);
            byte[] buffer = new byte[65536];
            int read = await stream.ReadAsync(buffer, cts.Token);
            if (read == 0)
            {
                return null;
            }

            string html = Encoding.UTF8.GetString(buffer, 0, read);
            Match match = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success)
            {
                string rawTitle = match.Groups[1].Value;
                string cleanTitle = WebUtility.HtmlDecode(rawTitle);
                cleanTitle = Regex.Replace(cleanTitle, @"\s+", " ").Trim();
                cleanTitle = cleanTitle.Replace("[", "\\[").Replace("]", "\\]");
                if (!string.IsNullOrWhiteSpace(cleanTitle))
                {
                    return cleanTitle;
                }
            }
        }
        catch (Exception ex)
        {
        }

        return null;
    }

    private static bool IsAbsoluteUrl(string text)
        => !text.Contains('\n') && !text.Contains('\r') && !text.Contains(' ')
            && Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Backspace at the very start of a decorated line strips its marker instead of joining lines.
    /// </summary>
    private bool TryOutdentOrClearMarker()
    {
        int line = document.GetLineIndex(selection.End);
        int lineStart = document.GetLineStart(line);
        string text = document.GetLineText(line);

        string body = StripBlockPrefix(text, out string prefix);
        if (prefix.Length == 0)
        {
            return false;
        }

        int indent = text.Length - text.TrimStart(' ').Length;
        if (selection.End != lineStart + indent + prefix.Length)
        {
            return false;
        }

        document.Replace(lineStart, text.Length, new string(' ', indent) + body, MarkdownEditKind.Structural, selection);
        engine.Invalidate();
        SetSelection(TextRange.Caret(lineStart + indent));
        return true;
    }

    /// <summary>
    /// Appends attachment markdown, used by the note page when a file is added.
    /// </summary>
    public void AppendAttachmentLink(string fileName, string target, bool isImage)
    {
        if (isImage)
        {
            InsertImage(target, fileName);
        }
        else
        {
            InsertBlock($"[{fileName}]({target})");
        }
    }

    /// <summary>
    /// Checks if <paramref name="position"/> falls inside a markdown link or image, extracting its range, label, and target.
    /// </summary>
    public bool TryGetLinkAt(int position, out int groupStart, out int groupEnd, out string label, out string target, out bool isImage)
    {
        groupStart = groupEnd = 0;
        label = string.Empty;
        target = string.Empty;
        isImage = false;

        BlockLayout? layout = engine.FindBlockFor(position);
        if (layout is null)
        {
            return false;
        }

        if (layout.Block.Kind == MarkdownBlockKind.Image)
        {
            groupStart = layout.Block.Start;
            groupEnd = layout.Block.End;
            label = layout.Block.AltText ?? string.Empty;
            target = layout.Block.Target ?? string.Empty;
            isImage = true;
            return true;
        }

        IReadOnlyList<MarkdownInline> inlines = layout.Block.Inlines;
        int index = -1;
        for (int i = 0; i < inlines.Count; i++)
        {
            if ((inlines[i].Style & InlineStyle.Link) != 0 && position >= inlines[i].Start && position <= inlines[i].End)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return false;
        }

        int first = index;
        while (first > 0 && (inlines[first - 1].Style & InlineStyle.Link) != 0)
        {
            first--;
        }

        int last = index;
        while (last + 1 < inlines.Count && (inlines[last + 1].Style & InlineStyle.Link) != 0)
        {
            last++;
        }

        groupStart = inlines[first].Start;
        groupEnd = inlines[last].End;

        StringBuilder labelBuilder = new();
        for (int i = first; i <= last; i++)
        {
            if (!inlines[i].IsSyntax)
            {
                labelBuilder.Append(inlines[i].Text);
            }

            if (inlines[i].LinkTarget is not null)
            {
                target = inlines[i].LinkTarget!;
            }
        }

        label = labelBuilder.ToString();
        isImage = groupStart > 0 && document[groupStart - 1] == '!';
        if (isImage)
        {
            groupStart--;
        }

        return true;
    }

    /// <summary>
    /// Replaces the link or image at <paramref name="groupStart"/>..<paramref name="groupEnd"/> with a new label.
    /// </summary>
    public void RenameLink(int groupStart, int groupEnd, string newLabel, string target, bool isImage)
    {
        if (IsReadOnly)
        {
            return;
        }

        string prefix = isImage ? "![" : "[";
        string replacement = $"{prefix}{newLabel}]({target})";
        document.Replace(groupStart, groupEnd - groupStart, replacement, MarkdownEditKind.Structural, selection);
        SetSelection(TextRange.Caret(groupStart + replacement.Length));
    }

    /// <summary>
    /// Renames all occurrences of a link/image targeting <paramref name="target"/> with the new name.
    /// </summary>
    public void RenameAttachmentLinks(string target, string newFileName)
    {
        if (IsReadOnly)
        {
            return;
        }

        string markdown = document.Text;
        int pos = 0;
        while ((pos = markdown.IndexOf(target, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int openParen = markdown.LastIndexOf('(', pos);
            if (openParen >= 0 && openParen < pos)
            {
                int closeBracket = openParen - 1;
                if (closeBracket >= 0 && markdown[closeBracket] == ']')
                {
                    int openBracket = markdown.LastIndexOf('[', closeBracket);
                    if (openBracket >= 0)
                    {
                        int closeParen = markdown.IndexOf(')', pos);
                        if (closeParen > pos)
                        {
                            bool isImage = openBracket > 0 && markdown[openBracket - 1] == '!';
                            int linkStart = isImage ? openBracket - 1 : openBracket;
                            int linkEnd = closeParen + 1;
                            string prefix = isImage ? "![" : "[";
                            string newLink = $"{prefix}{newFileName}]({target})";
                            document.Replace(linkStart, linkEnd - linkStart, newLink, MarkdownEditKind.Structural, selection);
                            markdown = document.Text;
                            pos = linkStart + newLink.Length;
                            continue;
                        }
                    }
                }
            }

            pos += target.Length;
        }
    }

    internal void ShowLinkContextMenu(global::Windows.Foundation.Point point, int groupStart, int groupEnd, string label, string target, bool isImage)
    {
        MenuFlyout menu = new();

        MenuFlyoutItem openLinkItem = new()
        {
            Text = "Open link",
            Icon = new FontIcon { Glyph = "\uE8A7", FontSize = 14 }
        };
        openLinkItem.Click += (_, _) =>
        {
            LinkInvoked?.Invoke(this, target);
        };
        menu.Items.Add(openLinkItem);

        MenuFlyoutItem copyLinkItem = new()
        {
            Text = "Copy link",
            Icon = new FontIcon { Glyph = "\uE8C8", FontSize = 14 }
        };
        copyLinkItem.Click += (_, _) =>
        {
            DataPackage package = new();
            package.SetText(target);
            Clipboard.SetContent(package);
        };
        menu.Items.Add(copyLinkItem);

        MenuFlyoutItem renameLabelItem = new()
        {
            Text = isImage ? "Rename image label" : "Rename link label",
            Icon = new FontIcon { Glyph = "\uE8AC", FontSize = 14 }
        };
        renameLabelItem.Click += async (_, _) =>
        {
            await PromptRenameLinkAsync(groupStart, groupEnd, label, target, isImage);
        };
        menu.Items.Add(renameLabelItem);

        menu.ShowAt(canvas, new FlyoutShowOptions { Position = point });
    }

    public async Task PromptRenameLinkAsync(int groupStart, int groupEnd, string currentLabel, string target, bool isImage)
    {
        if (XamlRoot is null)
        {
            return;
        }

        TextBox textBox = new()
        {
            Text = currentLabel,
            Margin = new Thickness(0, 8, 0, 0)
        };
        textBox.Loaded += (_, _) => textBox.SelectAll();

        ContentDialog dialog = new()
        {
            Title = isImage ? "Rename image label" : "Rename link label",
            Content = textBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            string newLabel = textBox.Text.Trim();
            RenameLink(groupStart, groupEnd, newLabel, target, isImage);
            LinkRenamed?.Invoke(this, new LinkRenamedEventArgs(target, newLabel));
        }
    }
}
