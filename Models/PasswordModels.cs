using System;
using CommunityToolkit.Mvvm.ComponentModel;
using LiteDB;

namespace Evania.Models;

/// <summary>
/// One credential in the database's "passwords" collection. Protection is the database's own:
/// when Settings has database encryption on, LiteDB encrypts the file with the master password
/// from Windows Credential Manager; when it is off, these fields sit in the file in plain text.
/// </summary>
public sealed class PasswordDocument
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    public string Site { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Multi-line on purpose: users paste recovery codes, private keys, and other long secrets here,
    /// not just single-line passwords.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Canonical base32 TOTP secret; empty when the entry has no authenticator.</summary>
    public string TotpSecret { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// UI projection of a password entry for the list. Carries no secrets other than what the card
/// shows live - the TOTP code, which the view model derives from secrets it fetches separately.
/// </summary>
public partial class PasswordListItem : ObservableObject
{
    public required ObjectId Id { get; init; }

    public required string Site { get; init; }

    public required string Username { get; init; }

    public required bool HasPassword { get; init; }

    public required bool HasTotp { get; init; }

    public required DateTime UpdatedAt { get; init; }

    [ObservableProperty]
    public partial string TotpCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double TotpProgress { get; set; }

    public string DisplaySite => string.IsNullOrWhiteSpace(Site) ? "Unnamed" : Site;

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