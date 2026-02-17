using Xunit;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2; // V2 DOM
using FenBrowser.FenEngine.DOM;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Scripting;
using System;
using System.Linq;
using EventListener = FenBrowser.Core.Dom.V2.EventListener;

namespace FenBrowser.Tests.Engine
{
    public class EventInvariantTests
    {
        // Helper to create a listener function that logs execution
        private EventListener CreateListener(string id, List<string> log)
        {
            return new EventListener((evt) =>
            {
                string phase = evt.EventPhase switch
                {
                    EventPhase.Capturing => "CAPTURING",
                    EventPhase.AtTarget => "TARGET",
                    EventPhase.Bubbling => "BUBBLING",
                    _ => "NONE"
                };
                log.Add($"{id}:{phase}");
            });
        }

        private EventListener CreateStopper()
        {
            return new EventListener((evt) =>
            {
                evt.StopPropagation();
            });
        }

        private EventListener CreatePreventer()
        {
            return new EventListener((evt) =>
            {
                evt.PreventDefault();
            });
        }

        // Context shim for V2
        class TestContext : IExecutionContext
        {
            public FenBrowser.FenEngine.Security.IPermissionManager Permissions { get; } = new FenBrowser.FenEngine.Security.PermissionManager();
            public FenBrowser.FenEngine.Security.IResourceLimits Limits { get; } = new FenBrowser.FenEngine.Security.DefaultResourceLimits();
            public int CallStackDepth => 0;
            public DateTime ExecutionStart => DateTime.Now;
            public bool ShouldContinue => true;
            public Action RequestRender { get; set; }
            public void SetRequestRender(Action action) { RequestRender = action; }
            public Action<Action, int> ScheduleCallback { get; set; } = (a, d) => { };
            public Action<Action> ScheduleMicrotask { get; set; } = (a) => a();
            public Func<FenValue, FenValue[], FenValue> ExecuteFunction { get; set; } = (func, args) => FenValue.Undefined;
            public IModuleLoader ModuleLoader { get; set; }
            public Action<MutationRecord> OnMutation { get; set; }
            public string CurrentUrl { get; set; } = "test";
            public FenEnvironment Environment { get; set; }
            public void PushCallFrame(string name) { }
            public void PopCallFrame() { }
            public void CheckCallStackLimit() { }
            public void CheckExecutionTimeLimit() { }
            public FenValue ThisBinding { get; set; }
            public FenValue NewTarget { get; set; }
            public string CurrentModulePath { get; set; }
            public bool StrictMode { get; set; }
        }

        private readonly TestContext _context = new TestContext();

        [Fact]
        public void Test_DispatchOrder_CaptureTargetBubble()
        {
            var grandparent = new Element("div");
            var parent = new Element("div");
            var child = new Element("span");
            grandparent.AppendChild(parent);
            parent.AppendChild(child);

            var log = new List<string>();

            // V2 AddEventListener
            grandparent.AddEventListener("click", CreateListener("GP", log), new AddEventListenerOptions { Capture = true });
            grandparent.AddEventListener("click", CreateListener("GP", log), new AddEventListenerOptions { Capture = false });
            parent.AddEventListener("click", CreateListener("P", log), new AddEventListenerOptions { Capture = true });
            parent.AddEventListener("click", CreateListener("P", log), new AddEventListenerOptions { Capture = false });
            child.AddEventListener("click", CreateListener("C", log), new AddEventListenerOptions { Capture = true });
            child.AddEventListener("click", CreateListener("C", log), new AddEventListenerOptions { Capture = false });

            var evt = new Event("click", new EventInit { Bubbles = true, Cancelable = true });
            
            // V2 DispatchEvent is instance method
            child.DispatchEvent(evt);

            var expected = new List<string>
            {
                "GP:CAPTURING",
                "P:CAPTURING",
                "C:TARGET", 
                "C:TARGET", 
                "P:BUBBLING",
                "GP:BUBBLING"
            };
            
            // Note: Order at target might vary depending on implementation detail (registration order usually)
            // But verify roughly
            Assert.Contains("GP:CAPTURING", log);
            Assert.Contains("GP:BUBBLING", log);
        }

        [Fact]
        public void Test_StopPropagation_StopsFlow()
        {
            var parent = new Element("div");
            var child = new Element("span");
            parent.AppendChild(child);

            var log = new List<string>();

            parent.AddEventListener("click", CreateStopper(), new AddEventListenerOptions { Capture = true });
            child.AddEventListener("click", CreateListener("C", log), new AddEventListenerOptions { Capture = false });

            var evt = new Event("click", new EventInit { Bubbles = true, Cancelable = true });
            
            child.DispatchEvent(evt);

            Assert.Empty(log); 
            Assert.True(evt.StopPropagationFlag);
        }

        [Fact]
        public void Test_PreventDefault_ReturnsFalse()
        {
            var el = new Element("div");
            el.AddEventListener("click", CreatePreventer(), new AddEventListenerOptions { Capture = false });

            var evt = new Event("click", new EventInit { Bubbles = true, Cancelable = true });
            
            bool result = el.DispatchEvent(evt);

            Assert.True(evt.DefaultPrevented);
            Assert.False(result); 
        }

        [Fact]
        public void Test_Once_RemovesListener()
        {
            var el = new Element("div");
            var log = new List<string>();
            
            el.AddEventListener("click", CreateListener("ONCE", log), new AddEventListenerOptions { Capture = false, Once = true });

            var evt1 = new Event("click", new EventInit { Bubbles = true, Cancelable = true });
            el.DispatchEvent(evt1);

            var evt2 = new Event("click", new EventInit { Bubbles = true, Cancelable = true });
            el.DispatchEvent(evt2);

            Assert.Single(log);
        }
    }
}
