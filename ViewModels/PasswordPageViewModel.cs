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
/// Drives the Passwords page: the list-vs-editor split and the live TOTP codes. Entries live in the
/// database's "passwords" collection through <see cref="PasswordService"/>; whether they are
/// encrypted at rest is the database's own setting, and <see cref="ShowUnencryptedWarning"/> keeps
/// the user honest when it is off.
/// </summary>
public partial class PasswordPageViewModel : ObservableObject
{
    private PasswordDocument? currentEntry;
    private IReadOnlyDictionary<ObjectId, string> totpSecrets = new Dictionary<ObjectId, string>();

    public ObservableCollection<PasswordListItem> Entries { get; } = new();

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

    /// <summary>Live TOTP code for the entry being edited; empty when no valid secret is present.</summary>
    [ObservableProperty]
    public partial string EditTotpCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double EditTotpProgress { get; set; }

    public bool HasError => ErrorMessage.Length > 0;

    public bool HasStatus => StatusMessage.Length > 0;

    public bool IsEmpty => !IsBusy && Entries.Count == 0;

    public bool HasEditTotpCode => EditTotpCode.Length > 0;

    public ObjectId? CurrentEntryId => currentEntry?.Id;

    public string EditUpdatedText => currentEntry is null
        ? string.Empty
        : $"Created {currentEntry.CreatedAt.ToLocalTime():d MMM yyyy} · Updated {currentEntry.UpdatedAt.ToLocalTime():d MMM yyyy}";

    partial void OnFilterChanged(string value) => _ = ReloadListAsync();

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnEditTotpSecretChanged(string value) => RefreshTotp();

    partial void OnEditTotpCodeChanged(string value) => OnPropertyChanged(nameof(HasEditTotpCode));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

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

            IReadOnlyList<PasswordListItem> items = await PasswordService.ListAsync(Filter);
            totpSecrets = await PasswordService.GetTotpSecretsAsync();

            Entries.Clear();
            foreach (PasswordListItem item in items)
            {
                Entries.Add(item);
            }

            RefreshTotp();
            OnPropertyChanged(nameof(IsEmpty));
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

    public void NewEntry()
    {
        currentEntry = new PasswordDocument();
        EditSite = string.Empty;
        EditUsername = string.Empty;
        EditPassword = string.Empty;
        EditTotpSecret = string.Empty;
        EditTotpCode = string.Empty;
        IsEditing = true;
        OnPropertyChanged(nameof(EditUpdatedText));
    }

    public async Task OpenEntryAsync(PasswordListItem item)
    {
        try
        {
            IsBusy = true;

            PasswordDocument? entry = await PasswordService.GetAsync(item.Id);
            if (entry is null)
            {
                ErrorMessage = "That entry no longer exists.";
                await ReloadListAsync();

                return;
            }

            currentEntry = entry;
            EditSite = entry.Site;
            EditUsername = entry.Username;
            EditPassword = entry.Password;
            EditTotpSecret = entry.TotpSecret;
            IsEditing = true;
            RefreshTotp();
            OnPropertyChanged(nameof(EditUpdatedText));
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
        EditTotpCode = string.Empty;
    }

    /// <summary>Returns false when validation failed; the reason lands in <see cref="ErrorMessage"/>.</summary>
    public async Task<bool> SaveEntryAsync()
    {
        if (currentEntry is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(EditSite))
        {
            ErrorMessage = "Give the entry an app or site name.";

            return false;
        }

        if (!string.IsNullOrWhiteSpace(EditTotpSecret) &&
            !TotpService.TryParseSecret(EditTotpSecret, out _, out _, out _))
        {
            ErrorMessage = "The authenticator key is not valid. Paste a base32 secret or an otpauth:// link.";

            return false;
        }

        try
        {
            IsSaving = true;

            currentEntry.Site = EditSite.Trim();
            currentEntry.Username = EditUsername.Trim();
            currentEntry.Password = EditPassword;
            currentEntry.TotpSecret = TotpService.TryParseSecret(EditTotpSecret, out string normalizedSecret, out _, out _)
                ? normalizedSecret
                : EditTotpSecret.Trim();

            await PasswordService.SaveAsync(currentEntry);
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

    public async Task DeleteEntryAsync(ObjectId id)
    {
        try
        {
            IsBusy = true;
            await PasswordService.DeleteAsync(id);

            if (currentEntry?.Id == id)
            {
                CloseEditor();
            }

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

            (int imported, int skipped) = await PasswordService.ImportCsvAsync(csvText);
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

            return await PasswordService.ExportCsvAsync();
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
    /// Ticks every visible TOTP code - list cards and the editor - once per second. Card codes come
    /// from the secret map fetched with the list; the editor derives from the field being typed.
    /// </summary>
    public void RefreshTotp()
    {
        foreach (PasswordListItem item in Entries)
        {
            if (item.HasTotp &&
                totpSecrets.TryGetValue(item.Id, out string? secret) &&
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
}