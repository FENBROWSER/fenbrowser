using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core; // Added for EnginePhaseManager
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class EventLoopTests
    {
        public EventLoopTests()
        {
            EventLoopCoordinator.Instance.Clear();
            EnginePhaseManager.EnterPhase(EnginePhase.Idle);
        }

        [Fact]
        public void EnqueueTask_RunsInOrder()
        {
            var log = new List<string>();
            var loop = EventLoopCoordinator.Instance;

            loop.EnqueueTask(() => log.Add("Task 1"));
            loop.EnqueueTask(() => log.Add("Task 2"));

            Assert.Empty(log);

            loop.ProcessNextTask();
            Assert.Single(log);
            Assert.Equal("Task 1", log[0]);

            loop.ProcessNextTask();
            Assert.Equal(2, log.Count);
            Assert.Equal("Task 2", log[1]);
        }

        [Fact]
        public void Microtasks_RunImmediatelyAfterTask()
        {
            var log = new List<string>();
            var loop = EventLoopCoordinator.Instance;

            loop.EnqueueTask(() => 
            {
                log.Add("Task 1");
                loop.EnqueueMicrotask(() => log.Add("Microtask A"));
                loop.EnqueueMicrotask(() => log.Add("Microtask B"));
            });

            loop.EnqueueTask(() => log.Add("Task 2"));

            loop.ProcessNextTask(); // Should run Task 1 -> Microtask A -> Microtask B

            Assert.Equal(3, log.Count);
            Assert.Equal("Task 1", log[0]);
            Assert.Equal("Microtask A", log[1]);
            Assert.Equal("Microtask B", log[2]);
            
            // Task 2 hasn't run yet
            loop.ProcessNextTask();
            Assert.Equal(4, log.Count);
            Assert.Equal("Task 2", log[3]);
        }

        [Fact]
        public void Microtasks_DrainRecursively()
        {
            var log = new List<string>();
            var loop = EventLoopCoordinator.Instance;

            loop.EnqueueTask(() => 
            {
                log.Add("Task 1");
                loop.EnqueueMicrotask(() => 
                {
                    log.Add("Microtask Level 1");
                    loop.EnqueueMicrotask(() => log.Add("Microtask Level 2"));
                });
            });

            loop.ProcessNextTask();

            Assert.Equal(3, log.Count);
            Assert.Equal("Task 1", log[0]);
            Assert.Equal("Microtask Level 1", log[1]);
            Assert.Equal("Microtask Level 2", log[2]);
        }

        [Fact]
        public void ProcessNextTask_SetsJSExecutionPhase()
        {
            var loop = EventLoopCoordinator.Instance;
            bool wasInJSPhase = false;

            loop.EnqueueTask(() => 
            {
                wasInJSPhase = EnginePhaseManager.IsInJSExecutionWindow;
            });

            loop.ProcessNextTask();

            Assert.True(wasInJSPhase);
            // Should return to Idle
            Assert.Equal(EnginePhase.Idle, EnginePhaseManager.CurrentPhase);
        }
    }
}
