using System;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Represents a SerialPort connection per W3C Web Serial API spec.
    /// https://wicg.github.io/serial/#serialport-interface
    /// </summary>
    public sealed class SerialPort : FenObject
    {
        public string Url { get; }
        public bool IsOpen { get; private set; }
        public int BaudRate { get; private set; }

        public SerialPort(string url)
        {
            Url = url;
            IsOpen = false;
            InternalClass = "SerialPort";
        }

        public FenObject GetInfo()
        {
            var info = new FenObject();
            info.Set("usbVendorId", FenValue.FromNumber(0));
            info.Set("usbProductId", FenValue.FromNumber(0));
            info.Set("bluetoothServiceClassId", FenValue.Null);
            info.Set("bluetoothDeviceId", FenValue.Null);
            return info;
        }

        public FenValue Open(FenValue[] options, IExecutionContext context)
        {
            if (IsOpen)
                return FenValue.FromObject(ResolvedThenable.Rejected("InvalidStateError: Port is already open", context));

            int baudRate = 9600;
            int dataBits = 8;
            int stopBits = 1;
            string parity = "none";

            if (options.Length > 0 && options[0].IsObject)
            {
                var opts = options[0].AsObject();
                var br = opts.Get("baudRate");
                if (br.IsNumber) baudRate = (int)br.AsNumber();
                var db = opts.Get("databits");
                if (db.IsNumber) dataBits = (int)db.AsNumber();
                var sb = opts.Get("stopbits");
                if (sb.IsNumber) stopBits = (int)sb.AsNumber();
                var pr = opts.Get("parity");
                if (!pr.IsNull) parity = pr.ToString();
            }

            BaudRate = baudRate;
            IsOpen = true;

            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, context));
        }

        public FenValue Close(IExecutionContext context)
        {
            if (!IsOpen)
                return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, context));

            IsOpen = false;
            return FenValue.FromObject(ResolvedThenable.Resolved(FenValue.Undefined, context));
        }

        public FenObject GetSignals()
        {
            var signals = new FenObject();
            signals.Set("dtr", FenValue.FromBoolean(IsOpen));
            signals.Set("rts", FenValue.FromBoolean(true));
            signals.Set("cts", FenValue.FromBoolean(true));
            signals.Set("dsr", FenValue.FromBoolean(false));
            signals.Set("cd", FenValue.FromBoolean(false));
            signals.Set("rng", FenValue.FromBoolean(false));
            return signals;
        }
    }
}