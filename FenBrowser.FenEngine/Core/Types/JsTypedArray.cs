using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Core.Types
{
    public class JsArrayBuffer : FenObject
    {
        public byte[] Data { get; private set; }

        public JsArrayBuffer(int length)
        {
            Data = new byte[length];
            Set("byteLength", FenValue.FromNumber(length));
            Set("slice", FenValue.FromFunction(new FenFunction("slice", Slice)));
        }

        private FenValue Slice(FenValue[] args, FenValue thisVal)
        {
            int begin = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            int end = args.Length > 1 ? (int)args[1].ToNumber() : Data.Length;
            
            if (begin < 0) begin += Data.Length;
            if (end < 0) end += Data.Length;
            begin = Math.Max(0, Math.Min(begin, Data.Length));
            end = Math.Max(0, Math.Min(end, Data.Length));
            
            if (end < begin) end = begin;

            var newLen = end - begin;
            var newBuf = new JsArrayBuffer(newLen);
            Array.Copy(Data, begin, newBuf.Data, 0, newLen);
            return FenValue.FromObject(newBuf);
        }
    }

    public abstract class JsTypedArrayView : FenObject
    {
        public JsArrayBuffer Buffer { get; protected set; }
        public int ByteOffset { get; protected set; }
        public int ByteLength { get; protected set; }

        protected void InitView(JsArrayBuffer buffer, int byteOffset, int byteLength)
        {
            Buffer = buffer;
            ByteOffset = byteOffset;
            ByteLength = byteLength;

            Set("buffer", FenValue.FromObject(Buffer));
            Set("byteOffset", FenValue.FromNumber(ByteOffset));
            Set("byteLength", FenValue.FromNumber(ByteLength));
        }
    }

    public class JsDataView : JsTypedArrayView
    {
        public JsDataView(JsArrayBuffer buffer, int byteOffset = 0, int byteLength = -1)
        {
            if (byteLength == -1) byteLength = buffer.Data.Length - byteOffset;
            InitView(buffer, byteOffset, byteLength);

            // Methods
            Set("getUint8", FenValue.FromFunction(new FenFunction("getUint8", (a,t) => GetVal(a, 1, (d,idx) => d[idx]))));
            Set("setUint8", FenValue.FromFunction(new FenFunction("setUint8", (a,t) => SetVal(a, 1, (d,idx,v) => d[idx]=(byte)v))));
        }

        private FenValue GetVal(FenValue[] args, int size, Func<byte[], int, double> reader) {
            int offset = (int)args[0].ToNumber();
            if (offset + size > ByteLength) throw new Exception("RangeError");
            return FenValue.FromNumber(reader(Buffer.Data, ByteOffset + offset));
        }
        
        private FenValue SetVal(FenValue[] args, int size, Action<byte[], int, double> writer) {
             int offset = (int)args[0].ToNumber();
             double val = args[1].ToNumber();
             if (offset + size > ByteLength) throw new Exception("RangeError");
             writer(Buffer.Data, ByteOffset + offset, val);
             return FenValue.Undefined;
        }
    }

    public abstract class JsTypedArray : JsTypedArrayView
    {
        public int Length { get; protected set; }
        public int BytesPerElement { get; protected set; }

        protected JsTypedArray(int bytesPerElement)
        {
            BytesPerElement = bytesPerElement;
            Set("BYTES_PER_ELEMENT", FenValue.FromNumber(BytesPerElement));
        }
        
        protected void Init(IValue arg0, IValue arg1, IValue arg2)
        {
             if (arg0 != null && arg0.IsNumber)
             {
                 Length = (int)arg0.ToNumber();
                 InitView(new JsArrayBuffer(Length * BytesPerElement), 0, Length * BytesPerElement);
             }
             else if (arg0 != null && arg0.IsObject && arg0.AsObject() is JsArrayBuffer buf)
             {
                 int offset = arg1 != null ? (int)arg1.ToNumber() : 0;
                 int len = arg2 != null ? (int)arg2.ToNumber() : (buf.Data.Length - offset) / BytesPerElement;
                 Length = len;
                 InitView(buf, offset, len * BytesPerElement);
             }
             else if (arg0 != null && arg0.IsObject) 
             {
                 var obj = arg0.AsObject();
                 var lenVal = obj.Get("length");
                 Length = (int)lenVal.ToNumber();
                 var newBuf = new JsArrayBuffer(Length * BytesPerElement);
                 InitView(newBuf, 0, Length * BytesPerElement);
                 
                 for(int k=0; k<Length; k++)
                 {
                     var v = obj.Get(k.ToString());
                     SetIndex(k, v != null ? v.ToNumber() : 0);
                 }
             }
             else
             {
                 Length = 0;
                 InitView(new JsArrayBuffer(0), 0, 0);
             }
             
             Set("length", FenValue.FromNumber(Length));
        }

        public abstract double GetIndex(int index);
        public abstract void SetIndex(int index, double value);
        
        // Since FenObject methods are virtual now, we can override Get/Set if we want array index access.
        // For now, relies on explicit methods or manual usage.
    }

    public class JsUint8Array : JsTypedArray
    {
        public JsUint8Array(IValue a0 = null, IValue a1 = null, IValue a2 = null) : base(1) { Init(a0, a1, a2); }
        public override double GetIndex(int index) => Buffer.Data[ByteOffset + index];
        public override void SetIndex(int index, double value) => Buffer.Data[ByteOffset + index] = (byte)value;
    }
    
    public class JsFloat32Array : JsTypedArray
    {
         public JsFloat32Array(IValue a0 = null, IValue a1 = null, IValue a2 = null) : base(4) { Init(a0, a1, a2); }
         public override double GetIndex(int index) => BitConverter.ToSingle(Buffer.Data, ByteOffset + index * 4);
         public override void SetIndex(int index, double value) {
             byte[] b = BitConverter.GetBytes((float)value);
             Array.Copy(b, 0, Buffer.Data, ByteOffset + index * 4, 4);
         }
    }
}
