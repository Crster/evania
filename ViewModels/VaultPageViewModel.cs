using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Evania.Models;
using Evania.Services;
using LiteDB;

namespace Evania.ViewModels;

/// <summary>
/// Drives the vault page: the list-vs-editor split, the per-site grouping, and the live OTP codes.
/// Accounts live in the database's "vault" collection through <see cref="VaultService"/>, where
/// each site owns its accounts; this view model flattens them into list items and regroups them for
/// display. The editor itself is sectioned - an Account card, then Note/OTP/History tabs - with the
/// credential audit for the open account held in <see cref="HistoryItems"/>. Whether entries are
/// encrypted at rest is the database's own setting, and <see cref="ShowUnencryptedWarning"/> keeps
/// the user honest when it is off.
/// </summary>
public partial class VaultPageViewModel : ObservableObject
{
    /// <summary>The site and account being edited, or null when the editor holds a brand-new account.</summary>
    private (ObjectId SiteId, ObjectId AccountId)? currentEntry;

    /// <summary>The account as loaded (blank for a new one) - the baseline for the dirty check.</summary>
    private VaultAccount? loadedAccount;

    private string loadedSiteName = string.Empty;

    private IReadOnlyDictionary<ObjectId, string> totpSecrets = new Dictionary<ObjectId, string>();

    public ObservableCollection<VaultListItem> Entries { get; } = new();

    /// <summary>Grouped list source: accounts sharing a site name, one group per site.</summary>
    public IReadOnlyList<VaultSiteGroup> Groups { get; private set; } = [];

    /// <summary>The open account's credential audit, newest first, for the editor's History tab.</summary>
    public ObservableCollection<VaultHistoryItem> HistoryItems { get; } = new();

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    /// <summary>Success feedback (import/export results), shown in an informational InfoBar.</summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// Raised after each load while the database is unencrypted: without a master password these
    /// entries are open for anyone to see, and the page should say so until dismissed.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowUnencryptedWarning { get; set; }

    [ObservableProperty]
    public partial string EditSite { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditUsername { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditTotpSecret { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditNote { get; set; } = string.Empty;

    /// <summary>Live OTP code for the account being edited; empty when no valid secret is present.</summary>
    [ObservableProperty]
    public partial string EditTotpCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double EditTotpProgress { get; set; }

    /// <summary>Normalized 0-1 estimate of the edited password's strength, for the editor meter.</summary>
    [ObservableProperty]
    public partial double EditPasswordStrength { get; set; }

    [ObservableProperty]
    public partial string EditPasswordStrengthText { get; set; } = string.Empty;

    /// <summary>When the credentials last changed, from the audit trail; empty for a new account.</summary>
    [ObservableProperty]
    public partial string LastChangedText { get; set; } = string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    public bool HasStatus => StatusMessage.Length > 0;

    public bool IsEmpty => !IsBusy && Entries.Count == 0;

    public bool HasEditTotpCode => EditTotpCode.Length > 0;

    public bool HasEditPassword => (EditPassword ?? string.Empty).Length > 0;

    public bool HasHistory => HistoryItems.Count > 0;

    public bool HasLastChanged => LastChangedText.Length > 0;

    /// <summary>False while the editor holds a brand-new account, which has nothing to delete yet.</summary>
    public bool IsExistingEntry => currentEntry is not null;

    public int SiteCount => Groups.Count;

    /// <summary>True when saving would change anything - the back button asks before discarding.</summary>
    public bool IsDirty
    {
        get
        {
            if (loadedAccount is null)
            {
                return false;
            }

            return !string.Equals(Trimmed(EditSite), Trimmed(loadedSiteName), StringComparison.Ordinal)
                || !string.Equals(Trimmed(EditUsername), Trimmed(loadedAccount.Username), StringComparison.Ordinal)
                || !string.Equals(EditPassword ?? string.Empty, loadedAccount.Password ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(Trimmed(EditTotpSecret), Trimmed(loadedAccount.OtpSecret), StringComparison.Ordinal)
                || !string.Equals(EditNote ?? string.Empty, loadedAccount.Note ?? string.Empty, StringComparison.Ordinal);
        }
    }

    private static string Trimmed(string? value) => (value ?? string.Empty).Trim();

    partial void OnFilterChanged(string value) => _ = ReloadListAsync();

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnEditTotpSecretChanged(string value) => RefreshTotp();

    partial void OnEditTotpCodeChanged(string value) => OnPropertyChanged(nameof(HasEditTotpCode));

    partial void OnEditPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(HasEditPassword));
        UpdatePasswordStrength();
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnLastChangedTextChanged(string value) => OnPropertyChanged(nameof(HasLastChanged));

    public async Task LoadAsync()
    {
        await ReloadListAsync();

        // Ask after the first data access: that is what forces the database open and makes Status
        // describe the file the page is actually showing.
        ShowUnencryptedWarning = DatabaseService.Status.Connected && !DatabaseService.Status.Encrypted;
    }

    public async Task ReloadListAsync()
    {
        try
        {
            IsBusy = true;

            IReadOnlyList<VaultListItem> items = await VaultService.ListAsync(Filter);
            totpSecrets = await VaultService.GetTotpSecretsAsync();

            Entries.Clear();
            foreach (VaultListItem item in items)
            {
                Entries.Add(item);
            }

            // The service order keeps every account of a site contiguous, so one pass builds the groups.
            List<VaultSiteGroup> groups = [];
            VaultSiteGroup? currentGroup = null;
            foreach (VaultListItem item in items)
            {
                string siteName = item.Site.Trim();

                if (currentGroup is null ||
                    !string.Equals(currentGroup.Name, siteName, StringComparison.OrdinalIgnoreCase))
                {
                    currentGroup = new VaultSiteGroup { SiteId = item.SiteId, Name = siteName };
                    groups.Add(currentGroup);
                }

                currentGroup.Accounts.Add(item);
            }

            Groups = groups;
            OnPropertyChanged(nameof(Groups));
            OnPropertyChanged(nameof(SiteCount));
            OnPropertyChanged(nameof(IsEmpty));

            RefreshTotp();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens the editor for a brand-new account, optionally with the site name pre-filled.</summary>
    public void NewEntry(string? siteName = null)
    {
        currentEntry = null;
        OnPropertyChanged(nameof(IsExistingEntry));
        loadedAccount = new VaultAccount();
        loadedSiteName = siteName?.Trim() ?? string.Empty;

        EditSite = loadedSiteName;
        EditUsername = string.Empty;
        EditPassword = string.Empty;
        EditTotpSecret = string.Empty;
        EditNote = string.Empty;
        EditTotpCode = string.Empty;
        ResetHistory();
        IsEditing = true;
    }

    public async Task OpenEntryAsync(VaultListItem item)
    {
        try
        {
            IsBusy = true;

            (VaultSite Site, VaultAccount Account)? loaded =
                await VaultService.GetAccountAsync(item.SiteId, item.AccountId);
            if (loaded is null)
            {
                ErrorMessage = "That account no longer exists.";
                await ReloadListAsync();

                return;
            }

            currentEntry = (loaded.Value.Site.Id, loaded.Value.Account.Id);
            OnPropertyChanged(nameof(IsExistingEntry));
            loadedAccount = loaded.Value.Account;
            loadedSiteName = loaded.Value.Site.Site;

            EditSite = loadedSiteName;
            EditUsername = loadedAccount.Username;
            EditPassword = loadedAccount.Password;
            EditTotpSecret = loadedAccount.OtpSecret;
            EditNote = loadedAccount.Note;
            IsEditing = true;
            await LoadHistoryAsync(loaded.Value.Account.Id);
            RefreshTotp();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void CloseEditor()
    {
        IsEditing = false;
        currentEntry = null;
        OnPropertyChanged(nameof(IsExistingEntry));
        loadedAccount = null;
        EditTotpCode = string.Empty;
        ResetHistory();
    }

    /// <summary>Returns false when validation failed; the reason lands in <see cref="ErrorMessage"/>.</summary>
    public async Task<bool> SaveEntryAsync()
    {
        if (!IsEditing)
        {
            return false;
        }

        string siteName = EditSite.Trim();
        if (siteName.Length == 0)
        {
            ErrorMessage = "Give the account a site or app name.";

            return false;
        }

        bool hasContent = !string.IsNullOrWhiteSpace(EditUsername) ||
                          !string.IsNullOrWhiteSpace(EditPassword) ||
                          !string.IsNullOrWhiteSpace(EditTotpSecret) ||
                          !string.IsNullOrWhiteSpace(EditNote);

        if (!hasContent)
        {
            ErrorMessage = "Add at least one of a username, password, OTP secret, or note.";

            return false;
        }

        if (!string.IsNullOrWhiteSpace(EditTotpSecret) &&
            !TotpService.TryParseSecret(EditTotpSecret, out _, out _, out _))
        {
            ErrorMessage = "The OTP secret is not valid. Paste a base32 secret or an otpauth:// link.";

            return false;
        }

        try
        {
            IsSaving = true;

            VaultAccount account = new()
            {
                Username = Trimmed(EditUsername),
                Password = EditPassword ?? string.Empty,
                OtpSecret = TotpService.TryParseSecret(EditTotpSecret, out string normalizedSecret, out _, out _)
                    ? normalizedSecret
                    : Trimmed(EditTotpSecret),
                Note = EditNote ?? string.Empty,
            };

            (ObjectId siteId, ObjectId accountId) = await VaultService.SaveAccountAsync(
                currentEntry?.SiteId, currentEntry?.AccountId, siteName, account);

            currentEntry = (siteId, accountId);
            OnPropertyChanged(nameof(IsExistingEntry));
            loadedAccount = account;
            loadedSiteName = siteName;

            await ReloadListAsync();
            CloseEditor();

            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;

            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    public async Task DeleteAccountAsync()
    {
        if (currentEntry is not { } entry)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await VaultService.DeleteAccountAsync(entry.SiteId, entry.AccountId);
            CloseEditor();
            await ReloadListAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<(int Imported, int Skipped)> ImportCsvAsync(string csvText)
    {
        try
        {
            IsBusy = true;

            (int imported, int skipped) = await VaultService.ImportCsvAsync(csvText);
            await ReloadListAsync();

            return (imported, skipped);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;

            return (0, 0);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<string?> ExportCsvAsync()
    {
        try
        {
            IsBusy = true;

            return await VaultService.ExportCsvAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;

            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Ticks every visible OTP code - list cards and the editor - once per second. Card codes come
    /// from the secret map fetched with the list; the editor derives from the field being typed.
    /// </summary>
    public void RefreshTotp()
    {
        foreach (VaultListItem item in Entries)
        {
            if (item.HasTotp &&
                totpSecrets.TryGetValue(item.AccountId, out string? secret) &&
                TotpService.TryParseSecret(secret, out string normalized, out int digits, out int period))
            {
                item.TotpCode = TotpService.GenerateCode(normalized, digits, period);
                item.TotpProgress = TotpService.WindowProgress(period);
            }
        }

        if (IsEditing && TotpService.TryParseSecret(EditTotpSecret, out string editSecret, out int editDigits, out int editPeriod))
        {
            EditTotpCode = TotpService.GenerateCode(editSecret, editDigits, editPeriod);
            EditTotpProgress = TotpService.WindowProgress(editPeriod);
        }
        else if (EditTotpCode.Length > 0)
        {
            EditTotpCode = string.Empty;
        }
    }

    public void ClearError() => ErrorMessage = string.Empty;

    public void ClearStatus() => StatusMessage = string.Empty;

    private async Task LoadHistoryAsync(ObjectId accountId)
    {
        IReadOnlyList<VaultHistoryItem> items = await VaultService.GetHistoryAsync(accountId);

        HistoryItems.Clear();
        foreach (VaultHistoryItem item in items)
        {
            HistoryItems.Add(item);
        }

        OnPropertyChanged(nameof(HasHistory));

        LastChangedText = items.Count > 0
            ? $"Credentials last changed {items[0].ChangedAt.ToLocalTime():d MMM yyyy}"
            : string.Empty;
    }

    private void ResetHistory()
    {
        HistoryItems.Clear();
        OnPropertyChanged(nameof(HasHistory));
        LastChangedText = string.Empty;
    }

    private void UpdatePasswordStrength()
    {
        double bits = EstimateStrengthBits(EditPassword ?? string.Empty);

        EditPasswordStrength = Math.Clamp(bits / 100.0, 0.0, 1.0);
        EditPasswordStrengthText = bits <= 0
            ? string.Empty
            : bits switch
            {
                < 40 => "Weak password",
                < 60 => "Fair password",
                < 80 => "Good password",
                _ => "Strong password",
            };
    }

    /// <summary>Rough entropy estimate for a given password: its length times the alphabet size of the character classes it uses.</summary>
    private static double EstimateStrengthBits(string password)
    {
        if (password.Length == 0)
        {
            return 0;
        }

        double alphabet = 0;
        if (password.Any(char.IsLower))
        {
            alphabet += 26;
        }

        if (password.Any(char.IsUpper))
        {
            alphabet += 26;
        }

        if (password.Any(char.IsDigit))
        {
            alphabet += 10;
        }

        if (password.Any(c => !char.IsLetterOrDigit(c)))
        {
            alphabet += 33;
        }

        if (alphabet == 0)
        {
            alphabet = 26;
        }

        return password.Length * Math.Log2(alphabet);
    }
}
