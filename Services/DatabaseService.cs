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
        string? password;

        try
        {
            password = settings.DatabaseEncryption
                ? DatabaseCredentialVault.Read(settings.DatabasePath)
                : null;
        }
        catch (Exception ex)
        {
            return Status = new DatabaseStatus(false, settings.DatabaseEncryption, Explain(ex), SizeOf(settings.DatabasePath));
        }

        if (settings.DatabaseEncryption && string.IsNullOrEmpty(password))
        {
            return Status = new DatabaseStatus(
                false,
                true,
                "No database password was found in Windows Credential Manager. Enter it in Settings and apply it again.",
                SizeOf(settings.DatabasePath));
        }

        settings.DatabasePassword = password;

        return Open(settings.DatabasePath, password);
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
            AppSettings settings = SettingsService.Current;
            string? sourcePath = openedPath ?? settings.DatabasePath;

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

                if (!SamePath(sourcePath, path))
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

                if (!PasswordsMatch(actualPassword, targetPassword))
                {
                    RebuildTo(targetPassword);
                }

                DatabaseStatus opened = Open(path, targetPassword);

                if (!opened.Connected)
                {
                    return Rollback(
                        opened.Error ?? "The database could not be opened.",
                        path,
                        previousPassword,
                        movedTo);
                }

                settings.DatabasePath = path;
                settings.DatabaseEncryption = wantEncrypted;
                settings.DatabasePassword = targetPassword;
                settings.PendingDatabasePassword = null;
                SavePassword(path, targetPassword);

                if (!SamePath(sourcePath, path))
                {
                    DatabaseCredentialVault.Delete(sourcePath!);
                }

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
