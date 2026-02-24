using System;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;

namespace FenBrowser.Tests.WebAPIs
{
    /// <summary>
    /// Regression tests for API-3 tranche: Promise-returning stubs.
    /// Verifies that APIs that previously returned strings/undefined now return proper thenables.
    /// </summary>
    public class WebApiPromiseTests
    {
        // ------------------------------------------------------------------ Notifications

        [Fact]
        public void NotificationsAPI_RequestPermission_ReturnsThenableNotString()
        {
            var notif = NotificationsAPI.CreateNotificationConstructor();
            var requestPermission = notif.Get("requestPermission");

            Assert.True(requestPermission.IsFunction, "requestPermission must be a function");

            var result = requestPermission.AsFunction().Invoke(Array.Empty<FenValue>(), (IExecutionContext)null);

            // Must be an object (thenable), NOT a string
            Assert.True(result.IsObject, "requestPermission() must return an object (Promise-thenable), not a string");

            var obj = result.AsObject();
            var thenFn = obj.Get("then");
            Assert.True(thenFn.IsFunction, "requestPermission() result must have a .then() method");
        }

        [Fact]
        public void NotificationsAPI_RequestPermission_ThenCallbackReceivesPermissionString()
        {
            var notif = NotificationsAPI.CreateNotificationConstructor();
            var requestPermission = notif.Get("requestPermission");

            string receivedPermission = null;
            var result = requestPermission.AsFunction().Invoke(Array.Empty<FenValue>(), (IExecutionContext)null);

            var obj = result.AsObject();
            var thenFn = obj.Get("then");
            thenFn.AsFunction().Invoke(
                new[]
                {
                    FenValue.FromFunction(new FenFunction("cb", (args, _) =>
                    {
                        receivedPermission = args.Length > 0 ? args[0].ToString() : null;
                        return FenValue.Undefined;
                    }))
                },
                (IExecutionContext)null);

            Assert.NotNull(receivedPermission);
            Assert.True(
                receivedPermission is "denied" or "granted" or "default",
                $"Permission must be a valid value, got: {receivedPermission}");
        }

        // ------------------------------------------------------------------ Fullscreen

        [Fact]
        public void FullscreenAPI_ExitFullscreen_ReturnsThenableNotUndefined()
        {
            var methods = FullscreenAPI.CreateDocumentFullscreenMethods();
            var exitFullscreen = methods.Get("exitFullscreen");

            Assert.True(exitFullscreen.IsFunction, "exitFullscreen must be a function");

            var result = exitFullscreen.AsFunction().Invoke(Array.Empty<FenValue>(), (IExecutionContext)null);

            Assert.True(result.IsObject, "exitFullscreen() must return a Promise-thenable object, not undefined");
            var thenFn = result.AsObject().Get("then");
            Assert.True(thenFn.IsFunction, "exitFullscreen() result must have .then()");
        }

        [Fact]
        public void FullscreenAPI_RequestFullscreen_ReturnsThenableNotUndefined()
        {
            var method = FullscreenAPI.CreateElementFullscreenMethod();
            var requestFullscreen = method.Get("requestFullscreen");

            Assert.True(requestFullscreen.IsFunction, "requestFullscreen must be a function");

            var result = requestFullscreen.AsFunction().Invoke(Array.Empty<FenValue>(), (IExecutionContext)null);

            Assert.True(result.IsObject, "requestFullscreen() must return a Promise-thenable, not undefined");
            var thenFn = result.AsObject().Get("then");
            Assert.True(thenFn.IsFunction, "requestFullscreen() result must have .then()");
        }

        [Fact]
        public void FullscreenAPI_ExitFullscreen_ThenCallbackFires()
        {
            var methods = FullscreenAPI.CreateDocumentFullscreenMethods();
            var exitFullscreen = methods.Get("exitFullscreen");

            bool callbackFired = false;
            var result = exitFullscreen.AsFunction().Invoke(Array.Empty<FenValue>(), (IExecutionContext)null);
            result.AsObject().Get("then").AsFunction().Invoke(
                new[]
                {
                    FenValue.FromFunction(new FenFunction("cb", (args, _) =>
                    {
                        callbackFired = true;
                        return FenValue.Undefined;
                    }))
                },
                (IExecutionContext)null);

            Assert.True(callbackFired, "exitFullscreen() .then() callback must be called synchronously for resolved promise");
        }

        // ------------------------------------------------------------------ Clipboard

        [Fact]
        public void ClipboardAPI_WriteText_ReturnsThenableNotUndefined()
        {
            var clipboard = ClipboardAPI.CreateClipboardObject();
            var writeText = clipboard.Get("writeText");

            var result = writeText.AsFunction().Invoke(
                new[] { FenValue.FromString("hello") },
                (IExecutionContext)null);

            Assert.True(result.IsObject, "writeText() must return a Promise-thenable");
            Assert.True(result.AsObject().Get("then").IsFunction, "writeText() result must have .then()");
        }

        [Fact]
        public void ClipboardAPI_ReadText_ReturnsThenableWithString()
        {
            var clipboard = ClipboardAPI.CreateClipboardObject();
            var readText = clipboard.Get("readText");

            string received = null;
            var result = readText.AsFunction().Invoke(Array.Empty<FenValue>(), (IExecutionContext)null);
            result.AsObject().Get("then").AsFunction().Invoke(
                new[]
                {
                    FenValue.FromFunction(new FenFunction("cb", (args, _) =>
                    {
                        received = args.Length > 0 ? args[0].ToString() : null;
                        return FenValue.Undefined;
                    }))
                },
                (IExecutionContext)null);

            // Should resolve to empty string for privacy
            Assert.NotNull(received);
            Assert.IsType<string>(received);
        }

        [Fact]
        public void ClipboardAPI_Write_ReturnsThenableNotUndefined()
        {
            var clipboard = ClipboardAPI.CreateClipboardObject();
            var write = clipboard.Get("write");

            var result = write.AsFunction().Invoke(Array.Empty<FenValue>(), (IExecutionContext)null);
            Assert.True(result.IsObject, "write() must return a Promise-thenable");
            Assert.True(result.AsObject().Get("then").IsFunction);
        }

        [Fact]
        public void ClipboardAPI_Read_ReturnsThenableNotArray()
        {
            var clipboard = ClipboardAPI.CreateClipboardObject();
            var read = clipboard.Get("read");

            var result = read.AsFunction().Invoke(Array.Empty<FenValue>(), (IExecutionContext)null);
            Assert.True(result.IsObject, "read() must return a Promise-thenable");
            Assert.True(result.AsObject().Get("then").IsFunction);
        }

        // ------------------------------------------------------------------ ResolvedThenable helper

        [Fact]
        public void ResolvedThenable_Resolved_CallsThenImmediately()
        {
            var thenable = ResolvedThenable.Resolved(FenValue.FromString("ok"));

            bool called = false;
            string received = null;
            thenable.Get("then").AsFunction().Invoke(
                new[]
                {
                    FenValue.FromFunction(new FenFunction("cb", (args, _) =>
                    {
                        called = true;
                        received = args.Length > 0 ? args[0].ToString() : null;
                        return FenValue.Undefined;
                    }))
                },
                (IExecutionContext)null);

            Assert.True(called, "Resolved thenable must call .then() callback immediately");
            Assert.Equal("ok", received);
        }

        [Fact]
        public void ResolvedThenable_Rejected_CallsCatchImmediately()
        {
            var thenable = ResolvedThenable.Rejected("test-error");

            bool called = false;
            string reason = null;
            thenable.Get("catch").AsFunction().Invoke(
                new[]
                {
                    FenValue.FromFunction(new FenFunction("cb", (args, _) =>
                    {
                        called = true;
                        reason = args.Length > 0 ? args[0].ToString() : null;
                        return FenValue.Undefined;
                    }))
                },
                (IExecutionContext)null);

            Assert.True(called, "Rejected thenable must call .catch() callback immediately");
            Assert.Equal("test-error", reason);
        }

        [Fact]
        public void ResolvedThenable_HasFinallyMethod()
        {
            var thenable = ResolvedThenable.Resolved(FenValue.Undefined);
            Assert.True(thenable.Get("finally").IsFunction, "Thenable must have .finally() method");
        }

        [Fact]
        public void ResolvedThenable_Finally_CallsCallbackImmediately()
        {
            var thenable = ResolvedThenable.Resolved(FenValue.FromNumber(1));
            bool called = false;
            thenable.Get("finally").AsFunction().Invoke(
                new[]
                {
                    FenValue.FromFunction(new FenFunction("cb", (args, _) =>
                    {
                        called = true;
                        return FenValue.Undefined;
                    }))
                },
                (IExecutionContext)null);

            Assert.True(called, ".finally() callback must fire on resolved thenable");
        }
    }
}
