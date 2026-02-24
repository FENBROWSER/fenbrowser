using System.Collections.Generic;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing
{
    public class HtmlTreeBuilderInterleavedBuildTests
    {
        [Fact]
        public void Build_InterleavedMode_EmitsParsingCheckpointsBeforeTokenizingFinal()
        {
            var checkpoints = new List<HtmlParseCheckpoint>();
            var builder = new HtmlTreeBuilder("<!doctype html><html><body><div><p>A</p><p>B</p><p>C</p><p>D</p></div></body></html>")
            {
                ParseCheckpointTokenInterval = 2,
                InterleavedTokenBatchSize = 2,
                ParseCheckpointCallback = checkpoint => checkpoints.Add(checkpoint)
            };

            var document = builder.Build();
            var metrics = builder.LastBuildMetrics;

            Assert.NotNull(document?.DocumentElement);
            Assert.True(metrics.UsedInterleavedBuild);
            Assert.Equal(2, metrics.InterleavedTokenBatchSize);
            Assert.True(metrics.InterleavedBatchCount > 0);

            var tokenizingFinalIndex = checkpoints.FindIndex(checkpoint =>
                checkpoint.Phase == HtmlParseBuildPhase.Tokenizing && checkpoint.IsFinal);
            var parsingNonFinalIndex = checkpoints.FindIndex(checkpoint =>
                checkpoint.Phase == HtmlParseBuildPhase.Parsing && !checkpoint.IsFinal);

            Assert.True(tokenizingFinalIndex >= 0);
            Assert.True(parsingNonFinalIndex >= 0);
            Assert.True(parsingNonFinalIndex < tokenizingFinalIndex);
        }

        [Fact]
        public void Build_DefaultMode_EmitsTokenizingFinalBeforeParsingNonFinal()
        {
            var checkpoints = new List<HtmlParseCheckpoint>();
            var builder = new HtmlTreeBuilder("<!doctype html><html><body><div><p>A</p><p>B</p><p>C</p><p>D</p></div></body></html>")
            {
                ParseCheckpointTokenInterval = 2,
                InterleavedTokenBatchSize = 0,
                ParseCheckpointCallback = checkpoint => checkpoints.Add(checkpoint)
            };

            var document = builder.Build();
            var metrics = builder.LastBuildMetrics;

            Assert.NotNull(document?.DocumentElement);
            Assert.False(metrics.UsedInterleavedBuild);

            var tokenizingFinalIndex = checkpoints.FindIndex(checkpoint =>
                checkpoint.Phase == HtmlParseBuildPhase.Tokenizing && checkpoint.IsFinal);
            var parsingNonFinalIndex = checkpoints.FindIndex(checkpoint =>
                checkpoint.Phase == HtmlParseBuildPhase.Parsing && !checkpoint.IsFinal);

            Assert.True(tokenizingFinalIndex >= 0);
            Assert.True(parsingNonFinalIndex >= 0);
            Assert.True(tokenizingFinalIndex < parsingNonFinalIndex);
        }
    }
}
