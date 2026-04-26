using System;
using FenBrowser.Core.Logging;
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
        private static string _effectiveType = "4g";
        private static double _rtt = 50;
        private static double _downlink = 10;
        private static bool _saveData = false;
        private static FenObject _changeListeners;
        private static IExecutionContext _context;

        public static FenObject CreateNetworkInformation(IExecutionContext context)
        {
            _context = context;
            var connection = new FenObject();

            connection.Set("effectiveType", FenValue.FromString(_effectiveType));
            connection.Set("rtt", FenValue.FromNumber(_rtt));
            connection.Set("downlink", FenValue.FromNumber(_downlink));
            connection.Set("saveData", FenValue.FromBoolean(_saveData));

            connection.Set("onchange", FenValue.Null);

            _changeListeners = new FenObject();
            connection.Set("__changeListeners", FenValue.FromObject(_changeListeners));

            SetupEventTargetMethods(connection, context);

            return connection;
        }

        private static void SetupEventTargetMethods(FenObject connection, IExecutionContext context)
        {
            connection.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();
                var listener = args[1];
                if (type == "change")
                {
                    _changeListeners.Set("listener", listener);
                }
                return FenValue.Undefined;
            })));

            connection.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();
                if (type == "change")
                {
                    _changeListeners.Delete("listener");
                }
                return FenValue.Undefined;
            })));

connection.Set("dispatchEvent", FenValue.FromFunction(new FenFunction("dispatchEvent", (args, thisVal) =>
{
if (args.Length < 1) return FenValue.FromBoolean(false);
var eventArg = args[0];
string eventType = "";
if (eventArg.IsObject)
{
var typeVal = eventArg.AsObject().Get("type");
if (!typeVal.IsNull && !typeVal.IsUndefined)
eventType = typeVal.ToString();
}
if (eventType == "change")
{
FireChangeEvent();
}
return FenValue.FromBoolean(true);
})));
        }

        private static void FireChangeEvent()
        {
            if (_changeListeners == null || _context == null) return;
            var listener = _changeListeners.Get("listener");
            if (listener.IsFunction)
            {
                var eventObj = new FenObject();
                eventObj.Set("type", FenValue.FromString("change"));
                eventObj.Set("bubbles", FenValue.FromBoolean(false));
                eventObj.Set("cancelable", FenValue.FromBoolean(false));
                eventObj.Set("composed", FenValue.FromBoolean(false));

                EventLoopCoordinator.Instance.ScheduleMicrotask(() =>
                {
                    listener.AsFunction()?.Invoke(new[] { FenValue.FromObject(eventObj) }, _context);
                });
            }
        }

        public static void UpdateConnectionMetrics(string effectiveType, double rtt, double downlink, bool saveData)
        {
            bool changed = false;

            if (_effectiveType != effectiveType)
            {
                _effectiveType = effectiveType;
                changed = true;
            }
            if (_rtt != rtt)
            {
                _rtt = rtt;
                changed = true;
            }
            if (_downlink != downlink)
            {
                _downlink = downlink;
                changed = true;
            }
            if (_saveData != saveData)
            {
                _saveData = saveData;
                changed = true;
            }

            if (changed)
            {
                FireChangeEvent();
            }
        }

        public static string EffectiveType => _effectiveType;
        public static double Rtt => _rtt;
        public static double Downlink => _downlink;
        public static bool SaveData => _saveData;
    }
}