using System;
using LiteDB;

namespace Evania.Models;

/// <summary>
/// One pinned webpage, stored in the database rather than in settings.json.
///
/// Bookmarks are content the user created, not configuration: they belong beside notes and
/// passwords in the data file, which lives where the user pointed it and survives an uninstall.
/// </summary>
public sealed class BookmarkDocument
{
    /// <summary>
    /// The URL doubles as the identity: the same page cannot be pinned twice, and using it as the
    /// key means a re-save of the same list rewrites rows instead of duplicating them.
    /// </summary>
    [BsonId]
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Position in the sidebar; LiteDB has no insertion order to fall back on.</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
