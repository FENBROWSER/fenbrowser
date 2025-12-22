using System;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Storage;
using Xunit;

namespace FenBrowser.Tests.Storage
{
    /// <summary>
    /// Tests for IndexedDB storage backends
    /// </summary>
    public class StorageBackendTests : IDisposable
    {
        private readonly string _testPath;
        private readonly InMemoryStorageBackend _memoryBackend;
        private readonly FileStorageBackend _fileBackend;

        public StorageBackendTests()
        {
            _testPath = Path.Combine(Path.GetTempPath(), "FenBrowser_Tests_" + Guid.NewGuid());
            _memoryBackend = new InMemoryStorageBackend();
            _fileBackend = new FileStorageBackend(_testPath);
        }

        public void Dispose()
        {
            _memoryBackend.Clear();
            if (Directory.Exists(_testPath))
            {
                try { Directory.Delete(_testPath, true); } catch { }
            }
        }

        #region InMemoryStorageBackend Tests

        [Fact]
        public async Task Memory_OpenDatabase_CreatesNew()
        {
            var result = await _memoryBackend.OpenDatabase("https://example.com", "testdb", 1);

            Assert.True(result.Success);
            Assert.True(result.UpgradeNeeded); // New DB always needs "upgrade"
            Assert.Equal(0, result.OldVersion);
            Assert.Equal(1, result.NewVersion);
        }

        [Fact]
        public async Task Memory_OpenDatabase_SameVersion_NoUpgrade()
        {
            await _memoryBackend.OpenDatabase("https://example.com", "testdb", 1);
            var result = await _memoryBackend.OpenDatabase("https://example.com", "testdb", 1);

            Assert.True(result.Success);
            Assert.False(result.UpgradeNeeded);
        }

        [Fact]
        public async Task Memory_OpenDatabase_HigherVersion_UpgradeNeeded()
        {
            await _memoryBackend.OpenDatabase("https://example.com", "testdb", 1);
            var result = await _memoryBackend.OpenDatabase("https://example.com", "testdb", 2);

            Assert.True(result.Success);
            Assert.True(result.UpgradeNeeded);
            Assert.Equal(1, result.OldVersion);
            Assert.Equal(2, result.NewVersion);
        }

        [Fact]
        public async Task Memory_CreateObjectStore_Success()
        {
            await _memoryBackend.OpenDatabase("https://example.com", "testdb", 1);
            await _memoryBackend.CreateObjectStore("https://example.com", "testdb", "users", 
                new ObjectStoreOptions { KeyPath = "id", AutoIncrement = true });

            var info = await _memoryBackend.GetDatabaseInfo("https://example.com", "testdb");
            Assert.Contains("users", info.ObjectStoreNames);
        }

        [Fact]
        public async Task Memory_CRUD_PutAndGet()
        {
            await _memoryBackend.OpenDatabase("https://example.com", "testdb", 1);
            await _memoryBackend.CreateObjectStore("https://example.com", "testdb", "items", null);

            await _memoryBackend.Put("https://example.com", "testdb", "items", "key1", "value1");
            var result = await _memoryBackend.Get("https://example.com", "testdb", "items", "key1");

            Assert.Equal("value1", result);
        }

        [Fact]
        public async Task Memory_CRUD_AddThrowsOnDuplicate()
        {
            await _memoryBackend.OpenDatabase("https://example.com", "testdb", 1);
            await _memoryBackend.CreateObjectStore("https://example.com", "testdb", "items", null);

            await _memoryBackend.Add("https://example.com", "testdb", "items", "key1", "value1");
            
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _memoryBackend.Add("https://example.com", "testdb", "items", "key1", "value2"));
        }

        [Fact]
        public async Task Memory_CRUD_Delete()
        {
            await _memoryBackend.OpenDatabase("https://example.com", "testdb", 1);
            await _memoryBackend.CreateObjectStore("https://example.com", "testdb", "items", null);

            await _memoryBackend.Put("https://example.com", "testdb", "items", "key1", "value1");
            await _memoryBackend.Delete("https://example.com", "testdb", "items", "key1");
            var result = await _memoryBackend.Get("https://example.com", "testdb", "items", "key1");

            Assert.Null(result);
        }

        [Fact]
        public async Task Memory_CRUD_Clear()
        {
            await _memoryBackend.OpenDatabase("https://example.com", "testdb", 1);
            await _memoryBackend.CreateObjectStore("https://example.com", "testdb", "items", null);

            await _memoryBackend.Put("https://example.com", "testdb", "items", "key1", "value1");
            await _memoryBackend.Put("https://example.com", "testdb", "items", "key2", "value2");
            await _memoryBackend.Clear("https://example.com", "testdb", "items");
            var count = await _memoryBackend.Count("https://example.com", "testdb", "items");

            Assert.Equal(0, count);
        }

        [Fact]
        public async Task Memory_OriginPartitioning_IsolatesData()
        {
            // Store data for origin A
            await _memoryBackend.OpenDatabase("https://a.com", "testdb", 1);
            await _memoryBackend.CreateObjectStore("https://a.com", "testdb", "items", null);
            await _memoryBackend.Put("https://a.com", "testdb", "items", "key", "valueA");

            // Store data for origin B
            await _memoryBackend.OpenDatabase("https://b.com", "testdb", 1);
            await _memoryBackend.CreateObjectStore("https://b.com", "testdb", "items", null);
            await _memoryBackend.Put("https://b.com", "testdb", "items", "key", "valueB");

            // Verify isolation
            var valueA = await _memoryBackend.Get("https://a.com", "testdb", "items", "key");
            var valueB = await _memoryBackend.Get("https://b.com", "testdb", "items", "key");

            Assert.Equal("valueA", valueA);
            Assert.Equal("valueB", valueB);
        }

        #endregion

        #region FileStorageBackend Tests

        [Fact]
        public async Task File_OpenDatabase_CreatesNew()
        {
            var result = await _fileBackend.OpenDatabase("https://example.com", "testdb", 1);

            Assert.True(result.Success);
            Assert.True(result.UpgradeNeeded);
        }

        [Fact]
        public async Task File_CRUD_PutAndGet()
        {
            await _fileBackend.OpenDatabase("https://example.com", "testdb", 1);
            await _fileBackend.CreateObjectStore("https://example.com", "testdb", "items", null);

            await _fileBackend.Put("https://example.com", "testdb", "items", "key1", "value1");
            var result = await _fileBackend.Get("https://example.com", "testdb", "items", "key1");

            Assert.Equal("value1", result);
        }

        [Fact]
        public async Task File_DeleteDatabase_RemovesData()
        {
            await _fileBackend.OpenDatabase("https://example.com", "testdb", 1);
            await _fileBackend.CreateObjectStore("https://example.com", "testdb", "items", null);
            await _fileBackend.Put("https://example.com", "testdb", "items", "key1", "value1");

            await _fileBackend.DeleteDatabase("https://example.com", "testdb");

            var info = await _fileBackend.GetDatabaseInfo("https://example.com", "testdb");
            Assert.Null(info);
        }

        [Fact]
        public async Task File_Count_ReturnsCorrectCount()
        {
            await _fileBackend.OpenDatabase("https://example.com", "testdb", 1);
            await _fileBackend.CreateObjectStore("https://example.com", "testdb", "items", null);

            await _fileBackend.Put("https://example.com", "testdb", "items", "key1", "value1");
            await _fileBackend.Put("https://example.com", "testdb", "items", "key2", "value2");
            await _fileBackend.Put("https://example.com", "testdb", "items", "key3", "value3");

            var count = await _fileBackend.Count("https://example.com", "testdb", "items");

            Assert.Equal(3, count);
        }

        [Fact]
        public async Task File_GetAll_ReturnsAllValues()
        {
            await _fileBackend.OpenDatabase("https://example.com", "testdb", 1);
            await _fileBackend.CreateObjectStore("https://example.com", "testdb", "items", null);

            await _fileBackend.Put("https://example.com", "testdb", "items", "key1", "value1");
            await _fileBackend.Put("https://example.com", "testdb", "items", "key2", "value2");

            var values = await _fileBackend.GetAll("https://example.com", "testdb", "items");

            Assert.Contains("value1", values);
            Assert.Contains("value2", values);
        }

        #endregion
    }
}
