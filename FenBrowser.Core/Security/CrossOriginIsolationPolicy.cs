#nullable enable
using System;
using System.Threading;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Security;

/// <summary>
/// Cross-origin isolation policy per W3C HTML spec §13.1.6/§13.1.7 and
/// Fetch spec. Parses Cross-Origin-Opener-Policy and Cross-Origin-Embedder-Policy
/// headers and determines whether a document is cross-origin isolated.
///
/// Cross-origin isolation is the gate for SharedArrayBuffer, Atomics.wait,
/// and performance.measureUserAgentSpecificMemory().
/// </summary>
public sealed class CrossOriginIsolationPolicy
{
    /// <summary>
    /// COOP values as defined by HTML §13.1.6.
    /// </summary>
    public enum CoopValue
    {
        UnsafeNone,
        SameOrigin,
        SameOriginAllowPopups,
        SameOriginPlusCoep
    }

    /// <summary>
    /// COEP values as defined by HTML §13.1.7.
    /// </summary>
    public enum CoepValue
    {
        UnsafeNone,
        RequireCorp,
        Credentialless
    }

    /// <summary>
    /// CORP values as defined by Fetch §4.6.
    /// </summary>
    public enum CorpValue
    {
        None,
        SameOrigin,
        SameSite,
        CrossOrigin
    }

    public CoopValue OpenerPolicy { get; private set; } = CoopValue.UnsafeNone;
    public CoepValue EmbedderPolicy { get; private set; } = CoepValue.UnsafeNone;

    /// <summary>
    /// True when the document is cross-origin isolated (COOP same-origin + COEP require-corp
    /// or credentialless). This gates SharedArrayBuffer and Atomics.wait.
    /// </summary>
    public bool IsCrossOriginIsolated =>
        OpenerPolicy is CoopValue.SameOrigin or CoopValue.SameOriginPlusCoep &&
        EmbedderPolicy is CoepValue.RequireCorp or CoepValue.Credentialless;

    /// <summary>
    /// True when the current implementation requires a CORP opt-in for cross-origin
    /// no-cors fetches. Credentialless remains conservative here until request-side
    /// credential stripping is enforced by the fetch pipeline.
    /// </summary>
    public bool RequiresCorp => EmbedderPolicy is CoepValue.RequireCorp or CoepValue.Credentialless;

    /// <summary>
    /// Parses the COOP header. Invalid, empty, or duplicated policy values fail closed
    /// to unsafe-none instead of retaining policy from a previous parse/navigation.
    /// </summary>
    public void ParseCoopHeader(string? headerValue)
    {
        OpenerPolicy = CoopValue.UnsafeNone;
        if (!TryGetSinglePolicyToken(headerValue, out var token))
            return;

        OpenerPolicy = token switch
        {
            "same-origin" => CoopValue.SameOrigin,
            "same-origin-allow-popups" => CoopValue.SameOriginAllowPopups,
            "same-origin-plus-coep" => CoopValue.SameOriginPlusCoep,
            "unsafe-none" => CoopValue.UnsafeNone,
            _ => CoopValue.UnsafeNone
        };
    }

    /// <summary>
    /// Parses the COEP header. Invalid, empty, or duplicated policy values fail closed
    /// to unsafe-none instead of retaining policy from a previous parse/navigation.
    /// </summary>
    public void ParseCoepHeader(string? headerValue)
    {
        EmbedderPolicy = CoepValue.UnsafeNone;
        if (!TryGetSinglePolicyToken(headerValue, out var token))
            return;

        EmbedderPolicy = token switch
        {
            "require-corp" => CoepValue.RequireCorp,
            "credentialless" => CoepValue.Credentialless,
            "unsafe-none" => CoepValue.UnsafeNone,
            _ => CoepValue.UnsafeNone
        };
    }

    /// <summary>
    /// Checks whether a response can be embedded by this document under COEP.
    /// </summary>
    /// <param name="requestIsSameOrigin">True when the request was same-origin.</param>
    /// <param name="corpHeaderValue">The response's Cross-Origin-Resource-Policy header.</param>
    /// <param name="requestIsNoCors">True when the request mode is no-cors (script, img, style, etc.).</param>
    /// <returns>True when the response may be embedded.</returns>
    public bool IsResponseEmbeddable(
        bool requestIsSameOrigin,
        string? corpHeaderValue,
        bool requestIsNoCors)
    {
        if (!RequiresCorp)
            return true;

        // Same-origin responses are always embeddable under COEP.
        if (requestIsSameOrigin)
            return true;

        // no-cors requests require CORP when cross-origin.
        if (requestIsNoCors)
        {
            var corp = ParseCorpHeader(corpHeaderValue);
            return corp switch
            {
                CorpValue.CrossOrigin => true,
                _ => false
            };
        }

        // CORS-mode requests are governed by CORS, not CORP.
        return true;
    }

    /// <summary>
    /// Parses a Cross-Origin-Resource-Policy header value.
    /// </summary>
    public static CorpValue ParseCorpHeader(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return CorpValue.None;

        return headerValue.Trim().ToLowerInvariant() switch
        {
            "same-origin" => CorpValue.SameOrigin,
            "same-site" => CorpValue.SameSite,
            "cross-origin" => CorpValue.CrossOrigin,
            _ => CorpValue.None
        };
    }

    /// <summary>
    /// Logs the resulting isolation state without persisting path/query/fragment data.
    /// </summary>
    public void LogState(Uri documentUri)
    {
        EngineLogCompat.Info(
            $"[CrossOriginIsolation] {GetSafeOriginLabel(documentUri)}: COOP={OpenerPolicy} COEP={EmbedderPolicy} isolated={IsCrossOriginIsolated}",
            LogCategory.Security);
    }

    private static bool TryGetSinglePolicyToken(string? headerValue, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(headerValue))
            return false;

        // COOP/COEP are single policy values. Multiple comma-separated members are
        // ambiguous/invalid and must not accidentally enable isolation. Ignore commas
        // inside a quoted reporting parameter while checking for duplicate members.
        bool inQuote = false;
        char quote = '\0';
        for (int i = 0; i < headerValue.Length; i++)
        {
            char ch = headerValue[i];
            if (inQuote)
            {
                if (ch == '\\' && i + 1 < headerValue.Length)
                {
                    i++;
                    continue;
                }

                if (ch == quote)
                {
                    inQuote = false;
                    quote = '\0';
                }
                continue;
            }

            if (ch is '\'' or '"')
            {
                inQuote = true;
                quote = ch;
                continue;
            }

            if (ch == ',')
                return false;
        }

        if (inQuote)
            return false;

        var value = headerValue.Trim();
        int parameterIndex = value.IndexOf(';');
        if (parameterIndex >= 0)
            value = value.Substring(0, parameterIndex).Trim();

        if (value.Length == 0)
            return false;

        token = value.ToLowerInvariant();
        return true;
    }

    private static string GetSafeOriginLabel(Uri? documentUri)
    {
        if (documentUri == null || !documentUri.IsAbsoluteUri)
            return "<opaque-or-relative>";

        try
        {
            var builder = new UriBuilder(
                documentUri.Scheme,
                documentUri.Host,
                documentUri.IsDefaultPort ? -1 : documentUri.Port);
            return builder.Uri.GetLeftPart(UriPartial.Authority);
        }
        catch
        {
            return documentUri.Scheme + "://<invalid-host>";
        }
    }
}

/// <summary>
/// Ambient cross-origin isolation state for the current document.
/// The navigation/network layer sets this when a document's COOP/COEP
/// headers are parsed; the scripting layer reads it to expose
/// crossOriginIsolated and gate SharedArrayBuffer availability.
/// </summary>
