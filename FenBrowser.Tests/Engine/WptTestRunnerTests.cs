using System;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Testing;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class WptTestRunnerTests
    {
        [Fact]
        public async Task RunSingleTestAsync_WithoutNavigator_FailsFast()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"fen-wpt-{Guid.NewGuid():N}.html");
            File.WriteAllText(tempFile, "<!doctype html><html><body>test</body></html>");

            try
            {
                var runner = new WPTTestRunner(Path.GetDirectoryName(tempFile), navigator: null, timeoutMs: 500);
                var result = await runner.RunSingleTestAsync(tempFile);

                Assert.False(result.Success);
                Assert.Equal("no-navigator", result.CompletionSignal);
                Assert.Contains("Navigator delegate is required", result.Error, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }
}
