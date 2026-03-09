using FenBrowser.Core.Dom.V2;
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
            if (node  == null) return Task.Delay(0);
            if (sourceUri != null)
            {
                var key = sourceUri.AbsoluteUri;
                return ExecuteModuleAsync(key, () => LoadModuleFromNetworkAsync(sourceUri, baseUri));
            }

            var inlineKey = "inline:" + Guid.NewGuid().ToString("n");
            var inlineSource = node.TextContent ?? string.Empty;
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

            if (module  == null || string.IsNullOrWhiteSpace(module.Source))
                return;

            var deps = CollectDependencies(module.Source);
            foreach (var dep in deps)
            {
                var resolved = ResolveModuleUri(module.ModuleUri ?? module.BaseUri, dep);
                if (resolved  == null) continue;
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
            if (uri  == null) return null;
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

            // Pre-join multi-line export { ... } blocks into single lines so the
            // per-line processor below can parse them in one pass.
            source = CollapseMultiLineNamedExports(source);

            var sb = new StringBuilder();
            bool defaultDeclared = false;

            using (var reader = new StringReader(source))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.TrimStart();

                    // ── import statements ──────────────────────────────────────────────
                    // Stripped; importers rely on exported names being in global scope.
                    if (trimmed.StartsWith("import ", StringComparison.Ordinal))
                    {
                        sb.AppendLine("// import stripped: " + trimmed);
                        continue;
                    }

                    // ── export default ────────────────────────────────────────────────
                    if (trimmed.StartsWith("export default ", StringComparison.Ordinal))
                    {
                        var tail = line.Substring(line.IndexOf("export default ", StringComparison.Ordinal) + "export default ".Length);
                        if (!defaultDeclared)
                        {
                            sb.AppendLine("var __module_default;");
                            defaultDeclared = true;
                        }
                        sb.AppendLine("__module_default = " + tail);
                        sb.AppendLine("try { if (typeof window !== 'undefined') window.__lastModuleDefault = __module_default; } catch(e){}");
                        continue;
                    }

                    // ── export * from '...' ───────────────────────────────────────────
                    // Cannot resolve without a full module graph; strip.
                    if (trimmed.StartsWith("export *", StringComparison.Ordinal))
                    {
                        sb.AppendLine("// re-export stripped: " + trimmed);
                        continue;
                    }

                    // ── export { a, b as c [, ...] } [from '...'] ────────────────────
                    // Re-exports (with 'from') cannot be resolved here; strip them.
                    // Named local exports: publish each binding onto window so that
                    // sibling modules whose import statements were stripped can find them
                    // as globals — matching how stripped `import { a }` resolves names.
                    if (trimmed.StartsWith("export {", StringComparison.Ordinal))
                    {
                        bool isReExport = trimmed.Contains(" from '") || trimmed.Contains(" from \"");
                        if (isReExport)
                        {
                            sb.AppendLine("// re-export stripped: " + trimmed);
                            continue;
                        }

                        EmitNamedExportBindings(trimmed, sb);
                        continue;
                    }

                    // ── export function / export class / export const / export let / export var ──
                    // Strip the `export` keyword; the declaration remains in scope and the
                    // name is subsequently registered on window by the generic emit below.
                    if (trimmed.StartsWith("export ", StringComparison.Ordinal))
                    {
                        var idx = line.IndexOf("export ", StringComparison.Ordinal);
                        if (idx >= 0) line = line.Remove(idx, "export ".Length);
                        trimmed = line.TrimStart();

                        // After stripping 'export', emit the declaration then register the name
                        // on window for cross-module visibility.
                        sb.AppendLine(line);
                        EmitInlineExportBinding(trimmed, sb);
                        continue;
                    }

                    sb.AppendLine(line);
                }
            }

            return sb.ToString();
        }

        // Emits window assignments for `export { a, b as c }` bindings.
        private static void EmitNamedExportBindings(string trimmed, StringBuilder sb)
        {
            var braceOpen = trimmed.IndexOf('{');
            var braceClose = trimmed.IndexOf('}');
            if (braceOpen < 0 || braceClose <= braceOpen)
            {
                sb.AppendLine("// export stripped (unparseable): " + trimmed);
                return;
            }

            var inner = trimmed.Substring(braceOpen + 1, braceClose - braceOpen - 1);
            sb.AppendLine("// named export → global bindings:");
            foreach (var binding in inner.Split(','))
            {
                var parts = binding.Trim().Split(new[] { " as " }, 2, StringSplitOptions.None);
                var localName   = parts[0].Trim();
                var exportedName = parts.Length > 1 ? parts[1].Trim() : localName;
                if (string.IsNullOrEmpty(localName)) continue;
                // ECMA-262 §16.2.3 ExportClause: publish localName as exportedName in global scope.
                sb.AppendLine($"try {{ if (typeof window !== 'undefined' && typeof {localName} !== 'undefined') window['{exportedName}'] = {localName}; }} catch(e) {{}}");
            }
        }

        // For `export function foo`, `export const x = ...`, etc. — after stripping `export`,
        // register the declared name on window for cross-module visibility.
        private static void EmitInlineExportBinding(string declLine, StringBuilder sb)
        {
            string name = null;
            if (declLine.StartsWith("function ", StringComparison.Ordinal) ||
                declLine.StartsWith("async function ", StringComparison.Ordinal) ||
                declLine.StartsWith("function* ", StringComparison.Ordinal))
            {
                // function foo(...) { → extract "foo"
                var afterKeyword = declLine.IndexOf("function", StringComparison.Ordinal) + "function".Length;
                if (declLine[afterKeyword] == '*') afterKeyword++; // generator
                name = ExtractIdentifier(declLine, afterKeyword);
            }
            else if (declLine.StartsWith("class ", StringComparison.Ordinal))
            {
                name = ExtractIdentifier(declLine, "class ".Length);
            }
            // const/let/var: registration via `export { name }` pattern is emitted separately
            // when the user writes `export const x = …`; the declaration itself is already global.

            if (!string.IsNullOrEmpty(name))
                sb.AppendLine($"try {{ if (typeof window !== 'undefined' && typeof {name} !== 'undefined') window['{name}'] = {name}; }} catch(e) {{}}");
        }

        private static string ExtractIdentifier(string s, int startIndex)
        {
            while (startIndex < s.Length && (s[startIndex] == ' ' || s[startIndex] == '\t'))
                startIndex++;
            int end = startIndex;
            while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] == '_' || s[end] == '$'))
                end++;
            return end > startIndex ? s.Substring(startIndex, end - startIndex) : null;
        }

        // Collapses multi-line `export {\n  a,\n  b\n}` into a single line so the
        // per-line processor can handle it.  Only collapses export-brace blocks.
        private static string CollapseMultiLineNamedExports(string source)
        {
            // Fast path: no multi-line export block
            if (!System.Text.RegularExpressions.Regex.IsMatch(source, @"export\s*\{[^}]*\n", System.Text.RegularExpressions.RegexOptions.None))
                return source;

            var result = new StringBuilder(source.Length);
            bool inExportBrace = false;
            foreach (char ch in source)
            {
                if (!inExportBrace)
                {
                    result.Append(ch);
                    // Detect start of export { by checking the last few chars
                    if (ch == '{')
                    {
                        var s = result.ToString();
                        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"export\s*\{$"))
                            inExportBrace = true;
                    }
                }
                else
                {
                    if (ch == '\n' || ch == '\r') result.Append(' ');
                    else result.Append(ch);
                    if (ch == '}') inExportBrace = false;
                }
            }
            return result.ToString();
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



