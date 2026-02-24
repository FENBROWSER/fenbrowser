using System;
using System.Threading;
using FenBrowser.Core.Engine;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class PipelineContextTests
    {
        [Fact]
        public void BeginScopedFrame_AlwaysEndsInIdle()
        {
            PipelineContext.Reset();
            var context = PipelineContext.Current;

            using (context.BeginScopedFrame())
            {
                using (context.BeginScopedStage(PipelineStage.Styling))
                {
                    Assert.Equal(PipelineStage.Styling, context.CurrentStage);
                }
            }

            Assert.Equal(PipelineStage.Idle, context.CurrentStage);
        }

        [Fact]
        public void BeginScopedStage_RecordsPerStageTimings()
        {
            PipelineContext.Reset();
            var context = PipelineContext.Current;

            using (context.BeginScopedFrame())
            {
                using (context.BeginScopedStage(PipelineStage.Styling))
                {
                    Thread.Sleep(5);
                }

                using (context.BeginScopedStage(PipelineStage.Layout))
                {
                    Thread.Sleep(5);
                }

                using (context.BeginScopedStage(PipelineStage.Painting))
                {
                    Thread.Sleep(5);
                }
            }

            var times = context.GetStageTimes();
            Assert.True(times.ContainsKey(PipelineStage.Styling));
            Assert.True(times.ContainsKey(PipelineStage.Layout));
            Assert.True(times.ContainsKey(PipelineStage.Painting));
            Assert.True(times[PipelineStage.Styling] > TimeSpan.Zero);
            Assert.True(times[PipelineStage.Layout] > TimeSpan.Zero);
            Assert.True(times[PipelineStage.Painting] > TimeSpan.Zero);
        }

        [Fact]
        public void BeginScopedStage_EndsStageWhenExceptionIsThrown()
        {
            PipelineContext.Reset();
            var context = PipelineContext.Current;

            using (context.BeginScopedFrame())
            {
                Action thrower = () =>
                {
                    using (context.BeginScopedStage(PipelineStage.Styling))
                    {
                        throw new InvalidOperationException("expected");
                    }
                };
                var exception = Record.Exception(thrower);
                Assert.IsType<InvalidOperationException>(exception);

                using (context.BeginScopedStage(PipelineStage.Layout))
                {
                    Thread.Sleep(1);
                }
            }

            var times = context.GetStageTimes();
            Assert.True(times.ContainsKey(PipelineStage.Styling));
            Assert.True(times.ContainsKey(PipelineStage.Layout));
        }
    }
}
