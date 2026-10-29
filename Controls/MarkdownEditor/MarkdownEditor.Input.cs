using System;
using System.Collections.Generic;
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
        canvas.RightTapped += OnCanvasRightTapped;

        KeyDown += OnEditorKeyDown;
        CharacterReceived += OnEditorCharacterReceived;
    }

    private void OnCanvasRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        global::Windows.Foundation.Point point = e.GetPosition(canvas);
        int position = HitTestDocument(point);

        if (FindImageBlockAt(point) is BlockLayout image)
        {
            ShowLinkContextMenu(point, image.Block.Start, image.Block.End, image.Block.AltText ?? string.Empty, image.Block.Target ?? string.Empty, isImage: true);
            e.Handled = true;
            return;
        }

        if (TryGetLinkAt(position, out int groupStart, out int groupEnd, out string label, out string target, out bool isImage))
        {
            ShowLinkContextMenu(point, groupStart, groupEnd, label, target, isImage);
            e.Handled = true;
        }
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

        if (IsReadOnly)
        {
            return;
        }

        // Enter, Tab, Backspace and Escape all arrive here as control characters, but OnEditorKeyDown
        // already handled them; inserting them again would duplicate the edit.
        if (character is '\r' or '\n' or '\t' or '\b' or (char)0x1B)
        {
            return;
        }

        if (char.IsControl(character))
        {
            return;
        }

        InsertText(character.ToString());
        e.Handled = true;
    }

    /// <summary>
    /// Replaces the selection with <paramref name="text"/> and places the caret after it.
    /// </summary>
    public void InsertText(string text)
    {
        if (IsReadOnly)
        {
            return;
        }

        EscapeImageBlock();


        TableGuardResult guard = GetTableGuard(selection, text);
        if (guard.Blocked)
        {
            return;
        }

        string insertable = guard.Replacement;
        if (insertable.Length == 0)
        {
            return;
        }

        TextRange result = document.Replace(selection.Min, selection.Length, insertable, MarkdownEditKind.Typing, selection);
        SetSelection(result);
        AutoContinueList(result.End - insertable.Length, insertable);
    }

    // --- pointer ------------------------------------------------------------------------------

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        HideImageTooltip();

        global::Windows.Foundation.Point point = e.GetCurrentPoint(canvas).Position;
        int position = HitTestDocument(point);

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

        // A picture stands for its whole attachment rather than a run of characters, so clicking it
        // highlights the image. That is how the user reaches Delete on it, and the double and triple
        // click gestures have nothing meaningful to expand inside the hidden markdown anyway.
        if (FindImageBlockAt(point) is BlockLayout image && !IsShiftDown())
        {
            SetSelection(new TextRange(image.Block.Start, image.Block.End));
            isDragging = true;
            dragAnchor = selection.Start;
            canvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

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
        e.Handled = true;

        // Pointer samples far outrun glyph boundaries, so most moves resolve to the offset the
        // selection already has. Setting it anyway repaints the whole surface and re-runs the
        // scroll-into-view maths on every one of them.
        if (position == selection.End)
        {
            return;
        }

        SetSelection(new TextRange(dragAnchor, position));
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
    {
        HideImageTooltip();
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private void UpdateCursor(global::Windows.Foundation.Point point)
    {
        UpdateImageTooltip(point);

        // The checkbox is a button, not text, so it gets the arrow instead of the I-beam.
        BlockLayout? hoverBlock = engine.HitTestBlock((float)point.Y);
        if (hoverBlock is not null
            && hoverBlock.Block.Kind == MarkdownBlockKind.TaskItem
            && BlockRenderers.GetCheckboxBounds(theme, hoverBlock).Contains(point))
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
            return;
        }

        // A picture is an object, not text: the pointer over it stays the normal arrow too, so the
        // I-beam does not pretend the attachment is selectable as characters.
        if (hoverBlock is not null
            && hoverBlock.Block.Kind == MarkdownBlockKind.Image
            && BlockRenderers.GetImageBounds(hoverBlock).Contains(point))
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

        if (block.Block.Kind == MarkdownBlockKind.Image)
        {
            // The picture stands for its whole attachment, so a click resolves to the nearer edge.
            // Returning the start for both sides is what left no way to put the caret after an image,
            // which is exactly where Enter has to be pressed to write underneath it.
            global::Windows.Foundation.Rect box = BlockRenderers.GetImageBounds(block);
            return point.X > box.X + (box.Width / 2f) ? block.Block.End : block.Block.Start;
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

        if (ctrl && HandleShortcut(e.Key, shift))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Left:
                // A non-empty selection collapses to its own edge first, which is what every text
                // control does and what lets the user arrow out of a clicked image.
                MoveCaret(!selection.IsEmpty && !shift ? selection.Min : GetAdjacentPosition(selection.End, -1), shift);
                e.Handled = true;
                break;

            case VirtualKey.Right:
                MoveCaret(!selection.IsEmpty && !shift ? selection.Max : GetAdjacentPosition(selection.End, 1), shift);
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
        VirtualKey.H => Run(ToggleHighlight),
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
    /// Finds block-level markdown syntax that must remain indivisible while editing: only the markers
    /// that stay hidden even on the block holding the caret (list markers, which the gutter glyph
    /// stands in for, plus rules and images). Heading and quote markers are deliberately excluded
    /// because they are revealed - and therefore directly editable - while the caret is on their
    /// block, as is all inline syntax.
    /// </summary>
    private bool TryGetAtomicBlockSyntax(int position, out TextRange syntax)
    {
        foreach (MarkdownBlock block in MarkdownParser.Parse(document))
        {
            if (position < block.Start)
            {
                break;
            }

            if (position > block.End || block.Inlines.Count == 0)
            {
                continue;
            }

            bool isAtomicBlock = block.Kind is MarkdownBlockKind.BulletItem
                or MarkdownBlockKind.OrderedItem
                or MarkdownBlockKind.TaskItem
                or MarkdownBlockKind.ThematicBreak
                or MarkdownBlockKind.Image;
            MarkdownInline marker = block.Inlines[0];
            if (isAtomicBlock && marker.IsSyntax && marker.Start == block.Start
                && position >= marker.Start && position <= marker.End)
            {
                syntax = new TextRange(marker.Start, marker.End);
                return true;
            }
        }

        syntax = default;
        return false;
    }

    private int NormalizeAtomicBlockPosition(int position, int direction)
    {
        if (!TryGetAtomicBlockSyntax(position, out TextRange syntax)
            || position == syntax.Min || position == syntax.Max)
        {
            return position;
        }

        return direction < 0 ? syntax.Min : syntax.Max;
    }

    /// <summary>
    /// True when the block owning <paramref name="position"/> is the one holding the caret, so its
    /// syntax is at least partly visible. Any syntax character the caret could directly delete or
    /// type at necessarily sits in the group containing the caret, which is exactly what that block
    /// reveals, so a block-level check is enough for the edit guards.
    /// </summary>
    private bool IsSyntaxRevealedAt(int position)
    {
        BlockLayout? block = engine.FindBlockFor(position);
        return block is not null && block.Contains(position) && block.ShowsSyntax;
    }

    private TextRange ExpandSelectionOverAtomicBlockSyntax(TextRange range)
    {
        if (range.IsEmpty)
        {
            return range;
        }

        int min = range.Min;
        int max = range.Max;
        foreach (MarkdownBlock block in MarkdownParser.Parse(document))
        {
            if (block.Inlines.Count == 0 || max <= block.Start)
            {
                continue;
            }

            MarkdownInline marker = block.Inlines[0];
            bool isAtomicBlock = block.Kind is MarkdownBlockKind.BulletItem
                or MarkdownBlockKind.OrderedItem
                or MarkdownBlockKind.TaskItem
                or MarkdownBlockKind.ThematicBreak
                or MarkdownBlockKind.Image;
            if (isAtomicBlock && marker.IsSyntax && min < marker.End && max > marker.Start)
            {
                min = Math.Min(min, marker.Start);
                max = Math.Max(max, marker.End);
            }
        }

        return range.Start <= range.End ? new TextRange(min, max) : new TextRange(max, min);
    }

    /// <summary>
    /// Steps one caret position, treating a surrogate pair or CRLF-free newline as a single unit so the
    /// caret never lands inside a character.
    /// </summary>
    private int GetAdjacentPosition(int position, int direction)
    {
        // An image is treated as one oversized character: its markdown stays hidden, so there is
        // nothing in between to stop at. A step that would land inside the link jumps to its far edge
        // instead, which is what lets the arrow keys walk past a clicked or left-aligned caret.
        if (engine.FindBlockFor(position)?.Block is { Kind: MarkdownBlockKind.Image } image)
        {
            if (direction > 0 && position >= image.Start && position < image.End)
            {
                return image.End;
            }

            if (direction < 0 && position > image.Start && position <= image.End)
            {
                return image.Start;
            }
        }

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

        return NormalizeAtomicBlockPosition(next, direction);
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

        // A caret parked at an image edge is centred inside the tall image box (see TryGetCaretRect), so
        // a probe relative to the caret rect would land back inside the same image and Up/Down could
        // never leave it. Probe from the image box's own top or bottom edge instead.
        double probeY = direction < 0
            ? caretRect.Top - (caretRect.Height * 0.6)
            : caretRect.Bottom + (caretRect.Height * 0.6);

        BlockLayout? caretBlock = engine.FindBlockFor(selection.End);
        if (caretBlock is not null && caretBlock.Block.Kind == MarkdownBlockKind.Image)
        {
            global::Windows.Foundation.Rect box = BlockRenderers.GetImageBounds(caretBlock);
            probeY = direction < 0 ? box.Top - 2f : box.Bottom + 2f;
        }

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
        if (TryGetSelectedImage(out MarkdownBlock? selected))
        {
            DeleteImageBlock(selected!);
            return;
        }

        if (!selection.IsEmpty)
        {
            selection = ExpandSelectionOverAtomicBlockSyntax(selection);
            if (TryRemoveLinkGroupForSelection())
            {
                return;
            }

            DeleteSelectionPreservingTable();
            return;
        }

        if (selection.End == 0)
        {
            return;
        }

        // The caret sits right behind an image on its own line. Deleting into it one character at a
        // time would shred the invisible attachment link, so the whole block goes instead. A caret
        // past the line break is on the next line, where backspace should just join the lines.
        if (FindImageEndingAt(selection.End) is MarkdownBlock behind)
        {
            DeleteImageBlock(behind);
            return;
        }

        // Backspacing at the start of a list item removes the marker first, which is what turns a list
        // item back into a paragraph instead of merging it with the line above.
        if (TryOutdentOrClearMarker())
        {
            return;
        }

        // An inline attachment link hides its [ and ](url) punctuation, so backspacing that would eat
        // a bracket or empty the label removes the whole link rather than leaving "](url)" behind.
        if (TryRemoveLinkGroupBackward())
        {
            return;
        }

        int start = GetAdjacentPosition(selection.End, -1);

        if (TryGetAtomicBlockSyntax(selection.End, out TextRange syntax)
            && selection.End > syntax.Min && selection.End <= syntax.Max)
        {
            start = syntax.Min;
        }

        // The pipes and row breaks of a grid-rendered table are structure, not text: they are invisible
        // here, so deleting one breaks the table without the user being able to see why.
        if (IsTableStructure(start, selection.End - start))
        {
            return;
        }

        SetSelection(document.Replace(start, selection.End - start, string.Empty, MarkdownEditKind.Deleting, selection));
    }

    private void HandleDelete()
    {
        if (TryGetSelectedImage(out MarkdownBlock? selected))
        {
            DeleteImageBlock(selected!);
            return;
        }

        if (!selection.IsEmpty)
        {
            selection = ExpandSelectionOverAtomicBlockSyntax(selection);
            if (TryRemoveLinkGroupForSelection())
            {
                return;
            }

            DeleteSelectionPreservingTable();
            return;
        }

        if (selection.End >= document.Length)
        {
            return;
        }

        // Mirrors the backspace case: a caret parked in front of a picture deletes it whole rather
        // than nibbling away at the hidden link.
        if (FindImageStartingAt(selection.End) is MarkdownBlock ahead)
        {
            DeleteImageBlock(ahead);
            return;
        }

        // Deleting forward into the start of an inline attachment link removes the whole link rather
        // than exposing its hidden ](url) punctuation.
        if (TryRemoveLinkGroupForward())
        {
            return;
        }

        int end = GetAdjacentPosition(selection.End, 1);

        if (TryGetAtomicBlockSyntax(selection.End, out TextRange syntax)
            && selection.End >= syntax.Min && selection.End < syntax.Max)
        {
            end = syntax.Max;
        }

        if (IsTableStructure(selection.End, end - selection.End))
        {
            return;
        }

        SetSelection(document.Replace(selection.End, end - selection.End, string.Empty, MarkdownEditKind.Deleting, selection));
    }

    /// <summary>
    /// The image block whose markdown ends at <paramref name="position"/>: the one sitting directly in
    /// front of a caret there, and therefore the one backspace removes.
    /// </summary>
    private MarkdownBlock? FindImageEndingAt(int position)
    {
        MarkdownBlock? block = engine.FindBlockFor(position)?.Block;
        return block is not null && block.Kind == MarkdownBlockKind.Image && block.End == position ? block : null;
    }

    /// <summary>
    /// The image block that starts at <paramref name="position"/>: the one directly behind a caret
    /// there, and therefore the one the delete key removes.
    /// </summary>
    private MarkdownBlock? FindImageStartingAt(int position)
    {
        MarkdownBlock? block = engine.FindBlockFor(position)?.Block;
        return block is not null && block.Kind == MarkdownBlockKind.Image && block.Start == position ? block : null;
    }

    /// <summary>
    /// The image block drawn under <paramref name="point"/>, or null. Unlike
    /// <see cref="HitTestDocument"/> this reports a miss when the pointer is merely in the band around
    /// a picture, so a click beside one does not select it.
    /// </summary>
    private BlockLayout? FindImageBlockAt(global::Windows.Foundation.Point point)
    {
        BlockLayout? block = engine.HitTestBlock((float)point.Y);
        if (block is null || block.Block.Kind != MarkdownBlockKind.Image)
        {
            return null;
        }

        return BlockRenderers.GetImageBounds(block).Contains(point) ? block : null;
    }

    /// <summary>
    /// True when the selection is exactly one image's markdown, which is what clicking a picture
    /// leaves behind. Backspace and Delete turn that into "remove the attachment".
    /// </summary>
    private bool TryGetSelectedImage(out MarkdownBlock? image)
    {
        image = null;
        if (selection.IsEmpty)
        {
            return false;
        }

        foreach (BlockLayout block in engine.Blocks)
        {
            if (block.Block.Kind == MarkdownBlockKind.Image
                && block.Block.Start == selection.Min
                && block.Block.End == selection.Max)
            {
                image = block.Block;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes an image together with one of its line breaks, so no blank line is left where the
    /// attachment was. The image cache keeps the bitmap, so undoing brings it back without reloading.
    /// </summary>
    private void DeleteImageBlock(MarkdownBlock block)
    {
        int start = block.Start;
        int end = block.End;

        // Prefer swallowing the break that follows the image; at the very end of the document the one
        // before it goes instead so the preceding line is not left dangling.
        if (end < document.Length && document[end] == '\n')
        {
            end++;
        }
        else if (start > 0 && document[start - 1] == '\n')
        {
            start--;
        }

        SetSelection(document.Replace(start, end - start, string.Empty, MarkdownEditKind.Deleting, selection));
    }

    /// <summary>
    /// Locates the inline link group (the contiguous run of link-styled spans that spells
    /// <c>[label](url)</c>) whose source range contains <paramref name="position"/>. The <c>[</c> and
    /// <c>](url)</c> punctuation is hidden, so a delete that eats one of those characters - or the last
    /// visible label character - must take the whole link rather than stranding "](url)" as text.
    /// </summary>
    private bool TryGetLinkGroupAt(
        int position,
        out int groupStart,
        out int groupEnd,
        out bool positionIsSyntax,
        out int visibleLength)
    {
        groupStart = groupEnd = 0;
        positionIsSyntax = false;
        visibleLength = 0;

        BlockLayout? layout = engine.FindBlockFor(position);
        if (layout is null)
        {
            return false;
        }

        IReadOnlyList<MarkdownInline> inlines = layout.Block.Inlines;
        int index = -1;
        for (int i = 0; i < inlines.Count; i++)
        {
            if ((inlines[i].Style & InlineStyle.Link) != 0 && position >= inlines[i].Start && position < inlines[i].End)
            {
                index = i;
                positionIsSyntax = inlines[i].IsSyntax;
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

        for (int i = first; i <= last; i++)
        {
            if (!inlines[i].IsSyntax)
            {
                visibleLength += inlines[i].Length;
            }
        }

        return true;
    }

    /// <summary>
    /// Backspace at the caret: removes the whole link when the character being eaten is hidden link
    /// punctuation, or when it is the last visible label character, so "](url)" never survives.
    /// </summary>
    private bool TryRemoveLinkGroupBackward()
    {
        int delStart = GetAdjacentPosition(selection.End, -1);
        if (delStart >= selection.End)
        {
            return false;
        }

        if (!TryGetLinkGroupAt(delStart, out int groupStart, out int groupEnd, out bool isSyntax, out int visibleLength))
        {
            return false;
        }

        // The punctuation is visible on the block being edited, so the user can delete it one
        // character at a time instead of losing the whole link.
        if (IsSyntaxRevealedAt(delStart))
        {
            return false;
        }

        if (!isSyntax && visibleLength > 1)
        {
            return false;
        }

        RemoveLinkRange(groupStart, groupEnd);
        return true;
    }

    /// <summary>
    /// Delete at the caret: mirrors the backspace case for a caret parked just before a link.
    /// </summary>
    private bool TryRemoveLinkGroupForward()
    {
        if (!TryGetLinkGroupAt(selection.End, out int groupStart, out int groupEnd, out bool isSyntax, out int visibleLength))
        {
            return false;
        }

        // Visible punctuation is deleted character by character, mirroring the backspace case.
        if (IsSyntaxRevealedAt(selection.End))
        {
            return false;
        }

        if (!isSyntax && visibleLength > 1)
        {
            return false;
        }

        RemoveLinkRange(groupStart, groupEnd);
        return true;
    }

    /// <summary>
    /// Backspace/Delete over a selection: a selection that already spans the whole link deletes
    /// cleanly, but one that eats a hidden bracket/paren or empties the label would strand "](url)"
    /// or an empty link, so expand to the full group first. A partial edit inside the label is left
    /// to the normal path so selecting part of a filename does not delete the attachment.
    /// </summary>
    private bool TryRemoveLinkGroupForSelection()
    {
        int selStart = selection.Min;
        int selEnd = selection.Max;

        // The selection can clip a link at either end, so probe both boundary blocks.
        foreach (int probe in new[] { selStart, selEnd - 1 })
        {
            BlockLayout? layout = engine.FindBlockFor(probe);
            if (layout is null)
            {
                continue;
            }

            // On a revealed block the punctuation is visible text, so a selection deletes exactly
            // what it covers instead of expanding to the whole link.
            if (layout.ShowsSyntax)
            {
                continue;
            }

            if (TryFindClippedLinkGroup(layout.Block.Inlines, selStart, selEnd, out int groupStart, out int groupEnd, out bool mustRemove))
            {
                return mustRemove && RemoveClippedLink(selStart, selEnd, groupStart, groupEnd);
            }
        }

        return false;
    }

    private bool RemoveClippedLink(int selStart, int selEnd, int groupStart, int groupEnd)
    {
        RemoveLinkRange(Math.Min(selStart, groupStart), Math.Max(selEnd, groupEnd));
        return true;
    }

    /// <summary>
    /// Finds the link group (consecutive link-styled spans) that <paramref name="selStart"/>..<paramref name="selEnd"/>
    /// clips into, reporting its full source range and whether deleting the selection would break it.
    /// Returns false when the selection touches no link.
    /// </summary>
    private static bool TryFindClippedLinkGroup(
        IReadOnlyList<MarkdownInline> inlines,
        int selStart,
        int selEnd,
        out int groupStart,
        out int groupEnd,
        out bool mustRemove)
    {
        groupStart = groupEnd = 0;
        mustRemove = false;

        int first = -1;
        int last = -1;
        for (int i = 0; i < inlines.Count; i++)
        {
            if ((inlines[i].Style & InlineStyle.Link) != 0 && inlines[i].End > selStart && inlines[i].Start < selEnd)
            {
                if (first < 0)
                {
                    first = i;
                }

                last = i;
            }
        }

        if (first < 0)
        {
            return false;
        }

        // Expand out to the whole group even when the selection only clipped part of it.
        while (first > 0 && (inlines[first - 1].Style & InlineStyle.Link) != 0)
        {
            first--;
        }

        while (last + 1 < inlines.Count && (inlines[last + 1].Style & InlineStyle.Link) != 0)
        {
            last++;
        }

        groupStart = inlines[first].Start;
        groupEnd = inlines[last].End;

        // A selection that already covers the whole link deletes cleanly through the normal path.
        if (groupStart >= selStart && groupEnd <= selEnd)
        {
            return false;
        }

        int labelStart = int.MaxValue;
        int labelEnd = int.MinValue;
        for (int i = first; i <= last; i++)
        {
            if (inlines[i].IsSyntax)
            {
                if (inlines[i].End > selStart && inlines[i].Start < selEnd)
                {
                    mustRemove = true;
                }
            }
            else if (inlines[i].Length > 0)
            {
                labelStart = Math.Min(labelStart, inlines[i].Start);
                labelEnd = Math.Max(labelEnd, inlines[i].End);
            }
        }

        // Emptying the label leaves an invisible "[](url)" stub, so treat that as removing the link.
        if (labelStart != int.MaxValue && selStart <= labelStart && selEnd >= labelEnd)
        {
            mustRemove = true;
        }

        return true;
    }

    /// <summary>
    /// Deletes <paramref name="start"/>..<paramref name="end"/>. When the removed link was alone on its
    /// line the line break goes with it, so no blank line is left where the attachment was.
    /// </summary>
    private void RemoveLinkRange(int start, int end)
    {
        if (start >= end)
        {
            return;
        }

        bool aloneOnLine = (start == 0 || document[start - 1] == '\n')
            && (end >= document.Length || document[end] == '\n');

        if (aloneOnLine)
        {
            if (end < document.Length && document[end] == '\n')
            {
                end++;
            }
            else if (start > 0 && document[start - 1] == '\n')
            {
                start--;
            }
        }

        SetSelection(document.Replace(start, end - start, string.Empty, MarkdownEditKind.Deleting, selection));
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
