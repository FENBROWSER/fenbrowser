using System;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Tests.Logging;
using Xunit;

namespace FenBrowser.Tests.Core;

[Collection(EngineLogTestCollection.Name)]
public sealed class RenderPipelineDocumentMilestoneTests
{
    [Fact]
    public async Task DocumentMilestones_AreLoggedOnceAcrossRenderThreads()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-document-milestones-{Guid.NewGuid():N}.jsonl");
        try
        {
            ConfigureTrace(tracePath);
            var documentOwner = new object();

            await Task.WhenAll(
                Task.Run(() => RunFrame(documentOwner)),
                Task.Run(() => RunFrame(documentOwner)));

            DisableLogging();
            var trace = File.ReadAllLines(tracePath);
            Assert.Equal(1, trace.Count(line => line.Contains("First layout complete", StringComparison.Ordinal)));
            Assert.Equal(1, trace.Count(line => line.Contains("First paint submitted", StringComparison.Ordinal)));
        }
        finally
        {
            DisableLogging();
            if (File.Exists(tracePath)) File.Delete(tracePath);
        }
    }

    [Fact]
    public void DocumentMilestones_AreIndependentForDistinctDocuments()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-document-milestones-{Guid.NewGuid():N}.jsonl");
        try
        {
            ConfigureTrace(tracePath);

            RunFrame(new object());
            RunFrame(new object());

            DisableLogging();
            var trace = File.ReadAllLines(tracePath);
            Assert.Equal(2, trace.Count(line => line.Contains("First layout complete", StringComparison.Ordinal)));
            Assert.Equal(2, trace.Count(line => line.Contains("First paint submitted", StringComparison.Ordinal)));
        }
        finally
        {
            DisableLogging();
            if (File.Exists(tracePath)) File.Delete(tracePath);
        }
    }

    private static void RunFrame(object documentOwner)
    {
        RenderPipeline.Reset();
        RenderPipeline.EnterLayout();
        RenderPipeline.EndLayout(documentOwner);
        RenderPipeline.EnterPaint();
        RenderPipeline.EndPaint(documentOwner);
        RenderPipeline.EnterPresent();
        RenderPipeline.EndFrame();
    }

    private static void ConfigureTrace(string tracePath)
    {
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = true,
            GlobalMinimumSeverity = LogSeverity.Trace,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = false,
            EnableTraceSink = true,
            TraceFilePath = tracePath
        });
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
    }

}
