using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Dispatching;
using Windows.UI;
using Rect = Windows.Foundation.Rect;

namespace Evania.Controls.MarkdownEditor;

/// <summary>
/// Spell checking for <see cref="MarkdownEditor"/>: a red squiggle under words the Windows spell
/// checker does not recognise.
/// </summary>
/// <remarks>
/// Checking never runs on the UI thread. A draw pass tokenizes the text regions it is already
/// painting and looks each word up in a result cache; misses are queued for a background worker
/// that batches them through the Microsoft Spell Checking API and then asks the canvas to repaint,
/// so an underline appears a moment after a word first types or scrolls into view instead of
/// blocking the frame. Results are cached per word for the editor's lifetime - a word never changes
/// spelling on its own, and the OS dictionary already covers the user's languages.
/// <para>
/// The checker is reached through COM interop: the WinRT <c>Windows.Text.SpellChecking</c> type is
/// a dual WinRT/COM API that the .NET Windows projections deliberately omit. The interfaces, IIDs
/// and method order mirror the SDK's <c>spellcheck.idl</c> exactly.
/// </para>
/// </remarks>
public sealed partial class MarkdownEditor
{
    /// <summary>Squiggle colour; spell-check marks stay red in both themes, like every editor.</summary>
    private static readonly Color SpellUnderlineColor = Color.FromArgb(0xCC, 0xE8, 0x11, 0x23);

    private const float SquiggleStep = 3f;
    private const float SquiggleAmplitude = 1.6f;
    private const int SpellBatchSize = 64;

    private readonly ConcurrentDictionary<string, bool> spellResults = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> spellPending = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> spellQueue = new();

    private DispatcherQueue? dispatcherQueue;
    private bool spellCheckInFlight;
    private SpellCheckInterop.ISpellChecker? spellChecker;
    private bool spellCheckerUnavailable;

    private void InitializeSpelling()
    {
        dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    private void DisposeSpelling()
    {
        if (spellChecker is not null)
        {
            Marshal.ReleaseComObject(spellChecker);
            spellChecker = null;
        }

        spellResults.Clear();
        spellPending.Clear();
        spellQueue.Clear();
    }

    /// <summary>
    /// Draws the squiggle under every misspelled word of a block and queues words that have not been
    /// decided yet. Code blocks, the raw table mode and read-only previews are never checked - their
    /// text is source or somebody else's words, not prose being written.
    /// </summary>
    private void DrawSpellUnderlines(CanvasDrawingSession session, BlockLayout block)
    {
        if (!IsSpellCheckEnabled || IsReadOnly || spellCheckerUnavailable)
        {
            return;
        }

        if (block.Block.Kind == MarkdownBlockKind.CodeBlock || block.IsRawEdit)
        {
            return;
        }

        DrawSpellForRegion(session, block.Text, block.Y);
        foreach (TableCellLayout cell in block.Cells)
        {
            DrawSpellForRegion(session, cell.Region, block.Y);
        }
    }

    private void DrawSpellForRegion(CanvasDrawingSession session, TextRegion? region, float yOffset)
    {
        if (region is null || region.RenderedText.Length == 0)
        {
            return;
        }

        foreach ((int start, int length) in EnumerateSpellWords(region.RenderedText))
        {
            if (IsExcludedRun(region, start, length))
            {
                continue;
            }

            string word = region.RenderedText.Substring(start, length);
            if (!spellResults.TryGetValue(word, out bool misspelled))
            {
                QueueSpellWord(word);
                continue;
            }

            if (misspelled)
            {
                DrawWordSquiggle(session, region, yOffset, start, length);
            }
        }
    }

    /// <summary>
    /// True when the word's run is not prose: revealed syntax spans - which include the hidden part
    /// of a link, so its URL never flags - and inline code.
    /// </summary>
    private static bool IsExcludedRun(TextRegion region, int start, int length)
    {
        foreach ((int runStart, int runLength, MarkdownInline inline) in region.Runs)
        {
            if (runStart <= start && start + length <= runStart + runLength)
            {
                return inline.IsSyntax || inline.Style.HasFlag(InlineStyle.Code);
            }
        }

        return false;
    }

    /// <summary>
    /// Splits text into words: letter runs, with an apostrophe kept when it joins two letters, so
    /// "don't" is one word while "dogs'" ends at the apostrophe.
    /// </summary>
    private static IEnumerable<(int Start, int Length)> EnumerateSpellWords(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetter(text[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < text.Length)
            {
                if (char.IsLetter(text[i]))
                {
                    i++;
                }
                else if ((text[i] == '\'' || text[i] == '\u2019')
                    && i + 1 < text.Length
                    && char.IsLetter(text[i + 1]))
                {
                    i++;
                }
                else
                {
                    break;
                }
            }

            yield return (start, i - start);
        }
    }

    private void QueueSpellWord(string word)
    {
        if (!spellPending.TryAdd(word, 0))
        {
            return;
        }

        spellQueue.Enqueue(word);

        if (!spellCheckInFlight)
        {
            spellCheckInFlight = true;
            _ = Task.Run(RunSpellQueue);
        }
    }

    /// <summary>
    /// Drains the queue in batches on a background thread. The in-flight flag is only cleared once
    /// the queue is observed empty, and re-checked after clearing, so a word enqueued during the
    /// hand-off always finds a running worker.
    /// </summary>
    private void RunSpellQueue()
    {
        while (true)
        {
            if (spellQueue.IsEmpty)
            {
                spellCheckInFlight = false;
                if (spellQueue.IsEmpty)
                {
                    return;
                }
            }

            CheckSpellBatch();
        }
    }

    private void CheckSpellBatch()
    {
        SpellCheckInterop.ISpellChecker? checker = EnsureSpellChecker();
        var batch = new List<string>(SpellBatchSize);

        while (batch.Count < SpellBatchSize && spellQueue.TryDequeue(out string? word))
        {
            spellPending.TryRemove(word, out _);
            batch.Add(word);
        }

        foreach (string word in batch)
        {
            bool misspelled = false;
            if (checker is not null)
            {
                try
                {
                    misspelled = checker.Check(word).Next() is not null;
                }
                catch (Exception)
                {
                    // A checker that throws mid-session is done; treat everything as clean rather
                    // than decorate the note with errors.
                    spellChecker = null;
                    spellCheckerUnavailable = true;
                    break;
                }
            }

            spellResults[word] = misspelled;
        }

        if (batch.Count > 0 && dispatcherQueue is { } dispatcher)
        {
            dispatcher.TryEnqueue(() => canvas.Invalidate());
        }
    }

    private SpellCheckInterop.ISpellChecker? EnsureSpellChecker()
    {
        if (spellChecker is not null || spellCheckerUnavailable)
        {
            return spellChecker;
        }

        spellChecker = SpellCheckInterop.TryCreateChecker();
        spellCheckerUnavailable = spellChecker is null;
        return spellChecker;
    }

    /// <summary>
    /// Draws one squiggle per visual line the word occupies, anchored just under the word's lowest
    /// ink so descenders are not crossed out by it.
    /// </summary>
    private void DrawWordSquiggle(CanvasDrawingSession session, TextRegion region, float yOffset, int start, int length)
    {
        var boxes = new List<Rect>();
        foreach (CanvasTextLayoutRegion charRegion in region.Layout.GetCharacterRegions(start, length))
        {
            Rect box = charRegion.LayoutBounds;
            if (box.Width <= 0 || box.Height <= 0)
            {
                continue;
            }

            if (boxes.Count > 0 && Math.Abs(box.Y - boxes[^1].Y) < 1)
            {
                Rect last = boxes[^1];
                boxes[^1] = new Rect(
                    last.X,
                    last.Y,
                    Math.Max(last.X + last.Width, box.X + box.Width) - last.X,
                    Math.Max(last.Height, box.Height));
            }
            else
            {
                boxes.Add(box);
            }
        }

        foreach (Rect box in boxes)
        {
            // The line box carries the font's leading below the descent, so the baseline sits around
            // three quarters down; two pixels under it clears ascenders and most descenders alike.
            float x = (float)box.X + region.Origin.X;
            float y = (float)(box.Y + (box.Height * 0.75f)) + 2f + region.Origin.Y + yOffset;
            DrawSquiggle(session, x, y, (float)box.Width);
        }
    }

    private static void DrawSquiggle(CanvasDrawingSession session, float x, float y, float width)
    {
        int teeth = Math.Max(1, (int)MathF.Ceiling(width / SquiggleStep));

        using CanvasPathBuilder builder = new(session);
        builder.BeginFigure(x, y);
        for (int i = 1; i <= teeth; i++)
        {
            float toothX = MathF.Min(x + (i * SquiggleStep), x + width);
            builder.AddLine(toothX, y + (i % 2 == 0 ? -SquiggleAmplitude : SquiggleAmplitude));
        }

        builder.EndFigure(CanvasFigureLoop.Open);
        using CanvasGeometry geometry = CanvasGeometry.CreatePath(builder);
        session.DrawGeometry(geometry, SpellUnderlineColor, 1.25f);
    }
}

/// <summary>
/// COM interop for the Microsoft Spell Checking API, per the SDK's <c>spellcheck.idl</c>. Only the
/// vtable slots actually called are declared, in exact interface order; creating the checker uses
/// the user's first preferred language with an English fallback.
/// </summary>
internal static class SpellCheckInterop
{
    private const string DefaultLanguage = "en-US";

    [ComImport]
    [Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    private sealed class SpellCheckerFactoryClass
    {
    }

    [ComImport]
    [Guid("8E018A9D-2415-4677-BF08-794EA61F94BB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        void GetSupportedLanguages(out IEnumString value);

        void IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, [MarshalAs(UnmanagedType.Bool)] out bool supported);

        void CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out ISpellChecker checker);
    }

    [ComImport]
    [Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetLanguageTag();

        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
    }

    [ComImport]
    [Guid("803E3BD4-2828-4410-8290-418D1D73C762")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumSpellingError
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        ISpellingError? Next();
    }

    [ComImport]
    [Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISpellingError
    {
    }

    /// <summary>
    /// Creates a checker for the user's first preferred language, falling back to English, or null
    /// when the system offers no spell checking at all.
    /// </summary>
    internal static ISpellChecker? TryCreateChecker()
    {
        try
        {
            string language = global::Windows.Globalization.ApplicationLanguages.Languages is { Count: > 0 } languages
                ? languages[0]
                : DefaultLanguage;

            object factoryObject = new SpellCheckerFactoryClass();
            var factory = (ISpellCheckerFactory)factoryObject;
            try
            {
                factory.CreateSpellChecker(language, out ISpellChecker checker);
                return checker;
            }
            catch (COMException)
            {
                factory.CreateSpellChecker(DefaultLanguage, out ISpellChecker fallback);
                return fallback;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
