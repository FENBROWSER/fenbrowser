using System.Collections.Generic;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class EventLoopPriorityTests
    {
        public EventLoopPriorityTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        [Fact]
        public void ProcessNextTaskDetailed_PrioritizesInteractiveWork_WhenRequested()
        {
            var coordinator = EventLoopCoordinator.Instance;
            var order = new List<string>();

            coordinator.ScheduleTask(() => order.Add("timer"), TaskSource.Timer, "timer");
            coordinator.ScheduleTask(() => order.Add("input"), TaskSource.UserInteraction, "input");

            var result = coordinator.ProcessNextTaskDetailed(prioritizeInteractive: true);

            Assert.True(result.Processed);
            Assert.Equal(TaskSource.UserInteraction, result.Source);
            Assert.Equal(TaskPriorityGroup.Interactive, result.PriorityGroup);
            Assert.Single(order);
            Assert.Equal("input", order[0]);
        }

        [Fact]
        public void TaskQueueSnapshot_ReportsPriorityBuckets()
        {
            var coordinator = EventLoopCoordinator.Instance;

            coordinator.ScheduleTask(() => { }, TaskSource.UserInteraction, "input");
            coordinator.ScheduleTask(() => { }, TaskSource.Networking, "fetch");
            coordinator.ScheduleTask(() => { }, TaskSource.Timer, "timer");

            var snapshot = coordinator.GetTaskSnapshot();

            Assert.Equal(3, snapshot.TotalCount);
            Assert.Equal(1, snapshot.InteractiveCount);
            Assert.Equal(1, snapshot.UserVisibleCount);
            Assert.Equal(1, snapshot.BackgroundCount);
        }

        [Fact]
        public void ProcessNextTaskDetailed_DrainsPendingMicrotasks_WhenNoTaskIsQueued()
        {
            var coordinator = EventLoopCoordinator.Instance;
            var order = new List<string>();

            coordinator.ScheduleMicrotask(() =>
            {
                order.Add("microtask");
                coordinator.ScheduleTask(() => order.Add("task"), TaskSource.Timer, "timer");
            });

            var microtaskResult = coordinator.ProcessNextTaskDetailed();
            var taskResult = coordinator.ProcessNextTaskDetailed();

            Assert.True(microtaskResult.Processed);
            Assert.Equal(TaskSource.Other, microtaskResult.Source);
            Assert.Equal(TaskPriorityGroup.Background, microtaskResult.PriorityGroup);

            Assert.True(taskResult.Processed);
            Assert.Equal(TaskSource.Timer, taskResult.Source);
            Assert.Equal(TaskPriorityGroup.Background, taskResult.PriorityGroup);
            Assert.Equal(new[] { "microtask", "task" }, order);
        }
    }
}
