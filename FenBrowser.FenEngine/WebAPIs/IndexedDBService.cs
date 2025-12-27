using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FenBrowser.FenEngine.WebAPIs
{
    [Obsolete("Use IStorageBackend instead.")]
    public class IndexedDBService
    {
        public static IndexedDBService Instance => new IndexedDBService();
        
        // Keep public signatures just in case, but throw
        public Task<object> OpenDatabase(string origin, string name, int version) => throw new NotImplementedException();
    }
}
