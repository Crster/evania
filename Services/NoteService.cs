using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Evania.Controls.MarkdownEditor;
using Evania.Models;
using LiteDB;

namespace Evania.Services;

public static class NoteService
{
    public static Task<IReadOnlyList<NoteListItem>> ListAsync(string filter, NoteSortOrder sortOrder) =>
        Task.Run(() =>
        {
            IEnumerable<NoteDocument> query = DatabaseService.GetNotes();
            string normalizedFilter = filter.Trim();

            if (normalizedFilter.Length > 0)
            {
                string[] filterTerms = SearchService.SplitTerms(normalizedFilter);

                query = query.Where(note =>
                    SearchService.MatchesWordPrefixes(note.Title, filterTerms) ||
                    SearchService.MatchesWordPrefixes(note.PlainText, filterTerms));
            }

            query = sortOrder switch
            {
                NoteSortOrder.UpdatedAscending => query.OrderBy(note => note.UpdatedAt),
                NoteSortOrder.TitleAscending => query.OrderBy(GetDisplayTitle, StringComparer.CurrentCultureIgnoreCase),
                NoteSortOrder.TitleDescending => query.OrderByDescending(GetDisplayTitle, StringComparer.CurrentCultureIgnoreCase),
                NoteSortOrder.CreatedDescending => query.OrderByDescending(note => note.CreatedAt),
                _ => query.OrderByDescending(note => note.UpdatedAt),
            };

            bool sortByCreated = sortOrder == NoteSortOrder.CreatedDescending;

            return (IReadOnlyList<NoteListItem>)query.Select(note => new NoteListItem
            {
                Id = note.Id,
                DisplayTitle = GetDisplayTitle(note),
                Preview = CreatePreview(note),
                TimeText = sortByCreated
                    ? "Created " + RelativeTimeText(note.CreatedAt)
                    : RelativeTimeText(note.UpdatedAt),
            }).ToList();
        });

    public static Task SaveViewportAsync(NoteDocument note) =>
        Task.Run(() => DatabaseService.UpsertNote(note));

    public static Task<NoteDocument?> GetAsync(ObjectId id) => Task.Run(() => Normalize(DatabaseService.GetNote(id)));

    public static Task SaveAsync(
        NoteDocument note,
        IReadOnlyCollection<string>? removedAttachmentIds = null,
        bool bumpUpdatedAt = true) =>
        Task.Run(() =>
        {
            note.Title = (note.Title ?? string.Empty).Trim();

            // Opening a note for viewing saves it too, so only move UpdatedAt when content or the
            // attachment set actually changed; otherwise "updated" drifts toward "last opened" and
            // the updated/created sorts stop ever differing.
            if (bumpUpdatedAt)
            {
                note.UpdatedAt = DateTime.UtcNow;
            }

            DatabaseService.UpsertNote(note);

            if (removedAttachmentIds is null)
            {
                return;
            }

            HashSet<string> referencedIds = note.Attachments.Select(attachment => attachment.Id).ToHashSet();
            foreach (string id in removedAttachmentIds.Where(id => !referencedIds.Contains(id)))
            {
                DatabaseService.DeleteNoteAttachment(id);
            }
        });

    public static Task<NoteDocument> CreateAsync() => Task.Run(() =>
    {
        NoteDocument note = new();
        DatabaseService.UpsertNote(note);

        return note;
    });

    public static Task<NoteAttachment> AddAttachmentAsync(
        ObjectId noteId,
        string fileName,
        string contentType,
        bool isImage,
        Stream source) => Task.Run(() =>
    {
        string id = $"note/{noteId}/{Guid.NewGuid():N}";
        DatabaseService.UploadNoteAttachment(id, fileName, source);

        return new NoteAttachment
        {
            Id = id,
            FileName = fileName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            Length = source.CanSeek ? source.Length : 0,
            IsImage = isImage,
        };
    });

    public static Task<byte[]> ReadAttachmentAsync(string id) =>
        Task.Run(() => DatabaseService.DownloadNoteAttachment(id));

    public static Task<string> GetAttachmentFileNameAsync(string id) =>
        Task.Run(() => DatabaseService.GetNoteAttachmentFileName(id));

    public static Task DeleteAsync(NoteDocument note) => Task.Run(() =>
    {
        if (!DatabaseService.DeleteNote(note.Id))
        {
            return;
        }

        foreach (NoteAttachment attachment in note.Attachments)
        {
            DatabaseService.DeleteNoteAttachment(attachment.Id);
        }
    });

    public static string GetDisplayTitle(NoteDocument note)
    {
        if (!string.IsNullOrWhiteSpace(note.Title))
        {
            return note.Title.Trim();
        }

        string? firstLine = (note.PlainText ?? string.Empty)
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Length > 0);

        if (firstLine is null)
        {
            return "Untitled note";
        }

        return firstLine.Length <= 80 ? firstLine : $"{firstLine[..77]}...";
    }

    public static string CreateListStateKey(string filter, NoteSortOrder sortOrder) =>
        $"{sortOrder}:{filter.Trim().ToUpperInvariant()}";

    public static IEnumerable<(int Start, int Length)> FindWordPrefixMatchRanges(string? text, string? filter) =>
        SearchService.FindWordPrefixMatchRanges(text, filter);

    public static Task<double> GetListScrollOffsetAsync(string stateKey) =>
        Task.Run(() => DatabaseService.GetNoteListScrollOffset(stateKey));

    public static Task SaveListScrollOffsetAsync(string stateKey, double offset) =>
        Task.Run(() => DatabaseService.SaveNoteListScrollOffset(stateKey, offset));

    private static string RelativeTimeText(DateTime utc)
    {
        TimeSpan elapsed = DateTime.UtcNow - utc.ToUniversalTime();

        return elapsed.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)elapsed.TotalMinutes} min ago",
            < 24 * 60 => $"{(int)elapsed.TotalHours} hr ago",
            < 30 * 24 * 60 => $"{(int)elapsed.TotalDays} day ago",
            < 365 * 24 * 60 => $"{(int)(elapsed.TotalDays / 30)} mo ago",
            _ => $"{(int)(elapsed.TotalDays / 365)} yr ago",
        };
    }

    private const int PreviewMaxLength = 1024;

    /// <summary>
    /// Builds the list preview starting at the line that sat at the top of the editor viewport, so
    /// coming back to the list shows the part of the note the reader left off at, not always the top.
    /// </summary>
    /// <remarks>
    /// The projection runs over the same source the editor loads - markdown when there is any, and
    /// otherwise the stored plain text of a pre-markdown note - so the saved offset indexes the very
    /// string it was captured against and legacy notes anchor just as well as new ones.
    /// </remarks>
    private static string CreatePreview(NoteDocument note)
    {
        string markdown = !string.IsNullOrWhiteSpace(note.Markdown)
            ? note.Markdown
            : note.PlainText ?? string.Empty;

        string source = MarkdownTextUtilities.ToPlainText(markdown, Math.Max(0, note.EditorViewportTopPosition));
        string preview = NormalizePreviewText(source);

        return preview.Length <= PreviewMaxLength ? preview : $"{preview[..(PreviewMaxLength - 3)]}...";
    }

    /// <summary>
    /// Collapses runs of spaces and blank lines but keeps the line breaks: the preview renders in a
    /// multi-line TextBlock, so paragraph structure is what makes it readable at a glance.
    /// </summary>
    private static string NormalizePreviewText(string source)
    {
        var builder = new StringBuilder();
        bool pendingBlankLine = false;
        bool wroteLine = false;

        foreach (string rawLine in source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = string.Join(
                " ",
                rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            if (line.Length == 0)
            {
                // Leading blanks are dropped entirely; interior runs collapse to one empty line.
                pendingBlankLine = wroteLine;
                continue;
            }

            if (wroteLine)
            {
                builder.Append('\n');

                if (pendingBlankLine)
                {
                    builder.Append('\n');
                }
            }

            builder.Append(line);
            pendingBlankLine = false;
            wroteLine = true;
        }

        return builder.ToString();
    }

    private static NoteDocument? Normalize(NoteDocument? note)
    {
        if (note is null)
        {
            return null;
        }

        note.Title ??= string.Empty;
        note.Markdown ??= string.Empty;
        note.PlainText ??= string.Empty;
        note.Attachments ??= new List<NoteAttachment>();

        return note;
    }
}
