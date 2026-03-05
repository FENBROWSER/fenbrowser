using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.Core
{
    public class ModuleLoader : IModuleLoader
    {
        private readonly Dictionary<string, IObject> _cache = new Dictionary<string, IObject>();
        private readonly FenEnvironment _globalEnv;
        private readonly IExecutionContext _context;
        private readonly Func<Uri, string> _contentFetcher; // Helper for sync fetching (blocking)
        private readonly Func<Uri, bool> _uriPolicy;
        private readonly Dictionary<string, string> _importMap = new Dictionary<string, string>(StringComparer.Ordinal);
        public bool ThrowOnEvaluationError { get; set; }

        public ModuleLoader(
            FenEnvironment globalEnv,
            IExecutionContext context,
            Func<Uri, string> contentFetcher = null,
            Func<Uri, bool> uriPolicy = null)
        {
            _globalEnv = globalEnv;
            _context = context;
            _contentFetcher = contentFetcher ?? DefaultFileFetcher;
            _uriPolicy = uriPolicy;
        }

        public void SetImportMap(IDictionary<string, string> imports, Uri baseUri = null)
        {
            _importMap.Clear();
            if (imports == null || imports.Count == 0)
            {
                return;
            }

            foreach (var entry in imports)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                {
                    continue;
                }

                var key = entry.Key.Trim();
                var mapped = entry.Value.Trim();

                if (Uri.TryCreate(mapped, UriKind.Absolute, out var absolute))
                {
                    _importMap[key] = absolute.AbsoluteUri;
                    continue;
                }

                if (baseUri != null && Uri.TryCreate(baseUri, mapped, out var resolved))
                {
                    _importMap[key] = resolved.AbsoluteUri;
                    continue;
                }

                _importMap[key] = mapped;
            }
        }

        private static string DefaultFileFetcher(Uri uri)
        {
            if (uri.IsFile) return File.ReadAllText(uri.LocalPath);
            throw new FenTypeError($"Module fetcher only supports file:// URIs. Requested: {uri}");
        }

        public string Resolve(string specifier, string referrer)
        {
            try 
            {
                Uri baseUri = null;
                if (!string.IsNullOrEmpty(referrer))
                {
                    Uri.TryCreate(referrer, UriKind.RelativeOrAbsolute, out baseUri);
                }
                
                // If referrer is relative (e.g. just a path in tests), treat as file relative to CWD
                if (baseUri != null && !baseUri.IsAbsoluteUri)
                {
                     baseUri = new Uri(new Uri(Path.GetFullPath(Environment.CurrentDirectory) + Path.DirectorySeparatorChar), referrer);
                }
                else if (baseUri  == null)
                {
                     baseUri = new Uri(Path.GetFullPath(Environment.CurrentDirectory) + Path.DirectorySeparatorChar);
                }

                if (TryResolveImportMap(baseUri, specifier, out var mapResolved))
                {
                    if (Uri.TryCreate(mapResolved, UriKind.Absolute, out var mapUri) && IsDisallowedModuleUri(mapUri, baseUri))
                    {
                        throw new UnauthorizedAccessException($"Module import-map resolution blocked: {mapResolved}");
                    }
                    return mapResolved;
                }

                if (Uri.TryCreate(baseUri, specifier, out var resolved))
                {
                    var normalized = NormalizeResolvedModuleUri(specifier, resolved);
                    if (IsDisallowedModuleUri(normalized, baseUri))
                    {
                        throw new UnauthorizedAccessException($"Module resolution blocked: {normalized}");
                    }
                    return normalized.AbsoluteUri;
                }

                // Handle bare module specifiers (e.g., "react", "lodash")
                // Look for node_modules resolution
                if (!specifier.StartsWith(".") && !specifier.StartsWith("/") && !specifier.Contains("://"))
                {
                    string resolvedPath = ResolveNodeModules(specifier, baseUri);
                    if (!string.IsNullOrEmpty(resolvedPath))
                    {
                        return new Uri(resolvedPath).AbsoluteUri;
                    }
                }

                return specifier; // Fallback
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch
            {
                return specifier;
            }
        }

        private bool TryResolveImportMap(Uri baseUri, string specifier, out string resolved)
        {
            resolved = null;
            if (_importMap.Count == 0 || string.IsNullOrWhiteSpace(specifier))
            {
                return false;
            }

            if (_importMap.TryGetValue(specifier, out var direct))
            {
                resolved = ResolveMappedValue(baseUri, direct);
                return !string.IsNullOrWhiteSpace(resolved);
            }

            var prefixMatch = _importMap.Keys
                .Where(k => k.EndsWith("/", StringComparison.Ordinal) && specifier.StartsWith(k, StringComparison.Ordinal))
                .OrderByDescending(k => k.Length)
                .FirstOrDefault();

            if (prefixMatch == null)
            {
                return false;
            }

            var mappedPrefix = _importMap[prefixMatch];
            var suffix = specifier.Substring(prefixMatch.Length);
            var combined = mappedPrefix.EndsWith("/", StringComparison.Ordinal) ? mappedPrefix + suffix : mappedPrefix + "/" + suffix;
            resolved = ResolveMappedValue(baseUri, combined);
            return !string.IsNullOrWhiteSpace(resolved);
        }

        private static string ResolveMappedValue(Uri baseUri, string mappedValue)
        {
            if (Uri.TryCreate(mappedValue, UriKind.Absolute, out var absolute))
            {
                return absolute.AbsoluteUri;
            }
            if (baseUri != null && Uri.TryCreate(baseUri, mappedValue, out var relative))
            {
                return relative.AbsoluteUri;
            }
            return mappedValue;
        }

        private static Uri NormalizeResolvedModuleUri(string originalSpecifier, Uri resolved)
        {
            if (resolved == null || !resolved.IsAbsoluteUri)
            {
                return resolved;
            }

            var scheme = resolved.Scheme?.ToLowerInvariant();
            if ((scheme != "http" && scheme != "https") ||
                string.IsNullOrWhiteSpace(originalSpecifier))
            {
                return resolved;
            }

            if (originalSpecifier.EndsWith("/", StringComparison.Ordinal))
            {
                return resolved;
            }

            var path = resolved.AbsolutePath;
            if (string.IsNullOrWhiteSpace(path) || Path.HasExtension(path))
            {
                return resolved;
            }

            var builder = new UriBuilder(resolved)
            {
                Path = path + ".js"
            };
            return builder.Uri;
        }

        private bool IsDisallowedModuleUri(Uri resolved, Uri referrer)
        {
            if (resolved == null || !resolved.IsAbsoluteUri)
            {
                return false;
            }

            if (_uriPolicy != null && !_uriPolicy(resolved))
            {
                return true;
            }

            var scheme = (resolved.Scheme ?? string.Empty).ToLowerInvariant();
            if (scheme != "http" && scheme != "https" && scheme != "file")
            {
                return true;
            }

            if ((scheme == "http" || scheme == "https") &&
                referrer != null &&
                referrer.IsAbsoluteUri &&
                (string.Equals(referrer.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(referrer.Scheme, "https", StringComparison.OrdinalIgnoreCase)) &&
                !CorsHandler.IsSameOrigin(resolved, referrer))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolve bare module specifiers using node_modules lookup algorithm
        /// </summary>
        private string ResolveNodeModules(string moduleName, Uri baseUri)
        {
            try
            {
                string startDir = baseUri.IsFile ? Path.GetDirectoryName(baseUri.LocalPath) : Environment.CurrentDirectory;
                string currentDir = startDir;

                // Walk up directory tree looking for node_modules
                while (!string.IsNullOrEmpty(currentDir))
                {
                    string nodeModulesPath = Path.Combine(currentDir, "node_modules", moduleName);

                    // Check for package.json with "main" field
                    string packageJsonPath = Path.Combine(nodeModulesPath, "package.json");
                    if (File.Exists(packageJsonPath))
                    {
                        try
                        {
                            // SECURITY: Reject oversized package.json to prevent memory DoS
                            var packageJsonInfo = new FileInfo(packageJsonPath);
                            if (packageJsonInfo.Length <= 10 * 1024 * 1024) // 10 MB limit
                            {
                                string packageJson = File.ReadAllText(packageJsonPath);
                                // Simple JSON parse for "main" field (avoiding full JSON parser dependency)
                                var mainMatch = System.Text.RegularExpressions.Regex.Match(packageJson, "\"main\"\\s*:\\s*\"([^\"]+)\"");
                                if (mainMatch.Success)
                                {
                                    string mainFile = mainMatch.Groups[1].Value;
                                    // SECURITY: Resolve to absolute path then verify it stays within the
                                    // package directory to prevent path traversal (e.g. "main": "../../../../etc/passwd")
                                    string fullPath = Path.GetFullPath(Path.Combine(nodeModulesPath, mainFile));
                                    string safeRoot = Path.GetFullPath(nodeModulesPath) + Path.DirectorySeparatorChar;
                                    if (fullPath.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
                                    {
                                        return fullPath;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            FenLogger.Warn($"[ModuleLoader] package.json parse/read failed at '{packageJsonPath}': {ex.Message}", LogCategory.JavaScript);
                        }
                    }

                    // Check for index.js
                    string indexPath = Path.Combine(nodeModulesPath, "index.js");
                    if (File.Exists(indexPath))
                    {
                        return Path.GetFullPath(indexPath);
                    }

                    // Check for module.js (direct file)
                    string directPath = nodeModulesPath + ".js";
                    if (File.Exists(directPath))
                    {
                        return Path.GetFullPath(directPath);
                    }

                    // Move up one directory
                    string parentDir = Path.GetDirectoryName(currentDir);
                    if (parentDir == currentDir) break; // Reached root
                    currentDir = parentDir;
                }

                return null; // Not found
            }
            catch
            {
                return null;
            }
        }

        public IObject LoadModule(string path)
        {
            if (_cache.TryGetValue(path, out var cached)) return cached;

            string code = null;
            try
            {
                if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
                {
                    if (IsDisallowedModuleUri(uri, _context?.CurrentModulePath != null && Uri.TryCreate(_context.CurrentModulePath, UriKind.Absolute, out var currentModuleUri) ? currentModuleUri : null))
                    {
                        throw new UnauthorizedAccessException($"Module load blocked by policy: {uri}");
                    }
                    if (_uriPolicy != null && !_uriPolicy(uri))
                    {
                        throw new UnauthorizedAccessException($"Module load blocked by policy: {uri}");
                    }
                    code = _contentFetcher(uri);
                }
                else
                {
                    // Fallback to file for non-URIs
                    if (File.Exists(path)) code = File.ReadAllText(path);
                    else throw new FileNotFoundException($"Module file not found: {path}");
                }
            }
            catch (Exception ex)
            {
                 throw new InvalidOperationException($"Failed to load module '{path}': {ex.Message}", ex);
            }

            if (code  == null) throw new InvalidOperationException($"Empty code for module: {path}");

            var lexer = new Lexer(code);
            var parser = new Parser(lexer, isModule: true);
            var program = parser.ParseProgram();

            if (parser.Errors.Count > 0)
            {
                throw new FenSyntaxError($"Module parse error in '{path}': {string.Join(", ", parser.Errors)}");
            }

            // Pre-cache an export object before evaluation so cyclic module graphs can resolve.
            var exportObj = new FenObject();
            _cache[path] = exportObj;

            try
            {
                // Create module environment
                var moduleEnv = new FenEnvironment(_globalEnv);

                // Set current module path for nested imports
                var previousModulePath = _context.CurrentModulePath;
                _context.CurrentModulePath = path;

                try
                {
                    PrepareModuleImports(program, moduleEnv, path);
                    ExecuteModuleBytecode(program, moduleEnv);
                }
                finally
                {
                    // Restore previous module path
                    _context.CurrentModulePath = previousModulePath;
                }

                CopyModuleExports(moduleEnv, exportObj);

                return exportObj;
            }
            catch
            {
                _cache.Remove(path);
                throw;
            }
        }
        public IObject LoadModuleSrc(string code, string pseudoPath)
        {
            if (_cache.TryGetValue(pseudoPath, out var cached)) return cached;

            var lexer = new Lexer(code);
            var parser = new Parser(lexer, isModule: true);
            var program = parser.ParseProgram();

            if (parser.Errors.Count > 0)
            {
                throw new FenSyntaxError($"Module parse error in '{pseudoPath}': {string.Join(", ", parser.Errors)}");
            }

            // Pre-cache an export object before evaluation so cyclic module graphs can resolve.
            var exportObj = new FenObject();
            _cache[pseudoPath] = exportObj;

            try
            {
                // Create module environment
                var moduleEnv = new FenEnvironment(_globalEnv);

                // Set current module path for nested imports
                var previousModulePath = _context.CurrentModulePath;
                _context.CurrentModulePath = pseudoPath;

                try
                {
                    PrepareModuleImports(program, moduleEnv, pseudoPath);
                    ExecuteModuleBytecode(program, moduleEnv);
                }
                finally
                {
                    // Restore previous module path
                    _context.CurrentModulePath = previousModulePath;
                }

                CopyModuleExports(moduleEnv, exportObj);

                return exportObj;
            }
            catch
            {
                _cache.Remove(pseudoPath);
                throw;
            }
        }

        private void PrepareModuleImports(Program program, FenEnvironment moduleEnv, string modulePath)
        {
            if (program?.Statements == null)
            {
                return;
            }

            foreach (var statement in program.Statements)
            {
                if (statement is ImportDeclaration importDecl)
                {
                    BindModuleNamespace(importDecl.Source, modulePath, moduleEnv);
                    continue;
                }

                if (statement is ExportDeclaration exportDecl && !string.IsNullOrEmpty(exportDecl.Source))
                {
                    BindModuleNamespace(exportDecl.Source, modulePath, moduleEnv);
                }
            }
        }

        private void BindModuleNamespace(string source, string modulePath, FenEnvironment moduleEnv)
        {
            if (string.IsNullOrEmpty(source) || moduleEnv == null)
            {
                return;
            }

            string resolvedPath = Resolve(source, modulePath ?? string.Empty);
            var exports = LoadModule(resolvedPath);
            moduleEnv.Set(GetModuleBindingName(source), FenValue.FromObject(exports));
        }

        private void ExecuteModuleBytecode(Program program, FenEnvironment moduleEnv)
        {
            var compiler = new Bytecode.Compiler.BytecodeCompiler();
            var codeBlock = compiler.Compile(program);
            var vm = new Bytecode.VM.VirtualMachine();
            var evalResult = vm.Execute(codeBlock, moduleEnv);

            if (ThrowOnEvaluationError &&
                evalResult.Type == Interfaces.ValueType.Error)
            {
                throw new FenInternalError($"Module evaluation failed: {evalResult}");
            }
        }

        private static void CopyModuleExports(FenEnvironment moduleEnv, FenObject exportObj)
        {
            if (moduleEnv == null || exportObj == null)
            {
                return;
            }

            const string exportPrefix = "__fen_export_";
            foreach (var kvp in moduleEnv.InspectVariables())
            {
                if (!kvp.Key.StartsWith(exportPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var exportName = kvp.Key.Substring(exportPrefix.Length);
                exportObj.Set(exportName, kvp.Value);
            }
        }

        private static string GetModuleBindingName(string source)
        {
            return "__fen_module_" + (source ?? string.Empty);
        }
    }
}




