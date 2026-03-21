using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using Xunit;

namespace FenBrowser.Tests.WebAPIs
{
    public class MockHistoryBridge : IHistoryBridge
    {
        private sealed class Entry
        {
            public Uri Url { get; set; }
            public object State { get; set; }
            public string Title { get; set; }
        }

        private readonly List<Entry> _entries = new();
        private int _index = -1;
        private readonly Uri _initialUrl;

        public bool PushStateCalled { get; private set; }
        public bool ReplaceStateCalled { get; private set; }
        public bool GoCalled { get; private set; }
        
        public object LastState { get; private set; }
        public string LastTitle { get; private set; }
        public string LastUrl { get; private set; }
        public int LastDelta { get; private set; }
        
        public int Length => _entries.Count;

        public object State => _index >= 0 && _index < _entries.Count ? _entries[_index].State : null;

        public Uri CurrentUrl => _index >= 0 && _index < _entries.Count ? _entries[_index].Url : _initialUrl;

        public MockHistoryBridge(Uri initialUrl)
        {
            _initialUrl = initialUrl;
            _entries.Add(new Entry { Url = initialUrl, State = null, Title = string.Empty });
            _index = 0;
        }

        public void PushState(object state, string title, string url)
        {
            PushStateCalled = true;
            LastState = state;
            LastTitle = title;
            LastUrl = url;
            var nextUrl = string.IsNullOrWhiteSpace(url) ? CurrentUrl : new Uri(CurrentUrl, url);
            if (_index < _entries.Count - 1)
            {
                _entries.RemoveRange(_index + 1, _entries.Count - (_index + 1));
            }

            _entries.Add(new Entry { Url = nextUrl, State = state, Title = title ?? string.Empty });
            _index = _entries.Count - 1;
        }

        public void ReplaceState(object state, string title, string url)
        {
            ReplaceStateCalled = true;
            LastState = state;
            LastTitle = title;
            LastUrl = url;
            var nextUrl = string.IsNullOrWhiteSpace(url) ? CurrentUrl : new Uri(CurrentUrl, url);
            if (_index < 0)
            {
                _entries.Add(new Entry { Url = nextUrl, State = state, Title = title ?? string.Empty });
                _index = _entries.Count - 1;
                return;
            }

            _entries[_index].Url = nextUrl;
            _entries[_index].State = state;
            _entries[_index].Title = title ?? _entries[_index].Title;
        }

        public void Go(int delta)
        {
            GoCalled = true;
            LastDelta = delta;
            var target = _index + delta;
            if (target >= 0 && target < _entries.Count)
            {
                _index = target;
            }
        }
    }

    public class HistoryApiTests
    {
        private readonly IExecutionContext _context;
        private readonly FenRuntime _runtime;
        private readonly MockHistoryBridge _bridge;

        public HistoryApiTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();

            // Setup permissions
            var perm = new PermissionManager(JsPermissions.StandardWeb);
            _context = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
            
            // Setup bridge
            _bridge = new MockHistoryBridge(new Uri("https://example.test/start"));
            
            // Setup runtime
            _runtime = new FenRuntime(_context, null, null, _bridge);
            _runtime.SetDom(Document.CreateHtmlDocument(), new Uri("https://example.test/start"));
        }

        [Fact]
        public void History_Object_Exists()
        {
            var history = _context.Environment.Get("history");
            Assert.False(history.IsUndefined);
            Assert.True(history.IsObject);
            Assert.True(history.AsObject().Has("pushState"));
            Assert.True(history.AsObject().Has("replaceState"));
            Assert.True(history.AsObject().Has("back"));
        }

        [Fact]
        public void PushState_ShouldCallBridge()
        {
            var history = _context.Environment.Get("history").AsObject();
            var pushState = history.Get("pushState").AsFunction();
            var location = _context.Environment.Get("location").AsObject();
            
            var stateObj = new FenObject();
            var nested = new FenObject();
            nested.Set("value", FenValue.FromNumber(5));
            stateObj.Set("nested", FenValue.FromObject(nested));
            
            // pushState(state, title, url)
            pushState.Invoke(new FenValue[] { 
                FenValue.FromObject(stateObj), 
                FenValue.FromString("New Page"), 
                FenValue.FromString("/new-url") 
            }, _context);

            Assert.True(_bridge.PushStateCalled);
            Assert.Equal("New Page", _bridge.LastTitle);
            Assert.Equal("/new-url", _bridge.LastUrl);
            Assert.NotNull(_bridge.LastState);
            Assert.True(_bridge.LastState is FenValue);

            nested.Set("value", FenValue.FromNumber(9));
            var clonedState = history.Get("state").AsObject();
            Assert.NotNull(clonedState);
            Assert.Equal(5, clonedState.Get("nested").AsObject().Get("value").ToNumber());
            Assert.Equal(2, history.Get("length").ToNumber());
            Assert.Equal("https://example.test/new-url", location.Get("href").ToString());
        }

        [Fact]
        public void ReplaceState_ShouldCallBridge()
        {
            var history = _context.Environment.Get("history").AsObject();
            var replaceState = history.Get("replaceState").AsFunction();

            history.Get("pushState").AsFunction().Invoke(new FenValue[] {
                FenValue.FromString("old-state"),
                FenValue.FromString("Old"),
                FenValue.FromString("/before")
            }, _context);
            
            replaceState.Invoke(new FenValue[] { 
                FenValue.FromString("stateStr"), 
                FenValue.FromString("Title"), 
                FenValue.FromString("/after")
            }, _context);
            
            Assert.True(_bridge.ReplaceStateCalled);
            Assert.True(_bridge.LastState is FenValue);
            Assert.Equal("stateStr", history.Get("state").ToString());
            Assert.Equal(2, history.Get("length").ToNumber());
            Assert.Equal("https://example.test/after", _context.Environment.Get("location").AsObject().Get("href").ToString());
        }

        [Fact]
        public void Back_ShouldCallGoMinusOne()
        {
            _runtime.ExecuteSimple(@"
                window.__popCount = 0;
                window.__popHref = '';
                window.__popStateStep = -1;
                window.addEventListener('popstate', function(e) {
                    window.__popCount++;
                    window.__popHref = location.href;
                    window.__popStateStep = e.state.step;
                });
                history.pushState({ step: 1 }, '', '/one');
                history.pushState({ step: 2 }, '', '/two');
                history.back();
            ");

            Assert.True(_bridge.GoCalled);
            Assert.Equal(-1, _bridge.LastDelta);

            var window = _runtime.GetGlobal("window").AsObject();
            Assert.Equal(0, window.Get("__popCount").ToNumber());

            _runtime.NotifyPopState(_bridge.State);
            Assert.Equal("https://example.test/one", _context.Environment.Get("location").AsObject().Get("href").ToString());
            Assert.Equal(0, window.Get("__popCount").ToNumber());

            EventLoopCoordinator.Instance.RunUntilEmpty();

            Assert.Equal(1, window.Get("__popCount").ToNumber());
            Assert.Equal("https://example.test/one", window.Get("__popHref").ToString());
            Assert.Equal(1, window.Get("__popStateStep").ToNumber());
            Assert.Equal(1, _context.Environment.Get("history").AsObject().Get("state").AsObject().Get("step").ToNumber());
        }

        [Fact]
        public void Forward_ShouldCallGoPlusOne()
        {
            var history = _context.Environment.Get("history").AsObject();
            var forward = history.Get("forward").AsFunction();
            
            forward.Invoke(new FenValue[] { }, _context);
            
            Assert.True(_bridge.GoCalled);
            Assert.Equal(1, _bridge.LastDelta);
        }
        
        [Fact]
        public void Go_ShouldCallGoWithDelta()
        {
            var history = _context.Environment.Get("history").AsObject();
            var go = history.Get("go").AsFunction();
            
            go.Invoke(new FenValue[] { FenValue.FromNumber(-2) }, _context);
            
            Assert.True(_bridge.GoCalled);
            Assert.Equal(-2, _bridge.LastDelta);
        }
        
        [Fact]
        public void Length_ShouldReflectBridge()
        {
            var history = _context.Environment.Get("history").AsObject();
            Assert.Equal(1, history.Get("length").ToNumber());

            history.Get("pushState").AsFunction().Invoke(new FenValue[] {
                FenValue.FromString("state-1"),
                FenValue.FromString("First"),
                FenValue.FromString("/one")
            }, _context);
            history.Get("pushState").AsFunction().Invoke(new FenValue[] {
                FenValue.FromString("state-2"),
                FenValue.FromString("Second"),
                FenValue.FromString("/two")
            }, _context);

            Assert.Equal(3, history.Get("length").ToNumber());
        }
    }
}
