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

        public ModuleLoader(FenEnvironment globalEnv, IExecutionContext context)
        {
            _globalEnv = globalEnv;
            _context = context;
        }

        public string Resolve(string specifier, string referrer)
        {
            // Basic resolution
            try 
            {
                if (specifier.StartsWith("./") || specifier.StartsWith("../"))
                {
                    // Resolve relative to referrer (if referrer is a path)
                    // For now, assume referrer is CWD or ignore
                    return Path.GetFullPath(specifier);
                }
                return Path.GetFullPath(specifier);
            }
            catch
            {
                return specifier; // Fallback
            }
        }

        public IObject LoadModule(string path)
        {
            if (_cache.TryGetValue(path, out var cached)) return cached;

            if (!File.Exists(path)) throw new FileNotFoundException($"Module not found: {path}");

            string code = File.ReadAllText(path);
            var lexer = new Lexer(code);
            var parser = new Parser(lexer);
            var program = parser.ParseProgram();

            if (parser.Errors.Count > 0)
            {
                throw new Exception($"Module parse error: {string.Join(", ", parser.Errors)}");
            }

            // Create module environment
            var moduleEnv = new FenEnvironment(_globalEnv);
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
    }
}
