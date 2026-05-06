using System;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;

namespace FenBrowser.Tests.WebAPIs
{
    [Collection("Engine Tests")]
    public class NetworkInformationApiTests
    {
        [Fact]
        public void Connection_ExposesDeterministicDefaults()
        {
            var context = CreateContext();
            var connection = NetworkInformationAPI.CreateNetworkInformation(context);

            Assert.Equal("4g", connection.Get("effectiveType").AsString());
            Assert.Equal(50d, connection.Get("rtt").ToNumber());
            Assert.Equal(10d, connection.Get("downlink").ToNumber());
            Assert.False(connection.Get("saveData").ToBoolean());
        }

        [Fact]
        public void DispatchEvent_InvokesChangeListener()
        {
            var context = CreateContext();
            var connection = NetworkInformationAPI.CreateNetworkInformation(context);
            var addEventListener = connection.Get("addEventListener").AsFunction();
            var dispatchEvent = connection.Get("dispatchEvent").AsFunction();

            var callbackCount = 0;
            addEventListener.Invoke(
                new[]
                {
                    FenValue.FromString("change"),
                    FenValue.FromFunction(new FenFunction("onChange", (args, _) =>
                    {
                        callbackCount++;
                        return FenValue.Undefined;
                    }))
                },
                context);

            dispatchEvent.Invoke(new[] { CreateChangeEvent() }, context);
            EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();

            Assert.Equal(1, callbackCount);
        }

        [Fact]
        public void DispatchEvent_InvokesOnChangePropertyHandler()
        {
            var context = CreateContext();
            var connection = NetworkInformationAPI.CreateNetworkInformation(context);
            var dispatchEvent = connection.Get("dispatchEvent").AsFunction();

            var called = false;
            connection.Set("onchange", FenValue.FromFunction(new FenFunction("propOnChange", (args, _) =>
            {
                called = true;
                return FenValue.Undefined;
            })));

            dispatchEvent.Invoke(new[] { CreateChangeEvent() }, context);
            EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();

            Assert.True(called);
        }

        [Fact]
        public void RemoveEventListener_StopsDispatch()
        {
            var context = CreateContext();
            var connection = NetworkInformationAPI.CreateNetworkInformation(context);
            var addEventListener = connection.Get("addEventListener").AsFunction();
            var removeEventListener = connection.Get("removeEventListener").AsFunction();
            var dispatchEvent = connection.Get("dispatchEvent").AsFunction();

            var listener = FenValue.FromFunction(new FenFunction("listener", (args, _) =>
            {
                throw new InvalidOperationException("Listener should not run after removeEventListener.");
            }));

            addEventListener.Invoke(new[] { FenValue.FromString("change"), listener }, context);
            removeEventListener.Invoke(new[] { FenValue.FromString("change"), listener }, context);
            dispatchEvent.Invoke(new[] { CreateChangeEvent() }, context);

            EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
        }

        [Fact]
        public void ConnectionListeners_AreRuntimeLocal()
        {
            var contextA = CreateContext();
            var contextB = CreateContext();
            var connectionA = NetworkInformationAPI.CreateNetworkInformation(contextA);
            var connectionB = NetworkInformationAPI.CreateNetworkInformation(contextB);

            var countA = 0;
            var countB = 0;

            connectionA.Get("addEventListener").AsFunction().Invoke(
                new[]
                {
                    FenValue.FromString("change"),
                    FenValue.FromFunction(new FenFunction("listenerA", (args, _) =>
                    {
                        countA++;
                        return FenValue.Undefined;
                    }))
                },
                contextA);

            connectionB.Get("addEventListener").AsFunction().Invoke(
                new[]
                {
                    FenValue.FromString("change"),
                    FenValue.FromFunction(new FenFunction("listenerB", (args, _) =>
                    {
                        countB++;
                        return FenValue.Undefined;
                    }))
                },
                contextB);

            connectionB.Get("dispatchEvent").AsFunction().Invoke(new[] { CreateChangeEvent() }, contextB);
            EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();

            Assert.Equal(0, countA);
            Assert.Equal(1, countB);
        }

        private static FenValue CreateChangeEvent()
        {
            var evt = new FenObject();
            evt.Set("type", FenValue.FromString("change"));
            return FenValue.FromObject(evt);
        }

        private static FenBrowser.FenEngine.Core.ExecutionContext CreateContext()
        {
            return new FenBrowser.FenEngine.Core.ExecutionContext(new PermissionManager(JsPermissions.StandardWeb))
            {
                DocumentUrl = new Uri("https://example.com/page")
            };
        }
    }
}
