using System;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Network Information API implementation per WICG spec.
    /// https://wicg.github.io/netinfo/
    /// </summary>
    public static class NetworkInformationAPI
    {
        private sealed class ConnectionState
        {
            public string EffectiveType { get; set; } = "4g";
            public double Rtt { get; set; } = 50;
            public double Downlink { get; set; } = 10;
            public bool SaveData { get; set; }
            public FenValue EventListener { get; set; } = FenValue.Null;
            public IExecutionContext Context { get; init; }
        }

        public static FenObject CreateNetworkInformation(IExecutionContext context)
        {
            var state = new ConnectionState { Context = context };
            var connection = new FenObject();

            ApplyConnectionMetrics(connection, state);
            connection.Set("onchange", FenValue.Null);
            SetupEventTargetMethods(connection, state);

            return connection;
        }

        private static void SetupEventTargetMethods(FenObject connection, ConnectionState state)
        {
            connection.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, _) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();
                var listener = args[1];
                if (type == "change")
                {
                    state.EventListener = listener;
                }
                return FenValue.Undefined;
            })));

            connection.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (args, _) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();
                if (type == "change")
                {
                    state.EventListener = FenValue.Null;
                }
                return FenValue.Undefined;
            })));

            connection.Set("dispatchEvent", FenValue.FromFunction(new FenFunction("dispatchEvent", (args, _) =>
            {
                if (args.Length < 1) return FenValue.FromBoolean(false);

                var eventArg = args[0];
                var eventType = string.Empty;
                if (eventArg.IsObject)
                {
                    var typeVal = eventArg.AsObject().Get("type");
                    if (!typeVal.IsNull && !typeVal.IsUndefined)
                        eventType = typeVal.ToString();
                }

                if (eventType == "change")
                {
                    FireChangeEvent(state, connection);
                }

                return FenValue.FromBoolean(true);
            })));
        }

        private static void ApplyConnectionMetrics(FenObject connection, ConnectionState state)
        {
            connection.Set("effectiveType", FenValue.FromString(state.EffectiveType));
            connection.Set("rtt", FenValue.FromNumber(state.Rtt));
            connection.Set("downlink", FenValue.FromNumber(state.Downlink));
            connection.Set("saveData", FenValue.FromBoolean(state.SaveData));
        }

        private static void FireChangeEvent(ConnectionState state, FenObject connection)
        {
            var listener = state.EventListener;
            var onChange = connection.Get("onchange");
            var context = state.Context;
            if (context == null || (!listener.IsFunction && !onChange.IsFunction))
                return;

            var eventObj = new FenObject();
            eventObj.Set("type", FenValue.FromString("change"));
            eventObj.Set("bubbles", FenValue.FromBoolean(false));
            eventObj.Set("cancelable", FenValue.FromBoolean(false));
            eventObj.Set("composed", FenValue.FromBoolean(false));

            EventLoopCoordinator.Instance.ScheduleMicrotask(() =>
            {
                if (listener.IsFunction)
                    listener.AsFunction().Invoke(new[] { FenValue.FromObject(eventObj) }, context);
                if (onChange.IsFunction)
                    onChange.AsFunction().Invoke(new[] { FenValue.FromObject(eventObj) }, context);
            });
        }
    }
}
