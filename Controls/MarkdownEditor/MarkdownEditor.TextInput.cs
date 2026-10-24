using System;
using Microsoft.UI.Xaml;
using Windows.UI.Text.Core;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// OS text input plumbing for <see cref="MarkdownEditor"/>.
/// </summary>
/// <remarks>
/// Because the control is not a stock text box, Windows has no idea it edits text. A
/// <see cref="CoreTextEditContext"/> is the contract that fixes that: the editor answers "what text do
/// you hold" and "where is the selection", and in return gets keyboard input, IME composition, the
/// touch keyboard, handwriting, and the emoji panel. Every callback here runs on the UI thread and
/// must keep the document and the reported selection perfectly consistent, otherwise IMEs desync.
/// </remarks>
public sealed partial class MarkdownEditor
{
    private CoreTextEditContext? editContext;

    /// <summary>
    /// Set while we are applying an OS-driven edit, so the resulting document change does not get
    /// echoed back to <see cref="CoreTextEditContext"/> as if the app had changed the text.
    /// </summary>
    private bool applyingTextUpdate;

    private bool hasFocus;

    private bool HasFocusInternal => hasFocus;

    private void InitializeTextInput()
    {
        // Focus tracking drives the caret and must be wired even if the edit context cannot be
        // created, so it is hooked before anything that can fail.
        GotFocus += OnEditorGotFocus;
        LostFocus += OnEditorLostFocus;

        try
        {
            CoreTextServicesManager manager = CoreTextServicesManager.GetForCurrentView();
            editContext = manager.CreateEditContext();
        }
        catch (Exception ex)
        {
            // Desktop WinUI has no CoreWindow, so this can fail outright on some configurations.
            // Typing still works through CharacterReceived; only IME composition is lost.
            return;
        }

        editContext.InputPaneDisplayPolicy = CoreTextInputPaneDisplayPolicy.Automatic;
        editContext.InputScope = CoreTextInputScope.Text;

        editContext.TextRequested += OnTextRequested;
        editContext.SelectionRequested += OnSelectionRequested;
        editContext.TextUpdating += OnTextUpdating;
        editContext.SelectionUpdating += OnSelectionUpdating;
        editContext.FormatUpdating += OnFormatUpdating;
        editContext.LayoutRequested += OnLayoutRequested;
        editContext.CompositionStarted += OnCompositionStarted;
        editContext.CompositionCompleted += OnCompositionCompleted;
        editContext.FocusRemoved += OnFocusRemoved;

    }

    private void DisposeTextInput()
    {
        if (editContext is null)
        {
            return;
        }

        editContext.TextRequested -= OnTextRequested;
        editContext.SelectionRequested -= OnSelectionRequested;
        editContext.TextUpdating -= OnTextUpdating;
        editContext.SelectionUpdating -= OnSelectionUpdating;
        editContext.FormatUpdating -= OnFormatUpdating;
        editContext.LayoutRequested -= OnLayoutRequested;
        editContext.CompositionStarted -= OnCompositionStarted;
        editContext.CompositionCompleted -= OnCompositionCompleted;
        editContext.FocusRemoved -= OnFocusRemoved;
        editContext = null;
    }

    private void OnEditorGotFocus(object sender, RoutedEventArgs e)
    {
        hasFocus = true;
        editContext?.NotifyFocusEnter();
        RestartCaretBlink();
        InvalidateCaret();
    }

    private void OnEditorLostFocus(object sender, RoutedEventArgs e)
    {
        hasFocus = false;
        document.CloseUndoUnit();
        editContext?.NotifyFocusLeave();
        RestartCaretBlink();
        InvalidateCaret();
    }

    private void OnFocusRemoved(CoreTextEditContext sender, object args)
    {
        hasFocus = false;
        RestartCaretBlink();
        InvalidateCaret();
    }

    // --- state the OS asks us for ------------------------------------------------------------

    private void OnTextRequested(CoreTextEditContext sender, CoreTextTextRequestedEventArgs args)
    {
        CoreTextTextRequest request = args.Request;
        CoreTextRange range = ClampToDocument(request.Range);
        request.Text = document.GetText(range.StartCaretPosition, range.EndCaretPosition - range.StartCaretPosition);
    }

    private void OnSelectionRequested(CoreTextEditContext sender, CoreTextSelectionRequestedEventArgs args)
    {
        args.Request.Selection = new CoreTextRange
        {
            StartCaretPosition = selection.Min,
            EndCaretPosition = selection.Max,
        };
    }

    /// <summary>
    /// Reports the caret rectangle in screen coordinates so IME candidate windows and the touch
    /// keyboard dock next to the text instead of at the window origin.
    /// </summary>
    private void OnLayoutRequested(CoreTextEditContext sender, CoreTextLayoutRequestedEventArgs args)
    {
        CoreTextLayoutRequest request = args.Request;

        global::Windows.Foundation.Rect controlBounds = GetScreenRect(0, 0, ActualWidth, ActualHeight);
        request.LayoutBounds.ControlBounds = controlBounds;

        if (TryGetCaretRect(out global::Windows.Foundation.Rect caretRect))
        {
            double y = caretRect.Y - scroller.VerticalOffset;
            request.LayoutBounds.TextBounds = GetScreenRect(caretRect.X, y, Math.Max(1, caretRect.Width), caretRect.Height);
        }
        else
        {
            request.LayoutBounds.TextBounds = controlBounds;
        }
    }

    private global::Windows.Foundation.Rect GetScreenRect(double x, double y, double width, double height)
    {
        try
        {
            global::Windows.Foundation.Point origin = TransformToVisual(null).TransformPoint(new global::Windows.Foundation.Point(x, y));
            double scale = XamlRoot?.RasterizationScale ?? 1.0;

            // XamlRoot coordinates are DIPs relative to the window; CoreTextEditContext expects raw
            // screen pixels, so scale and offset by the window position.
            global::Windows.Graphics.PointInt32 windowOrigin = GetWindowOrigin();
            return new global::Windows.Foundation.Rect(
                (origin.X * scale) + windowOrigin.X,
                (origin.Y * scale) + windowOrigin.Y,
                width * scale,
                height * scale);
        }
        catch (Exception)
        {
            // Transform fails while the control is detached; the OS tolerates an empty rect.
            return default;
        }
    }

    private global::Windows.Graphics.PointInt32 GetWindowOrigin()
    {
        if (XamlRoot?.ContentIslandEnvironment is { } environment)
        {
            nint hwnd = (nint)environment.AppWindowId.Value;
            if (hwnd != 0 && Microsoft.UI.Windowing.AppWindow.GetFromWindowId(environment.AppWindowId) is { } appWindow)
            {
                return appWindow.Position;
            }
        }

        return default;
    }

    // --- edits driven by the OS ---------------------------------------------------------------

    /// <summary>
    /// Applies a keystroke, IME composition update or touch-keyboard insertion.
    /// </summary>
    private void OnTextUpdating(CoreTextEditContext sender, CoreTextTextUpdatingEventArgs args)
    {
        if (IsReadOnly)
        {
            args.Result = CoreTextTextUpdatingResult.Failed;
            return;
        }

        CoreTextRange range = ClampToDocument(args.Range);
        string newText = MarkdownDocument.Normalize(args.Text ?? string.Empty);

        applyingTextUpdate = true;
        try
        {
            int start = range.StartCaretPosition;
            int length = range.EndCaretPosition - range.StartCaretPosition;
            bool adjustedAtomicInsertion = false;

            if (length > 0)
            {
                TextRange expanded = ExpandSelectionOverAtomicBlockSyntax(new TextRange(start, start + length));
                adjustedAtomicInsertion = expanded.Min != start || expanded.Length != length;
                start = expanded.Min;
                length = expanded.Length;
            }

            if (length == 0 && TryGetAtomicBlockSyntax(start, out TextRange syntax))
            {
                if (newText.Contains('\n') && start > syntax.Min && start <= syntax.Max)
                {
                    start = syntax.Min;
                    adjustedAtomicInsertion = true;
                }
                else if (start > syntax.Min && start < syntax.Max)
                {
                    start = syntax.Max;
                    adjustedAtomicInsertion = true;
                }
            }

            TableGuardResult guard = GetTableGuard(new TextRange(start, start + length), newText);
            if (guard.Blocked)
            {
                args.Result = CoreTextTextUpdatingResult.Failed;
                return;
            }

            string replacement = guard.Replacement;
            MarkdownEditKind kind = replacement.Length == 0 ? MarkdownEditKind.Deleting : MarkdownEditKind.Typing;
            document.Replace(start, length, replacement, kind, selection);

            CoreTextRange newSelection = args.NewSelection;
            TextRange resolved = adjustedAtomicInsertion
                ? TextRange.Caret(start + replacement.Length)
                : newSelection.StartCaretPosition == newSelection.EndCaretPosition && newSelection.StartCaretPosition == 0
                ? TextRange.Caret(start + replacement.Length)
                : new TextRange(newSelection.StartCaretPosition, newSelection.EndCaretPosition);

            SetSelection(resolved);
            AutoContinueList(start, replacement);
            args.Result = CoreTextTextUpdatingResult.Succeeded;
        }
        catch (Exception ex)
        {
            args.Result = CoreTextTextUpdatingResult.Failed;
        }
        finally
        {
            applyingTextUpdate = false;
        }
    }

    private void OnSelectionUpdating(CoreTextEditContext sender, CoreTextSelectionUpdatingEventArgs args)
    {
        CoreTextRange range = ClampToDocument(args.Selection);
        SetSelection(new TextRange(range.StartCaretPosition, range.EndCaretPosition));
        args.Result = CoreTextSelectionUpdatingResult.Succeeded;
    }

    /// <summary>
    /// IMEs ask to underline the in-progress composition. The editor renders markdown-derived styles
    /// only, so composition formatting is accepted and ignored rather than refused, which would make
    /// some IMEs abort the composition.
    /// </summary>
    private void OnFormatUpdating(CoreTextEditContext sender, CoreTextFormatUpdatingEventArgs args)
        => args.Result = CoreTextFormatUpdatingResult.Succeeded;

    private void OnCompositionStarted(CoreTextEditContext sender, CoreTextCompositionStartedEventArgs args)
    {
        document.CloseUndoUnit();
    }

    private void OnCompositionCompleted(CoreTextEditContext sender, CoreTextCompositionCompletedEventArgs args)
    {
        document.CloseUndoUnit();
    }

    // --- notifications back to the OS ---------------------------------------------------------

    /// <summary>
    /// Tells the OS the app changed the text behind its back (paste, toolbar command, undo). Skipped
    /// while an OS-driven update is being applied, which would otherwise be reported twice.
    /// </summary>
    private void NotifyTextInputOfChange()
    {
        if (applyingTextUpdate || editContext is null)
        {
            return;
        }

        editContext.NotifyTextChanged(
            new CoreTextRange { StartCaretPosition = 0, EndCaretPosition = document.Length },
            document.Length,
            new CoreTextRange { StartCaretPosition = selection.Min, EndCaretPosition = selection.Max });
    }

    private void NotifySelectionChanged()
    {
        if (applyingTextUpdate || editContext is null)
        {
            return;
        }

        editContext.NotifySelectionChanged(new CoreTextRange
        {
            StartCaretPosition = selection.Min,
            EndCaretPosition = selection.Max,
        });
    }

    private CoreTextRange ClampToDocument(CoreTextRange range)
    {
        int start = Math.Clamp(range.StartCaretPosition, 0, document.Length);
        int end = Math.Clamp(range.EndCaretPosition, start, document.Length);
        return new CoreTextRange { StartCaretPosition = start, EndCaretPosition = end };
    }
}
