using System;
using System.Collections.Generic;
using System.IO;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Core
{
    public class ModuleLoader : IModuleLoader
    {
        private readonly Dictionary<string, IObject> _cache = new Dictionary<string, IObject>();
        private readonly FenEnvironment _globalEnv;
        private readonly IExecutionContext _context;
        private readonly Func<Uri, string> _contentFetcher; // Helper for sync fetching (blocking)
        public bool ThrowOnEvaluationError { get; set; }

        public ModuleLoader(FenEnvironment globalEnv, IExecutionContext context, Func<Uri, string> contentFetcher = null)
        {
            _globalEnv = globalEnv;
            _context = context;
            _contentFetcher = contentFetcher ?? DefaultFileFetcher;
        }

        private static string DefaultFileFetcher(Uri uri)
        {
            if (uri.IsFile) return File.ReadAllText(uri.LocalPath);
            throw new NotSupportedException($"Default loader only supports file://. Requested: {uri}");
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

                if (Uri.TryCreate(baseUri, specifier, out var resolved))
                {
                    return resolved.AbsoluteUri;
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
            catch
            {
                return specifier;
            }
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
                            string packageJson = File.ReadAllText(packageJsonPath);
                            // Simple JSON parse for "main" field (avoiding full JSON parser dependency)
                            var mainMatch = System.Text.RegularExpressions.Regex.Match(packageJson, "\"main\"\\s*:\\s*\"([^\"]+)\"");
                            if (mainMatch.Success)
                            {
                                string mainFile = mainMatch.Groups[1].Value;
                                string fullPath = Path.Combine(nodeModulesPath, mainFile);
                                if (File.Exists(fullPath))
                                {
                                    return Path.GetFullPath(fullPath);
                                }
                            }
                        }
                        catch { }
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
                 throw new Exception($"Failed to load module '{path}': {ex.Message}", ex);
            }

            if (code  == null) throw new Exception($"Empty code for module: {path}");

            var lexer = new Lexer(code);
            var parser = new Parser(lexer, isModule: true);
            var program = parser.ParseProgram();

            if (parser.Errors.Count > 0)
            {
                throw new Exception($"Module parse error in '{path}': {string.Join(", ", parser.Errors)}");
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

                // Pass THIS loader to the interpreter so nested imports use it
                var interpreter = new Interpreter();

                try
                {
                    // Evaluate module
                    var evalResult = interpreter.Eval(program, moduleEnv, _context);
                    if (ThrowOnEvaluationError &&
                        evalResult != null &&
                        evalResult.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Error)
                    {
                        throw new Exception(evalResult.ToString());
                    }
                }
                finally
                {
                    // Restore previous module path
                    _context.CurrentModulePath = previousModulePath;
                }

                // Extract exports into pre-cached object
                var exports = interpreter.Exports;
                foreach(var kvp in exports)
                {
                    exportObj.Set(kvp.Key, kvp.Value);
                }

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
                throw new Exception($"Module parse error in '{pseudoPath}': {string.Join(", ", parser.Errors)}");
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

                // Pass THIS loader to the interpreter so nested imports use it
                var interpreter = new Interpreter();

                try
                {
                    // Evaluate module
                    var evalResult = interpreter.Eval(program, moduleEnv, _context);
                    if (ThrowOnEvaluationError &&
                        evalResult != null &&
                        evalResult.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Error)
                    {
                        throw new Exception(evalResult.ToString());
                    }
                }
                finally
                {
                    // Restore previous module path
                    _context.CurrentModulePath = previousModulePath;
                }

                // Extract exports into pre-cached object
                var exports = interpreter.Exports;
                foreach(var kvp in exports)
                {
                    exportObj.Set(kvp.Key, kvp.Value);
                }

                return exportObj;
            }
            catch
            {
                _cache.Remove(pseudoPath);
                throw;
            }
        }
    }
}
