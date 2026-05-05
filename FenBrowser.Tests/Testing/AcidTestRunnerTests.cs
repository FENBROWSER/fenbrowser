using System;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Testing;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Testing
{
    public class AcidTestRunnerTests
    {
        [Fact]
        public async Task RunAcid2Async_UsesHttpTopUrl()
        {
            var runner = new AcidTestRunner();
            string capturedUrl = null;

            var result = await runner.RunAcid2Async(url =>
            {
                capturedUrl = url;
                return Task.FromResult(new SKBitmap(128, 128));
            });

            Assert.Equal("http://acid2.acidtests.org/#top", capturedUrl);
            Assert.Equal("http://acid2.acidtests.org/#top", result.Url);
        }

        [Fact]
        public async Task CompareWithReferenceAsync_MissingReference_CreatesReferenceAndPasses()
        {
            var tempDir = CreateTempTestDir();
            try
            {
                var runner = new AcidTestRunner(tempDir);
                using var actual = SolidBitmap(SKColors.Red);
                var referencePath = Path.Combine(tempDir, "references", "case.png");

                var result = await runner.CompareWithReferenceAsync("case", actual, referencePath);

                Assert.True(result.Passed);
                Assert.Equal("Reference image created", result.Message);
                Assert.Null(result.DiffImagePath);
                Assert.True(File.Exists(referencePath));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        [Fact]
        public async Task CompareWithReferenceAsync_DifferentImage_WritesDiffOnFailure()
        {
            var tempDir = CreateTempTestDir();
            try
            {
                var runner = new AcidTestRunner(tempDir);
                var referencePath = Path.Combine(tempDir, "reference.png");

                using (var reference = SolidBitmap(SKColors.Green))
                {
                    SavePng(reference, referencePath);
                }

                using var actual = SolidBitmap(SKColors.Red);
                var result = await runner.CompareWithReferenceAsync("visual_mismatch", actual, referencePath, threshold: 0.999);

                Assert.False(result.Passed);
                Assert.False(string.IsNullOrWhiteSpace(result.DiffImagePath));
                Assert.True(File.Exists(result.DiffImagePath));
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        private static SKBitmap SolidBitmap(SKColor color)
        {
            var bitmap = new SKBitmap(16, 16);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(color);
            return bitmap;
        }

        private static void SavePng(SKBitmap bitmap, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
            data.SaveTo(stream);
        }

        private static string CreateTempTestDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "fenbrowser-acid-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }
}
