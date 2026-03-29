using System;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Explicit runtime options for <see cref="BrowserHost"/>.
    /// Tooling integrations must flow through this object instead of embedding
    /// harness behavior directly in the product runtime.
    /// </summary>
    public sealed class BrowserHostOptions
    {
        public static BrowserHostOptions Default { get; } = new BrowserHostOptions();

        /// <summary>
        /// Optional URI remapper used by external tooling to redirect requests
        /// (for example WPT resource remapping in headless tooling).
        /// Product runtime paths should leave this null.
        /// </summary>
        public Func<Uri, Uri> RequestUriMapper { get; init; }

        /// <summary>
        /// Optional script override provider used by external tooling to replace
        /// specific fetched scripts without hard-coding harness logic in runtime.
        /// Product runtime paths should leave this null.
        /// </summary>
        public Func<Uri, string> ScriptOverrideProvider { get; init; }

        public Uri MapRequestUri(Uri requestUri)
        {
            if (requestUri == null)
            {
                return null;
            }

            return RequestUriMapper?.Invoke(requestUri) ?? requestUri;
        }

        public bool TryGetScriptOverride(Uri requestUri, out string scriptContent)
        {
            scriptContent = ScriptOverrideProvider?.Invoke(requestUri);
            return !string.IsNullOrWhiteSpace(scriptContent);
        }
    }
}
