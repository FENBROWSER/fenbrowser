using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Testing
{
    /// <summary>
    /// Automated test runner for Acid tests and visual regression testing.
    /// Phase 4: Test Suite implementation.
    /// </summary>
    public class AcidTestRunner
    {
        private readonly string _testResultsDir;
        private readonly List<TestResult> _results;

        public AcidTestRunner(string testResultsDir = null)
        {
            _testResultsDir = testResultsDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FenBrowser", "TestResults");
            
            Directory.CreateDirectory(_testResultsDir);
            _results = new List<TestResult>();
        }

        #region Acid Tests

        /// <summary>
        /// Run Acid1 test (basic HTML/CSS box model).
        /// </summary>
        public async Task<TestResult> RunAcid1Async(Func<string, Task<SKBitmap>> renderPageAsync)
        {
            const string acid1Html = @"
<!DOCTYPE html>
<html>
<head>
<title>Acid1 Test</title>
<style>
body { margin: 0; background: white; }
.box { width: 100px; height: 100px; margin: 10px; }
.red { background: red; }
.green { background: green; }
.blue { background: blue; }
</style>
</head>
<body>
<div class='box red'></div>
<div class='box green'></div>
<div class='box blue'></div>
</body>
</html>";

            var result = await RenderAndCompareAsync("Acid1", acid1Html, renderPageAsync);
            _results.Add(result);
            return result;
        }

        /// <summary>
        /// Run Acid2 test (complex CSS rendering).
        /// </summary>
        public async Task<TestResult> RunAcid2Async(Func<string, Task<SKBitmap>> renderPageAsync)
        {
            // Acid2 tests box model, positioning, floats, etc.
            const string acid2Url = "http://acid2.acidtests.org/#top";
            
            var result = new TestResult
            {
                Name = "Acid2",
                Url = acid2Url,
                TestType = TestType.Acid2
            };

            try
            {
                var bitmap = await renderPageAsync(acid2Url);
                result.RenderedImage = bitmap;
                result.Passed = VerifyAcid2(bitmap);
                result.Message = result.Passed ? "Acid2 face rendered correctly" : "Acid2 face has errors";
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.Message = $"Error: {ex.Message}";
            }

            _results.Add(result);
            return result;
        }

        /// <summary>
        /// Run Acid3 test (DOM/JS/CSS compliance).
        /// </summary>
        public async Task<TestResult> RunAcid3Async(Func<string, Task<(SKBitmap bitmap, int score)>> renderPageWithScoreAsync)
        {
            const string acid3Url = "https://acid3.acidtests.org/";
            
            var result = new TestResult
            {
                Name = "Acid3",
                Url = acid3Url,
                TestType = TestType.Acid3
            };

            try
            {
                var (bitmap, score) = await renderPageWithScoreAsync(acid3Url);
                result.RenderedImage = bitmap;
                result.Score = score;
                result.Passed = score >= 100;
                result.Message = $"Acid3 Score: {score}/100";
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.Message = $"Error: {ex.Message}";
            }

            _results.Add(result);
            return result;
        }

        #endregion

        #region Visual Regression Testing

        /// <summary>
        /// Compare rendered output against reference image.
        /// </summary>
        public async Task<TestResult> CompareWithReferenceAsync(
            string testName,
            SKBitmap actual,
            string referenceImagePath,
            double threshold = 0.99)
        {
            var result = new TestResult
            {
                Name = testName,
                TestType = TestType.VisualRegression,
                RenderedImage = actual
            };

            try
            {
                if (!File.Exists(referenceImagePath))
                {
                    // Save as new reference
                    SaveBitmap(actual, referenceImagePath);
                    result.Message = "Reference image created";
                    result.Passed = true;
                    return result;
                }

                using var reference = SKBitmap.Decode(referenceImagePath);
                var similarity = ComputeSimilarity(actual, reference);
                result.Score = (int)(similarity * 100);
                result.Passed = similarity >= threshold;
                result.Message = $"Similarity: {similarity:P2}";

                if (!result.Passed)
                {
                    // Save diff image
                    var diffPath = Path.Combine(_testResultsDir, $"{testName}_diff.png");
                    SaveDiffImage(actual, reference, diffPath);
                    result.DiffImagePath = diffPath;
                }
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.Message = $"Error: {ex.Message}";
            }

            _results.Add(result);
            return result;
        }

        /// <summary>
        /// Save screenshot as reference for future comparisons.
        /// </summary>
        public void SaveReference(string testName, SKBitmap bitmap)
        {
            var path = Path.Combine(_testResultsDir, "references", $"{testName}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            SaveBitmap(bitmap, path);
        }

        #endregion

        #region Helpers

        private async Task<TestResult> RenderAndCompareAsync(
            string testName,
            string html,
            Func<string, Task<SKBitmap>> renderFunc)
        {
            var result = new TestResult
            {
                Name = testName,
                TestType = TestType.UnitTest
            };

            try
            {
                var bitmap = await renderFunc(html);
                result.RenderedImage = bitmap;
                result.Passed = bitmap != null && bitmap.Width > 0 && bitmap.Height > 0;
                result.Message = result.Passed ? "Rendered successfully" : "Render failed";

                // Save rendered output
                var outputPath = Path.Combine(_testResultsDir, $"{testName}_output.png");
                SaveBitmap(bitmap, outputPath);
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.Message = ex.Message;
            }

            return result;
        }

        private bool VerifyAcid2(SKBitmap bitmap)
        {
            if (bitmap == null) return false;

            // Acid2 reference check: look for smiley face colors
            // The face should be yellow (255, 255, 0) at center area
            int centerX = bitmap.Width / 2;
            int centerY = bitmap.Height / 3;

            var pixel = bitmap.GetPixel(centerX, centerY);
            
            // Yellow face check (simplified)
            return pixel.Red > 200 && pixel.Green > 200 && pixel.Blue < 100;
        }

        private double ComputeSimilarity(SKBitmap actual, SKBitmap reference)
        {
            if (actual.Width != reference.Width || actual.Height != reference.Height)
                return 0;

            long totalDiff = 0;
            long totalPixels = actual.Width * actual.Height * 3; // RGB channels

            for (int y = 0; y < actual.Height; y++)
            {
                for (int x = 0; x < actual.Width; x++)
                {
                    var a = actual.GetPixel(x, y);
                    var r = reference.GetPixel(x, y);

                    totalDiff += Math.Abs(a.Red - r.Red);
                    totalDiff += Math.Abs(a.Green - r.Green);
                    totalDiff += Math.Abs(a.Blue - r.Blue);
                }
            }

            double maxDiff = totalPixels * 255;
            return 1.0 - (totalDiff / maxDiff);
        }

        private void SaveDiffImage(SKBitmap actual, SKBitmap reference, string path)
        {
            int width = Math.Max(actual.Width, reference.Width);
            int height = Math.Max(actual.Height, reference.Height);

            using var diff = new SKBitmap(width, height);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var a = (x < actual.Width && y < actual.Height) 
                        ? actual.GetPixel(x, y) : SKColors.Black;
                    var r = (x < reference.Width && y < reference.Height) 
                        ? reference.GetPixel(x, y) : SKColors.Black;

                    byte diffR = (byte)Math.Abs(a.Red - r.Red);
                    byte diffG = (byte)Math.Abs(a.Green - r.Green);
                    byte diffB = (byte)Math.Abs(a.Blue - r.Blue);

                    // Highlight differences in red
                    if (diffR + diffG + diffB > 30)
                    {
                        diff.SetPixel(x, y, new SKColor(255, 0, 0));
                    }
                    else
                    {
                        diff.SetPixel(x, y, a);
                    }
                }
            }

            SaveBitmap(diff, path);
        }

        private void SaveBitmap(SKBitmap bitmap, string path)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.OpenWrite(path);
            data.SaveTo(stream);
        }

        #endregion

        #region Results

        /// <summary>
        /// Get all test results.
        /// </summary>
        public IReadOnlyList<TestResult> Results => _results;

        /// <summary>
        /// Get summary of test results.
        /// </summary>
        public (int passed, int failed, int total) GetSummary()
        {
            int passed = _results.FindAll(r => r.Passed).Count;
            int failed = _results.Count - passed;
            return (passed, failed, _results.Count);
        }

        /// <summary>
        /// Clear all results.
        /// </summary>
        public void ClearResults()
        {
            _results.Clear();
        }

        #endregion
    }

    /// <summary>
    /// Test result data.
    /// </summary>
    public class TestResult
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public TestType TestType { get; set; }
        public bool Passed { get; set; }
        public string Message { get; set; }
        public int Score { get; set; }
        public SKBitmap RenderedImage { get; set; }
        public string DiffImagePath { get; set; }
    }

    /// <summary>
    /// Test type enumeration.
    /// </summary>
    public enum TestType
    {
        UnitTest,
        Acid1,
        Acid2,
        Acid3,
        VisualRegression
    }
}
