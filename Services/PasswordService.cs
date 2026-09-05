using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Evania.Models;
using LiteDB;

namespace Evania.Services;

/// <summary>
/// Async facade over the database's "passwords" collection - the same shape NoteService gives the
/// notes. Encryption is whatever the database itself carries: with Settings' database encryption
/// on, LiteDB encrypts the file under the master password from Windows Credential Manager; with it
/// off, entries are readable by anyone who can open the file, and the page says so out loud.
/// </summary>
public static class PasswordService
{
    public static Task<IReadOnlyList<PasswordListItem>> ListAsync(string filter) =>
        Task.Run(() =>
        {
            IEnumerable<PasswordDocument> query = DatabaseService.GetPasswords();
            string normalizedFilter = filter.Trim();

            if (normalizedFilter.Length > 0)
            {
                query = query.Where(entry =>
                    Contains(entry.Site, normalizedFilter) ||
                    Contains(entry.Username, normalizedFilter));
            }

            return (IReadOnlyList<PasswordListItem>)query
                .OrderByDescending(entry => entry.UpdatedAt)
                .Select(entry => new PasswordListItem
                {
                    Id = entry.Id,
                    Site = entry.Site,
                    Username = entry.Username,
                    HasPassword = entry.Password.Length > 0,
                    HasTotp = !string.IsNullOrWhiteSpace(entry.TotpSecret),
                    UpdatedAt = entry.UpdatedAt,
                })
                .ToList();
        });

    /// <summary>
    /// Secrets for the entries that have an authenticator, so list cards can tick their codes.
    /// Fetched apart from the list projection so the observable items never carry a secret.
    /// </summary>
    public static Task<IReadOnlyDictionary<ObjectId, string>> GetTotpSecretsAsync() =>
        Task.Run(() => (IReadOnlyDictionary<ObjectId, string>)DatabaseService.GetPasswords()
            .Where(entry => !string.IsNullOrWhiteSpace(entry.TotpSecret))
            .ToDictionary(entry => entry.Id, entry => entry.TotpSecret));

    public static Task<PasswordDocument?> GetAsync(ObjectId id) =>
        Task.Run(() => DatabaseService.GetPassword(id));

    public static Task SaveAsync(PasswordDocument entry) =>
        Task.Run(() =>
        {
            entry.UpdatedAt = DateTime.UtcNow;
            DatabaseService.UpsertPassword(entry);
        });

    public static Task DeleteAsync(ObjectId id) =>
        Task.Run(() => DatabaseService.DeletePassword(id));

    /// <summary>
    /// Imports a browser CSV export, skipping rows whose site, username, and password already exist
    /// (case-insensitive on site/username - re-importing the same export must not duplicate, and
    /// duplicates inside the file itself collapse too).
    /// </summary>
    public static Task<(int Imported, int Skipped)> ImportCsvAsync(string csvText) =>
        Task.Run(() =>
        {
            IReadOnlyList<PasswordDocument> parsed = PasswordCsv.FromBrowserExport(csvText);
            List<PasswordDocument> accepted = new();
            HashSet<string> seen = DatabaseService.GetPasswords().Select(Fingerprint).ToHashSet();
            int skipped = 0;

            foreach (PasswordDocument candidate in parsed)
            {
                string fingerprint = Fingerprint(candidate);

                if (!seen.Add(fingerprint))
                {
                    skipped++;
                    continue;
                }

                accepted.Add(candidate);
            }

            if (accepted.Count > 0)
            {
                DatabaseService.UpsertPasswords(accepted);
            }

            return (accepted.Count, skipped);
        });

    public static Task<string> ExportCsvAsync() =>
        Task.Run(() => PasswordCsv.ToBrowserCsv(DatabaseService.GetPasswords()));

    private static string Fingerprint(PasswordDocument entry) =>
        $"{entry.Site.Trim().ToUpperInvariant()}\u0000{entry.Username.Trim().ToUpperInvariant()}\u0000{entry.Password}";

    private static bool Contains(string? text, string filter) =>
        !string.IsNullOrEmpty(text) && text.Contains(filter, StringComparison.OrdinalIgnoreCase);
}