using System.Collections.Generic;

namespace FenBrowser.FenEngine.Core.Interfaces
{
    public interface IModuleLoader
    {
        /// <summary>
        /// Resolves a module specifier to an absolute path or unique ID.
        /// </summary>
        string Resolve(string specifier, string referrer);

        /// <summary>
        /// Loads and evaluates a module, returning its exports.
        /// </summary>
        IObject LoadModule(string path);
        
        /// <summary>
        /// Loads and evaluates a module from source code.
        /// </summary>
        IObject LoadModuleSrc(string code, string pseudoPath);
    }
}
