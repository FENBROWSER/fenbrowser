using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Web Serial API implementation per W3C spec.
    /// https://wicg.github.io/serial/
    /// </summary>
    public static class SerialAPI
    {
        private sealed class SerialState
        {
            public FenValue OnConnectHandler { get; set; } = FenValue.Null;
            public FenValue OnDisconnectHandler { get; set; } = FenValue.Null;
        }

        public static FenObject CreateSerial(IExecutionContext context)
        {
            var serial = new FenObject();
            var state = new SerialState();

            serial.Set("onconnect", state.OnConnectHandler);
            serial.Set("ondisconnect", state.OnDisconnectHandler);

            serial.Set("getPorts", FenValue.FromFunction(new FenFunction("getPorts", (args, _) =>
            {
                if (!IsSecureContext(context))
                    return Reject("NotAllowedError: navigator.serial.getPorts() requires a secure context.", context);

                // Keep compatibility with existing runtime tests that consume this shim
                // as an immediately-settled thenable in the same task.
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.FromObject(FenObject.CreateArray())));
            })));

            serial.Set("requestPort", FenValue.FromFunction(new FenFunction("requestPort", (args, _) =>
            {
                if (!IsSecureContext(context))
                    return Reject("NotAllowedError: navigator.serial.requestPort() requires a secure context.", context);

                if (!HasSerialPermission(context, "navigator.serial.requestPort"))
                    return Reject("NotAllowedError: Permission denied for serial devices.", context);

                if (args.Length > 0 && !args[0].IsObject && !args[0].IsUndefined && !args[0].IsNull)
                    return Reject("TypeError: requestPort options must be an object.", context);

                EngineLogCompat.Warn("[SerialAPI] requestPort blocked: serial picker backend is not available", LogCategory.JavaScript);
                return Reject("NotFoundError: No serial ports are available.", context);
            })));

            SetupEventTargetMethods(serial, state);
            return serial;
        }

        private static void SetupEventTargetMethods(FenObject serial, SerialState state)
        {
            serial.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, _) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();
                var listener = args[1];

                if (type == "connect")
                {
                    state.OnConnectHandler = listener;
                    serial.Set("onconnect", listener);
                }
                else if (type == "disconnect")
                {
                    state.OnDisconnectHandler = listener;
                    serial.Set("ondisconnect", listener);
                }

                return FenValue.Undefined;
            })));

            serial.Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (args, _) =>
            {
                if (args.Length < 2) return FenValue.Undefined;
                var type = args[0].ToString();

                if (type == "connect")
                {
                    state.OnConnectHandler = FenValue.Null;
                    serial.Set("onconnect", FenValue.Null);
                }
                else if (type == "disconnect")
                {
                    state.OnDisconnectHandler = FenValue.Null;
                    serial.Set("ondisconnect", FenValue.Null);
                }

                return FenValue.Undefined;
            })));
        }

        private static bool HasSerialPermission(IExecutionContext context, string operation)
        {
            var permissions = context?.Permissions;
            if (permissions == null)
                return false;

            return permissions.CheckAndLog(JsPermissions.Serial, operation);
        }

        private static bool IsSecureContext(IExecutionContext context)
        {
            var documentUrl = context?.DocumentUrl;
            if (documentUrl == null)
                return true;

            if (!documentUrl.IsAbsoluteUri)
                return false;

            var scheme = documentUrl.Scheme ?? string.Empty;
            if (scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return true;

            if (scheme.Equals("fen", StringComparison.OrdinalIgnoreCase))
                return true;

            if (scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
                return true;

            if (scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                return IsLoopbackHost(documentUrl.Host);

            return false;
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return false;

            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return true;

            if (host.Equals("::1", StringComparison.OrdinalIgnoreCase))
                return true;

            if (host.StartsWith("127.", StringComparison.Ordinal))
                return true;

            return false;
        }

        private static FenValue Reject(string reason, IExecutionContext context)
        {
            return FenValue.FromObject(ResolvedThenable.Rejected(reason, context));
        }
    }
}
