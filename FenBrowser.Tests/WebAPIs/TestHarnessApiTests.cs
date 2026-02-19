using FenBrowser.FenEngine.WebAPIs;
using Xunit;

namespace FenBrowser.Tests.WebAPIs
{
    public class TestHarnessApiTests
    {
        [Fact]
        public void ExecutionSnapshot_Tracks_Structured_Completion()
        {
            TestHarnessAPI.EnableTestMode(2000);
            try
            {
                TestHarnessAPI.AddResult("sample", TestHarnessAPI.TestStatus.Pass, "ok");
                TestHarnessAPI.ReportHarnessStatus("complete", "done");

                var snapshot = TestHarnessAPI.GetExecutionSnapshot();
                Assert.True(snapshot.TestMode);
                Assert.True(snapshot.TestDone);
                Assert.True(snapshot.HarnessStatusSeen);
                Assert.Equal("complete", snapshot.HarnessStatus);
                Assert.Equal(1, snapshot.ResultEventCount);
                Assert.Equal(1, snapshot.StructuredResultCount);
            }
            finally
            {
                TestHarnessAPI.DisableTestMode();
            }
        }
    }
}
