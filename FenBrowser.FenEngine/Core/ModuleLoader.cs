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
                
                return specifier; // Fallback (e.g. bare module specifier like "react")
            }
            catch
            {
                return specifier; 
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
            var parser = new Parser(lexer);
            var program = parser.ParseProgram();

            if (parser.Errors.Count > 0)
            {
                throw new Exception($"Module parse error in '{path}': {string.Join(", ", parser.Errors)}");
            }

            // Create module environment
            var moduleEnv = new FenEnvironment(_globalEnv);
            
            // Pass THIS loader to the interpreter so nested imports use it
            var interpreter = new Interpreter(); 
            
            // Evaluate module
            interpreter.Eval(program, moduleEnv, _context);

            // Extract exports
            var exports = interpreter.Exports;
            var exportObj = new FenObject();
            foreach(var kvp in exports)
            {
                exportObj.Set(kvp.Key, kvp.Value);
            }
            
            _cache[path] = exportObj;
            return exportObj;
        }
        public IObject LoadModuleSrc(string code, string pseudoPath)
        {
            if (_cache.TryGetValue(pseudoPath, out var cached)) return cached;

            var lexer = new Lexer(code);
            var parser = new Parser(lexer);
            var program = parser.ParseProgram();

            if (parser.Errors.Count > 0)
            {
                throw new Exception($"Module parse error in '{pseudoPath}': {string.Join(", ", parser.Errors)}");
            }

            // Create module environment
            var moduleEnv = new FenEnvironment(_globalEnv);
            
            // Pass THIS loader to the interpreter so nested imports use it
            var interpreter = new Interpreter(); 
            
            // Evaluate module
            interpreter.Eval(program, moduleEnv, _context);

            // Extract exports
            var exports = interpreter.Exports;
            var exportObj = new FenObject();
            foreach(var kvp in exports)
            {
                exportObj.Set(kvp.Key, kvp.Value);
            }
            
            _cache[pseudoPath] = exportObj;
            return exportObj;
        }
    }
}
