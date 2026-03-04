using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.Core.Types
{
    public class JsArrayBuffer : FenObject
    {
        public byte[] Data { get; private set; }
        private bool _detached = false;

        public JsArrayBuffer(int length)
        {
            Data = new byte[length];
            Set("byteLength", FenValue.FromNumber(length));
            Set("detached", FenValue.FromBoolean(false));
            Set("slice", FenValue.FromFunction(new FenFunction("slice", Slice)));
            // ES2024: transfer([newByteLength]) — detaches this buffer, returns new one with the data
            Set("transfer", FenValue.FromFunction(new FenFunction("transfer", (args, thisVal) =>
            {
                if (_detached) throw new FenTypeError("TypeError: Cannot transfer a detached ArrayBuffer");
                int newLen = args.Length > 0 && !args[0].IsUndefined ? (int)args[0].ToNumber() : Data.Length;
                var newBuf = new JsArrayBuffer(newLen);
                Array.Copy(Data, newBuf.Data, Math.Min(Data.Length, newLen));
                // Detach this buffer
                _detached = true;
                Data = Array.Empty<byte>();
                Set("byteLength", FenValue.FromNumber(0));
                Set("detached", FenValue.FromBoolean(true));
                return FenValue.FromObject(newBuf);
            })));
            // ES2024: transferToFixedLength([newByteLength]) — same as transfer
            Set("transferToFixedLength", FenValue.FromFunction(new FenFunction("transferToFixedLength", (args, thisVal) =>
            {
                if (_detached) throw new FenTypeError("TypeError: Cannot transfer a detached ArrayBuffer");
                int newLen = args.Length > 0 && !args[0].IsUndefined ? (int)args[0].ToNumber() : Data.Length;
                var newBuf = new JsArrayBuffer(newLen);
                Array.Copy(Data, newBuf.Data, Math.Min(Data.Length, newLen));
                _detached = true;
                Data = Array.Empty<byte>();
                Set("byteLength", FenValue.FromNumber(0));
                Set("detached", FenValue.FromBoolean(true));
                return FenValue.FromObject(newBuf);
            })));
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

            // Getters
            Set("getUint8",   FenValue.FromFunction(new FenFunction("getUint8",   (a,t) => GetInt(a, 1, false, false, false, false))));
            Set("getInt8",    FenValue.FromFunction(new FenFunction("getInt8",    (a,t) => GetInt(a, 1, true, false, false, false))));
            Set("getUint16",  FenValue.FromFunction(new FenFunction("getUint16",  (a,t) => GetInt(a, 2, false, true, false, false))));
            Set("getInt16",   FenValue.FromFunction(new FenFunction("getInt16",   (a,t) => GetInt(a, 2, true, true, false, false))));
            Set("getUint32",  FenValue.FromFunction(new FenFunction("getUint32",  (a,t) => GetInt(a, 4, false, true, false, false))));
            Set("getInt32",   FenValue.FromFunction(new FenFunction("getInt32",   (a,t) => GetInt(a, 4, true, true, false, false))));
            Set("getFloat32", FenValue.FromFunction(new FenFunction("getFloat32", (a,t) => GetFloat(a, 4))));
            Set("getFloat64", FenValue.FromFunction(new FenFunction("getFloat64", (a,t) => GetFloat(a, 8))));
            Set("getBigInt64",  FenValue.FromFunction(new FenFunction("getBigInt64",  (a,t) => GetBigInt(a, true))));
            Set("getBigUint64", FenValue.FromFunction(new FenFunction("getBigUint64", (a,t) => GetBigInt(a, false))));
            // Setters
            Set("setUint8",   FenValue.FromFunction(new FenFunction("setUint8",   (a,t) => SetInt(a, 1, false, false))));
            Set("setInt8",    FenValue.FromFunction(new FenFunction("setInt8",    (a,t) => SetInt(a, 1, true, false))));
            Set("setUint16",  FenValue.FromFunction(new FenFunction("setUint16",  (a,t) => SetInt(a, 2, false, true))));
            Set("setInt16",   FenValue.FromFunction(new FenFunction("setInt16",   (a,t) => SetInt(a, 2, true, true))));
            Set("setUint32",  FenValue.FromFunction(new FenFunction("setUint32",  (a,t) => SetInt(a, 4, false, true))));
            Set("setInt32",   FenValue.FromFunction(new FenFunction("setInt32",   (a,t) => SetInt(a, 4, true, true))));
            Set("setFloat32", FenValue.FromFunction(new FenFunction("setFloat32", (a,t) => SetFloat(a, 4))));
            Set("setFloat64", FenValue.FromFunction(new FenFunction("setFloat64", (a,t) => SetFloat(a, 8))));
            Set("setBigInt64",  FenValue.FromFunction(new FenFunction("setBigInt64",  (a,t) => SetBigInt(a, true))));
            Set("setBigUint64", FenValue.FromFunction(new FenFunction("setBigUint64", (a,t) => SetBigInt(a, false))));
        }

        private bool IsLittleEndian(FenValue[] args, int valueArgIdx)
        {
            // littleEndian is the argument after the value (for setters) or after offset (for getters)
            int idx = valueArgIdx;
            return args.Length > idx && args[idx].ToBoolean();
        }

        private FenValue GetInt(FenValue[] args, int size, bool signed, bool multiByte, bool unused1, bool unused2)
        {
            int offset = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            if (offset < 0 || offset + size > ByteLength) throw new FenRangeError("RangeError: offset out of bounds");
            bool le = args.Length > 1 && args[1].ToBoolean();
            int abs = ByteOffset + offset;
            byte[] d = Buffer.Data;
            if (size == 1)
            {
                return signed ? FenValue.FromNumber((sbyte)d[abs]) : FenValue.FromNumber(d[abs]);
            }
            else if (size == 2)
            {
                ushort raw = le ? (ushort)(d[abs] | d[abs+1] << 8) : (ushort)(d[abs] << 8 | d[abs+1]);
                return signed ? FenValue.FromNumber((short)raw) : FenValue.FromNumber(raw);
            }
            else // 4
            {
                uint raw = le
                    ? (uint)(d[abs] | d[abs+1]<<8 | d[abs+2]<<16 | d[abs+3]<<24)
                    : (uint)(d[abs]<<24 | d[abs+1]<<16 | d[abs+2]<<8 | d[abs+3]);
                return signed ? FenValue.FromNumber((int)raw) : FenValue.FromNumber(raw);
            }
        }

        private FenValue GetFloat(FenValue[] args, int size)
        {
            int offset = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            if (offset < 0 || offset + size > ByteLength) throw new FenRangeError("RangeError: offset out of bounds");
            bool le = args.Length > 1 && args[1].ToBoolean();
            int abs = ByteOffset + offset;
            byte[] d = Buffer.Data;
            byte[] tmp = new byte[size];
            System.Buffer.BlockCopy(d, abs, tmp, 0, size);
            if (le != BitConverter.IsLittleEndian) Array.Reverse(tmp);
            return size == 4
                ? FenValue.FromNumber(BitConverter.ToSingle(tmp, 0))
                : FenValue.FromNumber(BitConverter.ToDouble(tmp, 0));
        }

        private FenValue GetBigInt(FenValue[] args, bool signed)
        {
            int offset = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            if (offset < 0 || offset + 8 > ByteLength) throw new FenRangeError("RangeError: offset out of bounds");
            bool le = args.Length > 1 && args[1].ToBoolean();
            int abs = ByteOffset + offset;
            byte[] d = Buffer.Data;
            byte[] tmp = new byte[8];
            System.Buffer.BlockCopy(d, abs, tmp, 0, 8);
            if (le != BitConverter.IsLittleEndian) Array.Reverse(tmp);
            // Return as number (loses precision for > 53-bit values but keeps API surface)
            return signed
                ? FenValue.FromNumber((double)BitConverter.ToInt64(tmp, 0))
                : FenValue.FromNumber((double)BitConverter.ToUInt64(tmp, 0));
        }

        private FenValue SetInt(FenValue[] args, int size, bool signed, bool multiByte)
        {
            int offset = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            if (offset < 0 || offset + size > ByteLength) throw new FenRangeError("RangeError: offset out of bounds");
            double val = args.Length > 1 ? args[1].ToNumber() : 0;
            bool le = args.Length > 2 && args[2].ToBoolean();
            int abs = ByteOffset + offset;
            byte[] d = Buffer.Data;
            if (size == 1)
            {
                d[abs] = (byte)(sbyte)val;
            }
            else if (size == 2)
            {
                ushort v = (ushort)(short)val;
                if (le) { d[abs] = (byte)v; d[abs+1] = (byte)(v>>8); }
                else    { d[abs] = (byte)(v>>8); d[abs+1] = (byte)v; }
            }
            else // 4
            {
                uint v = (uint)(int)val;
                if (le) { d[abs]=(byte)v; d[abs+1]=(byte)(v>>8); d[abs+2]=(byte)(v>>16); d[abs+3]=(byte)(v>>24); }
                else    { d[abs]=(byte)(v>>24); d[abs+1]=(byte)(v>>16); d[abs+2]=(byte)(v>>8); d[abs+3]=(byte)v; }
            }
            return FenValue.Undefined;
        }

        private FenValue SetFloat(FenValue[] args, int size)
        {
            int offset = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            if (offset < 0 || offset + size > ByteLength) throw new FenRangeError("RangeError: offset out of bounds");
            double val = args.Length > 1 ? args[1].ToNumber() : 0;
            bool le = args.Length > 2 && args[2].ToBoolean();
            byte[] tmp = size == 4 ? BitConverter.GetBytes((float)val) : BitConverter.GetBytes(val);
            if (le != BitConverter.IsLittleEndian) Array.Reverse(tmp);
            System.Buffer.BlockCopy(tmp, 0, Buffer.Data, ByteOffset + offset, size);
            return FenValue.Undefined;
        }

        private FenValue SetBigInt(FenValue[] args, bool signed)
        {
            int offset = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            if (offset < 0 || offset + 8 > ByteLength) throw new FenRangeError("RangeError: offset out of bounds");
            double val = args.Length > 1 ? args[1].ToNumber() : 0;
            bool le = args.Length > 2 && args[2].ToBoolean();
            byte[] tmp = signed
                ? BitConverter.GetBytes((long)val)
                : BitConverter.GetBytes((ulong)val);
            if (le != BitConverter.IsLittleEndian) Array.Reverse(tmp);
            System.Buffer.BlockCopy(tmp, 0, Buffer.Data, ByteOffset + offset, 8);
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
             InitPrototypeMethods(null);
        }

        public abstract double GetIndex(int index);
        public abstract void SetIndex(int index, double value);

        protected void InitPrototypeMethods(IExecutionContext context)
        {
            // forEach
            Set("forEach", FenValue.FromFunction(new FenFunction("forEach", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var cb = args[0].AsFunction();
                for (int i = 0; i < Length; i++)
                    cb.Invoke(new[] { FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context);
                return FenValue.Undefined;
            })));

            // map
            Set("map", FenValue.FromFunction(new FenFunction("map", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return thisVal;
                var cb = args[0].AsFunction();
                var result = new FenObject();
                for (int i = 0; i < Length; i++)
                {
                    var r = cb.Invoke(new[] { FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context);
                    result.Set(i.ToString(), r, null);
                }
                result.Set("length", FenValue.FromNumber(Length), null);
                return FenValue.FromObject(result);
            })));

            // filter
            Set("filter", FenValue.FromFunction(new FenFunction("filter", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.FromObject(new FenObject());
                var cb = args[0].AsFunction();
                var list = new List<FenValue>();
                for (int i = 0; i < Length; i++)
                {
                    var v = FenValue.FromNumber(GetIndex(i));
                    if (cb.Invoke(new[] { v, FenValue.FromNumber(i), thisVal }, context).ToBoolean())
                        list.Add(v);
                }
                var result = new FenObject();
                for (int i = 0; i < list.Count; i++) result.Set(i.ToString(), list[i], null);
                result.Set("length", FenValue.FromNumber(list.Count), null);
                return FenValue.FromObject(result);
            })));

            // reduce
            Set("reduce", FenValue.FromFunction(new FenFunction("reduce", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var cb = args[0].AsFunction();
                FenValue acc = args.Length > 1 ? args[1] : FenValue.Undefined;
                int start = 0;
                if (args.Length < 2 && Length > 0) { acc = FenValue.FromNumber(GetIndex(0)); start = 1; }
                for (int i = start; i < Length; i++)
                    acc = cb.Invoke(new[] { acc, FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context);
                return acc;
            })));

            // find
            Set("find", FenValue.FromFunction(new FenFunction("find", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var cb = args[0].AsFunction();
                for (int i = 0; i < Length; i++)
                {
                    var v = FenValue.FromNumber(GetIndex(i));
                    if (cb.Invoke(new[] { v, FenValue.FromNumber(i), thisVal }, context).ToBoolean()) return v;
                }
                return FenValue.Undefined;
            })));

            // findIndex
            Set("findIndex", FenValue.FromFunction(new FenFunction("findIndex", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.FromNumber(-1);
                var cb = args[0].AsFunction();
                for (int i = 0; i < Length; i++)
                    if (cb.Invoke(new[] { FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context).ToBoolean()) return FenValue.FromNumber(i);
                return FenValue.FromNumber(-1);
            })));

            // some
            Set("some", FenValue.FromFunction(new FenFunction("some", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.FromBoolean(false);
                var cb = args[0].AsFunction();
                for (int i = 0; i < Length; i++)
                    if (cb.Invoke(new[] { FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context).ToBoolean()) return FenValue.FromBoolean(true);
                return FenValue.FromBoolean(false);
            })));

            // every
            Set("every", FenValue.FromFunction(new FenFunction("every", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.FromBoolean(true);
                var cb = args[0].AsFunction();
                for (int i = 0; i < Length; i++)
                    if (!cb.Invoke(new[] { FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context).ToBoolean()) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(true);
            })));

            // includes
            Set("includes", FenValue.FromFunction(new FenFunction("includes", (args, thisVal) =>
            {
                double target = args.Length > 0 ? args[0].ToNumber() : double.NaN;
                for (int i = 0; i < Length; i++)
                    if (GetIndex(i) == target || (double.IsNaN(target) && double.IsNaN(GetIndex(i)))) return FenValue.FromBoolean(true);
                return FenValue.FromBoolean(false);
            })));

            // indexOf
            Set("indexOf", FenValue.FromFunction(new FenFunction("indexOf", (args, thisVal) =>
            {
                double target = args.Length > 0 ? args[0].ToNumber() : double.NaN;
                int from = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (from < 0) from = Math.Max(0, Length + from);
                for (int i = from; i < Length; i++)
                    if (GetIndex(i) == target) return FenValue.FromNumber(i);
                return FenValue.FromNumber(-1);
            })));

            // fill
            Set("fill", FenValue.FromFunction(new FenFunction("fill", (args, thisVal) =>
            {
                double val = args.Length > 0 ? args[0].ToNumber() : 0;
                int start = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                int end = args.Length > 2 ? (int)args[2].ToNumber() : Length;
                if (start < 0) start = Math.Max(0, Length + start); else start = Math.Min(start, Length);
                if (end < 0) end = Math.Max(0, Length + end); else end = Math.Min(end, Length);
                for (int i = start; i < end; i++) SetIndex(i, val);
                return thisVal;
            })));

            // copyWithin
            Set("copyWithin", FenValue.FromFunction(new FenFunction("copyWithin", (args, thisVal) =>
            {
                int to = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                int from = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                int final = args.Length > 2 ? (int)args[2].ToNumber() : Length;
                if (to < 0) to = Math.Max(0, Length + to); else to = Math.Min(to, Length);
                if (from < 0) from = Math.Max(0, Length + from); else from = Math.Min(from, Length);
                if (final < 0) final = Math.Max(0, Length + final); else final = Math.Min(final, Length);
                int count = Math.Min(final - from, Length - to);
                double[] tmp = new double[Math.Max(0, count)];
                for (int i = 0; i < count; i++) tmp[i] = GetIndex(from + i);
                for (int i = 0; i < count; i++) SetIndex(to + i, tmp[i]);
                return thisVal;
            })));

            // slice
            Set("slice", FenValue.FromFunction(new FenFunction("slice", (args, thisVal) =>
            {
                int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                int end = args.Length > 1 ? (int)args[1].ToNumber() : Length;
                if (start < 0) start = Math.Max(0, Length + start);
                if (end < 0) end = Math.Max(0, Length + end);
                start = Math.Min(start, Length);
                end = Math.Min(end, Length);
                int count = Math.Max(0, end - start);
                var result = new FenObject();
                for (int i = 0; i < count; i++) result.Set(i.ToString(), FenValue.FromNumber(GetIndex(start + i)), null);
                result.Set("length", FenValue.FromNumber(count), null);
                return FenValue.FromObject(result);
            })));

            // join
            Set("join", FenValue.FromFunction(new FenFunction("join", (args, thisVal) =>
            {
                string sep = args.Length > 0 ? args[0].ToString() : ",";
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < Length; i++) { if (i > 0) sb.Append(sep); sb.Append(GetIndex(i)); }
                return FenValue.FromString(sb.ToString());
            })));

            // reverse
            Set("reverse", FenValue.FromFunction(new FenFunction("reverse", (args, thisVal) =>
            {
                for (int i = 0, j = Length - 1; i < j; i++, j--)
                { double tmp = GetIndex(i); SetIndex(i, GetIndex(j)); SetIndex(j, tmp); }
                return thisVal;
            })));

            // sort
            Set("sort", FenValue.FromFunction(new FenFunction("sort", (args, thisVal) =>
            {
                var vals = new double[Length];
                for (int i = 0; i < Length; i++) vals[i] = GetIndex(i);
                Array.Sort(vals);
                for (int i = 0; i < Length; i++) SetIndex(i, vals[i]);
                return thisVal;
            })));

            // set(array[, offset])
            Set("set", FenValue.FromFunction(new FenFunction("set", (args, thisVal) =>
            {
                if (args.Length < 1 || !args[0].IsObject) return FenValue.Undefined;
                var src = args[0].AsObject();
                if (src == null) return FenValue.Undefined;
                int offset = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                var lenVal = src.Get("length", null);
                int srcLen = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0;
                for (int i = 0; i < srcLen && offset + i < Length; i++)
                {
                    var v = src.Get(i.ToString(), null);
                    SetIndex(offset + i, v.IsNumber ? v.ToNumber() : 0);
                }
                return FenValue.Undefined;
            })));

            // subarray(begin[, end])
            Set("subarray", FenValue.FromFunction(new FenFunction("subarray", (args, thisVal) =>
            {
                int start = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                int end = args.Length > 1 ? (int)args[1].ToNumber() : Length;
                if (start < 0) start = Math.Max(0, Length + start);
                if (end < 0) end = Math.Max(0, Length + end);
                start = Math.Min(start, Length);
                end = Math.Min(end, Length);
                // Return same type view over same buffer
                return thisVal; // simplified: return self for now
            })));
        }
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

