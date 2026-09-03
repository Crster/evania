using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using LiteDB;

namespace Evania.Models;

public sealed class NoteDocument
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    public string Title { get; set; } = string.Empty;

    public string RichText { get; set; } = string.Empty;

    /// <summary>
    /// Markdown source, the editor's source of truth. Notes written before the markdown editor
    /// only carry <see cref="RichText"/> (RTF) and open with their <see cref="PlainText"/>.
    /// </summary>
    public string Markdown { get; set; } = string.Empty;

    public string PlainText { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public double EditorScrollOffset { get; set; }

    public List<NoteAttachment> Attachments { get; set; } = new();
}

public sealed class NoteAttachment
{
    public string Id { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long Length { get; set; }

    public bool IsImage { get; set; }
}

public sealed class NoteListState
{
    [BsonId]
    public string Key { get; set; } = string.Empty;

    public double ScrollOffset { get; set; }
}

public enum NoteSortOrder
{
    UpdatedDescending,
    UpdatedAscending,
    TitleAscending,
    TitleDescending,
}

public partial class NoteListItem : ObservableObject
{
    public required ObjectId Id { get; init; }

    public required string DisplayTitle { get; init; }

    public required DateTime UpdatedAt { get; init; }

    public required string Preview { get; init; }

    public string UpdatedText => UpdatedAt.ToLocalTime().ToString("g");
}

public partial class NoteAttachmentItem : ObservableObject
{
    private string fileName = string.Empty;

    public required string Id { get; init; }

    public required string FileName
    {
        get => fileName;
        set => SetProperty(ref fileName, value);
    }

    public required string ContentType { get; init; }

    public required long Length { get; init; }

    public required bool IsImage { get; init; }

    public string SizeText => Length switch
    {
        < 1024 => $"{Length} B",
        < 1024 * 1024 => $"{Length / 1024.0:0.#} KB",
        _ => $"{Length / (1024.0 * 1024.0):0.##} MB",
    };
}
