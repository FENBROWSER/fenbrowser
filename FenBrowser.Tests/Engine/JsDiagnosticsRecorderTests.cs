using System;
using System.IO;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Diagnostics;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    // The recorder is the only mechanism we have for surfacing why a heavily-
    // fenced production site (x.com, etc.) failed during boot without
    // attaching a CDP client. These tests pin its core contract: when an
    // uncaught throw hits a runtime context that has the recorder hooked up,
    // a line lands in the diagnostics file.
    public class JsDiagnosticsRecorderTests
    {
        [Fact]
        public void RecordException_WritesLineToDiagnosticsLog()
        {
            var marker = "boom-marker-" + Guid.NewGuid().ToString("N");
            var thrown = FenValue.FromString("TypeError: " + marker);

            var sizeBefore = SafeFileSize(JsDiagnosticsRecorder.LogFilePath);
            JsDiagnosticsRecorder.RecordException(thrown, "boot.js", "https://example.test/boot");

            var contents = File.ReadAllText(JsDiagnosticsRecorder.LogFilePath);
            Assert.Contains(marker, contents);
            Assert.Contains("uncaught", contents);
            Assert.True(new FileInfo(JsDiagnosticsRecorder.LogFilePath).Length > sizeBefore);
        }

        [Fact]
        public void RecordConsole_WritesLineToDiagnosticsLog()
        {
            var marker = "console-marker-" + Guid.NewGuid().ToString("N");
            JsDiagnosticsRecorder.RecordConsole("warn", marker, "https://example.test/console");

            var contents = File.ReadAllText(JsDiagnosticsRecorder.LogFilePath);
            Assert.Contains(marker, contents);
            Assert.Contains("console.warn", contents);
        }

        private static long SafeFileSize(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { return 0; }
        }
    }
}
