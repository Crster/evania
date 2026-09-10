using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Evania.Models;
using LiteDB;
using LiteDB.Engine;

namespace Evania.Services;

/// <summary>
/// What the Settings page shows under the database path: whether the file is open, whether it is
/// encrypted, how big it is, and the LiteDB message when it is not open.
/// </summary>
public sealed record DatabaseStatus(bool Connected, bool Encrypted, string? Error, long FileSizeBytes)
{
    public string Description => Connected
        ? $"Connected · {(Encrypted ? "encrypted" : "not encrypted")} · {(FileSizeBytes > 0 ? FormatSize(FileSizeBytes) : "empty")}"
        : Error ?? "Not connected";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.##} MB",
    };
}

/// <summary>
/// Holds the LiteDB connection the Database settings describe.
/// </summary>
public static class DatabaseService
{
    private static readonly object Gate = new();

    private static LiteDatabase? engine;
    private static string? openedPath;
    private static string? openedPassword;

    public static DatabaseStatus Status { get; private set; } = new(false, false, null, 0);

    /// <summary>The password the live connection uses, or null when it is unencrypted.</summary>
    public static string? CurrentPassword => openedPassword;

    /// <summary>The file the live connection has open, or null when nothing is open.</summary>
    public static string? CurrentPath => openedPath;

    /// <summary>
    /// Opens the database the stored settings describe. Used at startup.
    /// </summary>
    public static DatabaseStatus OpenFromSettings()
    {
        AppSettings settings = SettingsService.Current;
        string path = EnsurePersistentDatabasePath(settings);
        string? password;

        try
        {
            password = settings.DatabaseEncryption
                ? DatabaseCredentialVault.Read(path)
                : null;
        }
        catch (Exception ex)
        {
            return Status = new DatabaseStatus(false, settings.DatabaseEncryption, Explain(ex), SizeOf(path));
        }

        if (settings.DatabaseEncryption && string.IsNullOrEmpty(password))
        {
            return Status = new DatabaseStatus(
                false,
                true,
                "No database password was found in Windows Credential Manager. Enter it in Settings and apply it again.",
                SizeOf(path));
        }

        settings.DatabasePassword = password;

        DatabaseStatus status = Open(path, password);

        if (status.Connected)
        {
            BookmarkService.SyncWithDatabase();
        }

        return status;
    }

    private static string EnsurePersistentDatabasePath(AppSettings settings)
    {
        string configuredPath = string.IsNullOrWhiteSpace(settings.DatabasePath)
            ? SettingsService.DefaultDatabasePath
            : settings.DatabasePath;
        string persistentPath = SettingsService.DefaultDatabasePath;

        if (SamePath(configuredPath, persistentPath) || !IsPackageScopedPath(configuredPath))
        {
            return configuredPath;
        }

        bool sourceExists = File.Exists(configuredPath);
        bool targetExists = File.Exists(persistentPath);

        if (sourceExists && targetExists)
        {
            return configuredPath;
        }

        try
        {
            string? sourcePassword = ReadPassword(configuredPath);
            string? relocationError = Relocate(configuredPath, persistentPath);

            if (relocationError is not null)
            {
                return configuredPath;
            }

            if (!string.IsNullOrEmpty(sourcePassword) && string.IsNullOrEmpty(ReadPassword(persistentPath)))
            {
                SavePassword(persistentPath, sourcePassword);
            }

            DatabaseCredentialVault.Delete(configuredPath);
            settings.DatabasePath = persistentPath;

            return persistentPath;
        }
        catch
        {
            return configuredPath;
        }
    }

    private static bool IsPackageScopedPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string marker = $"{Path.DirectorySeparatorChar}AppData{Path.DirectorySeparatorChar}Local{Path.DirectorySeparatorChar}Packages{Path.DirectorySeparatorChar}";

        return fullPath.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The plaintext marker LiteDB writes into the header page of an unencrypted data file. An
    /// encrypted file starts with an AES salt page instead, so the marker is simply not there.
    /// </summary>
    private static readonly byte[] LiteDbHeaderMarker =
        System.Text.Encoding.ASCII.GetBytes("** This is a LiteDB file **");

    /// <summary>
    /// Reads the header of the file at <paramref name="path"/> and reports whether it is an
    /// encrypted LiteDB database. This answers the question before anything is opened, so the UI can
    /// say "this one needs a password" instead of waiting for an open to fail and reading the
    /// exception text. A missing, empty or unreadable file is reported as not encrypted.
    /// </summary>
    public static bool IsEncryptedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            byte[] header = new byte[64];
            int read = stream.Read(header, 0, header.Length);

            // Too short to be either kind of data file - let the open attempt produce the real error.
            if (read < header.Length)
            {
                return false;
            }

            return header.AsSpan(0, read).IndexOf(LiteDbHeaderMarker) < 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when a password for this file is already known - either it is the live connection's own
    /// file, or Windows Credential Manager still holds its password. Used to decide whether an
    /// encrypted file the user picked actually needs anything from them.
    /// </summary>
    public static bool HasKnownPasswordFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (SamePath(openedPath, path) && !string.IsNullOrEmpty(openedPassword))
        {
            return true;
        }

        return !string.IsNullOrEmpty(ReadPassword(path));
    }

    public static DatabaseStatus Open(string path, string? password)
    {
        lock (Gate)
        {
            FlushAndClose();

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                ConnectionString connection = new() { Filename = path };

                if (!string.IsNullOrEmpty(password))
                {
                    connection.Password = password;
                }

                engine = new LiteDatabase(connection);
                openedPath = path;
                openedPassword = password;

                Status = new DatabaseStatus(true, !string.IsNullOrEmpty(password), null, SizeOf(path));
            }
            catch (Exception ex)
            {
                engine = null;
                openedPath = null;
                openedPassword = null;

                Status = new DatabaseStatus(false, !string.IsNullOrEmpty(password), Explain(ex), SizeOf(path));
            }

            return Status;
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            FlushAndClose();
        }
    }

    public static IReadOnlyList<NoteDocument> GetNotes()
    {
        lock (Gate)
        {
            LiteDatabase database = RequireEngine();
            ILiteCollection<NoteDocument> notes = database.GetCollection<NoteDocument>("notes");
            notes.EnsureIndex(note => note.UpdatedAt);

            return notes.FindAll().ToList();
        }
    }

    public static NoteDocument? GetNote(ObjectId id)
    {
        lock (Gate)
        {
            return RequireEngine().GetCollection<NoteDocument>("notes").FindById(id);
        }
    }

    public static void UpsertNote(NoteDocument note)
    {
        lock (Gate)
        {
            RequireEngine().GetCollection<NoteDocument>("notes").Upsert(note);
        }
    }

    public static bool DeleteNote(ObjectId id)
    {
        lock (Gate)
        {
            return RequireEngine().GetCollection<NoteDocument>("notes").Delete(id);
        }
    }

    public static double GetNoteListScrollOffset(string stateKey)
    {
        lock (Gate)
        {
            NoteListState? state = RequireEngine()
                .GetCollection<NoteListState>("note_list_states")
                .FindById(stateKey);

            return state?.ScrollOffset ?? 0;
        }
    }

    public static void SaveNoteListScrollOffset(string stateKey, double offset)
    {
        lock (Gate)
        {
            RequireEngine().GetCollection<NoteListState>("note_list_states").Upsert(new NoteListState
            {
                Key = stateKey,
                ScrollOffset = Math.Max(0, offset),
            });
        }
    }

    public static void UploadNoteAttachment(string id, string fileName, Stream source)
    {
        lock (Gate)
        {
            RequireEngine().FileStorage.Upload(id, fileName, source);
        }
    }

    public static byte[] DownloadNoteAttachment(string id)
    {
        lock (Gate)
        {
            using MemoryStream destination = new();
            RequireEngine().FileStorage.Download(id, destination);

            return destination.ToArray();
        }
    }

    public static string GetNoteAttachmentFileName(string id)
    {
        lock (Gate)
        {
            return RequireEngine().FileStorage.FindById(id)?.Filename ?? "attachment.bin";
        }
    }

    public static bool DeleteNoteAttachment(string id)
    {
        lock (Gate)
        {
            return RequireEngine().FileStorage.Delete(id);
        }
    }

    /// <summary>
    /// The pinned webpages, in sidebar order. Answers with an empty list rather than throwing when
    /// nothing is open: bookmarks are read during startup, which races the background open.
    /// </summary>
    public static IReadOnlyList<BookmarkDocument> GetBookmarks()
    {
        lock (Gate)
        {
            if (engine is null)
            {
                return [];
            }

            return engine.GetCollection<BookmarkDocument>("bookmarks")
                .FindAll()
                .OrderBy(bookmark => bookmark.SortOrder)
                .ToList();
        }
    }

    /// <summary>
    /// Makes the stored bookmarks exactly <paramref name="bookmarks"/>. The list is written whole
    /// because sidebar order is a property of the list rather than of any one row, so an add or a
    /// remove renumbers everything after it anyway.
    /// </summary>
    /// <returns>False when nothing is open, so the caller can keep its fallback copy.</returns>
    public static bool ReplaceBookmarks(IReadOnlyList<BookmarkDocument> bookmarks)
    {
        lock (Gate)
        {
            if (engine is null)
            {
                return false;
            }

            ILiteCollection<BookmarkDocument> collection = engine.GetCollection<BookmarkDocument>("bookmarks");
            collection.DeleteAll();

            if (bookmarks.Count > 0)
            {
                collection.Insert(bookmarks);
            }

            return true;
        }
    }

    public static IReadOnlyList<PasswordDocument> GetPasswords()
    {
        lock (Gate)
        {
            LiteDatabase database = RequireEngine();
            ILiteCollection<PasswordDocument> passwords = database.GetCollection<PasswordDocument>("passwords");
            passwords.EnsureIndex(entry => entry.UpdatedAt);

            return passwords.FindAll().ToList();
        }
    }

    public static PasswordDocument? GetPassword(ObjectId id)
    {
        lock (Gate)
        {
            return RequireEngine().GetCollection<PasswordDocument>("passwords").FindById(id);
        }
    }

    public static void UpsertPassword(PasswordDocument entry)
    {
        lock (Gate)
        {
            RequireEngine().GetCollection<PasswordDocument>("passwords").Upsert(entry);
        }
    }

    public static void UpsertPasswords(IReadOnlyList<PasswordDocument> entries)
    {
        lock (Gate)
        {
            RequireEngine().GetCollection<PasswordDocument>("passwords").Upsert(entries);
        }
    }

    public static bool DeletePassword(ObjectId id)
    {
        lock (Gate)
        {
            return RequireEngine().GetCollection<PasswordDocument>("passwords").Delete(id);
        }
    }

    /// <summary>
    /// Makes the data file match the requested path and encryption state: relocate it, then rebuild
    /// under the target password if that differs from what the file actually answers to.
    /// </summary>
    public static Task<DatabaseStatus> ApplyAsync(string path, bool wantEncrypted, string? newPassword = null) =>
        Task.Run(() => Apply(path, wantEncrypted, newPassword));

    private static DatabaseStatus Apply(string path, bool wantEncrypted, string? newPassword)
    {
        lock (Gate)
        {
            // Everything below assumes a path the file APIs will accept. A blank or malformed one
            // used to throw out of here - past the try that guards the rest of the work, and past
            // the caller's await - so it arrived as an unhandled exception rather than as the
            // failed status every other bad input produces.
            // Status is deliberately left alone: nothing has been closed, so the connection the
            // status line describes is still the live one.
            if (!IsUsablePath(path))
            {
                return new DatabaseStatus(
                    false,
                    wantEncrypted,
                    string.IsNullOrWhiteSpace(path)
                        ? "Enter the path of a database file."
                        : "That is not a valid file path.",
                    0);
            }

            AppSettings settings = SettingsService.Current;
            string? sourcePath = openedPath ?? settings.DatabasePath;
            bool pathChanged = !SamePath(sourcePath, path);
            bool targetExists = File.Exists(path);

            // The running connection's password is the most reliable source for rollback: the stored
            // setting may lag behind a session-only password or a rotation that has not completed.
            string? previousPassword = openedPassword ?? settings.DatabasePassword ?? ReadPassword(sourcePath);

            string? targetPassword = wantEncrypted
                ? (!string.IsNullOrEmpty(newPassword) ? newPassword : previousPassword)
                : null;

            string? movedTo = null;

            try
            {
                FlushAndClose();

                // Switching to an already existing file means "use that file", not "move current over it".
                if (pathChanged && !targetExists)
                {
                    string? relocationError = Relocate(sourcePath!, path);

                    if (relocationError is not null)
                    {
                        return Rollback(relocationError, path, previousPassword);
                    }

                    movedTo = path;
                }

                if (!TryFindPassword(path, wantEncrypted ? targetPassword : null, out string? actualPassword))
                {
                    return Rollback(
                        Status.Error ?? "The file at that path is not a database Evania can open.",
                        path,
                        previousPassword,
                        movedTo);
                }

                bool switchedToExistingFile = pathChanged && targetExists;
                string? passwordToUse = switchedToExistingFile ? actualPassword : targetPassword;

                if (!switchedToExistingFile && !PasswordsMatch(actualPassword, targetPassword))
                {
                    RebuildTo(targetPassword);
                }

                DatabaseStatus opened = Open(path, passwordToUse);

                if (!opened.Connected)
                {
                    return Rollback(
                        opened.Error ?? "The database could not be opened.",
                        path,
                        previousPassword,
                        movedTo);
                }

                settings.DatabasePath = path;
                settings.DatabaseEncryption = !string.IsNullOrEmpty(passwordToUse);
                settings.DatabasePassword = passwordToUse;
                settings.PendingDatabasePassword = null;
                SavePassword(path, passwordToUse);

                if (movedTo is not null)
                {
                    DatabaseCredentialVault.Delete(sourcePath!);
                }

                // The bookmarks now in view belong to whichever file this ended up on, so they are
                // re-read here rather than carried over from the connection that just closed.
                BookmarkService.SyncWithDatabase();

                return opened;
            }
            catch (Exception ex)
            {
                return Rollback(Explain(ex), path, previousPassword, movedTo);
            }
        }
    }

    private static string? Relocate(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        bool sourceExists = File.Exists(sourcePath);
        bool targetExists = File.Exists(targetPath);

        if (sourceExists && targetExists)
        {
            long targetSize = new FileInfo(targetPath).Length;

            if (targetSize > 0)
            {
                return $"A file already exists at {targetPath}. Move or delete it first - Evania will not overwrite a database.";
            }

            DeleteDataFiles(targetPath);
        }

        if (!sourceExists)
        {
            return null;
        }

        foreach (string related in RelatedPaths(sourcePath))
        {
            if (File.Exists(related))
            {
                File.Move(related, targetPath + related.Substring(sourcePath.Length), overwrite: true);
            }
        }

        return null;
    }

    private static bool TryFindPassword(string path, string? expectedPassword, out string? actualPassword)
    {
        AppSettings settings = SettingsService.Current;

        IEnumerable<string?> candidates = new[]
            {
                expectedPassword,
                settings.DatabasePassword,
                ReadPassword(path),
                null,
            }
            .Distinct(PasswordComparer.Instance);

        foreach (string? candidate in candidates)
        {
            if (Open(path, candidate).Connected)
            {
                actualPassword = candidate;
                return true;
            }
        }

        actualPassword = null;
        return false;
    }

    private static DatabaseStatus Rollback(
        string error,
        string requestedPath,
        string? previousPassword,
        string? movedTo = null)
    {
        AppSettings settings = SettingsService.Current;

        string reconnectPath = movedTo ?? settings.DatabasePath;

        if (!File.Exists(reconnectPath))
        {
            return new DatabaseStatus(false, false, error, 0);
        }

        if (previousPassword is null || !Open(reconnectPath, previousPassword).Connected)
        {
            Open(reconnectPath, null);
        }

        string fullError = movedTo is null
            ? error
            : $"{error} The data file was moved to {movedTo} before that failed.";

        return new DatabaseStatus(false, Status.Encrypted, fullError, SizeOf(requestedPath));
    }

    private static IEnumerable<string> RelatedPaths(string path) =>
        new[] { path, path + "-log", path + "-lock" };

    private static void DeleteDataFiles(string path)
    {
        foreach (string related in RelatedPaths(path))
        {
            if (File.Exists(related))
            {
                File.Delete(related);
            }
        }
    }

    private static void FlushAndClose()
    {
        if (engine is null)
        {
            return;
        }

        try
        {
            engine.Checkpoint();
        }
        catch (Exception)
        {
        }

        engine.Dispose();
        engine = null;
        openedPath = null;
        openedPassword = null;
    }

    private static LiteDatabase RequireEngine()
    {
        if (engine is null)
        {
            DatabaseStatus status = OpenFromSettings();

            if (!status.Connected || engine is null)
            {
                throw new InvalidOperationException(status.Error ?? "The database is not connected.");
            }
        }

        return engine;
    }

    private static void RebuildTo(string? targetPassword)
    {
        if (engine is null)
        {
            return;
        }

        try
        {
            engine.Checkpoint();
            engine.Rebuild(new RebuildOptions { Password = targetPassword });
        }
        catch (LiteException)
        {
        }

        openedPassword = targetPassword;
    }

    private static string? ReadPassword(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return DatabaseCredentialVault.Read(path);
    }

    private static void SavePassword(string path, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            DatabaseCredentialVault.Delete(path);
        }
        else
        {
            DatabaseCredentialVault.Write(path, password);
        }
    }

    private static bool PasswordsMatch(string? a, string? b) => a is null ? b is null : a == b;

    /// <summary>
    /// Whether the file APIs will accept this path at all. <see cref="Path.GetFullPath(string)"/>
    /// throws on a blank or malformed one, and it is reached from several places here before any
    /// try block, so the question is answered once up front.
    /// </summary>
    private static bool IsUsablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return Path.GetFullPath(path).Length > 0 && Path.GetFileName(path).Length > 0;
        }
        catch (Exception)
        {
            // ArgumentException, PathTooLongException, NotSupportedException - all mean the same
            // thing to the caller.
            return false;
        }
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static long SizeOf(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string Explain(Exception ex) => ex.Message switch
    {
        var message when message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                         message.Contains("encrypted", StringComparison.OrdinalIgnoreCase) ||
                         message.Contains("invalid datafile", StringComparison.OrdinalIgnoreCase) =>
            $"{message} - incorrect master password for this database file.",
        var message => message,
    };

    private sealed class PasswordComparer : IEqualityComparer<string?>
    {
        public static readonly PasswordComparer Instance = new();

        public bool Equals(string? x, string? y) => x == y;

        public int GetHashCode(string? obj) => obj?.GetHashCode() ?? 0;
    }
}
