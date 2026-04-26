using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Web Serial API implementation per W3C spec.
    /// https://wicg.github.io/serial/
    /// </summary>
    public static class SerialAPI
    {
        private static readonly List<SerialPort> _availablePorts = new();
        private static FenValue _onConnectHandler;
        private static FenValue _onDisconnectHandler;
        private static IExecutionContext _context;

        public static FenObject CreateSerial(IExecutionContext context)
        {
            _context = context;
            var serial = new FenObject();

            _onConnectHandler = FenValue.Null;
            serial.Set("onconnect", _onConnectHandler);

            _onDisconnectHandler = FenValue.Null;
            serial.Set("ondisconnect", _onDisconnectHandler);

            serial.Set("getPorts", FenValue.FromFunction(new FenFunction("getPorts", (args, thisVal) =>
            {
                var portsArray = new FenObject();
                portsArray.Set("length", FenValue.FromNumber(_availablePorts.Count));
                for (int i = 0; i < _availablePorts.Count; i++)
                {
                    portsArray.Set(i.ToString(), FenValue.FromObject(_availablePorts[i]));
                }
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(portsArray), _context));
            })));

            serial.Set("requestPort", FenValue.FromFunction(new FenFunction("requestPort", (args, thisVal) =>
            {
                EngineLogCompat.Warn("[SerialAPI] requestPort() called - serial port picker UI not yet implemented", LogCategory.JavaScript);
                return FenValue.FromObject(ResolvedThenable.Rejected("NotFoundError: No serial ports selected", _context));
            })));

            SetupEventTargetMethods(serial, context);
            return serial;
        }

        private static void SetupEventTargetMethods(FenObject serial, IExecutionContext context)
        {
            serial.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();
                var listener = args[1];
                if (type == "connect") _onConnectHandler = listener;
                else if (type == "disconnect") _onDisconnectHandler = listener;
                return FenValue.Undefined;
            })));

            serial.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (args, thisVal) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();
                if (type == "connect") _onConnectHandler = FenValue.Null;
                else if (type == "disconnect") _onDisconnectHandler = FenValue.Null;
                return FenValue.Undefined;
            })));
        }
    }
}