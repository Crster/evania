using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Evania.Models;
using LiteDB;

namespace Evania.Services;

/// <summary>
/// Async facade over the database's "vault" collection, shaped the way the data is stored: sites
/// own their accounts as an aggregate, each account carrying exactly username, password, note, and
/// otp under the site's name. Every operation here routes through the aggregate - saving an
/// account finds or creates its site document, deleting an account removes the site when the last
/// account goes with it - so the list can simply group by site. Replaced credentials are archived
/// into the "vault_history" collection as an audit trail. Encryption is whatever the database
/// itself carries: with Settings' database encryption on, LiteDB encrypts the file under the
/// master password from Windows Credential Manager; with it off, entries are readable by anyone
/// who can open the file, and the page says so out loud.
/// </summary>
public static class VaultService
{
    private static readonly EmptyLastComparer NameOrder = new();

    public static Task<IReadOnlyList<VaultListItem>> ListAsync(string filter) =>
        Task.Run(() =>
        {
            string normalizedFilter = filter.Trim();

            IEnumerable<VaultListItem> items =
                from site in DatabaseService.GetVaultSites()
                from account in site.Accounts
                select Project(site, account);

            if (normalizedFilter.Length > 0)
            {
                items = items.Where(item =>
                    Contains(item.Site, normalizedFilter) ||
                    Contains(item.Username, normalizedFilter));
            }

            // Site name, then account name: the grouping in the view model relies on every account
            // of a site being contiguous in this order.
            return (IReadOnlyList<VaultListItem>)items
                .OrderBy(item => item.Site, NameOrder)
                .ThenBy(item => item.Username, NameOrder)
                .ToList();
        });

    /// <summary>
    /// OTP secrets for the accounts that have an authenticator, so list cards can tick their codes.
    /// Fetched apart from the list projection so the observable items never carry a secret.
    /// </summary>
    public static Task<IReadOnlyDictionary<ObjectId, string>> GetTotpSecretsAsync() =>
        Task.Run(() => (IReadOnlyDictionary<ObjectId, string>)DatabaseService.GetVaultSites()
            .SelectMany(site => site.Accounts)
            .Where(account => !string.IsNullOrWhiteSpace(account.OtpSecret))
            .ToDictionary(account => account.Id, account => account.OtpSecret));

    public static Task<(VaultSite Site, VaultAccount Account)?> GetAccountAsync(ObjectId siteId, ObjectId accountId) =>
        Task.Run(() =>
        {
            VaultSite? site = DatabaseService.GetVaultSite(siteId);
            VaultAccount? account = site?.Accounts.FirstOrDefault(entry => entry.Id == accountId);

            (VaultSite Site, VaultAccount Account)? loaded =
                site is null || account is null ? null : (site, account);

            return loaded;
        });

    /// <summary>
    /// Saves one account into the site aggregate. An edited account keeps its identity and follows a
    /// site rename; a rename that collides with another spelling of the same site moves the account
    /// into that document instead of forking a second one. A brand-new account joins the site its
    /// name matches case-insensitively, creating the site when none does. When the edit replaces the
    /// username or the password, the pair that is leaving goes to history first.
    /// </summary>
    public static Task<(ObjectId SiteId, ObjectId AccountId)> SaveAccountAsync(
        ObjectId? siteId, ObjectId? accountId, string siteName, VaultAccount account) =>
        Task.Run(() =>
        {
            IReadOnlyList<VaultSite> sites = DatabaseService.GetVaultSites();
            DateTime now = DateTime.UtcNow;

            VaultSite? current = accountId is { } id
                ? sites.FirstOrDefault(site => site.Accounts.Any(entry => entry.Id == id))
                : null;

            if (current is not null)
            {
                VaultAccount existing = current.Accounts.First(entry => entry.Id == accountId);

                if (!string.Equals(existing.Username, account.Username, StringComparison.Ordinal) ||
                    !string.Equals(existing.Password, account.Password, StringComparison.Ordinal))
                {
                    DatabaseService.InsertVaultHistory(new VaultHistoryEntry
                    {
                        SiteId = current.Id,
                        AccountId = existing.Id,
                        Site = current.Site,
                        Username = existing.Username,
                        Password = existing.Password,
                        ChangedAt = now,
                    });
                }

                account.Id = existing.Id;
            }

            string requestedName = siteName.Trim();
            VaultSite? destination = sites.FirstOrDefault(site =>
                string.Equals(site.Site.Trim(), requestedName, StringComparison.OrdinalIgnoreCase));

            if (destination is null)
            {
                destination = current ?? new VaultSite { Site = requestedName };
            }

            destination.Site = requestedName;

            if (current is not null && !ReferenceEquals(current, destination))
            {
                current.Accounts.RemoveAll(entry => entry.Id == account.Id);
                destination.Accounts.Add(account);
                DatabaseService.UpsertVaultSite(destination);

                if (current.Accounts.Count == 0)
                {
                    DatabaseService.DeleteVaultSite(current.Id);
                }
                else
                {
                    DatabaseService.UpsertVaultSite(current);
                }
            }
            else if (current is not null)
            {
                int index = current.Accounts.FindIndex(entry => entry.Id == account.Id);
                current.Accounts[index] = account;
                DatabaseService.UpsertVaultSite(current);
            }
            else
            {
                destination.Accounts.Add(account);
                DatabaseService.UpsertVaultSite(destination);
            }

            return (destination.Id, account.Id);
        });

    public static Task DeleteAccountAsync(ObjectId siteId, ObjectId accountId) =>
        Task.Run(() =>
        {
            // History is keyed to the account; with the account gone it is secret data nothing can
            // ever show, so it goes with the account.
            DatabaseService.DeleteVaultHistory(accountId);

            VaultSite? site = DatabaseService.GetVaultSite(siteId);
            if (site is null)
            {
                return;
            }

            site.Accounts.RemoveAll(entry => entry.Id == accountId);

            // A site exists only while it has accounts to show for it.
            if (site.Accounts.Count == 0)
            {
                DatabaseService.DeleteVaultSite(siteId);
            }
            else
            {
                DatabaseService.UpsertVaultSite(site);
            }
        });

    /// <summary>The audit trail for one account, newest first, for the editor's History tab.</summary>
    public static Task<IReadOnlyList<VaultHistoryItem>> GetHistoryAsync(ObjectId accountId) =>
        Task.Run(() => (IReadOnlyList<VaultHistoryItem>)DatabaseService.GetVaultHistory(accountId)
            .Select(entry => new VaultHistoryItem
            {
                Username = entry.Username,
                Password = entry.Password,
                ChangedAt = entry.ChangedAt,
            })
            .ToList());

    /// <summary>
    /// Imports a browser CSV export, grouping the rows into site documents as they land. Rows whose
    /// site, username, and password already exist are skipped (case-insensitive on site/username -
    /// re-importing the same export must not duplicate, and duplicates inside the file itself
    /// collapse too).
    /// </summary>
    public static Task<(int Imported, int Skipped)> ImportCsvAsync(string csvText) =>
        Task.Run(() =>
        {
            IReadOnlyList<VaultCsvRow> parsed = VaultCsv.FromBrowserExport(csvText);

            // Stage by site name: existing documents join first so imported rows land on them, and
            // rows for a site the vault has not seen share one new document per site.
            Dictionary<string, VaultSite> staging = new(StringComparer.OrdinalIgnoreCase);
            foreach (VaultSite site in DatabaseService.GetVaultSites())
            {
                staging[site.Site.Trim()] = site;
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (VaultSite site in staging.Values)
            {
                foreach (VaultAccount account in site.Accounts)
                {
                    seen.Add(Fingerprint(site.Site, account));
                }
            }

            HashSet<VaultSite> changed = new();
            int imported = 0;
            int skipped = 0;

            foreach (VaultCsvRow row in parsed)
            {
                string siteName = row.Site.Trim();
                string fingerprint = Fingerprint(siteName, row.Account);

                if (!seen.Add(fingerprint))
                {
                    skipped++;
                    continue;
                }

                if (!staging.TryGetValue(siteName, out VaultSite? site))
                {
                    site = new VaultSite { Site = siteName };
                    staging[siteName] = site;
                }

                site.Accounts.Add(row.Account);
                changed.Add(site);
                imported++;
            }

            if (changed.Count > 0)
            {
                DatabaseService.UpsertVaultSites(changed.ToList());
            }

            return (imported, skipped);
        });

    public static Task<string> ExportCsvAsync() =>
        Task.Run(() => VaultCsv.ToBrowserCsv(DatabaseService.GetVaultSites()
            .SelectMany(site => site.Accounts, (site, account) => new VaultCsvRow(site.Site, account))));

    private static VaultListItem Project(VaultSite site, VaultAccount account) => new()
    {
        SiteId = site.Id,
        AccountId = account.Id,
        Site = site.Site,
        Username = account.Username,
        HasTotp = !string.IsNullOrWhiteSpace(account.OtpSecret),
        HasNote = !string.IsNullOrWhiteSpace(account.Note),
    };

    private static string Fingerprint(string site, VaultAccount account) =>
        $"{(site ?? string.Empty).Trim().ToUpperInvariant()}\u0000{(account.Username ?? string.Empty).Trim().ToUpperInvariant()}\u0000{account.Password ?? string.Empty}";

    private static bool Contains(string? text, string filter) =>
        !string.IsNullOrEmpty(text) && text.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Alphabetical, case-insensitive, with blank names sorted last so "Unnamed" never leads the list.</summary>
    private sealed class EmptyLastComparer : IComparer<string>
    {
        public static readonly EmptyLastComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            bool xBlank = string.IsNullOrWhiteSpace(x);
            bool yBlank = string.IsNullOrWhiteSpace(y);

            if (xBlank || yBlank)
            {
                return (xBlank, yBlank) switch
                {
                    (true, true) => 0,
                    (true, false) => 1,
                    _ => -1,
                };
            }

            return StringComparer.OrdinalIgnoreCase.Compare(x!.Trim(), y!.Trim());
        }
    }
}
