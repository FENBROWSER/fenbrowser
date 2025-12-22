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
            storage.Get("setItem").AsFunction().Invoke(new IValue[] 
            { 
                FenValue.FromString("key1"), 
                FenValue.FromString("value1") 
            }, null);

            // getItem
            var result = storage.Get("getItem").AsFunction().Invoke(new IValue[] 
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
            storage1.Get("setItem").AsFunction().Invoke(new IValue[] 
            { 
                FenValue.FromString("foo"), 
                FenValue.FromString("bar") 
            }, null);

            // check storage2
            var result = storage2.Get("getItem").AsFunction().Invoke(new IValue[] 
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
            storage.Get("setItem").AsFunction().Invoke(new IValue[] 
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
            var result = storageNew.Get("getItem").AsFunction().Invoke(new IValue[] 
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
            storage1.Get("setItem").AsFunction().Invoke(new IValue[] 
            { 
                FenValue.FromString("sessionData"), 
                FenValue.FromString("123") 
            }, null);

            // check storage2
            var result = storage2.Get("getItem").AsFunction().Invoke(new IValue[] 
            { 
                FenValue.FromString("sessionData") 
            }, null);

            Assert.True(result.IsNull);
        }

        [Fact]
        public void Storage_Length_ShouldUpdate()
        {
            var storage = StorageApi.CreateSessionStorage();
            
            Assert.Equal(0, storage.Get("length").ToNumber());

            // Add item
            storage.Get("setItem").AsFunction().Invoke(new IValue[] 
            { 
                FenValue.FromString("a"), 
                FenValue.FromString("1") 
            }, null);

            Assert.Equal(1, storage.Get("length").ToNumber());

            // Clear
            storage.Get("clear").AsFunction().Invoke(new IValue[] {}, null);
            Assert.Equal(0, storage.Get("length").ToNumber());
        }
    }
}
