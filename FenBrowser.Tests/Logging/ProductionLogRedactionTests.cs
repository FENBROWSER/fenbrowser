using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using Xunit;

namespace FenBrowser.Tests.Logging;

[Collection(EngineLogTestCollection.Name)]
public sealed class ProductionLogRedactionTests
{
    [Fact]
    public void Write_RedactsBeforeEventAndCompatibilityFanOut_WithoutMutatingCallerFields()
    {
        ConfigureRingOnly();
        var nested = new Dictionary<string, object> { ["password"] = "nested-password-secret" };
        var fields = new Dictionary<string, object>
        {
            ["authorization"] = "Bearer write-authorization-secret",
            ["cookie"] = "session=write-cookie-secret",
            ["nested"] = nested,
            ["url"] = "https://example.test/?access_token=write-query-secret"
        };
        EngineLogEvent observed = default;
        void Capture(EngineLogEvent evt) => observed = evt;
        EngineLog.EngineEventWritten += Capture;

        try
        {
            EngineLog.Write(
                LogSubsystem.Security,
                LogSeverity.Warn,
                "Authorization: Bearer write-message-secret",
                context: new EngineLogContext(
                    Url: "https://example.test/?token=write-context-secret",
                    Referrer: "https://referrer.test/?api_key=write-referrer-secret"),
                fields: fields);

            string eventJson = JsonSerializer.Serialize(observed);
            string compatibilityJson = JsonSerializer.Serialize(EngineLog.GetCompatibilityRecentEntries());

            AssertSecretsAbsent(eventJson, "write-authorization-secret", "write-cookie-secret",
                "nested-password-secret", "write-query-secret", "write-message-secret",
                "write-context-secret", "write-referrer-secret");
            AssertSecretsAbsent(compatibilityJson, "write-authorization-secret", "write-cookie-secret",
                "nested-password-secret", "write-query-secret", "write-message-secret",
                "write-context-secret", "write-referrer-secret");
            Assert.Equal("Bearer write-authorization-secret", fields["authorization"]);
            Assert.Equal("nested-password-secret", nested["password"]);
        }
        finally
        {
            EngineLog.EngineEventWritten -= Capture;
            DisableLogging();
        }
    }

    [Fact]
    public void WriteRaw_RedactsExternalEventsBeforeAnyConsumer()
    {
        ConfigureRingOnly();
        EngineLogEvent observed = default;
        void Capture(EngineLogEvent evt) => observed = evt;
        EngineLog.EngineEventWritten += Capture;

        try
        {
            var external = CreateExternalEvent(
                "Cookie: session=raw-message-secret; refresh=raw-second-cookie-secret",
                "https://example.test/?secret=raw-context-secret",
                new Dictionary<string, object> { ["client_secret"] = "raw-field-secret" });

            EngineLog.PublishExternalEvent(external);

            string eventJson = JsonSerializer.Serialize(observed);
            string compatibilityJson = JsonSerializer.Serialize(EngineLog.GetCompatibilityRecentEntries());
            AssertSecretsAbsent(eventJson, "raw-message-secret", "raw-second-cookie-secret",
                "raw-context-secret", "raw-field-secret");
            AssertSecretsAbsent(compatibilityJson, "raw-message-secret", "raw-second-cookie-secret",
                "raw-context-secret", "raw-field-secret");
        }
        finally
        {
            EngineLog.EngineEventWritten -= Capture;
            DisableLogging();
        }
    }

    [Fact]
    public void FileSinkAndFailureBundle_DoNotPersistSecrets()
    {
        string ndjsonPath = Path.Combine(Path.GetTempPath(), $"fenbrowser-redaction-{Guid.NewGuid():N}.jsonl");
        string tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-redaction-trace-{Guid.NewGuid():N}.jsonl");
        string bundlePath = null;
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = true,
            EnabledCategories = LogCategory.All,
            GlobalMinimumSeverity = LogSeverity.Trace,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = true,
            NdjsonFilePath = ndjsonPath,
            EnableRingBufferSink = true,
            EnableTraceSink = true,
            TraceFilePath = tracePath
        });
        EngineLog.ClearCompatibilityBuffer();

        try
        {
            EngineLog.Write(
                LogSubsystem.Net,
                LogSeverity.Error,
                "Proxy-Authorization: file-message-secret",
                context: new EngineLogContext(ResourceUrl: "https://example.test/?password=file-context-secret"),
                fields: new Dictionary<string, object> { ["api_key"] = "file-field-secret" });
            Assert.True(EngineLog.Flush(TimeSpan.FromSeconds(2)));

            bundlePath = EngineLog.ExportFailureBundle(
                testId: "redaction-test",
                url: "https://bundle.test/?access_token=bundle-url-secret",
                summary: "Authorization: Bearer bundle-summary-secret");

            DisableLogging();

            string ndjson = File.ReadAllText(ndjsonPath);
            string trace = File.ReadAllText(tracePath);
            string bundle = string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(bundlePath, "*", SearchOption.AllDirectories)
                    .Where(path => Path.GetExtension(path) is ".json" or ".ndjson" or ".jsonl")
                    .Select(File.ReadAllText));

            AssertSecretsAbsent(ndjson, "file-message-secret", "file-context-secret", "file-field-secret");
            AssertSecretsAbsent(trace, "file-message-secret", "file-context-secret", "file-field-secret");
            AssertSecretsAbsent(bundle, "file-message-secret", "file-context-secret", "file-field-secret",
                "bundle-url-secret", "bundle-summary-secret");
        }
        finally
        {
            DisableLogging();
            if (File.Exists(ndjsonPath)) File.Delete(ndjsonPath);
            if (File.Exists(tracePath)) File.Delete(tracePath);
            if (!string.IsNullOrWhiteSpace(bundlePath) && Directory.Exists(bundlePath))
                Directory.Delete(bundlePath, recursive: true);
        }
    }

    private static void ConfigureRingOnly()
    {
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = true,
            EnabledCategories = LogCategory.All,
            GlobalMinimumSeverity = LogSeverity.Trace,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = true,
            EnableTraceSink = false
        });
        EngineLog.ClearCompatibilityBuffer();
    }

    private static void DisableLogging()
    {
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = false,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = false,
            EnableTraceSink = false
        });
        EngineLog.ClearCompatibilityBuffer();
    }

    private static EngineLogEvent CreateExternalEvent(
        string message,
        string url,
        IReadOnlyDictionary<string, object> fields)
    {
        return new EngineLogEvent(
            new EngineLogHeader(
                DateTimeOffset.UtcNow,
                1,
                Environment.ProcessId,
                Environment.CurrentManagedThreadId,
                LogSubsystem.Security,
                LogSeverity.Warn,
                LogMarker.None,
                Guid.NewGuid(),
                null,
                null,
                new EngineLogContext(Url: url)),
            new EngineLogPayload
            {
                MessageTemplate = message,
                Fields = fields,
                SourceFile = "external.cs",
                SourceLine = 1,
                SourceMember = "External"
            });
    }

    private static void AssertSecretsAbsent(string text, params string[] secrets)
    {
        Assert.Contains("redacted", text, StringComparison.OrdinalIgnoreCase);
        foreach (string secret in secrets)
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }
}
