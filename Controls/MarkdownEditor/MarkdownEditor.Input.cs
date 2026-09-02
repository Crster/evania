using System;
using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// Pointer and keyboard handling for <see cref="MarkdownEditor"/>.
/// </summary>
/// <remarks>
/// <see cref="CoreTextEditContext"/> delivers character input, but caret movement, selection gestures
/// and editing shortcuts are the control's own responsibility. Everything here converts a gesture
/// into either a <see cref="SetSelection"/> call or a document edit; nothing draws or measures.
/// </remarks>
public sealed partial class MarkdownEditor
{
    private bool isDragging;
    private int dragAnchor;
    private DateTime lastClickUtc;
    private int consecutiveClicks;
    private global::Windows.Foundation.Point lastClickPoint;

    private void InitializeInput()
    {
        canvas.PointerPressed += OnCanvasPointerPressed;
        canvas.PointerMoved += OnCanvasPointerMoved;
        canvas.PointerReleased += OnCanvasPointerReleased;
        canvas.PointerExited += OnCanvasPointerExited;

        KeyDown += OnEditorKeyDown;
        CharacterReceived += OnEditorCharacterReceived;
    }

    /// <summary>
    /// The actual source of typed characters.
    /// </summary>
    /// <remarks>
    /// <see cref="CoreTextEditContext"/> only delivers text through <c>TextUpdating</c> when it is
    /// attached to a CoreWindow, which a WinUI 3 desktop app does not have. The edit context is still
    /// worth keeping for IME composition and caret reporting, but ordinary keystrokes have to be taken
    /// from <see cref="UIElement.CharacterReceived"/> or nothing is ever inserted.
    /// </remarks>
    private void OnEditorCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        char character = e.Character;
        MarkdownEditorLog.Write($"character received U+{(int)character:X4} caret={selection.End}");

        if (IsReadOnly)
        {
            MarkdownEditorLog.Exit("editor is read-only");
            return;
        }

        // Enter, Tab, Backspace and Escape all arrive here as control characters, but OnEditorKeyDown
        // already handled them; inserting them again would duplicate the edit.
        if (character is '\r' or '\n' or '\t' or '\b' or (char)0x1B)
        {
            MarkdownEditorLog.Exit("control character already handled by key down");
            return;
        }

        if (char.IsControl(character))
        {
            MarkdownEditorLog.Exit("non-printable control character");
            return;
        }

        InsertText(character.ToString());
        e.Handled = true;
    }

    /// <summary>
    /// Replaces the selection with <paramref name="text"/> and places the caret after it.
    /// </summary>
    private void InsertText(string text)
    {
        MarkdownEditorLog.Write($"insert {MarkdownEditorLog.Snippet(text)} over {selection.Min}..{selection.Max}");

        TableGuardResult guard = GetTableGuard(selection, text);
        if (guard.Blocked)
        {
            MarkdownEditorLog.Exit("edit refused by table protection");
            return;
        }

        string insertable = guard.Replacement;
        if (insertable.Length == 0)
        {
            MarkdownEditorLog.Exit("typed characters were all table structure, nothing inserted");
            return;
        }

        TextRange result = document.Replace(selection.Min, selection.Length, insertable, MarkdownEditKind.Typing, selection);
        SetSelection(result);
        AutoContinueList(result.End - insertable.Length, insertable);
    }

    // --- pointer ------------------------------------------------------------------------------

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        bool focused = Focus(FocusState.Programmatic);

        global::Windows.Foundation.Point point = e.GetCurrentPoint(canvas).Position;
        int position = HitTestDocument(point);
        MarkdownEditorLog.Write($"pointer pressed at {point.X:F0},{point.Y:F0} -> offset {position}, focusTaken={focused}");

        if (TryToggleTaskAt(point))
        {
            e.Handled = true;
            return;
        }

        if (IsCtrlDown() && TryInvokeLinkAt(position))
        {
            e.Handled = true;
            return;
        }

        consecutiveClicks = IsDoubleClick(point) ? consecutiveClicks + 1 : 1;
        lastClickUtc = DateTime.UtcNow;
        lastClickPoint = point;

        if (consecutiveClicks == 2)
        {
            SetSelection(GetWordRange(position));
        }
        else if (consecutiveClicks >= 3)
        {
            SetSelection(document.GetLineSpan(TextRange.Caret(position)));
        }
        else if (IsShiftDown())
        {
            SetSelection(new TextRange(selection.Start, position));
        }
        else
        {
            SetSelection(TextRange.Caret(position));
        }

        isDragging = true;
        dragAnchor = selection.Start;
        canvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!isDragging)
        {
            UpdateCursor(e.GetCurrentPoint(canvas).Position);
            return;
        }

        int position = HitTestDocument(e.GetCurrentPoint(canvas).Position);
        SetSelection(new TextRange(dragAnchor, position));
        e.Handled = true;
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (isDragging)
        {
            isDragging = false;
            canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnCanvasPointerExited(object sender, PointerRoutedEventArgs e)
        => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    private void UpdateCursor(global::Windows.Foundation.Point point)
    {
        // The checkbox is a button, not text, so it gets the arrow instead of the I-beam.
        BlockLayout? hoverBlock = engine.HitTestBlock((float)point.Y);
        if (hoverBlock is not null
            && hoverBlock.Block.Kind == MarkdownBlockKind.TaskItem
            && BlockRenderers.GetCheckboxBounds(theme, hoverBlock).Contains(point))
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
            return;
        }

        int position = HitTestDocument(point);
        bool overLink = IsCtrlDown() && FindLinkTarget(position) is not null;
        ProtectedCursor = InputSystemCursor.Create(overLink ? InputSystemCursorShape.Hand : InputSystemCursorShape.IBeam);
    }

    private bool IsDoubleClick(global::Windows.Foundation.Point point)
        => DateTime.UtcNow - lastClickUtc < TimeSpan.FromMilliseconds(450)
            && Math.Abs(point.X - lastClickPoint.X) < 6
            && Math.Abs(point.Y - lastClickPoint.Y) < 6;

    /// <summary>
    /// Maps a canvas point to a document offset by finding the block band it falls in, then asking the
    /// block's text region (or the nearest table cell) to resolve the exact character.
    /// </summary>
    private int HitTestDocument(global::Windows.Foundation.Point point)
    {
        BlockLayout? block = engine.HitTestBlock((float)point.Y);
        if (block is null)
        {
            return 0;
        }

        if (block.Cells.Count > 0)
        {
            // Test against each cell's full grid rectangle, not just its text bounds: the padding
            // and any empty space left of a short line belong to the cell, so a click there must
            // stay inside it instead of snapping to a neighbour's left edge.
            float localY = (float)point.Y - block.Y;

            foreach (TableCellLayout cell in block.Cells)
            {
                float left = block.ColumnOffsets[cell.Column];
                float right = block.ColumnOffsets[cell.Column + 1];
                float top = block.RowOffsets[cell.Row];
                float bottom = block.RowOffsets[cell.Row + 1];

                if (point.X >= left && point.X < right && localY >= top && localY < bottom)
                {
                    return cell.Region.HitTest(new Vector2((float)point.X, localY));
                }
            }
        }

        if (block.Text is null)
        {
            return block.Block.Start;
        }

        return block.Text.HitTest(new Vector2((float)point.X, (float)(point.Y - block.Y)));
    }

    private bool TryToggleTaskAt(global::Windows.Foundation.Point point)
    {
        BlockLayout? block = engine.HitTestBlock((float)point.Y);
        if (block is null || block.Block.Kind != MarkdownBlockKind.TaskItem || IsReadOnly)
        {
            return false;
        }

        global::Windows.Foundation.Rect bounds = BlockRenderers.GetCheckboxBounds(theme, block);
        if (!bounds.Contains(point))
        {
            return false;
        }

        ToggleTask(block.Block);
        return true;
    }

    private bool TryInvokeLinkAt(int position)
    {
        string? target = FindLinkTarget(position);
        if (target is null)
        {
            return false;
        }

        LinkInvoked?.Invoke(this, target);
        return true;
    }

    private string? FindLinkTarget(int position)
    {
        BlockLayout? block = engine.FindBlockFor(position);
        if (block is null)
        {
            return null;
        }

        if (block.Block.Kind == MarkdownBlockKind.Image)
        {
            return block.Block.Target;
        }

        foreach (MarkdownInline inline in block.Block.Inlines)
        {
            if (inline.LinkTarget is not null && position >= inline.Start && position <= inline.End)
            {
                return inline.LinkTarget;
            }
        }

        return null;
    }

    // --- keyboard -----------------------------------------------------------------------------

    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsCtrlDown();
        bool shift = IsShiftDown();
        MarkdownEditorLog.Write($"key down {e.Key} ctrl={ctrl} shift={shift} caret={selection.End} readOnly={IsReadOnly}");

        if (ctrl && HandleShortcut(e.Key, shift))
        {
            MarkdownEditorLog.Write($"shortcut handled Ctrl+{e.Key}");
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Left:
                MoveCaret(GetAdjacentPosition(selection.End, -1), shift);
                e.Handled = true;
                break;

            case VirtualKey.Right:
                MoveCaret(GetAdjacentPosition(selection.End, 1), shift);
                e.Handled = true;
                break;

            case VirtualKey.Up:
                MoveCaretVertically(-1, shift);
                e.Handled = true;
                break;

            case VirtualKey.Down:
                MoveCaretVertically(1, shift);
                e.Handled = true;
                break;

            case VirtualKey.Home:
                MoveCaret(ctrl ? 0 : document.GetLineStart(document.GetLineIndex(selection.End)), shift);
                e.Handled = true;
                break;

            case VirtualKey.End:
                MoveCaret(ctrl ? document.Length : document.GetLineEnd(document.GetLineIndex(selection.End)), shift);
                e.Handled = true;
                break;

            case VirtualKey.PageUp:
                MoveCaretByPage(-1, shift);
                e.Handled = true;
                break;

            case VirtualKey.PageDown:
                MoveCaretByPage(1, shift);
                e.Handled = true;
                break;

            case VirtualKey.Back when !IsReadOnly:
                HandleBackspace();
                e.Handled = true;
                break;

            case VirtualKey.Delete when !IsReadOnly:
                HandleDelete();
                e.Handled = true;
                break;

            case VirtualKey.Enter when !IsReadOnly:
                HandleEnter();
                e.Handled = true;
                break;

            case VirtualKey.Tab when !IsReadOnly:
                HandleTab(shift);
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                SetSelection(TextRange.Caret(selection.End));
                e.Handled = true;
                break;
        }
    }

    private bool HandleShortcut(VirtualKey key, bool shift) => key switch
    {
        VirtualKey.B => Run(ToggleBold),
        VirtualKey.I => Run(ToggleItalic),
        VirtualKey.E => Run(ToggleInlineCode),
        VirtualKey.K => Run(() => InsertLink()),
        VirtualKey.A => Run(SelectAll),
        VirtualKey.C => Run(() => _ = CopyAsync()),
        VirtualKey.X => Run(() => _ = CutAsync()),
        VirtualKey.V => Run(() => _ = PasteAsync()),
        VirtualKey.Z => Run(() =>
        {
            if (shift)
            {
                Redo();
            }
            else
            {
                Undo();
            }
        }),
        VirtualKey.Y => Run(Redo),
        _ => false,
    };

    private static bool Run(Action action)
    {
        action();
        return true;
    }

    private void MoveCaret(int position, bool extend)
        => SetSelection(extend ? new TextRange(selection.Start, position) : TextRange.Caret(position));

    /// <summary>
    /// Steps one caret position, treating a surrogate pair or CRLF-free newline as a single unit so the
    /// caret never lands inside a character.
    /// </summary>
    private int GetAdjacentPosition(int position, int direction)
    {
        int next = position + direction;
        if (next < 0)
        {
            return 0;
        }

        if (next > document.Length)
        {
            return document.Length;
        }

        if (direction < 0 && next > 0 && char.IsLowSurrogate(document[next]))
        {
            next--;
        }
        else if (direction > 0 && next < document.Length && char.IsLowSurrogate(document[next]))
        {
            next++;
        }

        return next;
    }

    /// <summary>
    /// Moves the caret one visual line, using the remembered x offset so it tracks a straight column.
    /// </summary>
    private void MoveCaretVertically(int direction, bool extend)
    {
        if (!TryGetCaretRect(out global::Windows.Foundation.Rect caretRect))
        {
            return;
        }

        float x = desiredCaretX ?? (float)caretRect.X;
        double probeY = direction < 0
            ? caretRect.Top - (caretRect.Height * 0.6)
            : caretRect.Bottom + (caretRect.Height * 0.6);

        int position = HitTestDocument(new global::Windows.Foundation.Point(x, probeY));
        SetSelection(extend ? new TextRange(selection.Start, position) : TextRange.Caret(position), resetDesiredX: false);
        desiredCaretX = x;
    }

    private void MoveCaretByPage(int direction, bool extend)
    {
        if (!TryGetCaretRect(out global::Windows.Foundation.Rect caretRect))
        {
            return;
        }

        float x = desiredCaretX ?? (float)caretRect.X;
        double probeY = caretRect.Top + (direction * scroller.ViewportHeight);
        int position = HitTestDocument(new global::Windows.Foundation.Point(x, probeY));
        SetSelection(extend ? new TextRange(selection.Start, position) : TextRange.Caret(position), resetDesiredX: false);
        desiredCaretX = x;
    }

    private void HandleBackspace()
    {
        if (!selection.IsEmpty)
        {
            DeleteSelectionPreservingTable();
            return;
        }

        if (selection.End == 0)
        {
            return;
        }

        // Backspacing at the start of a list item removes the marker first, which is what turns a list
        // item back into a paragraph instead of merging it with the line above.
        if (TryOutdentOrClearMarker())
        {
            return;
        }

        int start = GetAdjacentPosition(selection.End, -1);

        // The pipes and row breaks of a grid-rendered table are structure, not text: they are invisible
        // here, so deleting one breaks the table without the user being able to see why.
        if (IsTableStructure(start, selection.End - start))
        {
            MarkdownEditorLog.Write($"backspace blocked on table structure at {start}");
            return;
        }

        SetSelection(document.Replace(start, selection.End - start, string.Empty, MarkdownEditKind.Deleting, selection));
    }

    private void HandleDelete()
    {
        if (!selection.IsEmpty)
        {
            DeleteSelectionPreservingTable();
            return;
        }

        if (selection.End >= document.Length)
        {
            return;
        }

        int end = GetAdjacentPosition(selection.End, 1);

        if (IsTableStructure(selection.End, end - selection.End))
        {
            MarkdownEditorLog.Write($"delete blocked on table structure at {selection.End}");
            return;
        }

        SetSelection(document.Replace(selection.End, end - selection.End, string.Empty, MarkdownEditKind.Deleting, selection));
    }

    private static bool IsCtrlDown()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsShiftDown()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// Expands to the word around <paramref name="position"/> for double-click selection.
    /// </summary>
    private TextRange GetWordRange(int position)
    {
        if (document.Length == 0)
        {
            return TextRange.Caret(0);
        }

        int start = Math.Clamp(position, 0, document.Length - 1);
        int end = start;

        while (start > 0 && IsWordChar(document[start - 1]))
        {
            start--;
        }

        while (end < document.Length && IsWordChar(document[end]))
        {
            end++;
        }

        return start == end ? TextRange.Caret(position) : new TextRange(start, end);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
