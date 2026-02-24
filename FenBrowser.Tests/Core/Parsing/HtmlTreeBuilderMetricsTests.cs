using FenBrowser.Core.Parsing;
using FenBrowser.Core.Engine;
using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing
{
    public class HtmlTreeBuilderMetricsTests
    {
        [Fact]
        public void Build_PopulatesNonNegativeParseMetrics()
        {
            var builder = new HtmlTreeBuilder("<!doctype html><html><body><p>Hello</p></body></html>");

            var document = builder.Build();
            var metrics = builder.LastBuildMetrics;

            Assert.NotNull(document);
            Assert.NotNull(metrics);
            Assert.True(metrics.TokenizingMs >= 0);
            Assert.True(metrics.ParsingMs >= 0);
            Assert.True(metrics.TokenCount > 0);
            Assert.True(metrics.DocumentReadyTokenCount > 0);
            Assert.True(metrics.DocumentReadyTokenCount <= metrics.TokenCount);
        }

        [Fact]
        public void Build_IncreasesTokenCountForLargerMarkup()
        {
            var small = new HtmlTreeBuilder("<html><body>x</body></html>");
            small.Build();
            var smallCount = small.LastBuildMetrics.TokenCount;

            var large = new HtmlTreeBuilder("<html><body><div><p>a</p><p>b</p><p>c</p><ul><li>1</li><li>2</li></ul></div></body></html>");
            large.Build();
            var largeCount = large.LastBuildMetrics.TokenCount;

            Assert.True(smallCount > 0);
            Assert.True(largeCount > smallCount);
        }

        [Fact]
        public void BuildWithPipelineStages_RecordsTokenizingAndParsingStages()
        {
            PipelineContext.Reset();
            var context = PipelineContext.Current;
            var builder = new HtmlTreeBuilder("<!doctype html><html><body><article><p>Hello</p><p>World</p></article></body></html>");

            var document = builder.BuildWithPipelineStages(context);
            var times = context.GetStageTimes();

            Assert.NotNull(document);
            Assert.True(times.ContainsKey(PipelineStage.Tokenizing));
            Assert.True(times.ContainsKey(PipelineStage.Parsing));
            Assert.True(times[PipelineStage.Tokenizing] >= TimeSpan.Zero);
            Assert.True(times[PipelineStage.Parsing] >= TimeSpan.Zero);
            Assert.Equal(PipelineStage.Idle, context.CurrentStage);
        }

        [Fact]
        public void Build_ReportsCheckpointMetricsAndFinalCallbacks()
        {
            var checkpoints = new List<HtmlParseCheckpoint>();
            var documentCheckpoints = new List<(Document Document, HtmlParseCheckpoint Checkpoint)>();
            var builder = new HtmlTreeBuilder("<!doctype html><html><body><div><p>A</p><p>B</p><p>C</p><p>D</p></div></body></html>")
            {
                ParseCheckpointTokenInterval = 2,
                ParseCheckpointCallback = checkpoint => checkpoints.Add(checkpoint),
                ParseDocumentCheckpointCallback = (document, checkpoint) => documentCheckpoints.Add((document, checkpoint))
            };

            builder.Build();
            var metrics = builder.LastBuildMetrics;

            Assert.True(metrics.TokenizingCheckpointCount > 0);
            Assert.True(metrics.ParsingCheckpointCount > 0);
            Assert.Contains(checkpoints, checkpoint => checkpoint.Phase == HtmlParseBuildPhase.Tokenizing && checkpoint.IsFinal);
            Assert.Contains(checkpoints, checkpoint => checkpoint.Phase == HtmlParseBuildPhase.Parsing && checkpoint.IsFinal);

            var tokenizingFinalIndex = checkpoints.FindIndex(checkpoint =>
                checkpoint.Phase == HtmlParseBuildPhase.Tokenizing && checkpoint.IsFinal);
            var parsingFinalIndex = checkpoints.FindIndex(checkpoint =>
                checkpoint.Phase == HtmlParseBuildPhase.Parsing && checkpoint.IsFinal);

            Assert.True(tokenizingFinalIndex >= 0);
            Assert.True(parsingFinalIndex > tokenizingFinalIndex);
            Assert.True(documentCheckpoints.Count > 0);
            Assert.Contains(documentCheckpoints, entry => entry.Document != null && entry.Checkpoint.Phase == HtmlParseBuildPhase.Parsing && entry.Checkpoint.IsFinal);
        }
    }
}
