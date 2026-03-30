using System.Collections.Generic;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class EventLoopPriorityTests
    {
        [Fact]
        public void ProcessNextTaskDetailed_PrioritizesInteractiveWork_WhenRequested()
        {
            EventLoopCoordinator.ResetInstance();
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
            EventLoopCoordinator.ResetInstance();
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
    }
}
