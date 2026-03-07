using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.WebAPIs;
using FenBrowser.FenEngine.Storage;
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
            var openPromise = _cacheStorage.Get("open").AsFunction().Invoke(new[] { FenValue.FromString("v1") }, null).AsObject() as FenObject;
            var cache = await AwaitPromise(openPromise);

            Assert.NotNull(cache);
            Assert.True(cache.IsObject);
            Assert.True(cache.AsObject() is Cache);

            var hasPromise = _cacheStorage.Get("has").AsFunction().Invoke(new[] { FenValue.FromString("v1") }, null).AsObject() as FenObject;
            var hasResult = await AwaitPromise(hasPromise);
            Assert.True(hasResult.AsBoolean());
        }

        [Fact]
        public async Task Cache_Put_StoresResponse()
        {
            var openPromise = _cacheStorage.Get("open").AsFunction().Invoke(new[] { FenValue.FromString("v1") }, null).AsObject() as FenObject;
            var cacheObj = (await AwaitPromise(openPromise)).AsObject();
            var cache = cacheObj as Cache;

            var request = FenValue.FromString("https://example.com/style.css");
            var response = new FenObject();
            response.Set("status", FenValue.FromNumber(200));
            response.Set("statusText", FenValue.FromString("OK"));
            response.Set("ok", FenValue.FromBoolean(true));

            var putPromise = cache.Get("put").AsFunction().Invoke(new[] { request, FenValue.FromObject(response) }, null).AsObject() as FenObject;
            await AwaitPromise(putPromise);

            var matchPromise = cache.Get("match").AsFunction().Invoke(new[] { request }, null).AsObject() as FenObject;
            var matchedResponse = (await AwaitPromise(matchPromise)).AsObject();

            Assert.NotNull(matchedResponse);
            Assert.Equal(200, matchedResponse.Get("status").AsNumber());
            Assert.Equal("OK", matchedResponse.Get("statusText").AsString());
        }

        [Fact]
        public async Task Cache_Delete_RemovesEntry()
        {
            var openPromise = _cacheStorage.Get("open").AsFunction().Invoke(new[] { FenValue.FromString("v1") }, null).AsObject() as FenObject;
            var cacheObj = (await AwaitPromise(openPromise)).AsObject();
            var cache = cacheObj as Cache;

            var request = FenValue.FromString("https://example.com/script.js");
            var response = new FenObject();
            response.Set("status", FenValue.FromNumber(200));

            await AwaitPromise(cache.Get("put").AsFunction().Invoke(new[] { request, FenValue.FromObject(response) }, null).AsObject() as FenObject);

            var deletePromise = cache.Get("delete").AsFunction().Invoke(new[] { request }, null).AsObject() as FenObject;
            var deleted = await AwaitPromise(deletePromise);
            Assert.True(deleted.AsBoolean());

            var matchPromise = cache.Get("match").AsFunction().Invoke(new[] { request }, null).AsObject() as FenObject;
            var result = await AwaitPromise(matchPromise);
            Assert.True(result.IsUndefined);
        }

        [Fact]
        public async Task CacheStorage_Delete_RemovesCache()
        {
            await AwaitPromise(_cacheStorage.Get("open").AsFunction().Invoke(new[] { FenValue.FromString("delete-me") }, null).AsObject() as FenObject);

            var hasResult = await AwaitPromise(_cacheStorage.Get("has").AsFunction().Invoke(new[] { FenValue.FromString("delete-me") }, null).AsObject() as FenObject);
            Assert.True(hasResult.AsBoolean());

            var deleteResult = await AwaitPromise(_cacheStorage.Get("delete").AsFunction().Invoke(new[] { FenValue.FromString("delete-me") }, null).AsObject() as FenObject);
            Assert.True(deleteResult.AsBoolean());

            var hasResult2 = await AwaitPromise(_cacheStorage.Get("has").AsFunction().Invoke(new[] { FenValue.FromString("delete-me") }, null).AsObject() as FenObject);
            Assert.False(hasResult2.AsBoolean());
        }

        [Fact]
        public async Task CacheStorage_Match_SearchesAcrossNamedCaches()
        {
            var cacheV1 = (await AwaitPromise(_cacheStorage.Get("open").AsFunction().Invoke(new[] { FenValue.FromString("v1") }, null).AsObject() as FenObject)).AsObject();
            var cacheV2 = (await AwaitPromise(_cacheStorage.Get("open").AsFunction().Invoke(new[] { FenValue.FromString("v2") }, null).AsObject() as FenObject)).AsObject();

            var request = FenValue.FromString("https://example.com/app.js");
            var response = new FenObject();
            response.Set("status", FenValue.FromNumber(201));
            response.Set("statusText", FenValue.FromString("Created"));
            response.Set("body", FenValue.FromString("console.log('cached');"));

            await AwaitPromise(cacheV2.Get("put").AsFunction().Invoke(new[] { request, FenValue.FromObject(response) }, null).AsObject() as FenObject);

            var matched = await AwaitPromise(_cacheStorage.Get("match").AsFunction().Invoke(new[] { request }, null).AsObject() as FenObject);
            Assert.True(matched.IsObject);
            Assert.Equal(201, matched.AsObject().Get("status").AsNumber());
            Assert.Equal("Created", matched.AsObject().Get("statusText").AsString());

            // v1 should still be empty for this request
            var v1Match = await AwaitPromise(cacheV1.Get("match").AsFunction().Invoke(new[] { request }, null).AsObject() as FenObject);
            Assert.True(v1Match.IsUndefined);
        }

        [Fact]
        public async Task Cache_Put_PersistsBody_ForTextAndJsonReaders()
        {
            var cache = (await AwaitPromise(_cacheStorage.Get("open").AsFunction().Invoke(new[] { FenValue.FromString("content") }, null).AsObject() as FenObject)).AsObject();

            var request = FenValue.FromString("https://example.com/data.json");
            var response = new FenObject();
            response.Set("status", FenValue.FromNumber(200));
            response.Set("statusText", FenValue.FromString("OK"));
            response.Set("body", FenValue.FromString("{\"ok\":true,\"count\":3}"));

            await AwaitPromise(cache.Get("put").AsFunction().Invoke(new[] { request, FenValue.FromObject(response) }, null).AsObject() as FenObject);

            var cached = (await AwaitPromise(cache.Get("match").AsFunction().Invoke(new[] { request }, null).AsObject() as FenObject)).AsObject();
            Assert.Equal("{\"ok\":true,\"count\":3}", cached.Get("body").AsString());

            var textValue = await AwaitPromise(cached.Get("text").AsFunction().Invoke(Array.Empty<FenValue>(), null).AsObject() as FenObject);
            Assert.Equal("{\"ok\":true,\"count\":3}", textValue.AsString());

            var jsonValue = await AwaitPromise(cached.Get("json").AsFunction().Invoke(Array.Empty<FenValue>(), null).AsObject() as FenObject);
            Assert.True(jsonValue.IsObject);
            Assert.True(jsonValue.AsObject().Get("ok").AsBoolean());
            Assert.Equal(3, jsonValue.AsObject().Get("count").AsNumber());
        }

        [Fact]
        public async Task Cache_Put_MissingResponse_RejectsPromise()
        {
            var cache = (await AwaitPromise(_cacheStorage.Get("open").AsFunction().Invoke(new[] { FenValue.FromString("reject") }, null).AsObject() as FenObject)).AsObject();
            var request = FenValue.FromString("https://example.com/reject.js");

            var promise = cache.Get("put").AsFunction().Invoke(new[] { request }, null).AsObject() as FenObject;
            var reason = await AwaitPromiseRejection(promise);

            Assert.Contains("requires request and response", reason, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<FenValue> AwaitPromise(FenObject promise)
        {
            for (var i = 0; i < 80; i++)
            {
                var state = promise.Get("__state").AsString();
                if (state == "fulfilled")
                {
                    return promise.Get("__result");
                }

                if (state == "rejected")
                {
                    throw new Exception($"Promise rejected: {promise.Get("__reason").ToString()}");
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("Promise timed out");
        }

        private static async Task<string> AwaitPromiseRejection(FenObject promise)
        {
            for (var i = 0; i < 80; i++)
            {
                var state = promise.Get("__state").AsString();
                if (state == "rejected")
                {
                    return promise.Get("__reason").ToString();
                }

                if (state == "fulfilled")
                {
                    throw new Exception("Expected promise rejection but it was fulfilled.");
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("Promise rejection timed out");
        }
    }
}
