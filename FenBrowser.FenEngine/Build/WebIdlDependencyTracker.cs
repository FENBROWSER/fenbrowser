using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace FenBrowser.FenEngine.Build
{
    /// <summary>
    /// Dependency tracker for WebIDL generation to avoid unnecessary rebuilds.
    /// Production-grade build optimization that prevents redundant WebIDL generation
    /// when IDL files haven't changed. This significantly speeds up build times.
    /// </summary>
    public class WebIdlDependencyTracker
    {
        private readonly string _idlDirectory;
        private readonly string _outputDirectory;
        private readonly string _cacheFile;
        
        public WebIdlDependencyTracker(string idlDirectory, string outputDirectory)
        {
            _idlDirectory = idlDirectory ?? throw new ArgumentNullException(nameof(idlDirectory));
            _outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
            _cacheFile = Path.Combine(outputDirectory, ".idl-cache");
        }
        
        /// <summary>
        /// Check if IDL files have changed since last generation.
        /// Returns true if bindings need regeneration.
        /// </summary>
        public bool HasChanges()
        {
            try
            {
                if (!Directory.Exists(_idlDirectory))
                    return false;
                
                var idlFiles = Directory.GetFiles(_idlDirectory, "*.idl", SearchOption.AllDirectories);
                if (idlFiles.Length == 0)
                    return false;
                
                if (!File.Exists(_cacheFile))
                    return true; // No cache = first run, needs generation
                
                var cacheData = File.ReadAllText(_cacheFile);
                var currentHash = ComputeDependencyHash(idlFiles);
                
                return currentHash != cacheData;
            }
            catch (Exception ex)
            {
                // If anything goes wrong with dependency checking, err on the side of regenerating
                Console.WriteLine($"[WebIdlDependencyTracker] Error checking dependencies: {ex.Message}");
                return true;
            }
        }
        
        /// <summary>
        /// Update dependency cache after successful generation
        /// </summary>
        public void UpdateCache()
        {
            try
            {
                Directory.CreateDirectory(_outputDirectory);
                
                var idlFiles = Directory.GetFiles(_idlDirectory, "*.idl", SearchOption.AllDirectories);
                var hash = ComputeDependencyHash(idlFiles);
                
                File.WriteAllText(_cacheFile, hash);
            }
            catch (Exception ex)
            {
                // Cache update failure is non-fatal
                Console.WriteLine($"[WebIdlDependencyTracker] Error updating cache: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Clear the dependency cache (force regeneration next build)
        /// </summary>
        public void ClearCache()
        {
            try
            {
                if (File.Exists(_cacheFile))
                    File.Delete(_cacheFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebIdlDependencyTracker] Error clearing cache: {ex.Message}");
            }
        }
        
        private string ComputeDependencyHash(string[] idlFiles)
        {
            using var sha256 = SHA256.Create();
            
            // Sort files for deterministic hash
            var sortedFiles = idlFiles.OrderBy(f => f).ToArray();
            
            foreach (var file in sortedFiles)
            {
                var content = File.ReadAllBytes(file);
                sha256.TransformBlock(content, 0, content.Length, null, 0);
            }
            
            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var hash = sha256.Hash;
            
            return Convert.ToBase64String(hash);
        }
    }
}
