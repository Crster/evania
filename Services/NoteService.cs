using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
                query = query.Where(note =>
                    (note.Title ?? string.Empty).Contains(normalizedFilter, StringComparison.CurrentCultureIgnoreCase) ||
                    (note.PlainText ?? string.Empty).Contains(normalizedFilter, StringComparison.CurrentCultureIgnoreCase));
            }

            query = sortOrder switch
            {
                NoteSortOrder.UpdatedAscending => query.OrderBy(note => note.UpdatedAt),
                NoteSortOrder.TitleAscending => query.OrderBy(GetDisplayTitle, StringComparer.CurrentCultureIgnoreCase),
                NoteSortOrder.TitleDescending => query.OrderByDescending(GetDisplayTitle, StringComparer.CurrentCultureIgnoreCase),
                _ => query.OrderByDescending(note => note.UpdatedAt),
            };

            return (IReadOnlyList<NoteListItem>)query.Select(note => new NoteListItem
            {
                Id = note.Id,
                DisplayTitle = GetDisplayTitle(note),
                Preview = CreatePreview(note.PlainText ?? string.Empty),
                UpdatedAt = note.UpdatedAt,
            }).ToList();
        });

    public static Task<NoteDocument?> GetAsync(ObjectId id) => Task.Run(() => DatabaseService.GetNote(id));

    public static Task SaveAsync(NoteDocument note, IReadOnlyCollection<string>? removedAttachmentIds = null) =>
        Task.Run(() =>
        {
            note.Title = (note.Title ?? string.Empty).Trim();
            note.UpdatedAt = DateTime.UtcNow;
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

    public static Task<double> GetListScrollOffsetAsync(string stateKey) =>
        Task.Run(() => DatabaseService.GetNoteListScrollOffset(stateKey));

    public static Task SaveListScrollOffsetAsync(string stateKey, double offset) =>
        Task.Run(() => DatabaseService.SaveNoteListScrollOffset(stateKey, offset));

    private static string CreatePreview(string plainText)
    {
        string preview = string.Join(
            " ",
            plainText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return preview.Length <= 180 ? preview : $"{preview[..177]}...";
    }
}
