using System;
using System.Collections;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using LiteDB;

namespace Evania.Models;

/// <summary>
/// One site in the database's "vault" collection: the aggregate root that owns every account the
/// user keeps for that site, so "multiple accounts per site" is the database's own shape. A site
/// document exists only while it has accounts. Protection is the database's own: when Settings has
/// database encryption on, LiteDB encrypts the file with the master password from Windows
/// Credential Manager; when it is off, these fields sit in the file in plain text.
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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Stamps the newest change across the accounts, so the aggregate can be sorted by recency.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One account under a site. Embedded in its <see cref="VaultSite"/> rather than stored flat:
/// adding or removing the last account creates or removes the site with it, and an account moved
/// between spellings of the same site stays inside one aggregate.
/// </summary>
public sealed class VaultAccount
{
    private string username = string.Empty;
    private string email = string.Empty;
    private string password = string.Empty;
    private string otpSecret = string.Empty;
    private string secret = string.Empty;
    private string note = string.Empty;

    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    /// <summary>
    /// LiteDB's mapper serializes empty strings as BSON null and hands null back on read, so every
    /// string setter coalesces - the model guarantees non-null values no matter what a round trip
    /// through the file does to a field left empty.
    /// </summary>
    public string Username { get => username; set => username = value ?? string.Empty; }

    public string Email { get => email; set => email = value ?? string.Empty; }

    /// <summary>
    /// Multi-line on purpose: users paste recovery codes, private keys, and other long secrets here,
    /// not just single-line passwords.
    /// </summary>
    public string Password { get => password; set => password = value ?? string.Empty; }

    /// <summary>Canonical base32 TOTP secret; empty when the account has no authenticator.</summary>
    public string OtpSecret { get => otpSecret; set => otpSecret = value ?? string.Empty; }

    /// <summary>Extra hidden field for misc secrets - API keys, recovery codes, security answers.</summary>
    public string Secret { get => secret; set => secret = value ?? string.Empty; }

    /// <summary>Free-form note shown in the editor only.</summary>
    public string Note { get => note; set => note = value ?? string.Empty; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
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

    public required string Email { get; init; }

    public required bool HasPassword { get; init; }

    public required bool HasTotp { get; init; }

    public required bool HasSecret { get; init; }

    public required bool HasNote { get; init; }

    public required DateTime UpdatedAt { get; init; }

    [ObservableProperty]
    public partial string TotpCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double TotpProgress { get; set; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Username) ? Username
        : !string.IsNullOrWhiteSpace(Email) ? Email
        : "Unnamed account";

    /// <summary>The other half of the identity when both are present - the email under a username.</summary>
    public string SecondaryText => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Email)
        ? Email
        : string.Empty;

    public bool HasSecondaryText => SecondaryText.Length > 0;

    public string UpdatedText
    {
        get
        {
            TimeSpan elapsed = DateTime.UtcNow - UpdatedAt.ToUniversalTime();

            return elapsed.TotalMinutes switch
            {
                < 1 => "just now",
                < 60 => $"{(int)elapsed.TotalMinutes} min ago",
                < 24 * 60 => $"{(int)elapsed.TotalHours} hr ago",
                < 30 * 24 * 60 => $"{(int)elapsed.TotalDays} day ago",
                < 365 * 24 * 60 => $"{(int)(elapsed.TotalDays / 30)} mo ago",
                _ => $"{(int)(elapsed.TotalDays / 365)} yr ago"
            };
        }
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

    public string AccountCountText => AccountCount == 1 ? "1 account" : $"{AccountCount} accounts";

    public IEnumerator<VaultListItem> GetEnumerator() => Accounts.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
