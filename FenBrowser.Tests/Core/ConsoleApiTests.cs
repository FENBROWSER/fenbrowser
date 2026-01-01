using System;
using Xunit;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// Tests for Console API methods added in Module 5B.
    /// Note: These tests verify that the console methods exist and can be invoked.
    /// Actual console output is side-effect based and not easily testable.
    /// </summary>
    public class ConsoleApiTests
    {
        [Fact]
        public void FenRuntime_HasConsoleObject()
        {
            var runtime = new FenRuntime();
            var console = runtime.GetGlobal("console");

            Assert.NotNull(console);
            Assert.True(console.IsObject);
        }

        [Fact]
        public void Console_HasLogMethod()
        {
            var runtime = new FenRuntime();
            var console = runtime.GetGlobal("console")?.AsObject();

            Assert.NotNull(console);
            var log = console.Get("log");
            Assert.NotNull(log);
            Assert.True(log.IsFunction);
        }

        [Fact]
        public void Console_HasDirMethod()
        {
            var runtime = new FenRuntime();
            var console = runtime.GetGlobal("console")?.AsObject();

            Assert.NotNull(console);
            var dir = console.Get("dir");
            Assert.NotNull(dir);
            Assert.True(dir.IsFunction);
        }

        [Fact]
        public void Console_HasTableMethod()
        {
            var runtime = new FenRuntime();
            var console = runtime.GetGlobal("console")?.AsObject();

            Assert.NotNull(console);
            var table = console.Get("table");
            Assert.NotNull(table);
            Assert.True(table.IsFunction);
        }

        [Fact]
        public void Console_HasGroupMethods()
        {
            var runtime = new FenRuntime();
            var console = runtime.GetGlobal("console")?.AsObject();

            Assert.NotNull(console);
            
            var group = console.Get("group");
            Assert.NotNull(group);
            Assert.True(group.IsFunction);

            var groupEnd = console.Get("groupEnd");
            Assert.NotNull(groupEnd);
            Assert.True(groupEnd.IsFunction);

            var groupCollapsed = console.Get("groupCollapsed");
            Assert.NotNull(groupCollapsed);
            Assert.True(groupCollapsed.IsFunction);
        }

        [Fact]
        public void Console_HasTimerMethods()
        {
            var runtime = new FenRuntime();
            var console = runtime.GetGlobal("console")?.AsObject();

            Assert.NotNull(console);
            
            var time = console.Get("time");
            Assert.NotNull(time);
            Assert.True(time.IsFunction);

            var timeEnd = console.Get("timeEnd");
            Assert.NotNull(timeEnd);
            Assert.True(timeEnd.IsFunction);
        }

        [Fact]
        public void Console_HasCountMethods()
        {
            var runtime = new FenRuntime();
            var console = runtime.GetGlobal("console")?.AsObject();

            Assert.NotNull(console);
            
            var count = console.Get("count");
            Assert.NotNull(count);
            Assert.True(count.IsFunction);

            var countReset = console.Get("countReset");
            Assert.NotNull(countReset);
            Assert.True(countReset.IsFunction);
        }

        [Fact]
        public void Console_HasAssertMethod()
        {
            var runtime = new FenRuntime();
            var console = runtime.GetGlobal("console")?.AsObject();

            Assert.NotNull(console);
            var assert = console.Get("assert");
            Assert.NotNull(assert);
            Assert.True(assert.IsFunction);
        }

        [Fact]
        public void Console_HasTraceMethod()
        {
            var runtime = new FenRuntime();
            var console = runtime.GetGlobal("console")?.AsObject();

            Assert.NotNull(console);
            var trace = console.Get("trace");
            Assert.NotNull(trace);
            Assert.True(trace.IsFunction);
        }
    }
}
