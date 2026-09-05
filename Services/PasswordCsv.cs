using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Evania.Models;

namespace Evania.Services;

/// <summary>
/// CSV portability for passwords. Import auto-detects the browser export format by its header row -
/// Chrome/Edge (<c>name,url,username,password</c>) and Firefox (<c>url,username,password,...)</c>
/// both carry the fields Evania needs. Export writes the Chrome-compatible form, which most other
/// managers accept too.
/// </summary>
public static class PasswordCsv
{
    public static string ExportHeader => "name,url,username,password";

    /// <summary>
    /// Parses a browser CSV export into password documents. Throws <see cref="FormatException"/>
    /// with a user-presentable message when the file is not a recognized export.
    /// </summary>
    public static IReadOnlyList<PasswordDocument> FromBrowserExport(string csvText)
    {
        IReadOnlyList<string[]> rows = Parse(csvText);

        if (rows.Count == 0)
        {
            throw new FormatException("The CSV file is empty.");
        }

        string[] header = rows[0].Select(cell => cell.Trim().ToLowerInvariant()).ToArray();

        int urlIndex = Array.IndexOf(header, "url");
        int usernameIndex = Array.IndexOf(header, "username");
        int passwordIndex = Array.IndexOf(header, "password");
        int nameIndex = Array.IndexOf(header, "name");

        if (usernameIndex < 0 || passwordIndex < 0 || (urlIndex < 0 && nameIndex < 0))
        {
            throw new FormatException(
                "Unrecognized CSV format. Expected a browser password export (Chrome, Edge, or Firefox) with url/username/password columns.");
        }

        List<PasswordDocument> entries = new();

        foreach (string[] row in rows.Skip(1))
        {
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string url = Cell(row, urlIndex);
            string name = Cell(row, nameIndex);
            string site = !string.IsNullOrWhiteSpace(url) ? url : name;
            string username = Cell(row, usernameIndex);
            string password = Cell(row, passwordIndex);

            // Rows with neither a site nor a credential are export metadata, not passwords.
            if (string.IsNullOrWhiteSpace(site) && string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(password))
            {
                continue;
            }

            entries.Add(new PasswordDocument
            {
                Site = site,
                Username = username,
                Password = password,
            });
        }

        if (entries.Count == 0)
        {
            throw new FormatException("The CSV file contains no password rows.");
        }

        return entries;
    }

    /// <summary>Builds a Chrome-compatible CSV document from the given entries.</summary>
    public static string ToBrowserCsv(IReadOnlyList<PasswordDocument> entries)
    {
        StringBuilder builder = new();
        builder.Append(ExportHeader).Append("\r\n");

        foreach (PasswordDocument entry in entries)
        {
            string site = entry.Site.Trim();
            bool looksLikeUrl = site.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                site.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            builder.Append(EscapeField(site)).Append(',');
            builder.Append(EscapeField(looksLikeUrl ? site : string.Empty)).Append(',');
            builder.Append(EscapeField(entry.Username)).Append(',');
            builder.Append(EscapeField(entry.Password)).Append('\r');
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Minimal RFC 4180 reader: quoted fields with embedded commas, newlines, and doubled quotes.
    /// Browsers export quoted fields whenever a value contains a comma or a line break, which CSVs
    /// full of passwords always do.
    /// </summary>
    public static IReadOnlyList<string[]> Parse(string text)
    {
        // Notepad and some exporters prepend a UTF-8 BOM; left in place it corrupts the first
        // header cell and the format stops being recognized.
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        List<string[]> rows = new();
        List<string> currentRow = new();
        StringBuilder currentField = new();
        bool inQuotes = false;
        int i = 0;

        void EndField()
        {
            currentRow.Add(currentField.ToString());
            currentField.Clear();
        }

        void EndRow()
        {
            EndField();
            rows.Add(currentRow.ToArray());
            currentRow.Clear();
        }

        while (i < text.Length)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        currentField.Append('"');
                        i += 2;
                    }
                    else
                    {
                        inQuotes = false;
                        i++;
                    }
                }
                else
                {
                    currentField.Append(c);
                    i++;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    i++;
                    break;
                case ',':
                    EndField();
                    i++;
                    break;
                case '\r':
                    // Treat CR, CRLF, and lone LF alike - exports differ between platforms.
                    i++;
                    if (i < text.Length && text[i] == '\n')
                    {
                        i++;
                    }

                    EndRow();
                    break;
                case '\n':
                    i++;
                    EndRow();
                    break;
                default:
                    currentField.Append(c);
                    i++;
                    break;
            }
        }

        // A trailing row without a final newline still counts.
        if (currentField.Length > 0 || currentRow.Count > 0)
        {
            EndRow();
        }

        // A blank line at the end of the file parses as a single empty field; drop it.
        if (rows.Count > 0 && rows[^1].Length == 1 && string.IsNullOrEmpty(rows[^1][0]))
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return rows;
    }

    private static string Cell(string[] row, int index) =>
        index >= 0 && index < row.Length ? row[index] : string.Empty;

    private static string EscapeField(string value)
    {
        bool needsQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;

        return needsQuotes
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
