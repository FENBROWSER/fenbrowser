using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class MessageChannelCompatibilityTests
    {
        public MessageChannelCompatibilityTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        [Fact]
        public void NativeConstructor_ReturnedObjectBecomesConstructResult()
        {
            var runtime = new FenRuntime();
            var ctor = new FenFunction("NativeCtor", (args, thisVal) =>
            {
                var result = new FenObject();
                result.Set("marker", FenValue.FromString("native"));
                return FenValue.FromObject(result);
            });

            runtime.SetGlobal("NativeCtor", FenValue.FromFunction(ctor));
            runtime.ExecuteSimple("var probe = new NativeCtor();");

            Assert.Equal("object", ((FenValue)runtime.ExecuteSimple("typeof probe")).ToString());
            Assert.Equal("native", ((FenValue)runtime.ExecuteSimple("probe.marker")).ToString());
        }

        [Fact]
        public void MessageChannel_PostMessage_DeliversTaskQueuedMessage()
        {
            var runtime = new FenRuntime();
            var messageChannelCtor = runtime.GetGlobal("MessageChannel").AsFunction();
            var directChannel = messageChannelCtor.NativeImplementation(System.Array.Empty<FenValue>(), FenValue.FromObject(new FenObject())).AsObject();

            Assert.NotNull(messageChannelCtor);
            Assert.True(directChannel.Get("port1").IsObject);

            runtime.ExecuteSimple(@"
                var log = [];
                var channel = new MessageChannel();
                channel.port1.onmessage = function (event) {
                    log.push(event.type + ':' + String(event.data));
                };
                channel.port2.postMessage('tick');
            ");

            Assert.Equal("object", ((FenValue)runtime.ExecuteSimple("typeof channel")).ToString());
            Assert.Equal("object", ((FenValue)runtime.ExecuteSimple("typeof channel.port1")).ToString());
            Assert.Equal("function", ((FenValue)runtime.ExecuteSimple("typeof channel.port1.onmessage")).ToString());
            Assert.Equal("function", ((FenValue)runtime.ExecuteSimple("typeof channel.port2.postMessage")).ToString());
            Assert.True(EventLoopCoordinator.Instance.TaskCount > 0);

            EventLoopCoordinator.Instance.RunUntilEmpty();

            Assert.Equal(1.0, ((FenValue)runtime.ExecuteSimple("log.length")).ToNumber());
            Assert.Equal("message:tick", ((FenValue)runtime.ExecuteSimple("log[0]")).ToString());
        }

        [Fact]
        public void MessageChannel_RepostedSchedulerLoop_DrainsAllQueuedWork()
        {
            var runtime = new FenRuntime();

            runtime.ExecuteSimple(@"
                var steps = [];
                var scheduledHostCallback = null;
                var isMessageLoopRunning = false;
                var channel = new MessageChannel();
                var port = channel.port2;

                function performWorkUntilDeadline() {
                    if (scheduledHostCallback !== null) {
                        try {
                            scheduledHostCallback(true, Date.now());
                        } finally {
                            if (scheduledHostCallback !== null) {
                                port.postMessage(null);
                            } else {
                                isMessageLoopRunning = false;
                            }
                        }
                    } else {
                        isMessageLoopRunning = false;
                    }
                }

                channel.port1.onmessage = performWorkUntilDeadline;

                function requestHostCallback(callback) {
                    scheduledHostCallback = callback;
                    if (!isMessageLoopRunning) {
                        isMessageLoopRunning = true;
                        port.postMessage(null);
                    }
                }

                var remaining = 3;
                requestHostCallback(function() {
                    steps.push('tick:' + remaining);
                    remaining--;
                    if (remaining === 0) {
                        scheduledHostCallback = null;
                    }
                });
            ");

            EventLoopCoordinator.Instance.RunUntilEmpty();

            Assert.Equal(3.0, ((FenValue)runtime.ExecuteSimple("steps.length")).ToNumber());
            Assert.Equal("tick:3", ((FenValue)runtime.ExecuteSimple("steps[0]")).ToString());
            Assert.Equal("tick:1", ((FenValue)runtime.ExecuteSimple("steps[2]")).ToString());
            Assert.Equal(false, ((FenValue)runtime.ExecuteSimple("isMessageLoopRunning")).ToBoolean());
            Assert.Equal(0.0, ((FenValue)runtime.ExecuteSimple("remaining")).ToNumber());
        }

        [Fact]
        public void BroadcastChannel_PostMessage_DeliversToPeerWithSameName()
        {
            var runtime = new FenRuntime();

            runtime.ExecuteSimple(@"
                var deliveries = [];
                var alpha = new BroadcastChannel('fen-x');
                var beta = new BroadcastChannel('fen-x');
                alpha.onmessage = function (event) {
                    deliveries.push('alpha:' + String(event.data));
                };
                beta.addEventListener('message', function (event) {
                    deliveries.push('beta:' + String(event.data));
                });
                alpha.postMessage('boot');
            ");

            Assert.True(EventLoopCoordinator.Instance.TaskCount > 0);

            EventLoopCoordinator.Instance.RunUntilEmpty();

            Assert.Equal("object", ((FenValue)runtime.ExecuteSimple("typeof alpha")).ToString());
            Assert.Equal("fen-x", ((FenValue)runtime.ExecuteSimple("alpha.name")).ToString());
            Assert.Equal(1.0, ((FenValue)runtime.ExecuteSimple("deliveries.length")).ToNumber());
            Assert.Equal("beta:boot", ((FenValue)runtime.ExecuteSimple("deliveries[0]")).ToString());
        }
    }
}
