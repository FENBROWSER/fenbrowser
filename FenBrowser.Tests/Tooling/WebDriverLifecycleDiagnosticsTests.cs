using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FenBrowser.Tooling;

namespace FenBrowser.Tests.Tooling;

public sealed class WebDriverLifecycleDiagnosticsTests
{
    [Fact]
    public void LifecycleLedger_WritesOrderedMachineReadableTerminalEvents()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "fenbrowser-webdriver-lifecycle-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            string path;
            using (var diagnostics = WebDriverLifecycleDiagnostics.Start(
                       4567,
                       root,
                       registerGlobalHandlers: false))
            {
                path = diagnostics.Path;
                diagnostics.Record("webdriver_server_started");
            }

            var entries = File.ReadLines(path)
                .Select(line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                Assert.Equal(
                    new[] { "process_started", "webdriver_server_started", "process_stopped" },
                    entries.Select(entry => entry.RootElement.GetProperty("eventName").GetString()));
                Assert.All(entries, entry =>
                {
                    Assert.Equal(1, entry.RootElement.GetProperty("schemaVersion").GetInt32());
                    Assert.Equal(4567, entry.RootElement.GetProperty("port").GetInt32());
                    Assert.True(entry.RootElement.GetProperty("processId").GetInt32() > 0);
                });
            }
            finally
            {
                foreach (var entry in entries)
                {
                    entry.Dispose();
                }
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
