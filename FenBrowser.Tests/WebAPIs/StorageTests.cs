using System;
using System.IO;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;

namespace FenBrowser.Tests.WebAPIs
{
    public class StorageTests : IDisposable
    {
        private sealed class StubDomBridge : IDomBridge
        {
            public StubDomBridge(string sessionStoragePartitionId)
            {
                SessionStoragePartitionId = sessionStoragePartitionId;
            }

            public string SessionStoragePartitionId { get; }
            public FenValue GetElementById(string id) => FenValue.Undefined;
            public FenValue QuerySelector(string selector) => FenValue.Undefined;
            public FenValue GetElementsByTagName(string tagName) => FenValue.Undefined;
            public FenValue GetElementsByClassName(string classNames) => FenValue.Undefined;
            public void AddEventListener(string elementId, string eventName, FenValue callback) { }
            public FenValue CreateElement(string tagName) => FenValue.Undefined;
            public FenValue CreateElementNS(string namespaceUri, string qualifiedName) => FenValue.Undefined;
            public FenValue CreateTextNode(string text) => FenValue.Undefined;
            public void AppendChild(FenValue parent, FenValue child) { }
            public void SetAttribute(FenValue element, string name, string value) { }
        }

        private readonly string _testPath;

        public StorageTests()
        {
            _testPath = Path.Combine(Path.GetTempPath(), $"localStorage_{Guid.NewGuid()}.json");
            StorageApi.LocalStoragePath = _testPath;
            StorageApi.ResetForTesting();
        }

        public void Dispose()
        {
            if (File.Exists(_testPath)) File.Delete(_testPath);
        }

        [Fact]
        public void LocalStorage_ShouldStoreAndRetrieveData()
        {
            var origin = "http://example.com";
            var storage = StorageApi.CreateLocalStorage(() => origin);

            // setItem
            storage.Get("setItem").AsFunction().Invoke(new FenValue[] 
            { 
                FenValue.FromString("key1"), 
                FenValue.FromString("value1") 
            }, null);

            // getItem
            var result = storage.Get("getItem").AsFunction().Invoke(new FenValue[] 
            { 
                FenValue.FromString("key1") 
            }, null);

            Assert.Equal("value1", result.ToString());
        }

        [Fact]
        public void LocalStorage_ShouldBePartitionedByOrigin()
        {
            var origin1 = "http://example.com";
            var origin2 = "http://google.com";

            var storage1 = StorageApi.CreateLocalStorage(() => origin1);
            var storage2 = StorageApi.CreateLocalStorage(() => origin2);

            // set in storage1
            storage1.Get("setItem").AsFunction().Invoke(new FenValue[] 
            { 
                FenValue.FromString("foo"), 
                FenValue.FromString("bar") 
            }, null);

            // check storage2
            var result = storage2.Get("getItem").AsFunction().Invoke(new FenValue[] 
            { 
                FenValue.FromString("foo") 
            }, null);

            Assert.True(result.IsNull);
        }

        [Fact]
        public void LocalStorage_ShouldPersistToDisk()
        {
            var origin = "http://persist.com";
            var storage = StorageApi.CreateLocalStorage(() => origin);

            // setItem
            storage.Get("setItem").AsFunction().Invoke(new FenValue[] 
            { 
                FenValue.FromString("persistKey"), 
                FenValue.FromString("persistVal") 
            }, null);

            // Force save (Save is auto called in SetItem in current implementation, but strictly synchronous)
            
            // Verify file exists
            Assert.True(File.Exists(_testPath));
            var content = File.ReadAllText(_testPath);
            Assert.Contains("persistKey", content);
            Assert.Contains("persistVal", content);

            // Simulate reload
            StorageApi.ResetForTesting(); 
            // _testPath is still set, Load() called in Reset should pick up file.

            var storageNew = StorageApi.CreateLocalStorage(() => origin);
            var result = storageNew.Get("getItem").AsFunction().Invoke(new FenValue[] 
            { 
                FenValue.FromString("persistKey") 
            }, null);

            Assert.Equal("persistVal", result.ToString());
        }

        [Fact]
        public void SessionStorage_ShouldBeIsolatedByInstance()
        {
            var storage1 = StorageApi.CreateSessionStorage();
            var storage2 = StorageApi.CreateSessionStorage();

            // set in storage1
            storage1.Get("setItem").AsFunction().Invoke(new FenValue[] 
            { 
                FenValue.FromString("sessionData"), 
                FenValue.FromString("123") 
            }, null);

            // check storage2
            var result = storage2.Get("getItem").AsFunction().Invoke(new FenValue[] 
            { 
                FenValue.FromString("sessionData") 
            }, null);

            Assert.True(result.IsNull);
        }

        [Fact]
        public void SessionStorage_ShouldPersistAcrossStorageRecreation_WithSamePartitionAndOrigin()
        {
            const string origin = "http://session.example";
            const string partitionId = "tab-session-1";

            var storage1 = StorageApi.CreateSessionStorage(() => origin, () => partitionId);
            storage1.Get("setItem").AsFunction().Invoke(new FenValue[]
            {
                FenValue.FromString("sessionData"),
                FenValue.FromString("123")
            }, null);

            var storage2 = StorageApi.CreateSessionStorage(() => origin, () => partitionId);
            var result = storage2.Get("getItem").AsFunction().Invoke(new FenValue[]
            {
                FenValue.FromString("sessionData")
            }, null);

            Assert.Equal("123", result.ToString());
        }

        [Fact]
        public void SessionStorage_ShouldPersistAcrossFenRuntimeReload_WithStableTabPartition()
        {
            var origin = new Uri("https://reload.example/app");
            var bridge = new StubDomBridge("tab-reload-1");
            var sessionScope = StorageApi.BuildSessionScope(bridge.SessionStoragePartitionId, "https://reload.example:443");

            try
            {
                var runtime1 = new FenRuntime(domBridge: bridge) { BaseUri = origin };
                runtime1.ExecuteSimple("sessionStorage.setItem('persisted', 'value');", origin.ToString());

                var runtime2 = new FenRuntime(domBridge: bridge) { BaseUri = origin };
                runtime2.ExecuteSimple("var restored = sessionStorage.getItem('persisted');", origin.ToString());

                var restored = (FenValue)runtime2.GetGlobal("restored");
                Assert.Equal("value", restored.ToString());
            }
            finally
            {
                StorageApi.ClearSessionStorage(sessionScope);
            }
        }

        [Fact]
        public void Storage_Length_ShouldUpdate()
        {
            var storage = StorageApi.CreateSessionStorage();
            
            Assert.Equal(0, storage.Get("length").ToNumber());

            // Add item
            storage.Get("setItem").AsFunction().Invoke(new FenValue[] 
            { 
                FenValue.FromString("a"), 
                FenValue.FromString("1") 
            }, null);

            Assert.Equal(1, storage.Get("length").ToNumber());

            // Clear
            storage.Get("clear").AsFunction().Invoke(new FenValue[] {}, null);
            Assert.Equal(0, storage.Get("length").ToNumber());
        }
    }
}
