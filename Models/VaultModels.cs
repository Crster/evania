using System;
using System.Collections;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using LiteDB;

namespace Evania.Models;

/// <summary>
/// One site in the database's "vault" collection: the aggregate root that owns every account the
/// user keeps for that site, so "multiple accounts per site" is the database's own shape. A site
/// document exists only while it has accounts. Each account carries exactly four content fields -
/// username, password, note, and otp - under the site's name, and nothing else; the audit trail of
/// replaced credentials lives in the separate "vault_history" collection. Protection is the
/// database's own: when Settings has database encryption on, LiteDB encrypts the file with the
/// master password from Windows Credential Manager; when it is off, these fields sit in the file
/// in plain text.
/// </summary>
public sealed class VaultSite
{
    private string site = string.Empty;

    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    /// <summary>The site or app name as first entered. Grouping compares names case-insensitively.</summary>
    /// <remarks>
    /// LiteDB's mapper serializes empty strings as BSON null and hands null back on read, so the
    /// setter coalesces - the model must not depend on what a round trip does to an empty name.
    /// </remarks>
    public string Site { get => site; set => site = value ?? string.Empty; }

    public List<VaultAccount> Accounts { get; set; } = new();
}

/// <summary>
/// One account under a site. Embedded in its <see cref="VaultSite"/> rather than stored flat:
/// adding or removing the last account creates or removes the site with it, and an account moved
/// between spellings of the same site stays inside one aggregate. The username field holds
/// whichever identity the site signs in with - a name or an email address - and the password field
/// holds its secret, whatever kind of secret that is.
/// </summary>
public sealed class VaultAccount
{
    private string username = string.Empty;
    private string password = string.Empty;
    private string note = string.Empty;
    private string otpSecret = string.Empty;

    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    /// <summary>
    /// LiteDB's mapper serializes empty strings as BSON null and hands null back on read, so every
    /// string setter coalesces - the model guarantees non-null values no matter what a round trip
    /// through the file does to a field left empty.
    /// </summary>
    public string Username { get => username; set => username = value ?? string.Empty; }

    public string Password { get => password; set => password = value ?? string.Empty; }

    /// <summary>Free-form markdown note, written with the markdown editor and shown in the editor only.</summary>
    public string Note { get => note; set => note = value ?? string.Empty; }

    /// <summary>Canonical base32 TOTP secret; empty when the account has no authenticator.</summary>
    public string OtpSecret { get => otpSecret; set => otpSecret = value ?? string.Empty; }
}

/// <summary>
/// One "vault_history" row: the username and password an account used immediately before a save
/// replaced them. Written whenever an edit changes either credential, keyed to the account so the
/// editor's History tab can list them newest first, and deleted with the account - history for an
/// account that no longer exists is secret data nothing can ever show.
/// </summary>
public sealed class VaultHistoryEntry
{
    private string site = string.Empty;
    private string username = string.Empty;
    private string password = string.Empty;

    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    public ObjectId SiteId { get; set; } = ObjectId.Empty;

    public ObjectId AccountId { get; set; } = ObjectId.Empty;

    /// <summary>The site name as it was spelled when the credentials were replaced - a later rename
    /// must not rewrite history.</summary>
    public string Site { get => site; set => site = value ?? string.Empty; }

    public string Username { get => username; set => username = value ?? string.Empty; }

    public string Password { get => password; set => password = value ?? string.Empty; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// UI projection of one account for the vault list. Carries no secrets other than what the card
/// shows live - the TOTP code, which the view model derives from a secret map fetched separately.
/// </summary>
public partial class VaultListItem : ObservableObject
{
    public required ObjectId SiteId { get; init; }

    public required ObjectId AccountId { get; init; }

    public required string Site { get; init; }

    public required string Username { get; init; }

    public required bool HasTotp { get; init; }

    public required bool HasNote { get; init; }

    [ObservableProperty]
    public partial string TotpCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double TotpProgress { get; set; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Username) ? Username : "Unnamed account";
}

/// <summary>
/// UI projection of one history row for the editor's History tab. It holds the real credentials
/// so reveal and copy work without a database round trip - the editor session already carries the
/// account's live secrets - but the password draws masked until the row's toggle flips.
/// </summary>
public partial class VaultHistoryItem : ObservableObject
{
    public required string Username { get; init; }

    public required string Password { get; init; }

    public required DateTime ChangedAt { get; init; }

    [ObservableProperty]
    public partial bool IsPasswordVisible { get; set; }

    public string DisplayPassword => IsPasswordVisible ? Password : new string('●', Password.Length);

    public bool HasUsername => Username.Length > 0;

    public bool HasPassword => Password.Length > 0;

    public string ChangedText => ChangedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm");

    public string EyeGlyph => IsPasswordVisible ? "\uED1A" : "\uE7B3";

    partial void OnIsPasswordVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayPassword));
        OnPropertyChanged(nameof(EyeGlyph));
    }
}

/// <summary>
/// One grouped-list section: every account that shares a site name, compared case-insensitively.
/// The ListView reads the accounts through <see cref="GetEnumerator"/>; the header template binds
/// the rest. Groups are rebuilt on every list reload, so the count never needs live updates.
/// </summary>
public sealed class VaultSiteGroup : IEnumerable<VaultListItem>
{
    public required ObjectId SiteId { get; init; }

    /// <summary>The site name in the casing of its newest account, used as the group key.</summary>
    public required string Name { get; init; }

    public List<VaultListItem> Accounts { get; } = new();

    public int AccountCount => Accounts.Count;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Unnamed" : Name;

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();

    /// <summary>True when the site name is itself a web address, so the header can offer it as a link.</summary>
    public bool IsUrl => Uri.TryCreate(Name, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public string AccountCountText => AccountCount == 1 ? "1 account" : $"{AccountCount} accounts";

    public IEnumerator<VaultListItem> GetEnumerator() => Accounts.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
