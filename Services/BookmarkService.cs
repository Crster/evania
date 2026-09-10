using System;
using System.Collections.Generic;
using System.Linq;
using Evania.Models;

namespace Evania.Services;

/// <summary>
/// Keeps <see cref="AppSettings.Bookmarks"/> - which the sidebar and the Settings page bind to -
/// backed by the <c>bookmarks</c> collection in the database instead of by settings.json.
///
/// Bookmarks are content the user made, not configuration, so they belong in the data file: that
/// file lives where the user pointed it, gets backed up with the notes and passwords, and survives
/// an uninstall that takes settings.json with it. The in-memory shape stays exactly as it was, so
/// every existing binding keeps working; only where the list is read and written has moved.
/// </summary>
public static class BookmarkService
{
    private static readonly object Gate = new();

    /// <summary>Suppresses the save-on-change while a load is assigning the list itself.</summary>
    private static bool loading;

    /// <summary>
    /// Whether the one-time hand-off from settings.json has already been considered. Only the first
    /// database opened this session can inherit the legacy list; switching data files afterwards
    /// must show that file's own bookmarks, empty included.
    /// </summary>
    private static bool migrationConsidered;

    private static bool subscribed;

    /// <summary>
    /// True once the list has actually reached a data file. Until then <see cref="SettingsService"/>
    /// keeps writing its own copy, so a session where the database never opens - a missing password,
    /// a path on a drive that is not there - cannot quietly drop the user's bookmarks.
    /// </summary>
    public static bool PersistedToDatabase { get; private set; }

    /// <summary>
    /// Points the in-memory list at the freshly opened database. Called by
    /// <see cref="DatabaseService"/> once a connection is actually usable - both at startup and
    /// after the Settings page applies a different file.
    /// </summary>
    public static void SyncWithDatabase()
    {
        lock (Gate)
        {
            AppSettings settings = SettingsService.Current;
            IReadOnlyList<BookmarkDocument> stored = DatabaseService.GetBookmarks();

            // A settings.json written by an older build is the only source for a first run after the
            // upgrade; once its entries are in the data file the JSON copy stops being read.
            if (stored.Count == 0 && !migrationConsidered && settings.Bookmarks.Count > 0)
            {
                migrationConsidered = true;
                Subscribe();
                Persist(settings.Bookmarks);

                return;
            }

            migrationConsidered = true;
            PersistedToDatabase = DatabaseService.Status.Connected;

            try
            {
                loading = true;
                settings.Bookmarks = stored
                    .Select(bookmark => new WebBookmark
                    {
                        Title = bookmark.Title ?? string.Empty,
                        Url = bookmark.Url,
                    })
                    .ToList();
            }
            finally
            {
                loading = false;
            }

            Subscribe();
        }
    }

    /// <summary>
    /// Mirrors every later assignment of the list back into the database. Adding and removing
    /// bookmarks already works by replacing <see cref="AppSettings.Bookmarks"/> wholesale, so the
    /// property change is the complete signal - no caller has to remember to save.
    /// </summary>
    private static void Subscribe()
    {
        if (subscribed)
        {
            return;
        }

        subscribed = true;
        SettingsService.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AppSettings.Bookmarks) || loading)
            {
                return;
            }

            Persist(SettingsService.Current.Bookmarks);
        };
    }

    private static void Persist(IReadOnlyList<WebBookmark> bookmarks)
    {
        List<BookmarkDocument> documents = [];
        HashSet<string> seenUrls = new(StringComparer.OrdinalIgnoreCase);

        foreach (WebBookmark bookmark in bookmarks)
        {
            string url = (bookmark.Url ?? string.Empty).Trim();

            // The URL is the document key, so a blank or repeated one would fail the insert and
            // take the rest of the list down with it.
            if (url.Length == 0 || !seenUrls.Add(url))
            {
                continue;
            }

            documents.Add(new BookmarkDocument
            {
                Url = url,
                Title = (bookmark.Title ?? string.Empty).Trim(),
                SortOrder = documents.Count,
            });
        }

        if (DatabaseService.ReplaceBookmarks(documents))
        {
            PersistedToDatabase = true;

            // The database is now the record; clear the superseded JSON copy in the same breath.
            SettingsService.Save();
        }
    }
}
