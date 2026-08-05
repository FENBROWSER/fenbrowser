using System;
using System.Collections.Generic;
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
    /// True when COEP requires CORP for cross-origin no-cors fetches.
    /// </summary>
    public bool RequiresCorp => EmbedderPolicy is CoepValue.RequireCorp or CoepValue.Credentialless;

    /// <summary>
    /// Parses the COOP header.
    /// </summary>
    public void ParseCoopHeader(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            OpenerPolicy = CoopValue.UnsafeNone;
            return;
        }

        var tokens = SplitHeader(headerValue);
        foreach (var token in tokens)
        {
            OpenerPolicy = token.ToLowerInvariant() switch
            {
                "same-origin" => CoopValue.SameOrigin,
                "same-origin-allow-popups" => CoopValue.SameOriginAllowPopups,
                "same-origin-plus-coep" => CoopValue.SameOriginPlusCoep,
                "unsafe-none" => CoopValue.UnsafeNone,
                _ => OpenerPolicy
            };
        }
    }

    /// <summary>
    /// Parses the COEP header.
    /// </summary>
    public void ParseCoepHeader(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            EmbedderPolicy = CoepValue.UnsafeNone;
            return;
        }

        var tokens = SplitHeader(headerValue);
        foreach (var token in tokens)
        {
            EmbedderPolicy = token.ToLowerInvariant() switch
            {
                "require-corp" => CoepValue.RequireCorp,
                "credentialless" => CoepValue.Credentialless,
                "unsafe-none" => CoepValue.UnsafeNone,
                _ => EmbedderPolicy
            };
        }
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
    /// Logs the resulting isolation state.
    /// </summary>
    public void LogState(Uri documentUri)
    {
        EngineLogCompat.Info(
            $"[CrossOriginIsolation] {documentUri}: COOP={OpenerPolicy} COEP={EmbedderPolicy} isolated={IsCrossOriginIsolated}",
            LogCategory.Security);
    }

    private static string[] SplitHeader(string headerValue)
    {
        return headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>
/// Ambient cross-origin isolation state for the current document.
/// The navigation/network layer sets this when a document's COOP/COEP
/// headers are parsed; the scripting layer reads it to expose
/// crossOriginIsolated and gate SharedArrayBuffer availability.
/// </summary>
public static class CrossOriginIsolationState
{
    private static readonly AsyncLocal<CrossOriginIsolationPolicy?> _current = new();

    /// <summary>
    /// Gets the isolation policy for the current document, or null when no
    /// document has set one yet.
    /// </summary>
    public static CrossOriginIsolationPolicy? Current => _current.Value;

    /// <summary>
    /// Sets the isolation policy for the current document context.
    /// </summary>
    public static void Set(CrossOriginIsolationPolicy? policy)
    {
        _current.Value = policy;
    }

    /// <summary>
    /// True when the current document context is cross-origin isolated.
    /// </summary>
    public static bool IsCrossOriginIsolated => _current.Value?.IsCrossOriginIsolated ?? false;

    /// <summary>
    /// Resets the ambient state (document teardown).
    /// </summary>
    public static void Reset()
    {
        _current.Value = null;
    }
}