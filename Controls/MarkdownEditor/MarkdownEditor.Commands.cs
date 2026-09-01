using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

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

    public void ToggleInlineCode() => ToggleWrap("`");

    /// <summary>
    /// Highlight uses the code delimiter and renders as inline code.
    /// </summary>
    public void ToggleHighlight() => ToggleWrap("`");

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
            MarkdownEditorLog.Exit("editor is read-only");
            return;
        }

        MarkdownEditorLog.Write($"toggle wrap {MarkdownEditorLog.Snippet(delimiter)} over {selection.Min}..{selection.Max}");

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
        int min = selection.Min;
        bool needsLeading = min > 0 && document[min - 1] != '\n';
        bool needsTrailing = selection.Max >= document.Length || document[selection.Max] != '\n';

        string payload = (needsLeading ? "\n" : string.Empty) + markdown + (needsTrailing ? "\n" : string.Empty);
        document.Replace(min, selection.Length, payload, MarkdownEditKind.Structural, selection);
        SetSelection(TextRange.Caret(min + payload.Length));
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
        SetSelection(new TextRange(selection.Start, Math.Max(span.Min, selection.End + delta)));
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
        if (TryInsertTableRowBelow())
        {
            return;
        }

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

    public void SelectAll() => SetSelection(new TextRange(0, document.Length));

    public void Undo()
    {
        if (document.Undo(out TextRange restored))
        {
            MarkdownEditorLog.Write($"undo applied, caret restored to {restored.Start}..{restored.End}");
            engine.Invalidate();
            SetSelection(restored);
        }
        else
        {
            MarkdownEditorLog.Exit("undo stack empty");
        }
    }

    public void Redo()
    {
        if (document.Redo(out TextRange restored))
        {
            MarkdownEditorLog.Write($"redo applied, caret restored to {restored.Start}..{restored.End}");
            engine.Invalidate();
            SetSelection(restored);
        }
        else
        {
            MarkdownEditorLog.Exit("redo stack empty");
        }
    }

    // --- clipboard ----------------------------------------------------------------------------

    public async Task CopyAsync()
    {
        if (selection.IsEmpty)
        {
            MarkdownEditorLog.Exit("empty selection");
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        string copied = document.GetText(selection);
        MarkdownEditorLog.Write($"copying {copied.Length} chars {MarkdownEditorLog.Snippet(copied)}");
        package.SetText(copied);
        Clipboard.SetContent(package);
        await Task.CompletedTask;
    }

    public async Task CutAsync()
    {
        if (selection.IsEmpty || IsReadOnly)
        {
            MarkdownEditorLog.Exit(IsReadOnly ? "editor is read-only" : "empty selection");
            return;
        }

        MarkdownEditorLog.Write($"cutting {selection.Min}..{selection.Max}");
        await CopyAsync();
        SetSelection(document.Delete(selection));
    }

    /// <summary>
    /// Pastes clipboard content as markdown.
    /// </summary>
    /// <remarks>
    /// Plain text is inserted verbatim because it may already be markdown. A bare URL dropped onto a
    /// non-empty selection becomes a link around that selection, which is the one transformation that
    /// is always what the user meant.
    /// </remarks>
    public async Task PasteAsync()
    {
        if (IsReadOnly)
        {
            MarkdownEditorLog.Exit("editor is read-only");
            return;
        }

        DataPackageView view = Clipboard.GetContent();
        if (!view.Contains(StandardDataFormats.Text))
        {
            MarkdownEditorLog.Exit("clipboard holds no text format");
            return;
        }

        string text = await view.GetTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            MarkdownEditorLog.Exit("clipboard text was empty");
            return;
        }

        MarkdownEditorLog.Write($"pasting {text.Length} chars over {selection.Min}..{selection.Max} {MarkdownEditorLog.Snippet(text)}");

        if (!selection.IsEmpty && IsAbsoluteUrl(text.Trim()))
        {
            MarkdownEditorLog.Write("clipboard is a URL, wrapping selection as a link");
            InsertLink(text.Trim(), document.GetText(selection));
            return;
        }

        SetSelection(document.Replace(selection.Min, selection.Length, text, MarkdownEditKind.Structural, selection));
    }

    private static bool IsAbsoluteUrl(string text)
        => Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
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
}
