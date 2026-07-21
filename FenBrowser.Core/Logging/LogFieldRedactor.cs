using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FenBrowser.Core.Logging;

/// <summary>
/// Centralized redaction of sensitive structured log fields.
/// Redacts secrets before events reach file, debugger, console,
/// DevTools, or failure-bundle sinks.
/// </summary>
public static class LogFieldRedactor
{
    /// <summary>Maximum length for a redacted value placeholder.</summary>
    private const int MaxRedactedLength = 128;

    // Field names that should always be redacted.
    private static readonly HashSet<string> SensitiveFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie",
        "access_token", "api_key", "api-key", "x-api-key",
        "token", "secret", "password", "passwd", "credential",
        "private_key", "private-key", "client_secret", "client-secret"
    };

    // Patterns for sensitive content in arbitrary string values.
    private static readonly Regex[] SensitiveValuePatterns =
    {
        // Authorization headers
        new(@"Authorization:\s*Bearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Authorization:\s*Basic\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Proxy-Authorization:\s*\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Cookie values
        new(@"Cookie:\s*[^=]+=[^;]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Set-Cookie:\s*[^=]+=[^;]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Token patterns in URLs/query strings
        new(@"[?&](access_token|api_key|api-key|token|secret|password)=[^&\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Bearer tokens in bodies
        new(@"""access_token""\s*:\s*""[^""]+""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"""api_key""\s*:\s*""[^""]+""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>
    /// Redacts sensitive values in a fields dictionary.
    /// Returns a new dictionary; the original is not modified.
    /// </summary>
    public static IReadOnlyDictionary<string, object> Redact(IReadOnlyDictionary<string, object> fields)
    {
        if (fields == null || fields.Count == 0)
        {
            return fields;
        }

        var redacted = new Dictionary<string, object>(fields.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
        {
            redacted[key] = RedactFieldValue(key, value);
        }

        return redacted;
    }

    /// <summary>
    /// Returns true if the field name indicates sensitive content.
    /// </summary>
    public static bool IsSensitiveFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        return SensitiveFieldNames.Contains(fieldName);
    }

    /// <summary>
    /// Redacts a single field value. Returns the value unchanged if non-sensitive.
    /// </summary>
    public static object RedactFieldValue(string fieldName, object value)
    {
        if (value == null)
        {
            return null;
        }

        if (IsSensitiveFieldName(fieldName))
        {
            return RedactValue(value);
        }

        // Also scan string values for embedded secrets.
        if (value is string strValue)
        {
            return RedactStringContent(strValue);
        }

        // Recurse into nested dictionaries.
        if (value is IReadOnlyDictionary<string, object> nestedDict)
        {
            return Redact(nestedDict);
        }

        return value;
    }

    /// <summary>
    /// Redacts a sensitive value, preserving type information but not content.
    /// </summary>
    public static object RedactValue(object value)
    {
        if (value == null)
        {
            return null;
        }

        return value switch
        {
            string s => s.Length <= 2
                ? "[redacted]"
                : $"[redacted:{s.Length} chars]",
            _ => "[redacted]"
        };
    }

    /// <summary>
    /// Scans a string for embedded secrets and redacts them.
    /// </summary>
    public static string RedactStringContent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value ?? string.Empty;
        }

        var result = value;
        foreach (var pattern in SensitiveValuePatterns)
        {
            result = pattern.Replace(result, match =>
            {
                // Preserve the header name but redact the value.
                var matched = match.Value;
                var colonIdx = matched.IndexOf(':');
                if (colonIdx > 0)
                {
                    return matched.Substring(0, colonIdx + 1) + " [redacted]";
                }

                var eqIdx = matched.IndexOf('=');
                if (eqIdx > 0)
                {
                    return matched.Substring(0, eqIdx + 1) + "[redacted]";
                }

                return "[redacted]";
            });
        }

        return result;
    }

    /// <summary>
    /// Redacts sensitive fields from a message template string.
    /// Used for console/debugger output where fields are interpolated.
    /// </summary>
    public static string RedactMessageTemplate(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return message ?? string.Empty;
        }

        return RedactStringContent(message);
    }
}
