using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;
using JsExecutionContext = FenBrowser.FenEngine.Core.ExecutionContext;

namespace FenBrowser.Tests.WebAPIs
{
    [Collection("Engine Tests")]
    public class ViewTransitionApiTests
    {
        [Fact]
        public void StartViewTransition_ExposesLifecyclePromisesAndSettlesAfterCallbacks()
        {
            var (context, scheduled) = CreateContextWithScheduler();
            var method = ViewTransitionAPI.CreateStartViewTransitionMethod(context).AsFunction();

            var vt = Assert.IsAssignableFrom<FenObject>(method.Invoke(Array.Empty<FenValue>(), context).AsObject());
            Assert.True(vt.Get("finished").IsObject);
            Assert.True(vt.Get("ready").IsObject);
            Assert.True(vt.Get("updateCallbackDone").IsObject);
            Assert.True(vt.Get("skipTransition").IsFunction);

            DrainScheduledCallbacks(scheduled);

            Assert.True(PromiseResolved(vt.Get("ready").AsObject()));
            Assert.True(PromiseResolved(vt.Get("updateCallbackDone").AsObject()));
            Assert.True(PromiseResolved(vt.Get("finished").AsObject()));
        }

        [Fact]
        public void SkipTransition_SettlesPromisesImmediately()
        {
            var (context, scheduled) = CreateContextWithScheduler();
            var vt = ViewTransitionAPI.CreateViewTransitionObject(context, FenValue.Undefined);

            vt.Get("skipTransition").AsFunction().Invoke(Array.Empty<FenValue>(), context, FenValue.FromObject(vt));
            EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();

            Assert.True(PromiseResolved(vt.Get("ready").AsObject()));
            Assert.True(PromiseResolved(vt.Get("updateCallbackDone").AsObject()));
            Assert.True(PromiseResolved(vt.Get("finished").AsObject()));

            DrainScheduledCallbacks(scheduled);
            Assert.True(PromiseResolved(vt.Get("finished").AsObject()));
        }

        [Fact]
        public void AttachToDocument_RegistersStartViewTransition()
        {
            var (context, _) = CreateContextWithScheduler();
            var document = new FenObject();

            ViewTransitionAPI.AttachToDocument(document, context);

            Assert.True(document.Get("startViewTransition").IsFunction);
        }

        private static (JsExecutionContext Context, List<(Action Action, int Delay)> Scheduled) CreateContextWithScheduler()
        {
            var scheduled = new List<(Action Action, int Delay)>();
            var context = new JsExecutionContext(new PermissionManager(JsPermissions.StandardWeb))
            {
                ScheduleCallback = (action, delay) =>
                {
                    if (action != null)
                        scheduled.Add((action, delay));
                },
                Environment = new FenEnvironment()
            };

            // Minimal document wiring so snapshot code can probe safely.
            var document = new FenObject();
            document.Set("documentElement", FenValue.FromObject(new FenObject()));
            context.Environment.Set("document", FenValue.FromObject(document));

            return (context, scheduled);
        }

        private static void DrainScheduledCallbacks(List<(Action Action, int Delay)> scheduled)
        {
            while (scheduled.Count > 0)
            {
                scheduled.Sort((a, b) => a.Delay.CompareTo(b.Delay));
                var work = scheduled[0];
                scheduled.RemoveAt(0);
                work.Action();
                EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
            }
        }

        private static bool PromiseResolved(IObject promise)
        {
            var resolved = false;
            promise.Get("then").AsFunction().Invoke(
                new[]
                {
                    FenValue.FromFunction(new FenFunction("onResolve", (args, _) =>
                    {
                        resolved = true;
                        return FenValue.Undefined;
                    }))
                },
                null);

            EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
            return resolved;
        }
    }
}
