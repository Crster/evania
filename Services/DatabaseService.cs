using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
///
/// Nothing is stored in this database yet - the screenshot and recording lists are still folder
/// scans - so the service exists to make the two settings above it real: a path and an encryption
/// switch that actually produce, relocate and re-key a data file. LiteDB can do all three, because
/// <c>Rebuild(RebuildOptions)</c> rewrites every page under a new password while keeping a
/// <c>-backup</c> copy. That is what lets encryption be a checkbox instead of a decision taken once
/// at file creation.
///
/// Apply is ordered so the file is never the first place a new key exists: the key is staged into
/// settings, then written into the file. Crash in between and the staged key is still recorded for
/// whichever state the file is in, and the next load promotes it.
/// </summary>
public static class DatabaseService
{
    private static readonly object Gate = new();

    private static LiteDatabase? engine;
    private static string? openedPath;
    private static string? openedKey;

    public static DatabaseStatus Status { get; private set; } = new(false, false, null, 0);

    /// <summary>The key the live connection uses, or null when it is unencrypted.</summary>
    public static string? CurrentKey => openedKey;

    /// <summary>The file the live connection has open, or null when nothing is open.</summary>
    public static string? CurrentPath => openedPath;

    /// <summary>
    /// The key that opens the file the user is looking at, for the page that lets them copy it
    /// somewhere safe. Prefers the one the live connection proves, since that is the key the file on
    /// disk answers to - the stored one may be mid-rotation.
    /// </summary>
    public static string? KeyForDisplay =>
        openedKey ?? (SettingsService.Current.DatabaseEncryption ? SettingsService.Current.DatabaseKey : null);

    /// <summary>
    /// Opens the database the stored settings describe. Used at startup, where a failure belongs on
    /// the Settings page rather than stopping the app.
    /// </summary>
    public static DatabaseStatus OpenFromSettings()
    {
        AppSettings settings = SettingsService.Current;

        return Open(settings.DatabasePath, settings.DatabaseEncryption ? settings.DatabaseKey : null);
    }

    public static DatabaseStatus Open(string path, string? password)
    {
        lock (Gate)
        {
            FlushAndClose();

            try
            {
                ConnectionString connection = new() { Filename = path };

                if (password is not null)
                {
                    connection.Password = password;
                }

                engine = new LiteDatabase(connection);
                openedPath = path;
                openedKey = password;

                Status = new DatabaseStatus(true, password is not null, null, SizeOf(path));
            }
            catch (Exception ex)
            {
                engine = null;
                openedPath = null;
                openedKey = null;

                Status = new DatabaseStatus(false, password is not null, Explain(ex), SizeOf(path));
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

    /// <summary>
    /// Makes the data file match the requested path and encryption state: relocate it, then rebuild
    /// under the target key if that differs from what the file actually answers to. On failure the
    /// stored settings are rolled back and the previous connection is retried, so the page can never
    /// end up showing a path the app is not using.
    /// </summary>
    public static Task<DatabaseStatus> ApplyAsync(string path, bool wantEncrypted) =>
        Task.Run(() => Apply(path, wantEncrypted));

    private static DatabaseStatus Apply(string path, bool wantEncrypted)
    {
        lock (Gate)
        {
            AppSettings settings = SettingsService.Current;
            string? sourcePath = openedPath ?? settings.DatabasePath;
            string? previousKey = settings.DatabaseKey;
            string? previousPending = settings.PendingDatabaseKey;

            string? targetKey = wantEncrypted
                ? previousKey ?? SettingsService.GenerateDatabaseKey()
                : null;

            string? movedTo = null;

            try
            {
                if (wantEncrypted && previousKey is null)
                {
                    SettingsService.StageDatabaseKey(targetKey!);
                }

                FlushAndClose();

                if (!SamePath(sourcePath, path))
                {
                    string? relocationError = Relocate(sourcePath!, path);

                    if (relocationError is not null)
                    {
                        return Rollback(relocationError, path, previousKey, previousPending);
                    }

                    movedTo = path;
                }

                // The file decides what it is: probe the keys we know about instead of trusting what
                // the settings last claimed, since the settings can be older than the file.
                if (!TryFindKey(path, wantEncrypted ? targetKey : null, out string? actualKey))
                {
                    return Rollback(
                        Status.Error ?? "The file at that path is not a database Evania can open.",
                        path,
                        previousKey,
                        previousPending,
                        movedTo);
                }

                if (!KeysMatch(actualKey, targetKey))
                {
                    RebuildTo(targetKey);
                }

                DatabaseStatus opened = Open(path, targetKey);

                if (!opened.Connected)
                {
                    return Rollback(
                        opened.Error ?? "The database could not be opened.",
                        path,
                        previousKey,
                        previousPending,
                        movedTo);
                }

                settings.DatabasePath = path;
                settings.DatabaseEncryption = wantEncrypted;

                if (wantEncrypted)
                {
                    SettingsService.CommitDatabaseKeyRotation();
                }
                else
                {
                    SettingsService.ClearDatabaseKey();
                }

                return opened;
            }
            catch (Exception ex)
            {
                return Rollback(Explain(ex), path, previousKey, previousPending, movedTo);
            }
        }
    }

    /// <summary>
    /// Writes a fresh key into the data file. Only possible while the current key still opens it -
    /// which the caller does have, since it is what the live connection uses.
    /// </summary>
    public static Task<DatabaseStatus> RotateKeyAsync() => Task.Run(RotateKey);

    private static DatabaseStatus RotateKey()
    {
        lock (Gate)
        {
            AppSettings settings = SettingsService.Current;
            string? oldKey = openedKey ?? settings.DatabaseKey;
            string path = openedPath ?? settings.DatabasePath;
            string newKey = SettingsService.GenerateDatabaseKey();
            string? previousPending = settings.PendingDatabaseKey;

            try
            {
                SettingsService.StageDatabaseKey(newKey);

                if (engine is null)
                {
                    DatabaseStatus reopened = Open(path, oldKey);

                    if (!reopened.Connected)
                    {
                        SettingsService.RestoreDatabaseKeys(oldKey, previousPending);
                        return reopened;
                    }
                }

                RebuildTo(newKey);

                DatabaseStatus opened = Open(path, newKey);

                if (opened.Connected)
                {
                    SettingsService.CommitDatabaseKeyRotation();
                }
                else
                {
                    SettingsService.RestoreDatabaseKeys(oldKey, previousPending);
                }

                return opened;
            }
            catch (Exception ex)
            {
                SettingsService.RestoreDatabaseKeys(oldKey, previousPending);

                return new DatabaseStatus(false, true, Explain(ex), SizeOf(path));
            }
        }
    }

    /// <summary>
    /// Throws the data file away and creates a new one in the requested state. This is the escape
    /// hatch for a file whose key nobody holds; it destroys what is inside, so the Settings page
    /// only reaches it through a confirmation dialog.
    /// </summary>
    public static Task<DatabaseStatus> RecreateAsync(string path, bool wantEncrypted) =>
        Task.Run(() => Recreate(path, wantEncrypted));

    private static DatabaseStatus Recreate(string path, bool wantEncrypted)
    {
        lock (Gate)
        {
            AppSettings settings = SettingsService.Current;
            string? previousKey = settings.DatabaseKey;
            string? targetKey = wantEncrypted
                ? previousKey ?? SettingsService.GenerateDatabaseKey()
                : null;

            try
            {
                if (wantEncrypted && previousKey is null)
                {
                    SettingsService.StageDatabaseKey(targetKey!);
                }

                FlushAndClose();
                DeleteDataFiles(path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                DatabaseStatus opened = Open(path, targetKey);

                if (!opened.Connected)
                {
                    if (wantEncrypted && previousKey is null)
                    {
                        SettingsService.AbandonDatabaseKeyRotation();
                    }

                    return opened;
                }

                settings.DatabasePath = path;
                settings.DatabaseEncryption = wantEncrypted;

                if (wantEncrypted)
                {
                    SettingsService.CommitDatabaseKeyRotation();
                }
                else
                {
                    SettingsService.ClearDatabaseKey();
                }

                return opened;
            }
            catch (Exception ex)
            {
                return new DatabaseStatus(false, wantEncrypted, Explain(ex), 0);
            }
        }
    }

    /// <summary>
    /// Moves the data file and its siblings to the new path. Returns the message to show the user,
    /// or null when the path is ready to open.
    /// </summary>
    private static string? Relocate(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        bool sourceExists = File.Exists(sourcePath);

        // Refuse rather than overwrite: a mistyped path must not be able to eat another database.
        if (sourceExists && File.Exists(targetPath))
        {
            return $"A file already exists at {targetPath}. Move or delete it first - Evania will not overwrite a database.";
        }

        if (!sourceExists)
        {
            return null;
        }

        foreach (string related in RelatedPaths(sourcePath))
        {
            if (File.Exists(related))
            {
                File.Move(related, targetPath + related.Substring(sourcePath.Length));
            }
        }

        return null;
    }

    /// <summary>
    /// Which key this file actually answers to. The expected one goes first so the common case costs
    /// one open; the staged key is next because a rebuild that was interrupted left the file in the
    /// state that key describes. Leaves the connection open on the winner, and reports false when
    /// none of them opened it.
    /// </summary>
    private static bool TryFindKey(string path, string? expectedKey, out string? actualKey)
    {
        AppSettings settings = SettingsService.Current;

        IEnumerable<string?> candidates = new[]
            {
                expectedKey,
                expectedKey is null ? settings.PendingDatabaseKey : null,
                settings.DatabaseKey,
                null,
            }
            .Distinct(KeyComparer.Instance);

        foreach (string? candidate in candidates)
        {
            if (Open(path, candidate).Connected)
            {
                actualKey = candidate;
                return true;
            }
        }

        actualKey = null;
        return false;
    }

    /// <summary>
    /// Restores the stored settings and reconnects to whatever is still there. Returns the status of
    /// the configuration that was *asked for*, not of the connection that survived: the page shows
    /// the refusal here and the live state from <see cref="Status"/> alongside it.
    /// </summary>
    private static DatabaseStatus Rollback(
        string error,
        string requestedPath,
        string? previousKey,
        string? previousPending,
        string? movedTo = null)
    {
        AppSettings settings = SettingsService.Current;

        // Apply only writes the path and the switch on success, so those need nothing. The staged
        // key does: a failed apply must leave behind exactly the key pair it found.
        SettingsService.RestoreDatabaseKeys(previousKey, previousPending);

        string reconnectPath = movedTo ?? settings.DatabasePath;

        if (!File.Exists(reconnectPath))
        {
            return new DatabaseStatus(false, false, error, 0);
        }

        // Reconnect with the key the file answered to before this attempt, whatever the stored
        // encryption switch still says - the switch describes intent, and the apply just failed.
        if (previousKey is null || !Open(reconnectPath, previousKey).Connected)
        {
            Open(reconnectPath, null);
        }

        string fullError = movedTo is null
            ? error
            : $"{error} The data file was moved to {movedTo} before that failed.";

        return new DatabaseStatus(false, Status.Encrypted, fullError, SizeOf(requestedPath));
    }

    /// <summary>
    /// LiteDB keeps write-ahead log and lock siblings next to the data file; all of them move or go
    /// with it, or the next open reads a log that no longer matches its data file.
    /// </summary>
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
            // Checkpoint pulls the write-ahead log back into the data file, which is what makes it
            // safe to move the data file on its own.
            engine.Checkpoint();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Checkpoint before closing failed: {ex.Message}");
        }

        engine.Dispose();
        engine = null;
        openedPath = null;
        openedKey = null;
    }

    /// <summary>
    /// Rewrites the open file under a new key.
    ///
    /// LiteDB does the rebuild - every page re-encrypted with a fresh salt, the data intact - and
    /// then reopens the file using the connection it was originally handed. Changing the key makes
    /// that last step throw even though the work succeeded, so a <c>LiteException</c> out of
    /// <c>Rebuild</c> is the expected shape of success here. What decides whether the file really is
    /// in the requested state is the <c>Open</c> the caller performs next, not this call's return.
    /// </summary>
    private static void RebuildTo(string? targetKey)
    {
        if (engine is null)
        {
            return;
        }

        try
        {
            engine.Checkpoint();
            engine.Rebuild(new RebuildOptions { Password = targetKey });
        }
        catch (LiteException)
        {
            // The post-rebuild reopen complaining about the key. Verified by the caller's Open.
        }

        openedKey = targetKey;
    }

    private static bool KeysMatch(string? a, string? b) => a is null ? b is null : a == b;

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

    /// <summary>
    /// LiteDB reports encryption failures in its own words. Translate the ones the user can act on
    /// and pass everything else through untouched rather than guessing at it.
    /// </summary>
    private static string Explain(Exception ex) => ex.Message switch
    {
        var message when SettingsService.DatabaseKeyUnreadable =>
            "The stored database key was protected on a different Windows account or machine and cannot be read here. Copy the key from the machine that created it, or recreate the file.",
        var message when message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                         message.Contains("encrypted", StringComparison.OrdinalIgnoreCase) ||
                         message.Contains("invalid datafile", StringComparison.OrdinalIgnoreCase) =>
            $"{message} - the key in settings does not match this file. Uncheck encryption, or recreate the file.",
        var message => message,
    };

    private sealed class KeyComparer : IEqualityComparer<string?>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(string? x, string? y) => x == y;

        public int GetHashCode(string? obj) => obj?.GetHashCode() ?? 0;
    }
}
