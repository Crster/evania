using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// Row and column operations for tables that are rendered as a grid.
/// </summary>
/// <remarks>
/// These edits are structural - they add and remove source text the WYSIWYG view never shows - so they
/// cannot be expressed as typing. Each operation rewrites the whole table block in a single
/// <see cref="MarkdownDocument.Replace"/>, which keeps it to one undo unit and guarantees every row's
/// separator count changes together. Rows are split into cells and re-emitted canonically rather than
/// spliced in place, because a hand-inserted pipe in a row that was missing one would silently change
/// its shape. Pipes inside cell text are escaped and the splitter skips them, matching
/// <see cref="MarkdownParser.ParseTableCells"/>.
/// </remarks>
public sealed partial class MarkdownEditor
{
    /// <summary>
    /// The grid cell holding the caret, resolving to the nearest cell when the caret sits in the trimmed
    /// padding between two. Returns false when the caret is not inside a table rendered as a grid.
    /// </summary>
    private bool TryGetCaretGridCell(out BlockLayout block, out int row, out int column)
    {
        block = null!;
        row = column = 0;

        BlockLayout? candidate = engine.FindBlockFor(selection.End);

        // FindBlockFor clamps to the last block when nothing contains the position, so containment has
        // to be verified rather than trusted.
        if (candidate is null
            || candidate.Block.Kind != MarkdownBlockKind.Table
            || !candidate.Block.Contains(selection.End)
            || candidate.Cells.Count == 0
            || candidate.IsRawEdit)
        {
            return false;
        }

        int nearestDistance = int.MaxValue;

        foreach (TableCellLayout cell in candidate.Cells)
        {
            if (cell.Region.ContainsDocumentPosition(selection.End))
            {
                row = cell.Row;
                column = cell.Column;
                block = candidate;
                return true;
            }

            int distance = selection.End < cell.Region.DocumentStart
                ? cell.Region.DocumentStart - selection.End
                : selection.End > cell.Region.DocumentEnd
                    ? selection.End - cell.Region.DocumentEnd
                    : 0;

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                row = cell.Row;
                column = cell.Column;
            }
        }

        block = candidate;
        return true;
    }

    /// <summary>Inserts an empty row under the one holding the caret.</summary>
    public void InsertTableRow()
    {
        if (!TryGetCaretGridCell(out _, out _, out _))
        {
            return;
        }

        if (IsReadOnly)
        {
            return;
        }

        // Shared with the Enter key so a toolbar insert and a newline behave identically.
        TryInsertTableRowBelow();
    }

    /// <summary>
    /// Removes the row holding the caret. The header row is off limits: without it the delimiter row is
    /// ordinary text again and the block stops being a table.
    /// </summary>
    public void DeleteTableRow()
    {
        if (!TryGetCaretGridCell(out BlockLayout block, out int row, out int column))
        {
            return;
        }

        if (IsReadOnly)
        {
            return;
        }

        List<string> lines = GetTableLines(block.Block);

        // Row 0 is the header and lives on the first source line; body row r is on line r + 1, since the
        // delimiter row between them is not part of the grid.
        int lineIndex = row == 0 ? 0 : row + 1;
        if (row == 0 || lineIndex >= lines.Count)
        {
            return;
        }

        lines.RemoveAt(lineIndex);
        RewriteTable(block, string.Join('\n', lines), Math.Max(0, row - 1), column);
    }

    /// <summary>Inserts a column to the right of the one holding the caret.</summary>
    public void InsertTableColumn()
    {
        if (!TryGetCaretGridCell(out BlockLayout block, out int row, out int column))
        {
            return;
        }

        if (IsReadOnly)
        {
            return;
        }

        List<string> lines = GetTableLines(block.Block);
        int added = CountColumns(lines) + 1;
        var builder = new StringBuilder();

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            // The header gets a name, the delimiter row gets dashes, body rows an empty cell. The
            // delimiter row otherwise keeps its cells so explicit alignment is not lost.
            List<string> cells = SplitRow(lines[i]);
            string content = i == 0 ? $"Column {added}" : i == 1 ? "---" : string.Empty;
            cells.Insert(Math.Min(column + 1, cells.Count), content);

            builder.Append(BuildRow(cells));
        }

        RewriteTable(block, builder.ToString(), row, column + 1);
    }

    /// <summary>Removes the column holding the caret from every row of the table.</summary>
    public void DeleteTableColumn()
    {
        if (!TryGetCaretGridCell(out BlockLayout block, out int row, out int column))
        {
            return;
        }

        if (IsReadOnly)
        {
            return;
        }

        List<string> lines = GetTableLines(block.Block);
        if (CountColumns(lines) <= 1)
        {
            return;
        }

        var builder = new StringBuilder();

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            List<string> cells = SplitRow(lines[i]);
            if (column < cells.Count)
            {
                cells.RemoveAt(column);
            }

            builder.Append(BuildRow(cells));
        }

        RewriteTable(block, builder.ToString(), row, Math.Max(0, column - 1));
    }

    /// <summary>
    /// Swaps the table's source for <paramref name="newText"/> and parks the caret in the cell the
    /// operation ended on. The block's start offset cannot move: the replacement spans the same range.
    /// </summary>
    private void RewriteTable(BlockLayout block, string newText, int row, int column)
    {
        int start = block.Block.Start;

        document.Replace(start, block.Block.End - start, newText, MarkdownEditKind.Structural, selection);
        PlaceCaretInCell(start, newText, row, column);
    }

    /// <summary>
    /// Resolves a grid (row, column) to a document offset inside the freshly written table text. Body row
    /// r is source line r + 1 because the delimiter row is not part of the grid.
    /// </summary>
    private void PlaceCaretInCell(int tableStart, string tableText, int row, int column)
    {
        string[] lines = tableText.Split('\n');
        int lineIndex = Math.Clamp(row == 0 ? 0 : row + 1, 0, lines.Length - 1);

        int offset = tableStart;
        for (int i = 0; i < lineIndex; i++)
        {
            offset += lines[i].Length + 1;
        }

        List<SourceCell> cells = GetSourceCells(lines[lineIndex]);
        if (cells.Count > 0)
        {
            offset += cells[Math.Clamp(column, 0, cells.Count - 1)].ContentStart;
        }

        SetSelection(TextRange.Caret(Math.Clamp(offset, 0, document.Length)));
    }

    private static List<string> GetTableLines(MarkdownBlock table)
        => (table.RawSource ?? string.Empty).Split('\n').ToList();

    private static int CountColumns(List<string> lines)
        => lines.Count == 0 ? 0 : lines.Max(line => SplitRow(line).Count);

    /// <summary>
    /// Splits one source row into its trimmed cell contents. Mirrors the parser's own cell walk so the
    /// boundaries edited here are the boundaries the grid draws.
    /// </summary>
    private static List<string> SplitRow(string line)
    {
        var cells = new List<string>();
        foreach (SourceCell cell in GetSourceCells(line))
        {
            cells.Add(line[cell.Start..cell.End].Trim());
        }

        return cells;
    }

    /// <summary>
    /// Re-emits a row canonically as <c>| a | b |</c>, the shape every row of a grid table uses.
    /// </summary>
    private static string BuildRow(IReadOnlyList<string> cells)
    {
        var builder = new StringBuilder();
        builder.Append('|');

        foreach (string cell in cells)
        {
            builder.Append(' ').Append(cell).Append(" |");
        }

        return builder.ToString();
    }

    /// <summary>
    /// A cell's span within one source line: the raw slice between separators, where its content starts
    /// once leading padding is trimmed, and the index of the pipe closing it (-1 at end of line).
    /// </summary>
    private readonly record struct SourceCell(int Start, int End, int ContentStart, int ClosePipe);

    /// <summary>
    /// Locates the cells of a single source line, skipping escaped pipes and dropping the empty final
    /// slice a trailing pipe produces - exactly as <see cref="MarkdownParser.ParseTableCells"/> does.
    /// </summary>
    private static List<SourceCell> GetSourceCells(string line)
    {
        var cells = new List<SourceCell>();
        int index = line.Length > 0 && line[0] == '|' ? 1 : 0;
        int cellStart = index;

        for (; index <= line.Length; index++)
        {
            bool atEnd = index == line.Length;
            if (!atEnd && (line[index] != '|' || IsEscapedPipe(line, index)))
            {
                continue;
            }

            string raw = line[cellStart..index];
            int trimOffset = raw.Length - raw.TrimStart().Length;

            if (!(atEnd && raw.Trim().Length == 0 && cells.Count > 0))
            {
                cells.Add(new SourceCell(cellStart, index, cellStart + trimOffset, atEnd ? -1 : index));
            }

            cellStart = index + 1;
        }

        return cells;
    }

    private static bool IsEscapedPipe(string text, int index)
    {
        if (text[index] != '|')
        {
            return false;
        }

        int backslashes = 0;
        for (int i = index - 1; i >= 0 && text[i] == '\\'; i--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }
}
