using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PdcHomeConnect.Core.Util;

/// <summary>Helpers to build stable, Home Assistant friendly keys.</summary>
public static class Slug
{
    /// <summary>Converts arbitrary text into [a-z0-9_] (max 48 chars).</summary>
    public static string From(string? text, int maxLength = 48)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "x";
        }

        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        var lastUnderscore = false;
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var c = char.ToLowerInvariant(ch);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
                lastUnderscore = false;
            }
            else if (!lastUnderscore && sb.Length > 0)
            {
                sb.Append('_');
                lastUnderscore = true;
            }
        }

        var result = sb.ToString().Trim('_');
        if (result.Length > maxLength)
        {
            result = result[..maxLength].TrimEnd('_');
        }
        return result.Length == 0 ? ShortHash(text) : result;
    }

    /// <summary>Builds "prefix_slug" keys.</summary>
    public static string Key(string prefix, string? id) => $"{prefix}_{From(id)}";

    /// <summary>Short stable hash (8 hex chars) of the input.</summary>
    public static string ShortHash(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }
}
