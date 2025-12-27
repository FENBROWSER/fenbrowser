using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Storage;
using FenBrowser.FenEngine.WebAPIs;
using FenBrowser.FenEngine.Workers;
using Xunit;

namespace FenBrowser.Tests.WebAPIs
{
    public class ServiceWorkerCacheTests : IDisposable
    {
        private readonly InMemoryStorageBackend _storage;
        private readonly CacheStorage _cacheStorage;
        private readonly string _origin = "https://example.com";

        public ServiceWorkerCacheTests()
        {
            _storage = new InMemoryStorageBackend();
            _cacheStorage = new CacheStorage(() => _origin, _storage);
        }

        public void Dispose()
        {
            _storage.Clear();
        }

        [Fact]
        public async Task CacheStorage_Open_CreatesNewCache()
        {
            var openPromise = _cacheStorage.Get("open").AsFunction().Invoke(new IValue[] { FenValue.FromString("v1") }, null).AsObject() as FenObject;
            var cache = await AwaitPromise(openPromise);

            Assert.NotNull(cache);
            Assert.True(((FenValue)cache).IsObject);
            Assert.True(cache.AsObject() is Cache);

            // Verify persistence
            var hasPromise = _cacheStorage.Get("has").AsFunction().Invoke(new IValue[] { FenValue.FromString("v1") }, null).AsObject() as FenObject;
            var hasResult = await AwaitPromise(hasPromise);
            Assert.True(((FenValue)hasResult).AsBoolean());
        }

        [Fact]
        public async Task Cache_Put_StoresResponse()
        {
            // Open cache
            var openPromise = _cacheStorage.Get("open").AsFunction().Invoke(new IValue[] { FenValue.FromString("v1") }, null).AsObject() as FenObject;
            var cacheObj = (await AwaitPromise(openPromise)).AsObject();
            var cache = cacheObj as Cache;

            // Create Request/Response mocks
            var request = FenValue.FromString("https://example.com/style.css");
            var response = new FenObject();
            response.Set("status", FenValue.FromNumber(200));
            response.Set("statusText", FenValue.FromString("OK"));
            response.Set("ok", FenValue.FromBoolean(true));
            
            // Put
            var putPromise = cache.Get("put").AsFunction().Invoke(new IValue[] { request, FenValue.FromObject(response) }, null).AsObject() as FenObject;
            await AwaitPromise(putPromise);

            // Match
            var matchPromise = cache.Get("match").AsFunction().Invoke(new IValue[] { request }, null).AsObject() as FenObject;
            var matchedResponse = (await AwaitPromise(matchPromise)).AsObject();

            Assert.NotNull(matchedResponse);
            Assert.Equal(200, ((FenValue)matchedResponse.Get("status")).AsNumber());
            Assert.Equal("OK", ((FenValue)matchedResponse.Get("statusText")).AsString());
        }

        [Fact]
        public async Task Cache_Delete_RemovesEntry()
        {
            var openPromise = _cacheStorage.Get("open").AsFunction().Invoke(new IValue[] { FenValue.FromString("v1") }, null).AsObject() as FenObject;
            var cacheObj = (await AwaitPromise(openPromise)).AsObject();
            var cache = cacheObj as Cache;

            var request = FenValue.FromString("https://example.com/script.js");
            var response = new FenObject();
            response.Set("status", FenValue.FromNumber(200));

            // Put
            await AwaitPromise(cache.Get("put").AsFunction().Invoke(new IValue[] { request, FenValue.FromObject(response) }, null).AsObject() as FenObject);

            // Delete
            var deletePromise = cache.Get("delete").AsFunction().Invoke(new IValue[] { request }, null).AsObject() as FenObject;
            var deleted = await AwaitPromise(deletePromise);
            Assert.True(((FenValue)deleted).AsBoolean());

            // Match -> undefined
            var matchPromise = cache.Get("match").AsFunction().Invoke(new IValue[] { request }, null).AsObject() as FenObject;
            var result = await AwaitPromise(matchPromise);
            Assert.True(((FenValue)result).IsUndefined);
        }

        [Fact]
        public async Task CacheStorage_Delete_RemovesCache()
        {
            // Create cache
            await AwaitPromise(_cacheStorage.Get("open").AsFunction().Invoke(new IValue[] { FenValue.FromString("delete-me") }, null).AsObject() as FenObject);

            // Verify exists
            var hasResult = await AwaitPromise(_cacheStorage.Get("has").AsFunction().Invoke(new IValue[] { FenValue.FromString("delete-me") }, null).AsObject() as FenObject);
            Assert.True(((FenValue)hasResult).AsBoolean());

            // Delete
            var deleteResult = await AwaitPromise(_cacheStorage.Get("delete").AsFunction().Invoke(new IValue[] { FenValue.FromString("delete-me") }, null).AsObject() as FenObject);
            Assert.True(((FenValue)deleteResult).AsBoolean());

            // Verify gone
            var hasResult2 = await AwaitPromise(_cacheStorage.Get("has").AsFunction().Invoke(new IValue[] { FenValue.FromString("delete-me") }, null).AsObject() as FenObject);
            Assert.False(((FenValue)hasResult2).AsBoolean());
        }

        private async Task<IValue> AwaitPromise(FenObject promise)
        {
            // Simple poll for promise resolution (since our basic promise impl is async via Task.Run)
            // Ideally we should hook 'then', but polling is easier for test harness without full event loop
            
            for(int i=0; i<50; i++) // 500ms max
            {
                var state = promise.Get("__state")?.ToString();
                if (state == "fulfilled") return promise.Get("__result");
                if (state == "rejected") throw new Exception($"Promise rejected: {promise.Get("__reason")}");
                
                await Task.Delay(10);
            }
            throw new TimeoutException("Promise timed out");
        }
    }
}
