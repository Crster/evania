using System;
using System.Collections.Generic;
using System.Text;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// Half-open text range over the markdown buffer. <see cref="Start"/> is always the anchor of a
/// selection and <see cref="End"/> the caret, so a range can be "reversed" while still describing
/// the same span of text.
/// </summary>
public readonly struct TextRange : IEquatable<TextRange>
{
    public TextRange(int start, int end)
    {
        Start = start;
        End = end;
    }

    public int Start { get; }

    public int End { get; }

    public int Min => Math.Min(Start, End);

    public int Max => Math.Max(Start, End);

    public int Length => Max - Min;

    public bool IsEmpty => Start == End;

    public static TextRange Caret(int position) => new(position, position);

    public TextRange Collapse(bool toEnd) => Caret(toEnd ? Max : Min);

    public bool Contains(int position) => position >= Min && position < Max;

    public bool Equals(TextRange other) => Start == other.Start && End == other.End;

    public override bool Equals(object? obj) => obj is TextRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Start, End);

    public override string ToString() => $"[{Start}..{End})";
}

/// <summary>
/// A single reversible replacement. The editor batches these into <see cref="UndoUnit"/>s so that
/// typing a word is undone as one action rather than one character at a time.
/// </summary>
internal readonly struct TextEdit
{
    public TextEdit(int position, string removed, string inserted, TextRange selectionBefore, TextRange selectionAfter)
    {
        Position = position;
        Removed = removed;
        Inserted = inserted;
        SelectionBefore = selectionBefore;
        SelectionAfter = selectionAfter;
    }

    public int Position { get; }

    public string Removed { get; }

    public string Inserted { get; }

    public TextRange SelectionBefore { get; }

    public TextRange SelectionAfter { get; }
}

internal sealed class UndoUnit
{
    private readonly List<TextEdit> edits = new();

    public UndoUnit(MarkdownEditKind kind)
    {
        Kind = kind;
    }

    public MarkdownEditKind Kind { get; }

    public IReadOnlyList<TextEdit> Edits => edits;

    public DateTime LastEditUtc { get; private set; } = DateTime.UtcNow;

    public void Add(TextEdit edit)
    {
        edits.Add(edit);
        LastEditUtc = DateTime.UtcNow;
    }
}

/// <summary>
/// Classifies an edit so the undo stack knows when consecutive edits may be merged. Typing merges
/// with typing, deleting with deleting, and anything explicit (a toolbar command, a paste) always
/// starts a fresh unit.
/// </summary>
public enum MarkdownEditKind
{
    Typing,
    Deleting,
    Structural,
}

public sealed class MarkdownDocumentChangedEventArgs : EventArgs
{
    public MarkdownDocumentChangedEventArgs(int position, int removedLength, int insertedLength)
    {
        Position = position;
        RemovedLength = removedLength;
        InsertedLength = insertedLength;
    }

    public int Position { get; }

    public int RemovedLength { get; }

    public int InsertedLength { get; }
}

/// <summary>
/// The markdown buffer plus the edit primitives the editor is built on. Everything the control does
/// to the text ultimately funnels through <see cref="Replace"/>, which is what keeps undo, change
/// notifications and the parser cache in sync.
/// </summary>
public sealed class MarkdownDocument
{
    /// <summary>
    /// Consecutive same-kind edits made within this window collapse into one undo unit, matching the
    /// "undo a word, not a keystroke" behaviour users expect from every other editor.
    /// </summary>
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1);

    private readonly StringBuilder buffer = new();
    private readonly Stack<UndoUnit> undoStack = new();
    private readonly Stack<UndoUnit> redoStack = new();

    private UndoUnit? openUnit;
    private bool suppressUndo;
    private List<int>? lineStarts;

    public MarkdownDocument()
        : this(string.Empty)
    {
    }

    public MarkdownDocument(string text)
    {
        buffer.Append(Normalize(text));
    }

    public event EventHandler<MarkdownDocumentChangedEventArgs>? Changed;

    public int Length => buffer.Length;

    public bool CanUndo => undoStack.Count > 0 || openUnit is not null;

    public bool CanRedo => redoStack.Count > 0;

    public string Text => buffer.ToString();

    public char this[int index] => buffer[index];

    /// <summary>
    /// Replaces the whole buffer without recording undo, for loading a different note.
    /// </summary>
    public void SetText(string text)
    {
        string normalized = Normalize(text);
        int removed = buffer.Length;
        buffer.Clear();
        buffer.Append(normalized);
        undoStack.Clear();
        redoStack.Clear();
        openUnit = null;
        lineStarts = null;
        Changed?.Invoke(this, new MarkdownDocumentChangedEventArgs(0, removed, normalized.Length));
    }

    public string GetText(int start, int length)
    {
        start = Math.Clamp(start, 0, buffer.Length);
        length = Math.Clamp(length, 0, buffer.Length - start);
        return buffer.ToString(start, length);
    }

    public string GetText(TextRange range) => GetText(range.Min, range.Length);

    /// <summary>
    /// The single mutation primitive: swaps <paramref name="length"/> characters at
    /// <paramref name="start"/> for <paramref name="replacement"/> and records it for undo.
    /// </summary>
    public TextRange Replace(int start, int length, string replacement, MarkdownEditKind kind, TextRange selectionBefore)
    {
        start = Math.Clamp(start, 0, buffer.Length);
        length = Math.Clamp(length, 0, buffer.Length - start);
        replacement = Normalize(replacement ?? string.Empty);

        if (length == 0 && replacement.Length == 0)
        {
            return selectionBefore;
        }

        string removed = buffer.ToString(start, length);
        buffer.Remove(start, length);
        buffer.Insert(start, replacement);
        lineStarts = null;

        var selectionAfter = TextRange.Caret(start + replacement.Length);

        if (!suppressUndo)
        {
            Record(new TextEdit(start, removed, replacement, selectionBefore, selectionAfter), kind);
        }

        Changed?.Invoke(this, new MarkdownDocumentChangedEventArgs(start, length, replacement.Length));
        return selectionAfter;
    }

    public TextRange Replace(TextRange range, string replacement, MarkdownEditKind kind)
        => Replace(range.Min, range.Length, replacement, kind, range);

    public TextRange Insert(int position, string text, MarkdownEditKind kind)
        => Replace(position, 0, text, kind, TextRange.Caret(position));

    public TextRange Delete(TextRange range)
        => Replace(range.Min, range.Length, string.Empty, MarkdownEditKind.Deleting, range);

    /// <summary>
    /// Ends the current undo unit so the next edit cannot merge into it. Called when the caret moves,
    /// the control loses focus, or the note is saved.
    /// </summary>
    public void CloseUndoUnit()
    {
        if (openUnit is not null)
        {
            undoStack.Push(openUnit);
            openUnit = null;
        }
    }

    public bool Undo(out TextRange selection)
    {
        CloseUndoUnit();
        selection = default;
        if (undoStack.Count == 0)
        {
            return false;
        }

        UndoUnit unit = undoStack.Pop();
        suppressUndo = true;
        try
        {
            for (int i = unit.Edits.Count - 1; i >= 0; i--)
            {
                TextEdit edit = unit.Edits[i];
                Replace(edit.Position, edit.Inserted.Length, edit.Removed, MarkdownEditKind.Structural, default);
                selection = edit.SelectionBefore;
            }
        }
        finally
        {
            suppressUndo = false;
        }

        redoStack.Push(unit);
        return true;
    }

    public bool Redo(out TextRange selection)
    {
        selection = default;
        if (redoStack.Count == 0)
        {
            return false;
        }

        UndoUnit unit = redoStack.Pop();
        suppressUndo = true;
        try
        {
            foreach (TextEdit edit in unit.Edits)
            {
                Replace(edit.Position, edit.Removed.Length, edit.Inserted, MarkdownEditKind.Structural, default);
                selection = edit.SelectionAfter;
            }
        }
        finally
        {
            suppressUndo = false;
        }

        undoStack.Push(unit);
        return true;
    }

    private void Record(TextEdit edit, MarkdownEditKind kind)
    {
        redoStack.Clear();

        bool mergeable = openUnit is not null
            && kind != MarkdownEditKind.Structural
            && openUnit.Kind == kind
            && DateTime.UtcNow - openUnit.LastEditUtc <= MergeWindow;

        if (!mergeable)
        {
            CloseUndoUnit();
            openUnit = new UndoUnit(kind);
        }

        openUnit!.Add(edit);

        if (kind == MarkdownEditKind.Structural)
        {
            CloseUndoUnit();
        }
    }

    // --- line helpers -------------------------------------------------------------------------
    // Blocks are line-oriented, so the parser and the toolbar commands both need cheap line lookups.
    // The index is rebuilt lazily because a single keystroke would otherwise shift every entry.

    private List<int> LineStarts
    {
        get
        {
            if (lineStarts is not null)
            {
                return lineStarts;
            }

            var starts = new List<int> { 0 };
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == '\n')
                {
                    starts.Add(i + 1);
                }
            }

            lineStarts = starts;
            return starts;
        }
    }

    public int LineCount => LineStarts.Count;

    public int GetLineIndex(int position)
    {
        position = Math.Clamp(position, 0, buffer.Length);
        List<int> starts = LineStarts;
        int low = 0;
        int high = starts.Count - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (starts[mid] <= position)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    public int GetLineStart(int lineIndex) => LineStarts[Math.Clamp(lineIndex, 0, LineStarts.Count - 1)];

    public int GetLineEnd(int lineIndex)
    {
        List<int> starts = LineStarts;
        lineIndex = Math.Clamp(lineIndex, 0, starts.Count - 1);
        if (lineIndex == starts.Count - 1)
        {
            return buffer.Length;
        }

        // Exclude the newline itself.
        return starts[lineIndex + 1] - 1;
    }

    public string GetLineText(int lineIndex)
    {
        int start = GetLineStart(lineIndex);
        return GetText(start, GetLineEnd(lineIndex) - start);
    }

    /// <summary>
    /// Expands a range so it covers whole lines, which is what block-level commands operate on.
    /// </summary>
    public TextRange GetLineSpan(TextRange range)
    {
        int firstLine = GetLineIndex(range.Min);
        int lastLine = GetLineIndex(range.Max);
        return new TextRange(GetLineStart(firstLine), GetLineEnd(lastLine));
    }

    /// <summary>
    /// CRLF and lone CR would each occupy caret positions that never render, so the buffer only ever
    /// stores LF and every entry point normalizes on the way in.
    /// </summary>
    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.IndexOf('\r') < 0)
        {
            return text;
        }

        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}
