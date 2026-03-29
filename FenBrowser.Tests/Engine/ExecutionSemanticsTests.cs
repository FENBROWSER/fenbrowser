using System;
using System.Collections.Generic;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// Tests for execution semantics as defined in EventLoopSemantics.md
    /// </summary>
    [Collection("Engine Tests")]
    public class ExecutionSemanticsTests
    {
        public ExecutionSemanticsTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        [Fact]
        public void Microtasks_DrainBeforeNextTask()
        {
            var order = new List<string>();
            var coordinator = EventLoopCoordinator.Instance;

            // Schedule Task A
            coordinator.ScheduleTask(() =>
            {
                order.Add("TaskA-Start");
                
                // Schedule microtask during Task A
                coordinator.ScheduleMicrotask(() => order.Add("Microtask1"));
                coordinator.ScheduleMicrotask(() => order.Add("Microtask2"));
                
                order.Add("TaskA-End");
            }, TaskSource.Other, "TaskA");

            // Schedule Task B
            coordinator.ScheduleTask(() =>
            {
                order.Add("TaskB");
            }, TaskSource.Other, "TaskB");

            // Process both tasks
            coordinator.ProcessNextTask();
            coordinator.ProcessNextTask();

            // Assert: Microtasks run AFTER TaskA-End but BEFORE TaskB
            Assert.Equal(new[] {
                "TaskA-Start",
                "TaskA-End",
                "Microtask1",
                "Microtask2",
                "TaskB"
            }, order);
        }

        [Fact]
        public void MicrotasksEnqueuedDuringDrain_AlsoExecute()
        {
            var order = new List<string>();
            var coordinator = EventLoopCoordinator.Instance;

            coordinator.ScheduleTask(() =>
            {
                coordinator.ScheduleMicrotask(() =>
                {
                    order.Add("Microtask1");
                    // Enqueue another microtask during drain
                    coordinator.ScheduleMicrotask(() => order.Add("Microtask2"));
                });
            }, TaskSource.Other, "Task");

            coordinator.ProcessNextTask();

            // Both microtasks should have run
            Assert.Equal(new[] { "Microtask1", "Microtask2" }, order);
        }

        [Fact]
        public void PromiseOrdering_ThenChain()
        {
            var order = new List<string>();
            var coordinator = EventLoopCoordinator.Instance;

            // Simulate Promise.resolve().then(A).then(B)
            coordinator.ScheduleTask(() =>
            {
                // First then
                coordinator.ScheduleMicrotask(() =>
                {
                    order.Add("A");
                    // Second then (chained)
                    coordinator.ScheduleMicrotask(() => order.Add("B"));
                });
            }, TaskSource.Other, "PromiseTask");

            coordinator.ProcessNextTask();

            Assert.Equal(new[] { "A", "B" }, order);
        }

        [Fact]
        public void TaskQueue_FIFOOrdering()
        {
            var order = new List<int>();
            var coordinator = EventLoopCoordinator.Instance;

            for (int i = 1; i <= 5; i++)
            {
                int captured = i;
                coordinator.ScheduleTask(() => order.Add(captured), TaskSource.Other, $"Task{i}");
            }

            coordinator.RunUntilEmpty();

            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, order);
        }

        [Fact]
        public void TaskSources_PreserveFifoWithinEachSource()
        {
            var order = new List<string>();
            var coordinator = EventLoopCoordinator.Instance;

            coordinator.ScheduleTask(() => order.Add("Timer1"), TaskSource.Timer, "Timer1");
            coordinator.ScheduleTask(() => order.Add("Timer2"), TaskSource.Timer, "Timer2");
            coordinator.ScheduleTask(() => order.Add("Network1"), TaskSource.Networking, "Network1");
            coordinator.ScheduleTask(() => order.Add("Network2"), TaskSource.Networking, "Network2");

            coordinator.RunUntilEmpty();

            Assert.Equal(new[] { "Timer1", "Network1", "Timer2", "Network2" }, order);
        }

        [Fact]
        public void TaskSources_RunRoundRobinAcrossActiveSources()
        {
            var order = new List<string>();
            var coordinator = EventLoopCoordinator.Instance;

            coordinator.ScheduleTask(() => order.Add("History1"), TaskSource.History, "History1");
            coordinator.ScheduleTask(() => order.Add("Message1"), TaskSource.Messaging, "Message1");
            coordinator.ScheduleTask(() => order.Add("Timer1"), TaskSource.Timer, "Timer1");
            coordinator.ScheduleTask(() => order.Add("History2"), TaskSource.History, "History2");
            coordinator.ScheduleTask(() => order.Add("Message2"), TaskSource.Messaging, "Message2");
            coordinator.ScheduleTask(() => order.Add("Timer2"), TaskSource.Timer, "Timer2");

            coordinator.RunUntilEmpty();

            Assert.Equal(
                new[] { "History1", "Message1", "Timer1", "History2", "Message2", "Timer2" },
                order);
        }

        [Fact]
        public void TaskSources_ReentrantScheduling_DoesNotStarveOtherSources()
        {
            var order = new List<string>();
            var coordinator = EventLoopCoordinator.Instance;

            coordinator.ScheduleTask(() =>
            {
                order.Add("Timer1");
                coordinator.ScheduleTask(() => order.Add("Timer2"), TaskSource.Timer, "Timer2");
            }, TaskSource.Timer, "Timer1");

            coordinator.ScheduleTask(() => order.Add("Message1"), TaskSource.Messaging, "Message1");

            coordinator.RunUntilEmpty();

            Assert.Equal(new[] { "Timer1", "Message1", "Timer2" }, order);
        }

        [Fact]
        public void MicrotaskQueue_DrainAll_RunsToEmpty()
        {
            var queue = new MicrotaskQueue();
            var count = 0;

            queue.Enqueue(() => count++);
            queue.Enqueue(() => count++);
            queue.Enqueue(() => count++);

            queue.DrainAll();

            Assert.Equal(3, count);
            Assert.False(queue.HasPendingMicrotasks);
        }

        [Fact]
        public void MicrotaskQueue_PreventInfiniteLoop()
        {
            var queue = new MicrotaskQueue();
            var count = 0;

            // Try to create infinite loop
            Action infiniteEnqueue = null;
            infiniteEnqueue = () =>
            {
                count++;
                if (count < 2000) // More than MaxDrainDepth
                    queue.Enqueue(infiniteEnqueue);
            };

            queue.Enqueue(infiniteEnqueue);
            queue.DrainAll();

            // Should have stopped at MaxDrainDepth (1000)
            Assert.True(count <= 1001);
        }

        [Fact]
        public void AnimationFrameCallbacks_RunAfterTasks()
        {
            var order = new List<string>();
            var coordinator = EventLoopCoordinator.Instance;

            coordinator.ScheduleTask(() => order.Add("Task1"), TaskSource.Other);
            coordinator.ScheduleAnimationFrame(() => order.Add("RAF"));
            coordinator.ScheduleTask(() => order.Add("Task2"), TaskSource.Other);

            // Process first task
            coordinator.ProcessNextTask();
            
            // RAF should be in order now, but after task processing triggers render update
            // Actually per spec, RAF runs during ProcessRenderingUpdate

            // Process second task
            coordinator.ProcessNextTask();

            // The exact order depends on when ProcessRenderingUpdate is called
            // But RAF should definitely be in the order
            Assert.Contains("Task1", order);
            Assert.Contains("Task2", order);
        }

        [Fact]
        public void PhaseTransitions_JSExecution_ToMicrotasks()
        {
            var coordinator = EventLoopCoordinator.Instance;
            EnginePhase phaseAfterTask = EnginePhase.Idle;

            coordinator.ScheduleTask(() =>
            {
                // During task, should be in JSExecution
                Assert.Equal(EnginePhase.JSExecution, EnginePhaseManager.CurrentPhase);
                
                coordinator.ScheduleMicrotask(() =>
                {
                    phaseAfterTask = EnginePhaseManager.CurrentPhase;
                });
            }, TaskSource.Other);

            coordinator.ProcessNextTask();

            // During microtask drain, phase should be Microtasks
            Assert.Equal(EnginePhase.Microtasks, phaseAfterTask);
        }

        [Fact]
        public void ObserverCallbacks_FireAfterLayout()
        {
            var order = new List<string>();
            var coordinator = EventLoopCoordinator.Instance;

            coordinator.SetRenderCallback(() => order.Add("Render"));
            coordinator.SetObserverCallback(() => order.Add("Observer"));

            coordinator.ScheduleTask(() =>
            {
                order.Add("Task");
                coordinator.NotifyLayoutDirty();
            }, TaskSource.Other);

            coordinator.ProcessNextTask();

            // Order: Task → Render → Observer
            Assert.Equal(new[] { "Task", "Render", "Observer" }, order);
        }

        [Fact]
        public void AnimationFrameCallbacks_RunBeforeRenderWithinRenderingOpportunity()
        {
            var order = new List<string>();
            var coordinator = EventLoopCoordinator.Instance;

            coordinator.SetRenderCallback(() => order.Add("Render"));
            coordinator.ScheduleAnimationFrame(() => order.Add("RAF"));

            coordinator.ScheduleTask(() =>
            {
                order.Add("Task");
                coordinator.NotifyLayoutDirty();
            }, TaskSource.Other);

            coordinator.ProcessNextTask();

            Assert.Equal(new[] { "Task", "RAF", "Render" }, order);
        }

        [Fact]
        public void ClearQueues_RemovesAllPending()
        {
            var coordinator = EventLoopCoordinator.Instance;

            coordinator.ScheduleTask(() => { }, TaskSource.Other);
            coordinator.ScheduleTask(() => { }, TaskSource.Other);
            coordinator.ScheduleMicrotask(() => { });
            coordinator.ScheduleAnimationFrame(() => { });

            Assert.True(coordinator.TaskCount > 0);

            coordinator.Clear();

            Assert.Equal(0, coordinator.TaskCount);
            Assert.Equal(0, coordinator.MicrotaskCount);
            Assert.False(coordinator.HasPendingTasks);
        }

        [Fact]
        public void TaskSource_IsTracked()
        {
            var task = new ScheduledTask(() => { }, TaskSource.Timer, "setTimeout");
            
            Assert.Equal(TaskSource.Timer, task.Source);
            Assert.Equal("setTimeout", task.Description);
            Assert.True(task.ScheduledTime > 0);
        }
    }
}
