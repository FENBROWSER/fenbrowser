using FenBrowser.Core.Dom;
using FenBrowser.Core;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FenBrowser.FenEngine.Scripting
{
    internal sealed class ModuleLoader
    {
        private readonly JavaScriptEngine _engine;
        private readonly ConcurrentDictionary<string, Task> _moduleTasks = new ConcurrentDictionary<string, Task>(StringComparer.Ordinal);
        private readonly Regex _importRegex = new Regex("^\\s*import\\s+(?:[^\\'\";]+?\\s+from\\s+)?['\"](?<spec>[^'\"]+)['\"]", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private readonly Regex _sideEffectImportRegex = new Regex("^\\s*import\\s+['\"](?<spec>[^'\"]+)['\"]", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        public ModuleLoader(JavaScriptEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public Task ExecuteModuleTagAsync(Element node, Uri sourceUri, Uri baseUri)
        {
            if (node == null) return Task.Delay(0);
            if (sourceUri != null)
            {
                var key = sourceUri.AbsoluteUri;
                return ExecuteModuleAsync(key, () => LoadModuleFromNetworkAsync(sourceUri, baseUri));
            }

            var inlineKey = "inline:" + Guid.NewGuid().ToString("n");
            var inlineSource = node.Text ?? string.Empty;
            return ExecuteModuleAsync(inlineKey, () => Task.FromResult(new ModuleCode
            {
                Key = inlineKey,
                ModuleUri = baseUri,
                BaseUri = baseUri,
                Source = inlineSource
            }));
        }

        private Task ExecuteModuleAsync(string key, Func<Task<ModuleCode>> provider)
        {
            if (string.IsNullOrWhiteSpace(key)) return Task.Delay(0);
            return _moduleTasks.GetOrAdd(key, _ => LoadAndExecuteAsync(provider));
        }

        private async Task LoadAndExecuteAsync(Func<Task<ModuleCode>> provider)
        {
            ModuleCode module;
            try
            {
                module = await provider().ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (module == null || string.IsNullOrWhiteSpace(module.Source))
                return;

            var deps = CollectDependencies(module.Source);
            foreach (var dep in deps)
            {
                var resolved = ResolveModuleUri(module.ModuleUri ?? module.BaseUri, dep);
                if (resolved == null) continue;
                var depKey = resolved.AbsoluteUri;
                await ExecuteModuleAsync(depKey, () => LoadModuleFromNetworkAsync(resolved, module.ModuleUri ?? module.BaseUri)).ConfigureAwait(false);
            }

            var transpiled = TranspileModule(module.Source);
            if (string.IsNullOrWhiteSpace(transpiled)) return;

            try
            {
                var ctx = new JsContext { BaseUri = module.ModuleUri ?? module.BaseUri };
                _engine.ExecuteScriptBlock(transpiled, ctx?.BaseUri?.ToString());
            }
            catch
            {
                // Ignore execution failures to keep parity with existing best-effort runner
            }
        }

        private async Task<ModuleCode> LoadModuleFromNetworkAsync(Uri uri, Uri referer)
        {
            if (uri == null) return null;
            string source = null;
            try
            {
                source = await _engine.FetchModuleTextAsync(uri, referer).ConfigureAwait(false);
            }
            catch
            {
                source = null;
            }

            return new ModuleCode
            {
                Key = uri.AbsoluteUri,
                ModuleUri = uri,
                BaseUri = referer ?? uri,
                Source = source ?? string.Empty
            };
        }

        private IEnumerable<string> CollectDependencies(string source)
        {
            if (string.IsNullOrEmpty(source)) yield break;

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match match in _importRegex.Matches(source))
            {
                var spec = match.Groups["spec"].Value;
                if (!IsRelativeModule(spec)) continue;
                if (seen.Add(spec)) yield return spec;
            }

            foreach (Match match in _sideEffectImportRegex.Matches(source))
            {
                var spec = match.Groups["spec"].Value;
                if (!IsRelativeModule(spec)) continue;
                if (seen.Add(spec)) yield return spec;
            }
        }

        private static bool IsRelativeModule(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return false;
            if (spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal) || spec.StartsWith("/", StringComparison.Ordinal))
                return true;
            return spec.Contains(".") || spec.Contains("/");
        }

        private static Uri ResolveModuleUri(Uri baseUri, string specifier)
        {
            if (string.IsNullOrWhiteSpace(specifier)) return null;
            try
            {
                Uri abs;
                if (Uri.TryCreate(specifier, UriKind.Absolute, out abs)) return abs;
                if (baseUri != null && Uri.TryCreate(baseUri, specifier, out abs)) return abs;
            }
            catch
            {
                return null;
            }
            return null;
        }

        private string TranspileModule(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return string.Empty;

            var sb = new StringBuilder();
            bool defaultDeclared = false;

            using (var reader = new StringReader(source))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("import ", StringComparison.Ordinal))
                    {
                        sb.AppendLine("// import stripped: " + trimmed);
                        continue;
                    }

                    if (trimmed.StartsWith("export default ", StringComparison.Ordinal))
                    {
                        var tail = line.Substring(line.IndexOf("export default ", StringComparison.Ordinal) + "export default ".Length);
                        if (!defaultDeclared)
                        {
                            sb.AppendLine("var __module_default;");
                            defaultDeclared = true;
                        }
                        sb.AppendLine("__module_default = " + tail);
                        sb.AppendLine("try { if (typeof window !== 'undefined') window.__lastModuleDefault = __module_default; } catch { }");
                        continue;
                    }

                    if (trimmed.StartsWith("export {", StringComparison.Ordinal) || trimmed.StartsWith("export *", StringComparison.Ordinal))
                    {
                        sb.AppendLine("// export stripped: " + trimmed);
                        continue;
                    }

                    if (trimmed.StartsWith("export ", StringComparison.Ordinal))
                    {
                        var idx = line.IndexOf("export ", StringComparison.Ordinal);
                        if (idx >= 0) line = line.Remove(idx, "export ".Length);
                    }

                    sb.AppendLine(line);
                }
            }

            return sb.ToString();
        }

        private sealed class ModuleCode
        {
            public string Key;
            public Uri ModuleUri;
            public Uri BaseUri;
            public string Source;
        }
    }
}

