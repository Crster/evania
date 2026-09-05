using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Evania.Services;

/// <summary>
/// Authenticator (RFC 6238 TOTP) support. Secrets are stored as canonical base32; users may also
/// paste an otpauth:// URI straight from another authenticator's export or QR text, in which case
/// the URI is parsed and only the secret (plus optional digits/period) is kept.
/// </summary>
public static class TotpService
{
    public const int DefaultPeriod = 30;
    public const int DefaultDigits = 6;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// Accepts a bare base32 secret or an otpauth:// URI and hands back the pieces needed to store
    /// and use it. <paramref name="secret"/> is normalized upper-case base32 with padding removed.
    /// </summary>
    public static bool TryParseSecret(
        string? input,
        out string secret,
        out int digits,
        out int period)
    {
        secret = string.Empty;
        digits = DefaultDigits;
        period = DefaultPeriod;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string trimmed = input.Trim();

        if (trimmed.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseOtpAuthUri(trimmed, out secret, out digits, out period))
            {
                return false;
            }
        }
        else
        {
            secret = NormalizeBase32(trimmed);
        }

        return IsDecodableBase32(secret, out _);
    }

    public static string GenerateCode(string secret, int digits = DefaultDigits, int period = DefaultPeriod, DateTime? utcNow = null)
    {
        byte[] key = DecodeBase32(secret);
        long counter = UnixSeconds(utcNow ?? DateTime.UtcNow) / Math.Max(1, period);

        Span<byte> counterBytes = stackalloc byte[8];
        for (int i = 7; i >= 0; i--)
        {
            counterBytes[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counterBytes, hash);

        int offset = hash[^1] & 0x0F;
        int truncated =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        return (truncated % (int)Math.Pow(10, digits)).ToString().PadLeft(digits, '0');
    }

    /// <summary>Seconds left in the current code window, for countdown displays.</summary>
    public static int SecondsRemaining(int period = DefaultPeriod, DateTime? utcNow = null) =>
        period - (int)(UnixSeconds(utcNow ?? DateTime.UtcNow) % period);

    /// <summary>0..1 fraction of the current window elapsed, for progress displays.</summary>
    public static double WindowProgress(int period = DefaultPeriod, DateTime? utcNow = null) =>
        (UnixSeconds(utcNow ?? DateTime.UtcNow) % period) / (double)period;

    private static long UnixSeconds(DateTime utc) =>
        (long)(utc.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;

    private static string NormalizeBase32(string input)
    {
        StringBuilder builder = new(input.Length);

        foreach (char c in input)
        {
            if (char.IsWhiteSpace(c) || c == '-' || c == '=')
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    private static bool IsDecodableBase32(string secret, out byte[] data)
    {
        data = Array.Empty<byte>();

        if (secret.Length == 0)
        {
            return false;
        }

        foreach (char c in secret)
        {
            if (Base32Alphabet.IndexOf(c) < 0)
            {
                return false;
            }
        }

        try
        {
            data = DecodeBase32(secret);
            return data.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] DecodeBase32(string secret)
    {
        // 8 base32 characters encode 5 bytes; walk in groups, collecting bits.
        List<byte> output = new((secret.Length * 5) / 8);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (char c in secret)
        {
            int value = Base32Alphabet.IndexOf(c);
            if (value < 0)
            {
                throw new FormatException("Not a valid base32 secret.");
            }

            buffer = (buffer << 5) | value;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }

        return output.ToArray();
    }

    private static bool TryParseOtpAuthUri(string uri, out string secret, out int digits, out int period)
    {
        secret = string.Empty;
        digits = DefaultDigits;
        period = DefaultPeriod;

        // Uri refuses unencoded spaces and the like that real-world otpauth texts carry, so the
        // query is pulled apart by hand instead.
        int queryStart = uri.IndexOf('?');
        if (queryStart < 0 || !uri.StartsWith("otpauth://totp/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string pair in uri[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string key = pair[..equals];
            string value = WebUtility.UrlDecode(pair[(equals + 1)..]);

            if (key.Equals("secret", StringComparison.OrdinalIgnoreCase))
            {
                secret = NormalizeBase32(value);
            }
            else if (key.Equals("digits", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(value, out int parsedDigits) &&
                     parsedDigits is >= 6 and <= 8)
            {
                digits = parsedDigits;
            }
            else if (key.Equals("period", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(value, out int parsedPeriod) &&
                     parsedPeriod is >= 10 and <= 120)
            {
                period = parsedPeriod;
            }
        }

        return secret.Length > 0;
    }
}
