using System;
using System.Collections.Generic;
using System.Text;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// The outcome of checking an edit against the hidden structure of a grid-rendered table.
/// </summary>
internal readonly struct TableGuardResult
{
    private TableGuardResult(bool blocked, string replacement)
    {
        Blocked = blocked;
        Replacement = replacement;
    }

    /// <summary>True when the edit must not be applied at all.</summary>
    public bool Blocked { get; }

    /// <summary>The text to write in place of the edited range.</summary>
    public string Replacement { get; }

    /// <summary>No table structure is involved; the caller applies the edit exactly as it asked.</summary>
    public static TableGuardResult PassThrough(string inserted) => new(false, inserted);

    public static TableGuardResult Block() => new(true, string.Empty);

    public static TableGuardResult Rewrite(string replacement) => new(false, replacement);
}

/// <summary>
/// Keeps the invisible structure of a grid-rendered table out of reach of ordinary editing.
/// </summary>
/// <remarks>
/// While a table renders as a grid its markdown source is hidden: only the cells draw text and only the
/// cells are hit-testable, yet the caret still walks onto the surrounding pipes and the delimiter row.
/// Editing one of those characters rewrites the row underneath the user - the separator disappears from
/// a row whose cells are still laid out, leaving text that renders but cannot be clicked. Every edit
/// whose range touches a grid table therefore goes through <see cref="GetTableGuard"/>, which holds
/// column pipes, row breaks and the delimiter row exactly where they are.
/// <para>
/// The table the toolbar has toggled into raw markdown editing (<see cref="editModeTableStart"/>) is
/// exempt: there the source is on screen and editing it is deliberate.
/// </para>
/// </remarks>
public sealed partial class MarkdownEditor
{
    /// <summary>
    /// Checks an edit that would replace <paramref name="range"/> with <paramref name="inserted"/>.
    /// </summary>
    /// <remarks>
    /// Structure inside the range is rebuilt in its original position and structure in the incoming text
    /// is dropped, so the grid can neither lose a separator nor gain one.
    /// </remarks>
    private TableGuardResult GetTableGuard(TextRange range, string inserted)
    {
        int min = range.Min;
        int max = range.Max;

        List<MarkdownBlock> tables = FindGridTables(min, max);
        if (tables.Count == 0)
        {
            return TableGuardResult.PassThrough(inserted);
        }

        string safe = StripTableCharacters(inserted);

        // The incoming text was nothing but table meaning. Refusing beats rewriting it to an empty
        // string, which would destroy a live selection to insert nothing.
        if (inserted.Length > 0 && safe.Length == 0)
        {
            return TableGuardResult.Block();
        }

        if (range.IsEmpty)
        {
            // A collapsed caret can only damage the table through what it inserts, except on the
            // delimiter row, which renders no text at all and would swallow the edit invisibly.
            foreach (MarkdownBlock table in tables)
            {
                if (IsDelimiterRow(table, min))
                {
                    return TableGuardResult.Block();
                }
            }

            return safe.Length == inserted.Length
                ? TableGuardResult.PassThrough(inserted)
                : TableGuardResult.Rewrite(safe);
        }

        var builder = new StringBuilder((max - min) + safe.Length);
        bool hasEditableGap = false;
        bool insertPlaced = false;

        for (int offset = min; offset < max; offset++)
        {
            if (IsTableStructure(tables, offset))
            {
                builder.Append(document[offset]);
                continue;
            }

            hasEditableGap = true;

            if (!insertPlaced && safe.Length > 0)
            {
                builder.Append(safe);
                insertPlaced = true;
            }
        }

        if (!hasEditableGap)
        {
            // Nothing but structure in the range, so there is no cell text left to touch.
            return TableGuardResult.Block();
        }

        if (!insertPlaced)
        {
            builder.Append(safe);
        }

        string replacement = builder.ToString();
        if (replacement == document.GetText(min, max - min))
        {
            return TableGuardResult.Block();
        }

        return TableGuardResult.Rewrite(replacement);
    }

    /// <summary>
    /// Whether the <paramref name="length"/> characters at <paramref name="position"/> are what holds a
    /// grid table together, meaning an edit there has to be refused.
    /// </summary>
    private bool IsTableStructure(int position, int length)
    {
        int end = Math.Min(position + length, document.Length);
        if (position >= end || FindGridTables(position, end) is not { Count: > 0 } tables)
        {
            return false;
        }

        for (int offset = position; offset < end; offset++)
        {
            if (IsTableStructure(tables, offset))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deletes the current selection while leaving table structure intact.
    /// </summary>
    private void DeleteSelectionPreservingTable()
    {
        TableGuardResult guard = GetTableGuard(selection, string.Empty);

        if (guard.Blocked)
        {
            MarkdownEditorLog.Write($"delete refused, {selection.Min}..{selection.Max} held only table structure");
            return;
        }

        if (guard.Replacement == document.GetText(selection))
        {
            return;
        }

        MarkdownEditorLog.Write($"deleting {selection.Min}..{selection.Max} keeping structure as {MarkdownEditorLog.Snippet(guard.Replacement)}");
        SetSelection(document.Replace(selection.Min, selection.Length, guard.Replacement, MarkdownEditKind.Deleting, selection));
    }

    /// <summary>
    /// The tables overlapping <paramref name="start"/>..<paramref name="end"/> while they render as a
    /// grid.
    /// </summary>
    private List<MarkdownBlock> FindGridTables(int start, int end)
    {
        var found = new List<MarkdownBlock>();

        foreach (MarkdownBlock block in MarkdownParser.Parse(document))
        {
            if (block.Kind != MarkdownBlockKind.Table || block.Start == editModeTableStart)
            {
                continue;
            }

            // The row breaks flanking the block are structure as well, so the character before its
            // header is in scope too.
            if (start > block.End || end < block.Start - 1)
            {
                continue;
            }

            // Removing a table whole is deliberate and leaves nothing stale behind, so it stays allowed.
            if (start != end && start <= block.Start && end >= block.End)
            {
                continue;
            }

            found.Add(block);
        }

        return found;
    }

    private bool IsTableStructure(List<MarkdownBlock> tables, int offset)
    {
        foreach (MarkdownBlock table in tables)
        {
            if (IsTableCharacter(table, offset))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the source character at <paramref name="offset"/> is table structure: a column pipe, the
    /// backslash escaping one, a row break, or anything on the delimiter row, whose dashes and colons
    /// are what make the parser recognise the block as a table at all.
    /// </summary>
    private bool IsTableCharacter(MarkdownBlock table, int offset)
    {
        // The row breaks on either side of the block are in scope as well: deleting the one above turns
        // the previous line into the header's first cell, and deleting the one below pulls the following
        // line into the last row. Both reshape the grid without any visible change to explain it.
        if (offset < table.Start - 1 || offset > table.End || offset >= document.Length)
        {
            return false;
        }

        if (document[offset] == '\n')
        {
            return true;
        }

        if (offset < table.Start)
        {
            return false;
        }

        if (IsDelimiterRow(table, offset))
        {
            return true;
        }

        if (document[offset] == '|')
        {
            return true;
        }

        // Losing only the backslash of an escaped pipe would turn literal text into a new separator.
        return document[offset] == '\\'
            && offset + 1 < document.Length
            && offset + 1 <= table.End
            && document[offset + 1] == '|';
    }

    /// <summary>
    /// Whether <paramref name="offset"/> lands on the table's delimiter row, the line parsed for column
    /// alignment that never renders as content.
    /// </summary>
    private bool IsDelimiterRow(MarkdownBlock table, int offset)
    {
        int line = document.GetLineIndex(table.Start) + 1;
        int start = document.GetLineStart(line);
        int end = document.GetLineEnd(line);
        return offset >= start && offset < end;
    }

    /// <summary>
    /// Drops the characters that carry table meaning, so a keystroke, paste or IME commit can neither
    /// add a column separator nor split a row.
    /// </summary>
    private static string StripTableCharacters(string text)
    {
        if (text.Length == 0 || (text.IndexOf('|') < 0 && text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is not ('|' or '\n' or '\r'))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
