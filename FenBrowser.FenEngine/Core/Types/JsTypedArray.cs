using System;
using System.Collections.Generic;
using System.Numerics;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Errors;

namespace FenBrowser.FenEngine.Core.Types
{
    public class JsArrayBuffer : FenObject
    {
        public byte[] Data { get; private set; }
        private bool _detached = false;
        // ECMA-262 §9.4.5.7: IsDetachedBuffer
        public bool IsDetached => _detached;

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

        /// <summary>
        /// ECMA-262 §9.4.5.7: Throws a TypeError if the backing buffer has been detached
        /// (e.g. via ArrayBuffer.prototype.transfer()).
        /// </summary>
        protected void CheckDetachedBuffer()
        {
            if (Buffer != null && Buffer.IsDetached)
                throw new FenTypeError("TypeError: Cannot perform operation on a detached ArrayBuffer");
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

        private void CheckDetached()
        {
            // ECMA-262 §9.4.5.7: If IsDetachedBuffer(buffer) is true, throw a TypeError.
            if (Buffer.IsDetached)
                throw new FenTypeError("TypeError: Cannot perform operation on a detached ArrayBuffer");
        }

        private FenValue GetInt(FenValue[] args, int size, bool signed, bool multiByte, bool unused1, bool unused2)
        {
            CheckDetached();
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
            CheckDetached();
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
            // ECMA-262 §25.3.2.3: getBigInt64 / getBigUint64 must return a BigInt value.
            CheckDetached();
            int offset = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            if (offset < 0 || offset + 8 > ByteLength) throw new FenRangeError("RangeError: offset out of bounds");
            bool le = args.Length > 1 && args[1].ToBoolean();
            int abs = ByteOffset + offset;
            byte[] d = Buffer.Data;
            byte[] tmp = new byte[8];
            System.Buffer.BlockCopy(d, abs, tmp, 0, 8);
            if (le != BitConverter.IsLittleEndian) Array.Reverse(tmp);
            if (signed)
            {
                long raw = BitConverter.ToInt64(tmp, 0);
                return FenValue.FromBigInt(new JsBigInt(new BigInteger(raw)));
            }
            else
            {
                ulong raw = BitConverter.ToUInt64(tmp, 0);
                return FenValue.FromBigInt(new JsBigInt(new BigInteger(raw)));
            }
        }

        private FenValue SetInt(FenValue[] args, int size, bool signed, bool multiByte)
        {
            CheckDetached();
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
            CheckDetached();
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
            // ECMA-262 §25.3.2.4: setBigInt64 / setBigUint64 must accept a BigInt value.
            CheckDetached();
            int offset = args.Length > 0 ? (int)args[0].ToNumber() : 0;
            if (offset < 0 || offset + 8 > ByteLength) throw new FenRangeError("RangeError: offset out of bounds");
            if (args.Length < 2 || !args[1].IsBigInt)
                throw new FenTypeError("TypeError: Cannot mix BigInt and other types, use explicit conversions");
            JsBigInt bigIntVal = args[1].AsBigInt();
            bool le = args.Length > 2 && args[2].ToBoolean();
            byte[] tmp = signed
                ? BitConverter.GetBytes((long)bigIntVal.Value)
                : BitConverter.GetBytes((ulong)bigIntVal.Value);
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

        // ECMA-262 §22.2.3.18: %TypedArray%.prototype.map and similar methods must return a new
        // TypedArray of the same type (§22.2.4.7 TypedArraySpeciesCreate).
        protected abstract JsTypedArray CreateSameType(int length);

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

        // Helper: create a JS iterator object from a next-function closure.
        // ECMA-262 §22.1.5.1: The iterator protocol requires next() + @@iterator returning self.
        private static FenObject MakeIterator(Func<FenValue> nextFn)
        {
            var iter = new FenObject();
            iter.Set("next", FenValue.FromFunction(new FenFunction("next", (a, t) => nextFn())));
            iter.SetSymbol(JsSymbol.Iterator, FenValue.FromFunction(
                new FenFunction("[Symbol.iterator]", (a, t) => FenValue.FromObject(iter))));
            return iter;
        }

        protected void InitPrototypeMethods(IExecutionContext context)
        {
            // forEach — ECMA-262 §22.2.3.14
            Set("forEach", FenValue.FromFunction(new FenFunction("forEach", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var cb = args[0].AsFunction();
                for (int i = 0; i < Length; i++)
                    cb.Invoke(new[] { FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context);
                return FenValue.Undefined;
            })));

            // map — ECMA-262 §22.2.3.18: returns new TypedArray of same type
            Set("map", FenValue.FromFunction(new FenFunction("map", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.FromObject(CreateSameType(Length));
                var cb = args[0].AsFunction();
                var result = CreateSameType(Length);
                for (int i = 0; i < Length; i++)
                {
                    var r = cb.Invoke(new[] { FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context);
                    result.SetIndex(i, r.ToNumber());
                }
                return FenValue.FromObject(result);
            })));

            // filter — ECMA-262 §22.2.3.9: returns new TypedArray of same type with matching elements
            Set("filter", FenValue.FromFunction(new FenFunction("filter", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.FromObject(CreateSameType(0));
                var cb = args[0].AsFunction();
                var list = new List<double>();
                for (int i = 0; i < Length; i++)
                {
                    var v = GetIndex(i);
                    if (cb.Invoke(new[] { FenValue.FromNumber(v), FenValue.FromNumber(i), thisVal }, context).ToBoolean())
                        list.Add(v);
                }
                var result = CreateSameType(list.Count);
                for (int i = 0; i < list.Count; i++) result.SetIndex(i, list[i]);
                return FenValue.FromObject(result);
            })));

            // reduce — ECMA-262 §22.2.3.21: reduce left to right
            Set("reduce", FenValue.FromFunction(new FenFunction("reduce", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var cb = args[0].AsFunction();
                FenValue acc = args.Length > 1 ? args[1] : FenValue.Undefined;
                int start = 0;
                if (args.Length < 2 && Length > 0) { acc = FenValue.FromNumber(GetIndex(0)); start = 1; }
                for (int i = start; i < Length; i++)
                    acc = cb.Invoke(new[] { acc, FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context);
                return acc;
            })));

            // reduceRight — ECMA-262 §22.2.3.22: reduce right to left
            Set("reduceRight", FenValue.FromFunction(new FenFunction("reduceRight", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var cb = args[0].AsFunction();
                FenValue acc = args.Length > 1 ? args[1] : FenValue.Undefined;
                int start = Length - 1;
                if (args.Length < 2 && Length > 0) { acc = FenValue.FromNumber(GetIndex(Length - 1)); start = Length - 2; }
                for (int i = start; i >= 0; i--)
                    acc = cb.Invoke(new[] { acc, FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context);
                return acc;
            })));

            // find — ECMA-262 §22.2.3.10
            Set("find", FenValue.FromFunction(new FenFunction("find", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var cb = args[0].AsFunction();
                for (int i = 0; i < Length; i++)
                {
                    var v = FenValue.FromNumber(GetIndex(i));
                    if (cb.Invoke(new[] { v, FenValue.FromNumber(i), thisVal }, context).ToBoolean()) return v;
                }
                return FenValue.Undefined;
            })));

            // findIndex — ECMA-262 §22.2.3.11
            Set("findIndex", FenValue.FromFunction(new FenFunction("findIndex", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.FromNumber(-1);
                var cb = args[0].AsFunction();
                for (int i = 0; i < Length; i++)
                    if (cb.Invoke(new[] { FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context).ToBoolean()) return FenValue.FromNumber(i);
                return FenValue.FromNumber(-1);
            })));

            // findLast — ES2023 §22.2.3.12: search from end, return value or undefined
            Set("findLast", FenValue.FromFunction(new FenFunction("findLast", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.Undefined;
                var cb = args[0].AsFunction();
                for (int i = Length - 1; i >= 0; i--)
                {
                    var v = FenValue.FromNumber(GetIndex(i));
                    if (cb.Invoke(new[] { v, FenValue.FromNumber(i), thisVal }, context).ToBoolean()) return v;
                }
                return FenValue.Undefined;
            })));

            // findLastIndex — ES2023 §22.2.3.13: search from end, return index or -1
            Set("findLastIndex", FenValue.FromFunction(new FenFunction("findLastIndex", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.FromNumber(-1);
                var cb = args[0].AsFunction();
                for (int i = Length - 1; i >= 0; i--)
                    if (cb.Invoke(new[] { FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context).ToBoolean())
                        return FenValue.FromNumber(i);
                return FenValue.FromNumber(-1);
            })));

            // some — ECMA-262 §22.2.3.25
            Set("some", FenValue.FromFunction(new FenFunction("some", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.FromBoolean(false);
                var cb = args[0].AsFunction();
                for (int i = 0; i < Length; i++)
                    if (cb.Invoke(new[] { FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context).ToBoolean()) return FenValue.FromBoolean(true);
                return FenValue.FromBoolean(false);
            })));

            // every — ECMA-262 §22.2.3.7
            Set("every", FenValue.FromFunction(new FenFunction("every", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                if (args.Length < 1 || !args[0].IsFunction) return FenValue.FromBoolean(true);
                var cb = args[0].AsFunction();
                for (int i = 0; i < Length; i++)
                    if (!cb.Invoke(new[] { FenValue.FromNumber(GetIndex(i)), FenValue.FromNumber(i), thisVal }, context).ToBoolean()) return FenValue.FromBoolean(false);
                return FenValue.FromBoolean(true);
            })));

            // includes — ECMA-262 §22.2.3.15: SameValueZero comparison (NaN matches NaN)
            Set("includes", FenValue.FromFunction(new FenFunction("includes", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                double target = args.Length > 0 ? args[0].ToNumber() : double.NaN;
                int from = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (from < 0) from = Math.Max(0, Length + from);
                for (int i = from; i < Length; i++)
                    if (GetIndex(i) == target || (double.IsNaN(target) && double.IsNaN(GetIndex(i)))) return FenValue.FromBoolean(true);
                return FenValue.FromBoolean(false);
            })));

            // indexOf — ECMA-262 §22.2.3.16: strict equality (no NaN match)
            Set("indexOf", FenValue.FromFunction(new FenFunction("indexOf", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                double target = args.Length > 0 ? args[0].ToNumber() : double.NaN;
                int from = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                if (from < 0) from = Math.Max(0, Length + from);
                for (int i = from; i < Length; i++)
                    if (GetIndex(i) == target) return FenValue.FromNumber(i);
                return FenValue.FromNumber(-1);
            })));

            // lastIndexOf — ECMA-262 §22.2.3.17: search from end, strict equality
            Set("lastIndexOf", FenValue.FromFunction(new FenFunction("lastIndexOf", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                double target = args.Length > 0 ? args[0].ToNumber() : double.NaN;
                int from = args.Length > 1 ? (int)args[1].ToNumber() : Length - 1;
                if (from < 0) from = Length + from;
                from = Math.Min(from, Length - 1);
                for (int i = from; i >= 0; i--)
                    if (GetIndex(i) == target) return FenValue.FromNumber(i);
                return FenValue.FromNumber(-1);
            })));

            // fill — ECMA-262 §22.2.3.8
            Set("fill", FenValue.FromFunction(new FenFunction("fill", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                double val = args.Length > 0 ? args[0].ToNumber() : 0;
                int start = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                int end = args.Length > 2 ? (int)args[2].ToNumber() : Length;
                if (start < 0) start = Math.Max(0, Length + start); else start = Math.Min(start, Length);
                if (end < 0) end = Math.Max(0, Length + end); else end = Math.Min(end, Length);
                for (int i = start; i < end; i++) SetIndex(i, val);
                return thisVal;
            })));

            // copyWithin — ECMA-262 §22.2.3.4
            Set("copyWithin", FenValue.FromFunction(new FenFunction("copyWithin", (args, thisVal) =>
            {
                CheckDetachedBuffer();
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

            // slice — ECMA-262 §22.2.3.24: returns new TypedArray of same type
            Set("slice", FenValue.FromFunction(new FenFunction("slice", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                int start = args.Length > 0 && !args[0].IsUndefined ? (int)args[0].ToNumber() : 0;
                int end = args.Length > 1 && !args[1].IsUndefined ? (int)args[1].ToNumber() : Length;
                if (start < 0) start = Math.Max(0, Length + start);
                if (end < 0) end = Math.Max(0, Length + end);
                start = Math.Min(start, Length);
                end = Math.Min(end, Length);
                int count = Math.Max(0, end - start);
                var result = CreateSameType(count);
                for (int i = 0; i < count; i++) result.SetIndex(i, GetIndex(start + i));
                return FenValue.FromObject(result);
            })));

            // join — ECMA-262 §22.2.3.16
            Set("join", FenValue.FromFunction(new FenFunction("join", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                string sep = args.Length > 0 && !args[0].IsUndefined ? args[0].ToString2() : ",";
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < Length; i++) { if (i > 0) sb.Append(sep); sb.Append(GetIndex(i)); }
                return FenValue.FromString(sb.ToString());
            })));

            // reverse — ECMA-262 §22.2.3.23: reverse in place, return this
            Set("reverse", FenValue.FromFunction(new FenFunction("reverse", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                for (int i = 0, j = Length - 1; i < j; i++, j--)
                { double tmp = GetIndex(i); SetIndex(i, GetIndex(j)); SetIndex(j, tmp); }
                return thisVal;
            })));

            // sort — ECMA-262 §22.2.3.26: optional compareFn; default numeric sort
            Set("sort", FenValue.FromFunction(new FenFunction("sort", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                var vals = new double[Length];
                for (int i = 0; i < Length; i++) vals[i] = GetIndex(i);
                if (args.Length > 0 && !args[0].IsUndefined && args[0].IsFunction)
                {
                    var cmp = args[0].AsFunction();
                    Array.Sort(vals, (a, b) =>
                    {
                        var r = cmp.Invoke(new[] { FenValue.FromNumber(a), FenValue.FromNumber(b) }, context).ToNumber();
                        return r < 0 ? -1 : r > 0 ? 1 : 0;
                    });
                }
                else
                {
                    Array.Sort(vals);
                }
                for (int i = 0; i < Length; i++) SetIndex(i, vals[i]);
                return thisVal;
            })));

            // set(array[, offset]) — ECMA-262 §22.2.3.23
            Set("set", FenValue.FromFunction(new FenFunction("set", (args, thisVal) =>
            {
                CheckDetachedBuffer();
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

            // subarray(begin[, end]) — ECMA-262 §22.2.3.27: new view over same buffer, same type
            Set("subarray", FenValue.FromFunction(new FenFunction("subarray", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                int start = args.Length > 0 && !args[0].IsUndefined ? (int)args[0].ToNumber() : 0;
                int end = args.Length > 1 && !args[1].IsUndefined ? (int)args[1].ToNumber() : Length;
                if (start < 0) start = Math.Max(0, Length + start); else start = Math.Min(start, Length);
                if (end < 0) end = Math.Max(0, Length + end); else end = Math.Min(end, Length);
                int newLen = Math.Max(0, end - start);
                int newByteOffset = ByteOffset + start * BytesPerElement;
                // CreateSameType(0) gives an uninitialized view; we override InitView to share the buffer
                var sub = CreateSameType(0);
                sub.InitView(Buffer, newByteOffset, newLen * BytesPerElement);
                sub.Length = newLen;
                sub.Set("length", FenValue.FromNumber(newLen));
                return FenValue.FromObject(sub);
            })));

            // at — ES2022 §22.2.3.1: supports negative indices
            Set("at", FenValue.FromFunction(new FenFunction("at", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                if (args.Length < 1) return FenValue.Undefined;
                int idx = (int)args[0].ToNumber();
                if (idx < 0) idx = Length + idx;
                if (idx < 0 || idx >= Length) return FenValue.Undefined;
                return FenValue.FromNumber(GetIndex(idx));
            })));

            // keys — ECMA-262 §22.2.3.16: iterator of indices
            Set("keys", FenValue.FromFunction(new FenFunction("keys", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                int idx = 0;
                int len = Length;
                return FenValue.FromObject(MakeIterator(() =>
                {
                    var result = new FenObject();
                    if (idx < len)
                    {
                        result.Set("value", FenValue.FromNumber(idx), null);
                        result.Set("done", FenValue.FromBoolean(false), null);
                        idx++;
                    }
                    else
                    {
                        result.Set("value", FenValue.Undefined, null);
                        result.Set("done", FenValue.FromBoolean(true), null);
                    }
                    return FenValue.FromObject(result);
                }));
            })));

            // values — ECMA-262 §22.2.3.30: iterator of values
            Set("values", FenValue.FromFunction(new FenFunction("values", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                int idx = 0;
                int len = Length;
                return FenValue.FromObject(MakeIterator(() =>
                {
                    var result = new FenObject();
                    if (idx < len)
                    {
                        result.Set("value", FenValue.FromNumber(GetIndex(idx)), null);
                        result.Set("done", FenValue.FromBoolean(false), null);
                        idx++;
                    }
                    else
                    {
                        result.Set("value", FenValue.Undefined, null);
                        result.Set("done", FenValue.FromBoolean(true), null);
                    }
                    return FenValue.FromObject(result);
                }));
            })));

            // entries — ECMA-262 §22.2.3.6: iterator of [index, value] pairs
            Set("entries", FenValue.FromFunction(new FenFunction("entries", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                int idx = 0;
                int len = Length;
                return FenValue.FromObject(MakeIterator(() =>
                {
                    var result = new FenObject();
                    if (idx < len)
                    {
                        var pair = new FenObject();
                        pair.Set("0", FenValue.FromNumber(idx), null);
                        pair.Set("1", FenValue.FromNumber(GetIndex(idx)), null);
                        pair.Set("length", FenValue.FromNumber(2), null);
                        result.Set("value", FenValue.FromObject(pair), null);
                        result.Set("done", FenValue.FromBoolean(false), null);
                        idx++;
                    }
                    else
                    {
                        result.Set("value", FenValue.Undefined, null);
                        result.Set("done", FenValue.FromBoolean(true), null);
                    }
                    return FenValue.FromObject(result);
                }));
            })));

            // @@iterator — ECMA-262 §22.2.3.31: same as values()
            SetSymbol(JsSymbol.Iterator, FenValue.FromFunction(new FenFunction("[Symbol.iterator]", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                int idx = 0;
                int len = Length;
                return FenValue.FromObject(MakeIterator(() =>
                {
                    var result = new FenObject();
                    if (idx < len)
                    {
                        result.Set("value", FenValue.FromNumber(GetIndex(idx)), null);
                        result.Set("done", FenValue.FromBoolean(false), null);
                        idx++;
                    }
                    else
                    {
                        result.Set("value", FenValue.Undefined, null);
                        result.Set("done", FenValue.FromBoolean(true), null);
                    }
                    return FenValue.FromObject(result);
                }));
            })));

            // toReversed — ES2023 §22.2.3.28: return new reversed TypedArray (non-mutating)
            Set("toReversed", FenValue.FromFunction(new FenFunction("toReversed", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                var result = CreateSameType(Length);
                for (int i = 0; i < Length; i++) result.SetIndex(i, GetIndex(Length - 1 - i));
                return FenValue.FromObject(result);
            })));

            // toSorted — ES2023 §22.2.3.29: return new sorted TypedArray (non-mutating)
            Set("toSorted", FenValue.FromFunction(new FenFunction("toSorted", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                var vals = new double[Length];
                for (int i = 0; i < Length; i++) vals[i] = GetIndex(i);
                if (args.Length > 0 && !args[0].IsUndefined && args[0].IsFunction)
                {
                    var cmp = args[0].AsFunction();
                    Array.Sort(vals, (a, b) =>
                    {
                        var r = cmp.Invoke(new[] { FenValue.FromNumber(a), FenValue.FromNumber(b) }, context).ToNumber();
                        return r < 0 ? -1 : r > 0 ? 1 : 0;
                    });
                }
                else
                {
                    Array.Sort(vals);
                }
                var result = CreateSameType(Length);
                for (int i = 0; i < Length; i++) result.SetIndex(i, vals[i]);
                return FenValue.FromObject(result);
            })));

            // with — ES2023 §22.2.3.32: return new TypedArray with one element replaced
            Set("with", FenValue.FromFunction(new FenFunction("with", (args, thisVal) =>
            {
                CheckDetachedBuffer();
                int idx = args.Length > 0 ? (int)args[0].ToNumber() : 0;
                double val = args.Length > 1 ? args[1].ToNumber() : 0;
                if (idx < 0) idx = Length + idx;
                if (idx < 0 || idx >= Length) throw new FenRangeError("RangeError: Invalid typed array index");
                var result = CreateSameType(Length);
                for (int i = 0; i < Length; i++) result.SetIndex(i, i == idx ? val : GetIndex(i));
                return FenValue.FromObject(result);
            })));

            if (this is JsUint8Array)
            {
                Set("setFromBase64", FenValue.FromFunction(new FenFunction("setFromBase64", (args, thisVal) =>
                {
                    if (thisVal.AsObject() is not JsUint8Array target)
                    {
                        throw new FenTypeError("TypeError: Uint8Array.prototype.setFromBase64 called on incompatible receiver");
                    }

                    if (args.Length == 0)
                    {
                        throw new FenTypeError("TypeError: Uint8Array.prototype.setFromBase64 requires a source string");
                    }

                    var source = args[0].ToString2();
                    var alphabet = "base64";
                    var lastChunkHandling = "loose";

                    if (args.Length > 1 && args[1].IsObject)
                    {
                        var options = args[1].AsObject();
                        var alphabetValue = options.Get("alphabet", null);
                        if (!alphabetValue.IsUndefined)
                        {
                            alphabet = alphabetValue.ToString2();
                        }

                        var lastChunkValue = options.Get("lastChunkHandling", null);
                        if (!lastChunkValue.IsUndefined)
                        {
                            lastChunkHandling = lastChunkValue.ToString2();
                        }
                    }

                    if (alphabet != "base64" && alphabet != "base64url")
                    {
                        throw new FenTypeError("TypeError: Invalid base64 alphabet option");
                    }

                    if (lastChunkHandling != "loose" &&
                        lastChunkHandling != "strict" &&
                        lastChunkHandling != "stop-before-partial")
                    {
                        throw new FenTypeError("TypeError: Invalid lastChunkHandling option");
                    }

                    return target.SetFromBase64(source, alphabet == "base64url", lastChunkHandling);
                })));

                Set("setFromHex", FenValue.FromFunction(new FenFunction("setFromHex", (args, thisVal) =>
                {
                    if (thisVal.AsObject() is not JsUint8Array target)
                    {
                        throw new FenTypeError("TypeError: Uint8Array.prototype.setFromHex called on incompatible receiver");
                    }

                    if (args.Length == 0)
                    {
                        throw new FenTypeError("TypeError: Uint8Array.prototype.setFromHex requires a source string");
                    }

                    return target.SetFromHex(args[0].ToString2());
                })));
            }
        }

        private static bool IsBase64Whitespace(char ch)
        {
            return ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n' || ch == '\f';
        }

        private static bool IsHexWhitespace(char ch)
        {
            return ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n' || ch == '\f';
        }

        private static int DecodeHexNibble(char ch)
        {
            if (ch >= '0' && ch <= '9') return ch - '0';
            if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
            if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
            return -1;
        }

        private static int DecodeBase64Sextet(char ch, bool useBase64UrlAlphabet)
        {
            if (ch >= 'A' && ch <= 'Z') return ch - 'A';
            if (ch >= 'a' && ch <= 'z') return ch - 'a' + 26;
            if (ch >= '0' && ch <= '9') return ch - '0' + 52;
            if (useBase64UrlAlphabet)
            {
                if (ch == '-') return 62;
                if (ch == '_') return 63;
            }
            else
            {
                if (ch == '+') return 62;
                if (ch == '/') return 63;
            }

            return -1;
        }

        protected FenValue SetFromBase64(string source, bool useBase64UrlAlphabet, string lastChunkHandling)
        {
            var sanitized = new List<char>(source.Length);
            var originalPositions = new List<int>(source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                var ch = source[i];
                if (IsBase64Whitespace(ch))
                {
                    continue;
                }

                sanitized.Add(ch);
                originalPositions.Add(i + 1);
            }

            var sanitizedLength = sanitized.Count;
            var fullQuartetCount = sanitizedLength / 4;
            var remainder = sanitizedLength % 4;
            if (remainder == 1)
            {
                throw new FenTypeError("TypeError: Invalid base64 string");
            }

            if (lastChunkHandling == "strict" && remainder != 0)
            {
                throw new FenTypeError("TypeError: Invalid base64 string");
            }

            var writableBytes = ByteLength;
            var bytesWritten = 0;
            var charsConsumed = 0;
            var fullCharsToProcess = fullQuartetCount * 4;

            for (int offset = 0; offset < fullCharsToProcess; offset += 4)
            {
                var isFinalQuartet = offset + 4 == fullCharsToProcess && remainder == 0;
                var c0 = sanitized[offset];
                var c1 = sanitized[offset + 1];
                var c2 = sanitized[offset + 2];
                var c3 = sanitized[offset + 3];

                if (c0 == '=' || c1 == '=')
                {
                    throw new FenTypeError("TypeError: Invalid base64 string");
                }

                var s0 = DecodeBase64Sextet(c0, useBase64UrlAlphabet);
                var s1 = DecodeBase64Sextet(c1, useBase64UrlAlphabet);
                if (s0 < 0 || s1 < 0)
                {
                    throw new FenTypeError("TypeError: Invalid base64 string");
                }

                var paddedBytes = 3;
                int s2;
                int s3;
                if (c2 == '=')
                {
                    if (c3 != '=' || !isFinalQuartet)
                    {
                        throw new FenTypeError("TypeError: Invalid base64 string");
                    }

                    paddedBytes = 1;
                    s2 = 0;
                    s3 = 0;
                    if ((s1 & 0x0F) != 0)
                    {
                        throw new FenTypeError("TypeError: Invalid base64 string");
                    }
                }
                else
                {
                    s2 = DecodeBase64Sextet(c2, useBase64UrlAlphabet);
                    if (s2 < 0)
                    {
                        throw new FenTypeError("TypeError: Invalid base64 string");
                    }

                    if (c3 == '=')
                    {
                        if (!isFinalQuartet)
                        {
                            throw new FenTypeError("TypeError: Invalid base64 string");
                        }

                        paddedBytes = 2;
                        s3 = 0;
                        if ((s2 & 0x03) != 0)
                        {
                            throw new FenTypeError("TypeError: Invalid base64 string");
                        }
                    }
                    else
                    {
                        s3 = DecodeBase64Sextet(c3, useBase64UrlAlphabet);
                        if (s3 < 0)
                        {
                            throw new FenTypeError("TypeError: Invalid base64 string");
                        }
                    }
                }

                if (bytesWritten + paddedBytes > writableBytes)
                {
                    break;
                }

                Buffer.Data[ByteOffset + bytesWritten] = (byte)((s0 << 2) | (s1 >> 4));
                if (paddedBytes > 1)
                {
                    Buffer.Data[ByteOffset + bytesWritten + 1] = (byte)(((s1 & 0x0F) << 4) | (s2 >> 2));
                }

                if (paddedBytes > 2)
                {
                    Buffer.Data[ByteOffset + bytesWritten + 2] = (byte)(((s2 & 0x03) << 6) | s3);
                }

                bytesWritten += paddedBytes;
                charsConsumed = originalPositions[offset + 3];
            }

            if (remainder > 0 && lastChunkHandling == "loose")
            {
                var partialOffset = fullCharsToProcess;
                var s0 = DecodeBase64Sextet(sanitized[partialOffset], useBase64UrlAlphabet);
                var s1 = DecodeBase64Sextet(sanitized[partialOffset + 1], useBase64UrlAlphabet);
                if (s0 < 0 || s1 < 0)
                {
                    throw new FenTypeError("TypeError: Invalid base64 string");
                }

                if (remainder == 2)
                {
                    if (bytesWritten + 1 <= writableBytes)
                    {
                        Buffer.Data[ByteOffset + bytesWritten] = (byte)((s0 << 2) | (s1 >> 4));
                        bytesWritten += 1;
                        charsConsumed = originalPositions[partialOffset + 1];
                    }
                }
                else if (remainder == 3)
                {
                    var s2 = DecodeBase64Sextet(sanitized[partialOffset + 2], useBase64UrlAlphabet);
                    if (s2 < 0)
                    {
                        throw new FenTypeError("TypeError: Invalid base64 string");
                    }

                    if (bytesWritten + 2 <= writableBytes)
                    {
                        Buffer.Data[ByteOffset + bytesWritten] = (byte)((s0 << 2) | (s1 >> 4));
                        Buffer.Data[ByteOffset + bytesWritten + 1] = (byte)(((s1 & 0x0F) << 4) | (s2 >> 2));
                        bytesWritten += 2;
                        charsConsumed = originalPositions[partialOffset + 2];
                    }
                }
            }
            else if (charsConsumed == 0 && sanitizedLength == 0)
            {
                charsConsumed = source.Length;
            }

            if (charsConsumed == 0 && bytesWritten == 0 && sanitizedLength > 0 && lastChunkHandling == "stop-before-partial")
            {
                charsConsumed = 0;
            }
            else if (charsConsumed == 0 && bytesWritten == 0 && sanitizedLength > 0 && fullCharsToProcess > 0)
            {
                charsConsumed = 0;
            }
            else if (charsConsumed > 0 && charsConsumed < source.Length)
            {
                var trailingWhitespaceOnly = true;
                for (int i = charsConsumed; i < source.Length; i++)
                {
                    if (!IsBase64Whitespace(source[i]))
                    {
                        trailingWhitespaceOnly = false;
                        break;
                    }
                }

                if (trailingWhitespaceOnly)
                {
                    charsConsumed = source.Length;
                }
            }
            else if (sanitizedLength > 0 && charsConsumed == 0 && bytesWritten > 0)
            {
                charsConsumed = source.Length;
            }

            var result = new FenObject();
            result.Set("read", FenValue.FromNumber(charsConsumed));
            result.Set("written", FenValue.FromNumber(bytesWritten));
            return FenValue.FromObject(result);
        }

        protected FenValue SetFromHex(string source)
        {
            var sanitized = new List<char>(source.Length);
            var originalPositions = new List<int>(source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                var ch = source[i];
                if (IsHexWhitespace(ch))
                {
                    continue;
                }

                sanitized.Add(ch);
                originalPositions.Add(i + 1);
            }

            if ((sanitized.Count & 1) != 0)
            {
                throw new FenTypeError("TypeError: Invalid hex string");
            }

            var maxWritableBytes = Math.Max(0, Length);
            var availablePairs = sanitized.Count / 2;
            var writablePairs = Math.Min(availablePairs, maxWritableBytes);
            for (int pairIndex = 0; pairIndex < writablePairs; pairIndex++)
            {
                int sourceIndex = pairIndex * 2;
                var hi = DecodeHexNibble(sanitized[sourceIndex]);
                var lo = DecodeHexNibble(sanitized[sourceIndex + 1]);
                if (hi < 0 || lo < 0)
                {
                    throw new FenTypeError("TypeError: Invalid hex string");
                }

                Buffer.Data[ByteOffset + pairIndex] = (byte)((hi << 4) | lo);
            }

            if (writablePairs < availablePairs)
            {
                int sourceIndex = writablePairs * 2;
                if (sourceIndex < sanitized.Count)
                {
                    var hi = DecodeHexNibble(sanitized[sourceIndex]);
                    var lo = DecodeHexNibble(sanitized[sourceIndex + 1]);
                    if (hi < 0 || lo < 0)
                    {
                        throw new FenTypeError("TypeError: Invalid hex string");
                    }
                }
            }

            var result = new FenObject();
            result.Set("read", FenValue.FromNumber(writablePairs * 2));
            result.Set("written", FenValue.FromNumber(writablePairs));
            return FenValue.FromObject(result);
        }
    }

    // ECMA-262 §22.2.7: Uint8Array — unsigned 8-bit integer array
    public class JsUint8Array : JsTypedArray
    {
        public JsUint8Array(IValue a0 = null, IValue a1 = null, IValue a2 = null) : base(1) { Init(a0, a1, a2); }
        // Internal constructor used by CreateSameType — buffer already sized correctly
        internal JsUint8Array(JsArrayBuffer buf) : base(1)
        {
            Length = buf.Data.Length;
            InitView(buf, 0, buf.Data.Length);
            Set("length", FenValue.FromNumber(Length));
            InitPrototypeMethods(null);
        }
        protected override JsTypedArray CreateSameType(int length) => new JsUint8Array(new JsArrayBuffer(length));
        public override double GetIndex(int index)
        {
            CheckDetachedBuffer();
            return Buffer.Data[ByteOffset + index];
        }
        public override void SetIndex(int index, double value)
        {
            CheckDetachedBuffer();
            Buffer.Data[ByteOffset + index] = (byte)value;
        }
    }

    // ECMA-262 §22.2.6: Int8Array — signed 8-bit integer array
    public class JsInt8Array : JsTypedArray
    {
        public JsInt8Array(IValue a0 = null, IValue a1 = null, IValue a2 = null) : base(1) { Init(a0, a1, a2); }
        internal JsInt8Array(JsArrayBuffer buf) : base(1)
        {
            Length = buf.Data.Length;
            InitView(buf, 0, buf.Data.Length);
            Set("length", FenValue.FromNumber(Length));
            InitPrototypeMethods(null);
        }
        protected override JsTypedArray CreateSameType(int length) => new JsInt8Array(new JsArrayBuffer(length));
        public override double GetIndex(int index)
        {
            CheckDetachedBuffer();
            return (sbyte)Buffer.Data[ByteOffset + index];
        }
        public override void SetIndex(int index, double value)
        {
            CheckDetachedBuffer();
            Buffer.Data[ByteOffset + index] = (byte)(sbyte)value;
        }
    }

    // ECMA-262 §22.2.8: Uint8ClampedArray — clamped unsigned 8-bit integer array
    public class JsUint8ClampedArray : JsTypedArray
    {
        public JsUint8ClampedArray(IValue a0 = null, IValue a1 = null, IValue a2 = null) : base(1) { Init(a0, a1, a2); }
        internal JsUint8ClampedArray(JsArrayBuffer buf) : base(1)
        {
            Length = buf.Data.Length;
            InitView(buf, 0, buf.Data.Length);
            Set("length", FenValue.FromNumber(Length));
            InitPrototypeMethods(null);
        }
        protected override JsTypedArray CreateSameType(int length) => new JsUint8ClampedArray(new JsArrayBuffer(length));
        public override double GetIndex(int index)
        {
            CheckDetachedBuffer();
            return Buffer.Data[ByteOffset + index];
        }
        public override void SetIndex(int index, double value)
        {
            CheckDetachedBuffer();
            // ECMA-262 §22.2.8: clamp to [0, 255], round half-to-even (banker's rounding)
            Buffer.Data[ByteOffset + index] = (byte)Math.Min(255, Math.Max(0, double.IsNaN(value) ? 0 : (int)Math.Round(value, MidpointRounding.ToEven)));
        }
    }

    // ECMA-262 §22.2.10: Uint16Array — unsigned 16-bit integer array
    public class JsUint16Array : JsTypedArray
    {
        public JsUint16Array(IValue a0 = null, IValue a1 = null, IValue a2 = null) : base(2) { Init(a0, a1, a2); }
        internal JsUint16Array(JsArrayBuffer buf) : base(2)
        {
            Length = buf.Data.Length / 2;
            InitView(buf, 0, buf.Data.Length);
            Set("length", FenValue.FromNumber(Length));
            InitPrototypeMethods(null);
        }
        protected override JsTypedArray CreateSameType(int length) => new JsUint16Array(new JsArrayBuffer(length * 2));
        public override double GetIndex(int index)
        {
            CheckDetachedBuffer();
            return BitConverter.ToUInt16(Buffer.Data, ByteOffset + index * 2);
        }
        public override void SetIndex(int index, double value)
        {
            CheckDetachedBuffer();
            byte[] b = BitConverter.GetBytes((ushort)value);
            Array.Copy(b, 0, Buffer.Data, ByteOffset + index * 2, 2);
        }
    }

    // ECMA-262 §22.2.9: Int16Array — signed 16-bit integer array
    public class JsInt16Array : JsTypedArray
    {
        public JsInt16Array(IValue a0 = null, IValue a1 = null, IValue a2 = null) : base(2) { Init(a0, a1, a2); }
        internal JsInt16Array(JsArrayBuffer buf) : base(2)
        {
            Length = buf.Data.Length / 2;
            InitView(buf, 0, buf.Data.Length);
            Set("length", FenValue.FromNumber(Length));
            InitPrototypeMethods(null);
        }
        protected override JsTypedArray CreateSameType(int length) => new JsInt16Array(new JsArrayBuffer(length * 2));
        public override double GetIndex(int index)
        {
            CheckDetachedBuffer();
            return BitConverter.ToInt16(Buffer.Data, ByteOffset + index * 2);
        }
        public override void SetIndex(int index, double value)
        {
            CheckDetachedBuffer();
            byte[] b = BitConverter.GetBytes((short)value);
            Array.Copy(b, 0, Buffer.Data, ByteOffset + index * 2, 2);
        }
    }

    // ECMA-262 §22.2.12: Uint32Array — unsigned 32-bit integer array
    public class JsUint32Array : JsTypedArray
    {
        public JsUint32Array(IValue a0 = null, IValue a1 = null, IValue a2 = null) : base(4) { Init(a0, a1, a2); }
        internal JsUint32Array(JsArrayBuffer buf) : base(4)
        {
            Length = buf.Data.Length / 4;
            InitView(buf, 0, buf.Data.Length);
            Set("length", FenValue.FromNumber(Length));
            InitPrototypeMethods(null);
        }
        protected override JsTypedArray CreateSameType(int length) => new JsUint32Array(new JsArrayBuffer(length * 4));
        public override double GetIndex(int index)
        {
            CheckDetachedBuffer();
            return BitConverter.ToUInt32(Buffer.Data, ByteOffset + index * 4);
        }
        public override void SetIndex(int index, double value)
        {
            CheckDetachedBuffer();
            byte[] b = BitConverter.GetBytes((uint)value);
            Array.Copy(b, 0, Buffer.Data, ByteOffset + index * 4, 4);
        }
    }

    // ECMA-262 §22.2.11: Int32Array — signed 32-bit integer array
    public class JsInt32Array : JsTypedArray
    {
        public JsInt32Array(IValue a0 = null, IValue a1 = null, IValue a2 = null) : base(4) { Init(a0, a1, a2); }
        internal JsInt32Array(JsArrayBuffer buf) : base(4)
        {
            Length = buf.Data.Length / 4;
            InitView(buf, 0, buf.Data.Length);
            Set("length", FenValue.FromNumber(Length));
            InitPrototypeMethods(null);
        }
        protected override JsTypedArray CreateSameType(int length) => new JsInt32Array(new JsArrayBuffer(length * 4));
        public override double GetIndex(int index)
        {
            CheckDetachedBuffer();
            return BitConverter.ToInt32(Buffer.Data, ByteOffset + index * 4);
        }
        public override void SetIndex(int index, double value)
        {
            CheckDetachedBuffer();
            byte[] b = BitConverter.GetBytes((int)value);
            Array.Copy(b, 0, Buffer.Data, ByteOffset + index * 4, 4);
        }
    }

    // ECMA-262 §22.2.13: Float32Array — 32-bit IEEE 754 floating point array
    public class JsFloat32Array : JsTypedArray
    {
        public JsFloat32Array(IValue a0 = null, IValue a1 = null, IValue a2 = null) : base(4) { Init(a0, a1, a2); }
        internal JsFloat32Array(JsArrayBuffer buf) : base(4)
        {
            Length = buf.Data.Length / 4;
            InitView(buf, 0, buf.Data.Length);
            Set("length", FenValue.FromNumber(Length));
            InitPrototypeMethods(null);
        }
        protected override JsTypedArray CreateSameType(int length) => new JsFloat32Array(new JsArrayBuffer(length * 4));
        public override double GetIndex(int index)
        {
            CheckDetachedBuffer();
            return BitConverter.ToSingle(Buffer.Data, ByteOffset + index * 4);
        }
        public override void SetIndex(int index, double value)
        {
            CheckDetachedBuffer();
            byte[] b = BitConverter.GetBytes((float)value);
            Array.Copy(b, 0, Buffer.Data, ByteOffset + index * 4, 4);
        }
    }

    // ECMA-262 §22.2.14: Float64Array — 64-bit IEEE 754 floating point array
    public class JsFloat64Array : JsTypedArray
    {
        public JsFloat64Array(IValue a0 = null, IValue a1 = null, IValue a2 = null) : base(8) { Init(a0, a1, a2); }
        internal JsFloat64Array(JsArrayBuffer buf) : base(8)
        {
            Length = buf.Data.Length / 8;
            InitView(buf, 0, buf.Data.Length);
            Set("length", FenValue.FromNumber(Length));
            InitPrototypeMethods(null);
        }
        protected override JsTypedArray CreateSameType(int length) => new JsFloat64Array(new JsArrayBuffer(length * 8));
        public override double GetIndex(int index)
        {
            CheckDetachedBuffer();
            return BitConverter.ToDouble(Buffer.Data, ByteOffset + index * 8);
        }
        public override void SetIndex(int index, double value)
        {
            CheckDetachedBuffer();
            byte[] b = BitConverter.GetBytes(value);
            Array.Copy(b, 0, Buffer.Data, ByteOffset + index * 8, 8);
        }
    }

    /// <summary>
    /// ECMA-262 §22.2: BigInt64Array — typed array of 64-bit signed integers.
    /// Elements are accessed as BigInt values (not Number).
    /// </summary>
    public class JsBigInt64Array : JsTypedArray
    {
        public JsBigInt64Array(IValue a0 = null, IValue a1 = null, IValue a2 = null) : base(8) { Init(a0, a1, a2); }
        internal JsBigInt64Array(JsArrayBuffer buf) : base(8)
        {
            Length = buf.Data.Length / 8;
            InitView(buf, 0, buf.Data.Length);
            Set("length", FenValue.FromNumber(Length));
            InitPrototypeMethods(null);
        }
        protected override JsTypedArray CreateSameType(int length) => new JsBigInt64Array(new JsArrayBuffer(length * 8));

        // GetIndex/SetIndex use double as the bridge, but BigInt arrays override
        // the JS-level get/set via GetBigIntIndex / SetBigIntIndex below.
        public override double GetIndex(int index)
        {
            CheckDetachedBuffer();
            return (double)BitConverter.ToInt64(Buffer.Data, ByteOffset + index * 8);
        }
        public override void SetIndex(int index, double value)
        {
            CheckDetachedBuffer();
            byte[] b = BitConverter.GetBytes((long)value);
            Array.Copy(b, 0, Buffer.Data, ByteOffset + index * 8, 8);
        }

        /// <summary>Returns the element at <paramref name="index"/> as a BigInt FenValue.</summary>
        public FenValue GetBigIntIndex(int index)
        {
            CheckDetachedBuffer();
            long raw = BitConverter.ToInt64(Buffer.Data, ByteOffset + index * 8);
            return FenValue.FromBigInt(new JsBigInt(new BigInteger(raw)));
        }

        /// <summary>Stores <paramref name="value"/> (must be BigInt) at <paramref name="index"/>.</summary>
        public void SetBigIntIndex(int index, FenValue value)
        {
            CheckDetachedBuffer();
            if (!value.IsBigInt)
                throw new FenTypeError("TypeError: Cannot mix BigInt and other types, use explicit conversions");
            long raw = (long)value.AsBigInt().Value;
            byte[] b = BitConverter.GetBytes(raw);
            Array.Copy(b, 0, Buffer.Data, ByteOffset + index * 8, 8);
        }
    }

    /// <summary>
    /// ECMA-262 §22.2: BigUint64Array — typed array of 64-bit unsigned integers.
    /// Elements are accessed as BigInt values (not Number).
    /// </summary>
    public class JsBigUint64Array : JsTypedArray
    {
        public JsBigUint64Array(IValue a0 = null, IValue a1 = null, IValue a2 = null) : base(8) { Init(a0, a1, a2); }
        internal JsBigUint64Array(JsArrayBuffer buf) : base(8)
        {
            Length = buf.Data.Length / 8;
            InitView(buf, 0, buf.Data.Length);
            Set("length", FenValue.FromNumber(Length));
            InitPrototypeMethods(null);
        }
        protected override JsTypedArray CreateSameType(int length) => new JsBigUint64Array(new JsArrayBuffer(length * 8));

        public override double GetIndex(int index)
        {
            CheckDetachedBuffer();
            return (double)BitConverter.ToUInt64(Buffer.Data, ByteOffset + index * 8);
        }
        public override void SetIndex(int index, double value)
        {
            CheckDetachedBuffer();
            byte[] b = BitConverter.GetBytes((ulong)value);
            Array.Copy(b, 0, Buffer.Data, ByteOffset + index * 8, 8);
        }

        /// <summary>Returns the element at <paramref name="index"/> as a BigInt FenValue.</summary>
        public FenValue GetBigIntIndex(int index)
        {
            CheckDetachedBuffer();
            ulong raw = BitConverter.ToUInt64(Buffer.Data, ByteOffset + index * 8);
            return FenValue.FromBigInt(new JsBigInt(new BigInteger(raw)));
        }

        /// <summary>Stores <paramref name="value"/> (must be BigInt) at <paramref name="index"/>.</summary>
        public void SetBigIntIndex(int index, FenValue value)
        {
            CheckDetachedBuffer();
            if (!value.IsBigInt)
                throw new FenTypeError("TypeError: Cannot mix BigInt and other types, use explicit conversions");
            ulong raw = (ulong)value.AsBigInt().Value;
            byte[] b = BitConverter.GetBytes(raw);
            Array.Copy(b, 0, Buffer.Data, ByteOffset + index * 8, 8);
        }
    }
}
