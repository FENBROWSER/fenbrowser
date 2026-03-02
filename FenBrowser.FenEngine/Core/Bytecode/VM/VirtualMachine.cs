using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Types;
using FenValue = FenBrowser.FenEngine.Core.FenValue;
using JsValueType = FenBrowser.FenEngine.Core.Interfaces.ValueType;

namespace FenBrowser.FenEngine.Core.Bytecode.VM
{
    /// <summary>
    /// The core execution engine for FenBrowser's compiled Javascript bytecode.
    /// Eliminates recursive AST evaluation in favor of a flat, stack-based opcode loop.
    /// </summary>
    public class VirtualMachine
    {
        private const int CachedIndexKeyCount = 2048;
        private static readonly string[] s_cachedIndexKeys = BuildCachedIndexKeys();
        private static readonly ConditionalWeakTable<CodeBlock, string[]> s_stringConstantCache = new ConditionalWeakTable<CodeBlock, string[]>();
        private static readonly ConditionalWeakTable<CodeBlock, Dictionary<int, PropertyInlineCacheEntry>> s_loadPropertyInlineCaches = new ConditionalWeakTable<CodeBlock, Dictionary<int, PropertyInlineCacheEntry>>();
        private static readonly ConditionalWeakTable<CodeBlock, Dictionary<int, PropertyInlineCacheEntry>> s_storePropertyInlineCaches = new ConditionalWeakTable<CodeBlock, Dictionary<int, PropertyInlineCacheEntry>>();

        private sealed class PropertyInlineCacheEntry
        {
            public string Key;
            public Shape Shape;
            public int SlotIndex;
        }

        private sealed class BytecodeArrayObject : FenObject
        {
            private FenValue[] _elements = new FenValue[8];
            private bool[] _present = new bool[8];
            private int _length;

            public BytecodeArrayObject()
            {
                InternalClass = "Array";
                if (DefaultArrayPrototype != null && !ReferenceEquals(DefaultArrayPrototype, this))
                {
                    SetPrototype(DefaultArrayPrototype);
                }
            }

            public int Length => _length;

            public void Append(FenValue value)
            {
                SetElement(_length, value);
            }

            public void SetElement(int index, FenValue value)
            {
                if (index < 0)
                {
                    return;
                }

                EnsureCapacity(index + 1);
                _elements[index] = value;
                _present[index] = true;
                if (index >= _length)
                {
                    _length = index + 1;
                }
            }

            public bool TryGetElement(int index, out FenValue value)
            {
                if ((uint)index < (uint)_length && _present[index])
                {
                    value = _elements[index];
                    return true;
                }

                value = FenValue.Undefined;
                return false;
            }

            private void SetLength(int newLength)
            {
                if (newLength < 0)
                {
                    newLength = 0;
                }

                if (newLength < _length)
                {
                    for (int i = newLength; i < _length; i++)
                    {
                        _present[i] = false;
                        _elements[i] = FenValue.Undefined;
                    }
                }
                else if (newLength > _elements.Length)
                {
                    EnsureCapacity(newLength);
                }

                _length = newLength;
            }

            public override FenValue Get(string key, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                if (string.Equals(key, "length", StringComparison.Ordinal))
                {
                    return FenValue.FromNumber(_length);
                }

                if (TryParseArrayIndex(key, out int index))
                {
                    return TryGetElement(index, out var value) ? value : FenValue.Undefined;
                }

                return base.Get(key, context);
            }

            public override void Set(string key, FenValue value, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                if (string.Equals(key, "length", StringComparison.Ordinal))
                {
                    SetLength((int)value.ToNumber());
                    return;
                }

                if (TryParseArrayIndex(key, out int index))
                {
                    SetElement(index, value);
                    return;
                }

                base.Set(key, value, context);
            }

            public override bool Has(string key, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                if (string.Equals(key, "length", StringComparison.Ordinal))
                {
                    return true;
                }

                if (TryParseArrayIndex(key, out int index))
                {
                    return (uint)index < (uint)_length && _present[index];
                }

                return base.Has(key, context);
            }

            public override bool Delete(string key, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                if (string.Equals(key, "length", StringComparison.Ordinal))
                {
                    return false;
                }

                if (TryParseArrayIndex(key, out int index))
                {
                    if ((uint)index < (uint)_length)
                    {
                        _present[index] = false;
                        _elements[index] = FenValue.Undefined;
                    }

                    return true;
                }

                return base.Delete(key, context);
            }

            public override IEnumerable<string> Keys(FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                for (int i = 0; i < _length; i++)
                {
                    if (_present[i])
                    {
                        yield return IndexKey(i);
                    }
                }

                foreach (var key in base.Keys(context))
                {
                    if (!TryParseArrayIndex(key, out _) && !string.Equals(key, "length", StringComparison.Ordinal))
                    {
                        yield return key;
                    }
                }
            }

            public override IEnumerable<string> GetOwnPropertyNames()
            {
                for (int i = 0; i < _length; i++)
                {
                    if (_present[i])
                    {
                        yield return IndexKey(i);
                    }
                }

                yield return "length";

                foreach (var key in base.GetOwnPropertyNames())
                {
                    if (!TryParseArrayIndex(key, out _) && !string.Equals(key, "length", StringComparison.Ordinal))
                    {
                        yield return key;
                    }
                }
            }

            private void EnsureCapacity(int size)
            {
                if (size <= _elements.Length)
                {
                    return;
                }

                int newSize = _elements.Length;
                while (newSize < size)
                {
                    newSize *= 2;
                }

                Array.Resize(ref _elements, newSize);
                Array.Resize(ref _present, newSize);
            }

            private static bool TryParseArrayIndex(string key, out int index)
            {
                index = -1;
                if (string.IsNullOrEmpty(key))
                {
                    return false;
                }

                int value = 0;
                for (int i = 0; i < key.Length; i++)
                {
                    char c = key[i];
                    if (c < '0' || c > '9')
                    {
                        return false;
                    }

                    int digit = c - '0';
                    if (value > (int.MaxValue - digit) / 10)
                    {
                        return false;
                    }

                    value = (value * 10) + digit;
                }

                index = value;
                return true;
            }
        }

        private sealed class EmptyFenValueEnumerator : IEnumerator<FenValue>
        {
            public static readonly EmptyFenValueEnumerator Instance = new EmptyFenValueEnumerator();

            public FenValue Current => FenValue.Undefined;
            object IEnumerator.Current => Current;

            public bool MoveNext() => false;
            public void Reset() { }
            public void Dispose() { }
        }

        private sealed class KeyIteratorEnumerator : IEnumerator<FenValue>
        {
            private readonly IEnumerator<string> _keys;

            public KeyIteratorEnumerator(IEnumerator<string> keys)
            {
                _keys = keys;
            }

            public FenValue Current { get; private set; }
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (!_keys.MoveNext())
                {
                    return false;
                }

                Current = FenValue.FromString(_keys.Current);
                return true;
            }

            public void Reset() => _keys.Reset();
            public void Dispose() => _keys.Dispose();
        }

        private sealed class ValueIteratorEnumerator : IEnumerator<FenValue>
        {
            private readonly FenBrowser.FenEngine.Core.Interfaces.IObject _source;
            private readonly IEnumerator<string> _keys;

            public ValueIteratorEnumerator(FenBrowser.FenEngine.Core.Interfaces.IObject source)
            {
                _source = source;
                _keys = source?.Keys().GetEnumerator();
            }

            public FenValue Current { get; private set; }
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_keys == null || !_keys.MoveNext())
                {
                    return false;
                }

                Current = _source.Get(_keys.Current);
                return true;
            }

            public void Reset() => _keys?.Reset();
            public void Dispose() => _keys?.Dispose();
        }

        private sealed class StringIteratorEnumerator : IEnumerator<FenValue>
        {
            private readonly string _source;
            private int _index = -1;

            public StringIteratorEnumerator(string source)
            {
                _source = source ?? string.Empty;
            }

            public FenValue Current { get; private set; }
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                int nextIndex = _index + 1;
                if (nextIndex >= _source.Length)
                {
                    return false;
                }

                _index = nextIndex;
                Current = FenValue.FromString(_source[_index].ToString());
                return true;
            }

            public void Reset()
            {
                _index = -1;
                Current = FenValue.Undefined;
            }

            public void Dispose() { }
        }

        // Cached primitive prototype lookups (lazily resolved from the environment on first use)
        private readonly Dictionary<string, FenObject> _primitivePrototypeCache = new Dictionary<string, FenObject>(StringComparer.Ordinal);

        // Fixed-size fast heap for operands (prevents boxing and allocation in hot loop)
        private const int STACK_SIZE = 16384;
        private readonly FenValue[] _stack = new FenValue[STACK_SIZE];
        private int _sp = 0; // Stack pointer
        private FenValue _completionValue = FenValue.Undefined; // Stores the result of the last evaluated expression

        // Call stack managed entirely on the heap to prevent .NET StackOverflowException
        private const int MAX_FRAMES = 1024;
        private readonly CallFrame[] _callFrames = new CallFrame[MAX_FRAMES];
        private int _frameCount = 0;

        public VirtualMachine()
        {
        }

        private static string[] BuildCachedIndexKeys()
        {
            var keys = new string[CachedIndexKeyCount];
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = i.ToString(CultureInfo.InvariantCulture);
            }

            return keys;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static string IndexKey(int index)
        {
            if ((uint)index < CachedIndexKeyCount)
            {
                return s_cachedIndexKeys[index];
            }

            return index.ToString(CultureInfo.InvariantCulture);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static string PropertyKey(in FenValue value)
        {
            if (value.Type == JsValueType.String)
            {
                return value.AsString();
            }

            if (value.Type == JsValueType.Number)
            {
                double number = value._numberValue;
                if (number >= 0 && number <= int.MaxValue && number == Math.Truncate(number))
                {
                    return IndexKey((int)number);
                }
            }

            if (value.Type == JsValueType.Symbol)
            {
                var symbol = value.AsSymbol();
                if (symbol != null)
                {
                    return symbol.IsWellKnownSymbol
                        ? $"[{symbol.Description}]"
                        : symbol.ToPropertyKey();
                }
            }

            return value.AsString();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool CanUseBindingCache(CallFrame frame)
        {
            return !frame.HasWithEnvironments;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string GetStringConstant(CodeBlock block, List<FenValue> constants, int constantIndex)
        {
            if (!s_stringConstantCache.TryGetValue(block, out var cache))
            {
                cache = BuildStringConstantCache(constants);
                s_stringConstantCache.Add(block, cache);
            }

            if ((uint)constantIndex < (uint)cache.Length)
            {
                var value = cache[constantIndex];
                if (value != null)
                {
                    return value;
                }
            }

            return constants[constantIndex].AsString();
        }

        private static string[] BuildStringConstantCache(List<FenValue> constants)
        {
            if (constants == null || constants.Count == 0)
            {
                return Array.Empty<string>();
            }

            var cache = new string[constants.Count];
            for (int i = 0; i < constants.Count; i++)
            {
                var value = constants[i];
                if (value.Type == JsValueType.String)
                {
                    cache[i] = value.AsString();
                }
            }

            return cache;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Dictionary<int, PropertyInlineCacheEntry> GetLoadPropertyInlineCache(CodeBlock block)
        {
            return s_loadPropertyInlineCaches.GetOrCreateValue(block);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Dictionary<int, PropertyInlineCacheEntry> GetStorePropertyInlineCache(CodeBlock block)
        {
            return s_storePropertyInlineCaches.GetOrCreateValue(block);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryLoadPropertyInlineCache(
            Dictionary<int, PropertyInlineCacheEntry> cache,
            int instructionOffset,
            FenObject obj,
            string key,
            out FenValue value)
        {
            value = FenValue.Undefined;
            if (!cache.TryGetValue(instructionOffset, out var entry))
            {
                return false;
            }

            if (!string.Equals(entry.Key, key, StringComparison.Ordinal) || entry.Shape != obj.GetShape())
            {
                return false;
            }

            var storage = obj.GetPropertyStorage();
            if ((uint)entry.SlotIndex >= (uint)storage.Length)
            {
                return false;
            }

            var descriptor = storage[entry.SlotIndex];
            if (descriptor.IsAccessor || !descriptor.Value.HasValue)
            {
                return false;
            }

            value = descriptor.Value.Value;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PopulatePropertyInlineCache(
            Dictionary<int, PropertyInlineCacheEntry> cache,
            int instructionOffset,
            FenObject obj,
            string key,
            bool writableRequired)
        {
            var shape = obj.GetShape();
            if (!shape.TryGetPropertyOffset(key, out int slotIndex))
            {
                cache.Remove(instructionOffset);
                return;
            }

            var storage = obj.GetPropertyStorage();
            if ((uint)slotIndex >= (uint)storage.Length)
            {
                cache.Remove(instructionOffset);
                return;
            }

            var descriptor = storage[slotIndex];
            if (descriptor.IsAccessor || !descriptor.Value.HasValue)
            {
                cache.Remove(instructionOffset);
                return;
            }

            if (writableRequired && descriptor.Writable == false)
            {
                cache.Remove(instructionOffset);
                return;
            }

            cache[instructionOffset] = new PropertyInlineCacheEntry
            {
                Key = key,
                Shape = shape,
                SlotIndex = slotIndex
            };
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static FenValue ResolveVariable(CallFrame frame, string varName)
        {
            if (CanUseBindingCache(frame) &&
                frame.TryGetCachedBindingEnvironment(varName, out var cachedEnvironment))
            {
                if (cachedEnvironment != null && cachedEnvironment.TryGetLocal(varName, out var cachedValue))
                {
                    return cachedValue;
                }

                frame.RemoveCachedBindingEnvironment(varName);
            }

            var bindingEnvironment = frame.Environment.ResolveBindingEnvironment(varName);
            if (bindingEnvironment != null && bindingEnvironment.TryGetLocal(varName, out var value))
            {
                if (CanUseBindingCache(frame))
                {
                    frame.CacheBindingEnvironment(varName, bindingEnvironment);
                }

                return value;
            }

            return FenValue.Undefined;
        }

        public FenValue Execute(CodeBlock initialBlock, FenEnvironment initialEnv)
        {
            _sp = 0;
            _completionValue = FenValue.Undefined;
            _frameCount = 0;
            
            // Push initial frame
            PushFrame(initialBlock, initialEnv, 0);

            return RunLoop();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private CallFrame PushFrame(CodeBlock block, FenEnvironment env, int stackBase)
        {
            if (_frameCount >= MAX_FRAMES)
                throw new Exception("VM Error: Call stack exceeded maximum depth.");

            if (block != null && block.LocalSlotCount > 0)
            {
                env.InitializeFastStore(block.LocalSlotCount);
            }
                
            var frame = _callFrames[_frameCount];
            if (frame == null)
            {
                frame = new CallFrame(block, env, stackBase);
                _callFrames[_frameCount] = frame;
            }
            else
            {
                frame.Reset(block, env, stackBase);
            }
            _frameCount++;
            return frame;
        }

        private FenValue RunLoop()
        {
            try
            {
                while (_frameCount > 0)
                {
        fetch_frame:
                    var frame = _callFrames[_frameCount - 1];
                    var instructions = frame.Block.Instructions;
                    var constants = frame.Block.Constants;
                    
                    while (frame.IP < instructions.Length)
                    {
                        OpCode op = (OpCode)instructions[frame.IP++];
                        int instructionOffset = frame.IP - 1;
                        
                        switch (op)
                        {
                            case OpCode.LoadConst:
                            {
                                int constIndex = ReadInt32(instructions, ref frame);
                                _stack[_sp++] = constants[constIndex];
                                break;
                            }
                            case OpCode.LoadNull:
                                _stack[_sp++] = FenValue.Null;
                                break;
                            case OpCode.LoadUndefined:
                                _stack[_sp++] = FenValue.Undefined;
                                break;
                            case OpCode.LoadTrue:
                                _stack[_sp++] = FenValue.FromBoolean(true);
                                break;
                            case OpCode.LoadFalse:
                                _stack[_sp++] = FenValue.FromBoolean(false);
                                break;
                            case OpCode.Add:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                _stack[_sp++] = ExecuteAdd(left, right);
                                break;
                            }
                            case OpCode.Subtract:
                            {
                                var right = _stack[--_sp].ToNumber();
                                var left = _stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left - right);
                                break;
                            }
                            case OpCode.Multiply:
                            {
                                var right = _stack[--_sp].ToNumber();
                                var left = _stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left * right);
                                break;
                            }
                            case OpCode.Divide:
                            {
                                var right = _stack[--_sp].ToNumber();
                                var left = _stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left / right);
                                break;
                            }
                            case OpCode.Modulo:
                            {
                                var right = _stack[--_sp].ToNumber();
                                var left = _stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left % right);
                                break;
                            }
                            case OpCode.Exponent:
                            {
                                var right = _stack[--_sp].ToNumber();
                                var left = _stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(Math.Pow(left, right));
                                break;
                            }
                            case OpCode.Equal:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                _stack[_sp++] = FenValue.FromBoolean(left.LooseEquals(right));
                                break;
                            }
                            case OpCode.StrictEqual:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                _stack[_sp++] = FenValue.FromBoolean(left.StrictEquals(right));
                                break;
                            }
                            case OpCode.NotEqual:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                _stack[_sp++] = FenValue.FromBoolean(!left.LooseEquals(right));
                                break;
                            }
                            case OpCode.StrictNotEqual:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                _stack[_sp++] = FenValue.FromBoolean(!left.StrictEquals(right));
                                break;
                            }
                            case OpCode.LessThan:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                var result = FenValue.AbstractRelationalComparison(left, right, null, true);
                                _stack[_sp++] = FenValue.FromBoolean(result.ToBoolean());
                                break;
                            }
                            case OpCode.GreaterThan:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                var result = FenValue.AbstractRelationalComparison(right, left, null, false);
                                _stack[_sp++] = FenValue.FromBoolean(result.ToBoolean());
                                break;
                            }
                            case OpCode.LessThanOrEqual:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                var result = FenValue.AbstractRelationalComparison(right, left, null, false);
                                // Per spec: a <= b = !(b < a), but if comparison is undefined (NaN), result is false
                                _stack[_sp++] = FenValue.FromBoolean(!result.IsUndefined && !result.ToBoolean());
                                break;
                            }
                            case OpCode.GreaterThanOrEqual:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                var result = FenValue.AbstractRelationalComparison(left, right, null, true);
                                // Per spec: a >= b = !(a < b), but if comparison is undefined (NaN), result is false
                                _stack[_sp++] = FenValue.FromBoolean(!result.IsUndefined && !result.ToBoolean());
                                break;
                            }
                            case OpCode.InOperator:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];
                                if (right.IsObject)
                                {
                                    _stack[_sp++] = FenValue.FromBoolean(right.AsObject().Has(PropertyKey(left)));
                                }
                                else
                                {
                                    _stack[_sp++] = FenValue.FromBoolean(false);
                                }
                                break;
                            }
                            case OpCode.InstanceOf:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];

                                if (!right.IsFunction || !left.IsObject)
                                {
                                    _stack[_sp++] = FenValue.FromBoolean(false);
                                    break;
                                }

                                var constructor = right.AsFunction() as FenFunction;
                                if (constructor == null)
                                {
                                    _stack[_sp++] = FenValue.FromBoolean(false);
                                    break;
                                }

                                var prototypeVal = constructor.Get("prototype");
                                var expectedPrototype = prototypeVal.IsObject
                                    ? prototypeVal.AsObject()
                                    : (constructor.Prototype as FenObject);
                                if (expectedPrototype == null)
                                {
                                    _stack[_sp++] = FenValue.FromBoolean(false);
                                    break;
                                }

                                var currentPrototype = left.AsObject().GetPrototype();
                                bool isMatch = false;
                                while (currentPrototype != null)
                                {
                                    if (ReferenceEquals(currentPrototype, expectedPrototype))
                                    {
                                        isMatch = true;
                                        break;
                                    }

                                    currentPrototype = currentPrototype.GetPrototype();
                                }

                                _stack[_sp++] = FenValue.FromBoolean(isMatch);
                                break;
                            }
                            case OpCode.BitwiseAnd:
                            {
                                var right = (int)_stack[--_sp].ToNumber();
                                var left = (int)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left & right);
                                break;
                            }
                            case OpCode.BitwiseOr:
                            {
                                var right = (int)_stack[--_sp].ToNumber();
                                var left = (int)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left | right);
                                break;
                            }
                            case OpCode.BitwiseXor:
                            {
                                var right = (int)_stack[--_sp].ToNumber();
                                var left = (int)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left ^ right);
                                break;
                            }
                            case OpCode.LeftShift:
                            {
                                var right = (int)_stack[--_sp].ToNumber() & 0x1F;
                                var left = (int)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left << right);
                                break;
                            }
                            case OpCode.RightShift:
                            {
                                var right = (int)_stack[--_sp].ToNumber() & 0x1F;
                                var left = (int)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left >> right);
                                break;
                            }
                            case OpCode.UnsignedRightShift:
                            {
                                var right = (int)_stack[--_sp].ToNumber() & 0x1F;
                                var left = (uint)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(left >> right);
                                break;
                            }
                            case OpCode.Jump:
                            {
                                int offset = ReadInt32(instructions, ref frame);
                                frame.IP = offset;
                                break;
                            }
                            case OpCode.JumpIfFalse:
                            {
                                int offset = ReadInt32(instructions, ref frame);
                                var condition = _stack[--_sp];
                                if (!condition.ToBoolean())
                                {
                                    frame.IP = offset;
                                }
                                break;
                            }
                            case OpCode.JumpIfTrue:
                            {
                                int offset = ReadInt32(instructions, ref frame);
                                var condition = _stack[--_sp];
                                if (condition.ToBoolean())
                                {
                                    frame.IP = offset;
                                }
                                break;
                            }
                            case OpCode.LoadVar:
                            {
                                int nameIndex = ReadInt32(instructions, ref frame);
                                string varName = GetStringConstant(frame.Block, constants, nameIndex);
                                var value = ResolveVariable(frame, varName);
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.StoreVar:
                            {
                                int nameIndex = ReadInt32(instructions, ref frame);
                                string varName = GetStringConstant(frame.Block, constants, nameIndex);
                                var value = _stack[--_sp];
                                frame.Environment.Set(varName, value);
                                if (CanUseBindingCache(frame))
                                {
                                    frame.CacheBindingEnvironment(varName, frame.Environment);
                                }
                                // Assignment leaves value on stack
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.UpdateVar:
                            {
                                // Assignment: walks scope chain to update an existing binding,
                                // or creates in current scope if not found (implicit global in non-strict mode).
                                int nameIndex = ReadInt32(instructions, ref frame);
                                string varName = GetStringConstant(frame.Block, constants, nameIndex);
                                var value = _stack[--_sp];
                                var updateResult = frame.Environment.Update(varName, value);
                                if (updateResult.Type == JsValueType.Error)
                                {
                                    throw new Exception(updateResult.ToString());
                                }
                                // Invalidate binding cache since the binding may be in an outer scope
                                if (CanUseBindingCache(frame))
                                {
                                    frame.RemoveCachedBindingEnvironment(varName);
                                }
                                // Assignment leaves value on stack
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.LoadLocal:
                            {
                                int localSlot = ReadInt32(instructions, ref frame);
                                _stack[_sp++] = frame.Environment.GetFast(localSlot);
                                break;
                            }
                            case OpCode.StoreLocal:
                            {
                                int localSlot = ReadInt32(instructions, ref frame);
                                var value = _stack[--_sp];
                                frame.Environment.SetFast(localSlot, value);
                                string localName = frame.Block.GetLocalSlotName(localSlot);
                                if (!string.IsNullOrEmpty(localName))
                                {
                                    frame.Environment.Set(localName, value);
                                    if (CanUseBindingCache(frame))
                                    {
                                        frame.CacheBindingEnvironment(localName, frame.Environment);
                                    }
                                }

                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.Dup:
                            {
                                var value = _stack[_sp - 1];
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.Pop:
                            {
                                _sp--;
                                break;
                            }
                            case OpCode.PopAccumulator:
                            {
                                _completionValue = _stack[--_sp];
                                break;
                            }
                            case OpCode.MakeClosure:
                            {
                                int idx = ReadInt32(instructions, ref frame);
                                var templateFunc = constants[idx].AsObject() as FenFunction;
                                var newFunc = new FenFunction(templateFunc.Parameters, templateFunc.BytecodeBlock, frame.Environment);
                                newFunc.Name = templateFunc.Name;
                                newFunc.IsArrowFunction = templateFunc.IsArrowFunction;
                                newFunc.IsAsync = templateFunc.IsAsync;
                                newFunc.IsGenerator = templateFunc.IsGenerator;
                                newFunc.NeedsArgumentsObject = templateFunc.NeedsArgumentsObject;
                                newFunc.LocalMap = templateFunc.LocalMap;

                                if (!newFunc.IsArrowFunction)
                                {
                                    var fnPrototype = new FenObject();
                                    fnPrototype.Set("constructor", FenValue.FromFunction(newFunc));
                                    newFunc.Prototype = fnPrototype;
                                    newFunc.Set("prototype", FenValue.FromObject(fnPrototype));
                                }

                                _stack[_sp++] = FenValue.FromFunction(newFunc);
                                break;
                            }
                            case OpCode.Call:
                            {
                                int argCount = ReadInt32(instructions, ref frame);
                                int argStart = _sp - argCount;
                                var callee = _stack[argStart - 1];
                                
                                if (!callee.IsFunction) throw new Exception("VM Error: Attempted to call non-function.");
                                var func = callee.AsObject() as FenFunction;

                                if (func.IsNative)
                                {
                                    var args = new FenValue[argCount];
                                    if (argCount > 0)
                                    {
                                        Array.Copy(_stack, argStart, args, 0, argCount);
                                    }

                                    _sp = argStart - 1; // Pop callee + args
                                    if (!func.ProxyHandler.IsUndefined && func.ProxyHandler.IsObject)
                                    {
                                        _stack[_sp++] = func.Invoke(args, null, FenValue.Undefined);
                                    }
                                    else
                                    {
                                        _stack[_sp++] = func.NativeImplementation(args, FenValue.Undefined); // Phase 1: no 'this' ctx
                                    }
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    var newEnv = new FenEnvironment(func.Env);
                                    InitializeFunctionFastStore(func, newEnv);
                                    if (func.LocalMap != null && !string.IsNullOrEmpty(func.Name) && func.LocalMap.ContainsKey(func.Name))
                                    {
                                        SetFunctionBinding(func, newEnv, func.Name, FenValue.FromFunction(func));
                                    }
                                    BindFunctionArgumentsFromStack(func, newEnv, argCount, argStart);

                                    _sp = argStart - 1; // Pop callee + args
                                     
                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.NewTarget = FenValue.Undefined;
                                    newFrame.IsAsyncFunction = func.IsAsync;
                                    goto fetch_frame; // Break out of inner loop to process new frame
                                }
                                else
                                {
                                    throw new Exception("VM Error: Bytecode-only mode does not support AST-backed function calls.");
                                }
                                break;
                            }
                            case OpCode.CallFromArray:
                            {
                                var argsArrayVal = _stack[--_sp];
                                var callee = _stack[--_sp];

                                if (!callee.IsFunction) throw new Exception("VM Error: Attempted to call non-function.");
                                var func = callee.AsObject() as FenFunction;
                                var argsObject = argsArrayVal.IsObject ? argsArrayVal.AsObject() : null;
                                int argCount = GetArrayLikeLength(argsObject);

                                if (func.IsNative)
                                {
                                    var args = ExtractArrayLikeValues(argsArrayVal);
                                    if (!func.ProxyHandler.IsUndefined && func.ProxyHandler.IsObject)
                                    {
                                        _stack[_sp++] = func.Invoke(args, null, FenValue.Undefined);
                                    }
                                    else
                                    {
                                        _stack[_sp++] = func.NativeImplementation(args, FenValue.Undefined);
                                    }
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    var newEnv = new FenEnvironment(func.Env);
                                    InitializeFunctionFastStore(func, newEnv);
                                    if (func.LocalMap != null && !string.IsNullOrEmpty(func.Name) && func.LocalMap.ContainsKey(func.Name))
                                    {
                                        SetFunctionBinding(func, newEnv, func.Name, FenValue.FromFunction(func));
                                    }
                                    BindFunctionArgumentsFromArrayLike(func, newEnv, argsObject, argCount);

                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.NewTarget = FenValue.Undefined;
                                    newFrame.IsAsyncFunction = func.IsAsync;
                                    goto fetch_frame;
                                }
                                else
                                {
                                    throw new Exception("VM Error: Bytecode-only mode does not support AST-backed function calls.");
                                }
                                break;
                            }
                            case OpCode.CallMethod:
                            {
                                int argCount = ReadInt32(instructions, ref frame);
                                int argStart = _sp - argCount;
                                var callee = _stack[argStart - 1];
                                var receiver = _stack[argStart - 2];

                                if (!callee.IsFunction) throw new Exception("VM Error: Attempted to call non-function.");
                                var func = callee.AsObject() as FenFunction;

                                if (func.IsNative)
                                {
                                    var args = new FenValue[argCount];
                                    if (argCount > 0)
                                    {
                                        Array.Copy(_stack, argStart, args, 0, argCount);
                                    }

                                    _sp = argStart - 2; // Pop receiver + callee + args
                                    if (!func.ProxyHandler.IsUndefined && func.ProxyHandler.IsObject)
                                    {
                                        _stack[_sp++] = func.Invoke(args, null, receiver);
                                    }
                                    else
                                    {
                                        _stack[_sp++] = func.NativeImplementation(args, receiver);
                                    }
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    var newEnv = new FenEnvironment(func.Env);
                                    InitializeFunctionFastStore(func, newEnv);
                                    if (func.LocalMap != null && !string.IsNullOrEmpty(func.Name) && func.LocalMap.ContainsKey(func.Name))
                                    {
                                        SetFunctionBinding(func, newEnv, func.Name, FenValue.FromFunction(func));
                                    }
                                    if (!func.IsArrowFunction)
                                    {
                                        SetFunctionBinding(func, newEnv, "this", receiver);
                                    }
                                    BindFunctionArgumentsFromStack(func, newEnv, argCount, argStart);

                                    _sp = argStart - 2; // Pop receiver + callee + args

                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.NewTarget = FenValue.Undefined;
                                    newFrame.IsAsyncFunction = func.IsAsync;
                                    goto fetch_frame;
                                }
                                else
                                {
                                    throw new Exception("VM Error: Bytecode-only mode does not support AST-backed function calls.");
                                }
                                break;
                            }
                            case OpCode.CallMethodFromArray:
                            {
                                var argsArrayVal = _stack[--_sp];
                                var callee = _stack[--_sp];
                                var receiver = _stack[--_sp];

                                if (!callee.IsFunction) throw new Exception("VM Error: Attempted to call non-function.");
                                var func = callee.AsObject() as FenFunction;
                                var argsObject = argsArrayVal.IsObject ? argsArrayVal.AsObject() : null;
                                int argCount = GetArrayLikeLength(argsObject);

                                if (func.IsNative)
                                {
                                    var args = ExtractArrayLikeValues(argsArrayVal);
                                    if (!func.ProxyHandler.IsUndefined && func.ProxyHandler.IsObject)
                                    {
                                        _stack[_sp++] = func.Invoke(args, null, receiver);
                                    }
                                    else
                                    {
                                        _stack[_sp++] = func.NativeImplementation(args, receiver);
                                    }
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    var newEnv = new FenEnvironment(func.Env);
                                    InitializeFunctionFastStore(func, newEnv);
                                    if (func.LocalMap != null && !string.IsNullOrEmpty(func.Name) && func.LocalMap.ContainsKey(func.Name))
                                    {
                                        SetFunctionBinding(func, newEnv, func.Name, FenValue.FromFunction(func));
                                    }
                                    if (!func.IsArrowFunction)
                                    {
                                        SetFunctionBinding(func, newEnv, "this", receiver);
                                    }
                                    BindFunctionArgumentsFromArrayLike(func, newEnv, argsObject, argCount);

                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.NewTarget = FenValue.Undefined;
                                    newFrame.IsAsyncFunction = func.IsAsync;
                                    goto fetch_frame;
                                }
                                else
                                {
                                    throw new Exception("VM Error: Bytecode-only mode does not support AST-backed function calls.");
                                }
                                break;
                            }
                            case OpCode.Construct:
                            {
                                int argCount = ReadInt32(instructions, ref frame);
                                int argStart = _sp - argCount;
                                var constructorVal = _stack[argStart - 1];

                                if (!constructorVal.IsFunction) throw new Exception("VM Error: Attempted to construct non-function.");
                                var func = constructorVal.AsObject() as FenFunction;
                                if (func.IsArrowFunction || !func.IsConstructor)
                                {
                                    throw new Exception("TypeError: " + func.Name + " is not a constructor");
                                }

                                // Create new empty object
                                var newObj = new FenObject();
                                
                                // Wire up prototype
                                var prototypeVal = func.Get("prototype");
                                if (prototypeVal.IsObject)
                                {
                                    newObj.SetPrototype(prototypeVal.AsObject());
                                }
                                else
                                {
                                    newObj.SetPrototype(frame.Environment.Get("Object").AsObject().Get("prototype").AsObject());
                                }
                                
                                if (func.IsNative)
                                {
                                    var args = new FenValue[argCount];
                                    if (argCount > 0)
                                    {
                                        Array.Copy(_stack, argStart, args, 0, argCount);
                                    }

                                    _sp = argStart - 1; // Pop constructor + args

                                    // Native constructors usually ignore 'this' passed in and return their own newly created object,
                                    // or we pass newObj as 'this' depending on FenRuntime design.
                                    var result = func.NativeImplementation(args, FenValue.FromObject(newObj));
                                    if (result.IsObject) _stack[_sp++] = result;
                                    else _stack[_sp++] = FenValue.FromObject(newObj);
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    var newEnv = new FenEnvironment(func.Env);
                                     
                                    // Bind 'this' to newObj
                                    InitializeFunctionFastStore(func, newEnv);
                                    if (func.LocalMap != null && !string.IsNullOrEmpty(func.Name) && func.LocalMap.ContainsKey(func.Name))
                                    {
                                        SetFunctionBinding(func, newEnv, func.Name, FenValue.FromFunction(func));
                                    }
                                    SetFunctionBinding(func, newEnv, "this", FenValue.FromObject(newObj));
                                    BindFunctionArgumentsFromStack(func, newEnv, argCount, argStart);

                                    _sp = argStart - 1; // Pop constructor + args
                                     
                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.IsConstruct = true;
                                    newFrame.ConstructedObject = newObj;
                                    newFrame.NewTarget = constructorVal;
                                    
                                    goto fetch_frame;
                                }
                                else
                                {
                                    throw new Exception("VM Error: Bytecode-only mode does not support AST-backed constructor calls.");
                                }
                                break;
                            }
                            case OpCode.ConstructFromArray:
                            {
                                var argsArrayVal = _stack[--_sp];
                                var constructorVal = _stack[--_sp];

                                if (!constructorVal.IsFunction) throw new Exception("VM Error: Attempted to construct non-function.");
                                var func = constructorVal.AsObject() as FenFunction;
                                if (func.IsArrowFunction || !func.IsConstructor)
                                {
                                    throw new Exception("TypeError: " + func.Name + " is not a constructor");
                                }

                                var newObj = new FenObject();
                                var prototypeVal = func.Get("prototype");
                                if (prototypeVal.IsObject)
                                {
                                    newObj.SetPrototype(prototypeVal.AsObject());
                                }
                                else
                                {
                                    newObj.SetPrototype(frame.Environment.Get("Object").AsObject().Get("prototype").AsObject());
                                }

                                var argsObject = argsArrayVal.IsObject ? argsArrayVal.AsObject() : null;
                                int argCount = GetArrayLikeLength(argsObject);

                                if (func.IsNative)
                                {
                                    var args = ExtractArrayLikeValues(argsArrayVal);
                                    var result = func.NativeImplementation(args, FenValue.FromObject(newObj));
                                    if (result.IsObject) _stack[_sp++] = result;
                                    else _stack[_sp++] = FenValue.FromObject(newObj);
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    var newEnv = new FenEnvironment(func.Env);
                                    InitializeFunctionFastStore(func, newEnv);
                                    if (func.LocalMap != null && !string.IsNullOrEmpty(func.Name) && func.LocalMap.ContainsKey(func.Name))
                                    {
                                        SetFunctionBinding(func, newEnv, func.Name, FenValue.FromFunction(func));
                                    }
                                    SetFunctionBinding(func, newEnv, "this", FenValue.FromObject(newObj));
                                    BindFunctionArgumentsFromArrayLike(func, newEnv, argsObject, argCount);

                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.IsConstruct = true;
                                    newFrame.ConstructedObject = newObj;
                                    newFrame.NewTarget = constructorVal;

                                    goto fetch_frame;
                                }
                                else
                                {
                                    throw new Exception("VM Error: Bytecode-only mode does not support AST-backed constructor calls.");
                                }
                                break;
                            }
                            case OpCode.MakeArray:
                            {
                                int count = ReadInt32(instructions, ref frame);
                                var arr = new BytecodeArrayObject();
                                for (int i = 0; i < count; i++)
                                {
                                    arr.SetElement(i, _stack[_sp - count + i]);
                                }
                                _sp -= count;
                                _stack[_sp++] = FenValue.FromObject(arr);
                                break;
                            }
                            case OpCode.MakeObject:
                            {
                                int propCount = ReadInt32(instructions, ref frame);
                                var obj = new FenObject();
                                int numValues = propCount * 2;
                                for (int i = 0; i < numValues; i += 2)
                                {
                                    var key = PropertyKey(_stack[_sp - numValues + i]);
                                    var value = _stack[_sp - numValues + i + 1];
                                    obj.Set(key, value);
                                }
                                _sp -= numValues;
                                _stack[_sp++] = FenValue.FromObject(obj);
                                break;
                            }
                            case OpCode.LoadProp:
                            {
                                var prop = _stack[--_sp];
                                var obj = _stack[--_sp];
                                var objectRef = obj.AsObject();
                                if (objectRef != null)
                                {
                                    var key = PropertyKey(prop);
                                    if (objectRef is FenObject fenObj)
                                    {
                                        // Lazy-link RegExp literal objects to RegExp.prototype so
                                        // methods like test/exec/compile are found via prototype chain.
                                        if (fenObj.GetPrototype() == null && fenObj.InternalClass == "RegExp")
                                        {
                                            EnsureRegExpPrototype(fenObj, frame);
                                        }

                                        var cache = GetLoadPropertyInlineCache(frame.Block);
                                        if (TryLoadPropertyInlineCache(cache, instructionOffset, fenObj, key, out var cachedValue))
                                        {
                                            _stack[_sp++] = cachedValue;
                                            break;
                                        }

                                        var value = fenObj.Get(key);
                                        _stack[_sp++] = value;
                                        PopulatePropertyInlineCache(cache, instructionOffset, fenObj, key, writableRequired: false);
                                    }
                                    else
                                    {
                                        _stack[_sp++] = objectRef.Get(key);
                                    }
                                }
                                else if (obj.IsString)
                                {
                                    // Primitive string: handle length, index access, and String.prototype lookup
                                    var str = obj.AsString();
                                    var key = PropertyKey(prop);
                                    if (string.Equals(key, "length", StringComparison.Ordinal))
                                    {
                                        _stack[_sp++] = FenValue.FromNumber(str.Length);
                                    }
                                    else if (TryParseNonNegativeInt(key, out int charIdx) && (uint)charIdx < (uint)str.Length)
                                    {
                                        _stack[_sp++] = FenValue.FromString(str[charIdx].ToString());
                                    }
                                    else
                                    {
                                        var proto = GetPrimitivePrototype(frame, "String");
                                        _stack[_sp++] = proto != null ? proto.Get(key) : FenValue.Undefined;
                                    }
                                }
                                else if (obj.IsNumber)
                                {
                                    var key = PropertyKey(prop);
                                    var proto = GetPrimitivePrototype(frame, "Number");
                                    _stack[_sp++] = proto != null ? proto.Get(key) : FenValue.Undefined;
                                }
                                else if (obj.IsBoolean)
                                {
                                    var key = PropertyKey(prop);
                                    var proto = GetPrimitivePrototype(frame, "Boolean");
                                    _stack[_sp++] = proto != null ? proto.Get(key) : FenValue.Undefined;
                                }
                                else
                                {
                                    _stack[_sp++] = FenValue.Undefined;
                                }
                                break;
                            }
                            case OpCode.StoreProp:
                            {
                                var value = _stack[--_sp];
                                var prop = _stack[--_sp];
                                var obj = _stack[--_sp];
                                var objectRef = obj.AsObject();
                                if (objectRef != null)
                                {
                                    var key = PropertyKey(prop);
                                    if (objectRef is FenObject fenObj && !string.Equals(key, "__proto__", StringComparison.Ordinal))
                                    {
                                        var cache = GetStorePropertyInlineCache(frame.Block);
                                        if (TryLoadPropertyInlineCache(cache, instructionOffset, fenObj, key, out _))
                                        {
                                            var cacheEntry = cache[instructionOffset];
                                            var storage = fenObj.GetPropertyStorage();
                                            if ((uint)cacheEntry.SlotIndex < (uint)storage.Length)
                                            {
                                                var descriptor = storage[cacheEntry.SlotIndex];
                                                if (!descriptor.IsAccessor && descriptor.Writable != false)
                                                {
                                                    descriptor.Value = value;
                                                    storage[cacheEntry.SlotIndex] = descriptor;
                                                    _stack[_sp++] = value;
                                                    break;
                                                }
                                            }
                                        }

                                        fenObj.Set(key, value);
                                        PopulatePropertyInlineCache(cache, instructionOffset, fenObj, key, writableRequired: true);
                                    }
                                    else
                                    {
                                        objectRef.Set(key, value);
                                    }
                                }
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.ArrayAppend:
                            {
                                var value = _stack[--_sp];
                                var arrayValue = _stack[--_sp];

                                if (arrayValue.IsObject)
                                {
                                    var arrayObj = arrayValue.AsObject();
                                    if (arrayObj is BytecodeArrayObject denseArray)
                                    {
                                        denseArray.Append(value);
                                    }
                                    else
                                    {
                                        int length = GetArrayLikeLength(arrayObj);
                                        arrayObj.Set(IndexKey(length), value);
                                        arrayObj.Set("length", FenValue.FromNumber(length + 1));
                                    }
                                }

                                _stack[_sp++] = arrayValue;
                                break;
                            }
                            case OpCode.ArrayAppendSpread:
                            {
                                var spreadValue = _stack[--_sp];
                                var arrayValue = _stack[--_sp];

                                if (arrayValue.IsObject)
                                {
                                    var arrayObj = arrayValue.AsObject();
                                    if (arrayObj is BytecodeArrayObject denseTarget)
                                    {
                                        bool expanded = false;
                                        if (spreadValue.IsString)
                                        {
                                            // Spread string as individual characters: [...'abc'] = ['a','b','c']
                                            var str = spreadValue.AsString();
                                            for (int i = 0; i < str.Length; i++)
                                            {
                                                denseTarget.Append(FenValue.FromString(str[i].ToString()));
                                            }
                                            expanded = true;
                                        }
                                        else if (spreadValue.IsObject && spreadValue.AsObject() is BytecodeArrayObject denseSource)
                                        {
                                            for (int i = 0; i < denseSource.Length; i++)
                                            {
                                                if (denseSource.TryGetElement(i, out var spreadElement))
                                                {
                                                    denseTarget.Append(spreadElement);
                                                }
                                                else
                                                {
                                                    denseTarget.Append(FenValue.Undefined);
                                                }
                                            }

                                            expanded = true;
                                        }
                                        else if (spreadValue.IsObject)
                                        {
                                            var spreadObj = spreadValue.AsObject();
                                            int spreadLength = GetArrayLikeLength(spreadObj);
                                            if (spreadLength > 0)
                                            {
                                                for (int i = 0; i < spreadLength; i++)
                                                {
                                                    denseTarget.Append(spreadObj.Get(IndexKey(i)));
                                                }

                                                expanded = true;
                                            }
                                        }

                                        if (!expanded)
                                        {
                                            denseTarget.Append(spreadValue);
                                        }
                                    }
                                    else
                                    {
                                        int length = GetArrayLikeLength(arrayObj);
                                        bool expanded = false;
                                        if (spreadValue.IsString)
                                        {
                                            // Spread string as individual characters into generic array
                                            var str = spreadValue.AsString();
                                            for (int i = 0; i < str.Length; i++)
                                            {
                                                arrayObj.Set(IndexKey(length + i), FenValue.FromString(str[i].ToString()));
                                            }
                                            length += str.Length;
                                            expanded = true;
                                        }
                                        else if (spreadValue.IsObject)
                                        {
                                            var spreadObj = spreadValue.AsObject();
                                            if (spreadObj != null && spreadObj.Has("length"))
                                            {
                                                var spreadLenVal = spreadObj.Get("length");
                                                if (spreadLenVal.IsNumber)
                                                {
                                                    int spreadLength = (int)spreadLenVal.ToNumber();
                                                    for (int i = 0; i < spreadLength; i++)
                                                    {
                                                        arrayObj.Set(IndexKey(length + i), spreadObj.Get(IndexKey(i)));
                                                    }
                                                    length += spreadLength;
                                                    expanded = true;
                                                }
                                            }
                                        }

                                        if (!expanded)
                                        {
                                            arrayObj.Set(IndexKey(length), spreadValue);
                                            length += 1;
                                        }

                                        arrayObj.Set("length", FenValue.FromNumber(length));
                                    }
                                }

                                _stack[_sp++] = arrayValue;
                                break;
                            }
                            case OpCode.ObjectSpread:
                            {
                                var sourceValue = _stack[--_sp];
                                var targetValue = _stack[--_sp];

                                if (targetValue.IsObject && sourceValue.IsObject)
                                {
                                    var targetObj = targetValue.AsObject();
                                    var sourceObj = sourceValue.AsObject();
                                    foreach (var key in sourceObj.Keys())
                                    {
                                        targetObj.Set(key, sourceObj.Get(key));
                                    }
                                }

                                _stack[_sp++] = targetValue;
                                break;
                            }
                            case OpCode.DeleteProp:
                            {
                                var prop = _stack[--_sp];
                                var obj = _stack[--_sp];
                                bool deleted = true;
                                var objectRef = obj.AsObject();
                                if (objectRef != null)
                                {
                                    deleted = objectRef.Delete(PropertyKey(prop));
                                }

                                _stack[_sp++] = FenValue.FromBoolean(deleted);
                                break;
                            }
                            case OpCode.MakeKeysIterator:
                            {
                                var objVal = _stack[--_sp];
                                var iterObj = new FenObject();
                                if (objVal.IsString)
                                {
                                    // for...in on a string yields "0", "1", "2", ... for each character index
                                    var str = objVal.AsString();
                                    var indexKeys = new string[str.Length];
                                    for (int i = 0; i < str.Length; i++)
                                    {
                                        indexKeys[i] = IndexKey(i);
                                    }
                                    iterObj.NativeObject = new KeyIteratorEnumerator(((IEnumerable<string>)indexKeys).GetEnumerator());
                                }
                                else if (objVal.IsObject)
                                {
                                    var obj = objVal.AsObject();
                                    iterObj.NativeObject = obj != null
                                        ? new KeyIteratorEnumerator(obj.Keys().GetEnumerator())
                                        : (IEnumerator<FenValue>)EmptyFenValueEnumerator.Instance;
                                }
                                else
                                {
                                    iterObj.NativeObject = EmptyFenValueEnumerator.Instance;
                                }
                                _stack[_sp++] = FenValue.FromObject(iterObj);
                                break;
                            }
                            case OpCode.MakeValuesIterator:
                            {
                                var objVal = _stack[--_sp];
                                var iterObj = new FenObject();
                                if (objVal.IsString)
                                {
                                    iterObj.NativeObject = new StringIteratorEnumerator(objVal.AsString());
                                }
                                else if (objVal.IsObject)
                                {
                                    var obj = objVal.AsObject();
                                    iterObj.NativeObject = obj != null
                                        ? new ValueIteratorEnumerator(obj)
                                        : (IEnumerator<FenValue>)EmptyFenValueEnumerator.Instance;
                                }
                                else
                                {
                                    iterObj.NativeObject = EmptyFenValueEnumerator.Instance;
                                }
                                _stack[_sp++] = FenValue.FromObject(iterObj);
                                break;
                            }
                            case OpCode.IteratorMoveNext:
                            {
                                var obj = (FenObject)_stack[--_sp].AsObject();
                                var iter = (IEnumerator<FenValue>)obj.NativeObject;
                                _stack[_sp++] = FenValue.FromBoolean(iter.MoveNext());
                                break;
                            }
                            case OpCode.IteratorCurrent:
                            {
                                var obj = (FenObject)_stack[--_sp].AsObject();
                                var iter = (IEnumerator<FenValue>)obj.NativeObject;
                                _stack[_sp++] = iter.Current;
                                break;
                            }
                            case OpCode.Negate:
                            {
                                var val = _stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(-val);
                                break;
                            }
                            case OpCode.LogicalNot:
                            {
                                var val = _stack[--_sp].ToBoolean();
                                _stack[_sp++] = FenValue.FromBoolean(!val);
                                break;
                            }
                            case OpCode.BitwiseNot:
                            {
                                var val = (int)_stack[--_sp].ToNumber();
                                _stack[_sp++] = FenValue.FromNumber(~val);
                                break;
                            }
                            case OpCode.Typeof:
                            {
                                var val = _stack[--_sp];
                                string typeStr = "object";
                                switch (val.Type)
                                {
                                    case JsValueType.Undefined: typeStr = "undefined"; break;
                                    case JsValueType.Null: typeStr = "object"; break;
                                    case JsValueType.Boolean: typeStr = "boolean"; break;
                                    case JsValueType.Number: typeStr = "number"; break;
                                    case JsValueType.String: typeStr = "string"; break;
                                    case JsValueType.Function: typeStr = "function"; break;
                                    case JsValueType.Symbol: typeStr = "symbol"; break;
                                    case JsValueType.BigInt: typeStr = "bigint"; break;
                                    default: typeStr = "object"; break;
                                }
                                _stack[_sp++] = FenValue.FromString(typeStr);
                                break;
                            }
                            case OpCode.ToNumber:
                            {
                                var val = _stack[--_sp];
                                _stack[_sp++] = FenValue.FromNumber(val.ToNumber());
                                break;
                            }
                            case OpCode.LoadNewTarget:
                            {
                                _stack[_sp++] = frame.NewTarget;
                                break;
                            }
                            case OpCode.Await:
                            {
                                var awaitValue = _stack[--_sp];
                                _stack[_sp++] = ResolveAwaitValue(awaitValue);
                                break;
                            }
                            case OpCode.EnterWith:
                            {
                                var withObjectValue = _stack[--_sp];
                                if (!withObjectValue.IsObject)
                                {
                                    // Keep bytecode path stable for unsupported non-object with operands.
                                    break;
                                }

                                var withEnv = new FenEnvironment(frame.Environment);
                                var withObject = withObjectValue.AsObject();
                                if (withObject != null)
                                {
                                    foreach (var key in withObject.Keys())
                                    {
                                        withEnv.Set(key, withObject.Get(key));
                                    }
                                }

                                frame.WithEnvironments.Push(frame.Environment);
                                frame.SetEnvironment(withEnv);
                                break;
                            }
                            case OpCode.ExitWith:
                            {
                                if (frame.HasWithEnvironments)
                                {
                                    frame.SetEnvironment(frame.WithEnvironments.Pop());
                                }
                                break;
                            }
                            case OpCode.Return:
                            {
                                var result = _stack[--_sp];
                                _frameCount--;
                                _sp = frame.StackBase;

                                // If this was a constructor call, returning a primitive yields the constructed object.
                                // Returning an object yields that object.
                                if (frame.IsConstruct && !result.IsObject)
                                {
                                    result = FenValue.FromObject(frame.ConstructedObject);
                                }
                                if (frame.IsAsyncFunction)
                                {
                                    result = WrapAsyncReturnValue(result);
                                }
                                
                                // Push result back for caller, or return if top level
                                if (_frameCount > 0)
                                {
                                    _stack[_sp++] = result;
                                    goto fetch_frame; // Re-fetch the caller's frame locals
                                }
                                else
                                {
                                    return result;
                                }
                            }
                            case OpCode.PushExceptionHandler:
                            {
                                int catchOffset = ReadInt32(instructions, ref frame);
                                int finallyOffset = ReadInt32(instructions, ref frame);
                                frame.ExceptionHandlers.Push(new ExceptionHandler(catchOffset, finallyOffset, _sp));
                                break;
                            }
                            case OpCode.PopExceptionHandler:
                            {
                                frame.ExceptionHandlers.Pop();
                                break;
                            }
                            case OpCode.Throw:
                            {
                                var exceptionValue = _stack[--_sp];
                                HandleException(exceptionValue, ref frame);
                                goto fetch_frame;
                            }
                            case OpCode.Halt:
                            {
                                // Halt marks the end of the current frame's code block.
                                // Top-level frame returns script completion value; nested frames
                                // fall through as implicit undefined (or constructed object).
                                if (_frameCount > 1)
                                {
                                    var result = FenValue.Undefined;
                                    _frameCount--;
                                    _sp = frame.StackBase;

                                    if (frame.IsConstruct)
                                    {
                                        result = FenValue.FromObject(frame.ConstructedObject);
                                    }
                                    if (frame.IsAsyncFunction)
                                    {
                                        result = WrapAsyncReturnValue(result);
                                    }

                                    _stack[_sp++] = result;
                                    goto fetch_frame;
                                }

                                return _completionValue;
                            }    
                            default:
                                throw new Exception($"VM Error: Unhandled OpCode {op} at IP {frame.IP - 1}");
                        }
                    }
                    
                    // Reached end of instructions without a return
                    if (_frameCount > 0 && _callFrames[_frameCount - 1] == frame)
                    {
                        var result = FenValue.Undefined;
                        _frameCount--;
                        _sp = frame.StackBase;
                        
                        if (frame.IsConstruct)
                        {
                            result = FenValue.FromObject(frame.ConstructedObject);
                        }
                        if (frame.IsAsyncFunction)
                        {
                            result = WrapAsyncReturnValue(result);
                        }

                        if (_frameCount > 0)
                            _stack[_sp++] = result;
                        else
                            return result;
                    }
                }
            }
            catch (Exception ex)
            {
                // Unwind and handle .NET exceptions gracefully
                // Parse error type from message prefix (e.g. "TypeError: ...")
                string rawMsg = ex.Message;
                string errType = "Error";
                string errMsg = rawMsg;
                var colonIdx = rawMsg.IndexOf(':');
                if (colonIdx > 0)
                {
                    var prefix = rawMsg.Substring(0, colonIdx);
                    if (prefix == "TypeError" || prefix == "RangeError" || prefix == "ReferenceError" ||
                        prefix == "SyntaxError" || prefix == "URIError" || prefix == "EvalError")
                    {
                        errType = prefix;
                        errMsg = rawMsg.Substring(colonIdx + 1).TrimStart();
                    }
                }

                FenValue errorObj;
                // Try to create a properly-typed error object using the registered constructor
                bool madeTyped = false;
                if (_frameCount > 0)
                {
                    try
                    {
                        var env = _callFrames[_frameCount - 1].Environment;
                        var ctorVal = env.Get(errType);
                        if (ctorVal.IsFunction)
                        {
                            var errCtor = ctorVal.AsFunction() as FenFunction;
                            var protoVal = errCtor?.Get("prototype") ?? FenValue.Undefined;
                            var errObj = new FenObject();
                            if (protoVal.IsObject) errObj.SetPrototype(protoVal.AsObject());
                            errObj.Set("name", FenValue.FromString(errType));
                            errObj.Set("message", FenValue.FromString(errMsg));
                            errObj.Set("stack", FenValue.FromString($"{errType}: {errMsg}\n    at <anonymous>"));
                            errorObj = FenValue.FromObject(errObj);
                            madeTyped = true;
                        }
                        else { errorObj = FenValue.Undefined; }
                    }
                    catch { errorObj = FenValue.Undefined; }
                }
                else { errorObj = FenValue.Undefined; }

                if (!madeTyped)
                {
                    var plainErr = new FenObject();
                    plainErr.Set("message", FenValue.FromString(errMsg));
                    plainErr.Set("name", FenValue.FromString(errType));
                    errorObj = FenValue.FromObject(plainErr);
                }

                if (_frameCount > 0)
                {
                    var topFrame = _callFrames[_frameCount - 1];
                    HandleException(errorObj, ref topFrame);
                    // Resume execution at the installed JS catch/finally handler.
                    return RunLoop();
                }
                else
                {
                    throw new Exception($"Uncaught JS Exception: {FormatExceptionValue(errorObj)}", ex);
                }
            }

            return FenValue.Undefined;
        }

        private string FormatExceptionValue(FenValue value)
        {
            if (value.IsObject)
            {
                var obj = value.AsObject();
                if (obj != null)
                {
                    var nameVal = obj.Get("name");
                    var msgVal = obj.Get("message");
                    if (nameVal.Type != JsValueType.Undefined || msgVal.Type != JsValueType.Undefined)
                    {
                        var nameStr = nameVal.Type != JsValueType.Undefined ? nameVal.AsString() : "Error";
                        var msgStr = msgVal.Type != JsValueType.Undefined ? msgVal.AsString() : "";
                        return string.IsNullOrEmpty(msgStr) ? nameStr : $"{nameStr}: {msgStr}";
                    }
                }
            }
            return value.AsString();
        }

        private void HandleException(FenValue exceptionValue, ref CallFrame currentFrame)
        {
            // Find nearest handler in current frame or unwind CallStack
            while (_frameCount > 0)
            {
                var frame = _callFrames[_frameCount - 1];
                if (frame.HasExceptionHandlers)
                {
                    var handler = frame.ExceptionHandlers.Pop();
                    _sp = handler.StackBase;
                    
                    if (handler.CatchOffset != -1)
                    {
                        frame.IP = handler.CatchOffset;
                        _stack[_sp++] = exceptionValue; // Push error for catch block
                        return; // Resume execution in RunLoop
                    }
                    else if (handler.FinallyOffset != -1)
                    {
                        frame.IP = handler.FinallyOffset;
                        // For pure finally, we might need to store the exception somewhere to rethrow later
                        // But JS 'finally' without 'catch' is rare in Phase 1
                        _stack[_sp++] = exceptionValue; 
                        return; // Resume execution in RunLoop
                    }
                }

                // Async functions capture uncaught exceptions as rejected promise results,
                // rather than propagating host exceptions through callers.
                if (frame.IsAsyncFunction)
                {
                    var rejection = WrapAsyncReturnValue(FenValue.FromError(exceptionValue.AsString()));
                    _frameCount--;
                    _sp = frame.StackBase;

                    if (_frameCount > 0)
                    {
                        _stack[_sp++] = rejection;
                    }
                    else
                    {
                        _completionValue = rejection;
                    }
                    return;
                }
                
                // No handler in this frame, unwind call stack!
                _frameCount--;
                if (_frameCount > 0)
                {
                    _sp = _callFrames[_frameCount - 1].StackBase;
                }
            }
            
            // Uncaught exception!
            var formattedErr = FormatExceptionValue(exceptionValue);
            Console.WriteLine($"[VM Uncaught Exception] {formattedErr}");
            throw new Exception($"Uncaught JS Exception: {formattedErr}");
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private int ReadInt32(byte[] instructions, ref CallFrame frame)
        {
            int ip = frame.IP;
            int val = instructions[ip] | (instructions[ip + 1] << 8) | (instructions[ip + 2] << 16) | (instructions[ip + 3] << 24);
            frame.IP = ip + 4;
            return val;
        }

        private static void InitializeFunctionFastStore(FenFunction func, FenEnvironment env)
        {
            if (func?.LocalMap != null && func.LocalMap.Count > 0)
            {
                env.InitializeFastStore(func.LocalMap.Count);
            }
        }

        private static void SetFunctionBinding(FenFunction func, FenEnvironment env, string name, FenValue value)
        {
            env.Set(name, value);
            if (func?.LocalMap != null && func.LocalMap.TryGetValue(name, out int localSlot))
            {
                env.SetFast(localSlot, value);
            }
        }

        private static void BindFunctionArguments(FenFunction func, FenEnvironment env, FenValue[] args)
        {
            if (func == null || env == null)
            {
                return;
            }

            InitializeFunctionFastStore(func, env);

            var effectiveArgs = args ?? Array.Empty<FenValue>();

            if (!func.IsArrowFunction && func.NeedsArgumentsObject)
            {
                var argumentsObj = new FenObject
                {
                    InternalClass = "Arguments"
                };

                for (int i = 0; i < effectiveArgs.Length; i++)
                {
                    argumentsObj.Set(IndexKey(i), effectiveArgs[i]);
                }

                argumentsObj.Set("length", FenValue.FromNumber(effectiveArgs.Length));
                argumentsObj.Set("callee", FenValue.FromFunction(func));

                var paramNames = new FenObject();
                if (func.Parameters != null)
                {
                    for (int i = 0; i < func.Parameters.Count && i < effectiveArgs.Length; i++)
                    {
                        var parameter = func.Parameters[i];
                        if (parameter == null || parameter.IsRest || string.IsNullOrEmpty(parameter.Value))
                        {
                            continue;
                        }

                        paramNames.Set(i.ToString(), FenValue.FromString(parameter.Value));
                    }
                }

                argumentsObj.Set("__paramNames__", FenValue.FromObject(paramNames));
                SetFunctionBinding(func, env, "arguments", FenValue.FromObject(argumentsObj));
            }

            if (func.Parameters == null)
            {
                return;
            }

            for (int i = 0; i < func.Parameters.Count; i++)
            {
                var parameter = func.Parameters[i];
                if (parameter == null || string.IsNullOrEmpty(parameter.Value))
                {
                    continue;
                }

                if (parameter.IsRest)
                {
                    var restArray = new BytecodeArrayObject();
                    for (int j = i; j < effectiveArgs.Length; j++)
                    {
                        restArray.Append(effectiveArgs[j]);
                    }

                    SetFunctionBinding(func, env, parameter.Value, FenValue.FromObject(restArray));
                    break;
                }

                var argValue = i < effectiveArgs.Length ? effectiveArgs[i] : FenValue.Undefined;
                SetFunctionBinding(func, env, parameter.Value, argValue);
            }
        }

        private void BindFunctionArgumentsFromStack(FenFunction func, FenEnvironment env, int argCount, int firstArgStackIndex)
        {
            if (func == null || env == null)
            {
                return;
            }

            InitializeFunctionFastStore(func, env);

            if (!func.IsArrowFunction && func.NeedsArgumentsObject)
            {
                var argumentsObj = new FenObject
                {
                    InternalClass = "Arguments"
                };

                for (int i = 0; i < argCount; i++)
                {
                    argumentsObj.Set(IndexKey(i), _stack[firstArgStackIndex + i]);
                }

                argumentsObj.Set("length", FenValue.FromNumber(argCount));
                argumentsObj.Set("callee", FenValue.FromFunction(func));

                var paramNames = new FenObject();
                if (func.Parameters != null)
                {
                    for (int i = 0; i < func.Parameters.Count && i < argCount; i++)
                    {
                        var parameter = func.Parameters[i];
                        if (parameter == null || parameter.IsRest || string.IsNullOrEmpty(parameter.Value))
                        {
                            continue;
                        }

                        paramNames.Set(IndexKey(i), FenValue.FromString(parameter.Value));
                    }
                }

                argumentsObj.Set("__paramNames__", FenValue.FromObject(paramNames));
                SetFunctionBinding(func, env, "arguments", FenValue.FromObject(argumentsObj));
            }

            if (func.Parameters == null)
            {
                return;
            }

            for (int i = 0; i < func.Parameters.Count; i++)
            {
                var parameter = func.Parameters[i];
                if (parameter == null || string.IsNullOrEmpty(parameter.Value))
                {
                    continue;
                }

                if (parameter.IsRest)
                {
                    var restArray = new BytecodeArrayObject();
                    for (int j = i; j < argCount; j++)
                    {
                        restArray.Append(_stack[firstArgStackIndex + j]);
                    }

                    SetFunctionBinding(func, env, parameter.Value, FenValue.FromObject(restArray));
                    break;
                }

                var argValue = i < argCount ? _stack[firstArgStackIndex + i] : FenValue.Undefined;
                SetFunctionBinding(func, env, parameter.Value, argValue);
            }
        }

        private static void BindFunctionArgumentsFromArrayLike(
            FenFunction func,
            FenEnvironment env,
            FenBrowser.FenEngine.Core.Interfaces.IObject argsObject,
            int argCount)
        {
            if (func == null || env == null)
            {
                return;
            }

            InitializeFunctionFastStore(func, env);

            if (!func.IsArrowFunction && func.NeedsArgumentsObject)
            {
                var argumentsObj = new FenObject
                {
                    InternalClass = "Arguments"
                };

                for (int i = 0; i < argCount; i++)
                {
                    argumentsObj.Set(IndexKey(i), argsObject?.Get(IndexKey(i)) ?? FenValue.Undefined);
                }

                argumentsObj.Set("length", FenValue.FromNumber(argCount));
                argumentsObj.Set("callee", FenValue.FromFunction(func));

                var paramNames = new FenObject();
                if (func.Parameters != null)
                {
                    for (int i = 0; i < func.Parameters.Count && i < argCount; i++)
                    {
                        var parameter = func.Parameters[i];
                        if (parameter == null || parameter.IsRest || string.IsNullOrEmpty(parameter.Value))
                        {
                            continue;
                        }

                        paramNames.Set(IndexKey(i), FenValue.FromString(parameter.Value));
                    }
                }

                argumentsObj.Set("__paramNames__", FenValue.FromObject(paramNames));
                SetFunctionBinding(func, env, "arguments", FenValue.FromObject(argumentsObj));
            }

            if (func.Parameters == null)
            {
                return;
            }

            for (int i = 0; i < func.Parameters.Count; i++)
            {
                var parameter = func.Parameters[i];
                if (parameter == null || string.IsNullOrEmpty(parameter.Value))
                {
                    continue;
                }

                if (parameter.IsRest)
                {
                    var restArray = new BytecodeArrayObject();
                    for (int j = i; j < argCount; j++)
                    {
                        restArray.Append(argsObject?.Get(IndexKey(j)) ?? FenValue.Undefined);
                    }

                    SetFunctionBinding(func, env, parameter.Value, FenValue.FromObject(restArray));
                    break;
                }

                var argValue = i < argCount
                    ? (argsObject?.Get(IndexKey(i)) ?? FenValue.Undefined)
                    : FenValue.Undefined;

                SetFunctionBinding(func, env, parameter.Value, argValue);
            }
        }

        private FenValue[] ExtractArrayLikeValues(FenValue argsArrayValue)
        {
            if (!argsArrayValue.IsObject)
            {
                return Array.Empty<FenValue>();
            }

            var argsObject = argsArrayValue.AsObject();
            if (argsObject is BytecodeArrayObject denseArgs)
            {
                if (denseArgs.Length <= 0)
                {
                    return Array.Empty<FenValue>();
                }

                var denseValues = new FenValue[denseArgs.Length];
                for (int i = 0; i < denseArgs.Length; i++)
                {
                    denseValues[i] = denseArgs.TryGetElement(i, out var value)
                        ? value
                        : FenValue.Undefined;
                }

                return denseValues;
            }

            int length = GetArrayLikeLength(argsObject);
            if (length <= 0)
            {
                return Array.Empty<FenValue>();
            }

            var args = new FenValue[length];
            for (int i = 0; i < length; i++)
            {
                args[i] = argsObject.Get(IndexKey(i));
            }

            return args;
        }

        private static int GetArrayLikeLength(FenBrowser.FenEngine.Core.Interfaces.IObject obj)
        {
            if (obj == null)
            {
                return 0;
            }

            if (obj is BytecodeArrayObject dense)
            {
                return dense.Length;
            }

            var lengthValue = obj.Get("length");
            if (!lengthValue.IsNumber)
            {
                return 0;
            }

            int length = (int)lengthValue.ToNumber();
            return length < 0 ? 0 : length;
        }

        private static FenValue WrapAsyncReturnValue(FenValue result)
        {
            if (result.Type == JsValueType.Error)
            {
                return FenValue.FromObject(JsPromise.Reject(result, null));
            }

            if (result.IsObject && result.AsObject() is JsPromise)
            {
                return result;
            }

            return FenValue.FromObject(JsPromise.Resolve(result, null));
        }

        private static FenValue ResolveAwaitValue(FenValue value)
        {
            JsPromise promise = null;
            if (value.IsObject && value.AsObject() is JsPromise existingPromise)
            {
                promise = existingPromise;
            }
            else if (value.IsObject)
            {
                var obj = value.AsObject();
                var thenVal = obj?.Get("then");
                if (thenVal.HasValue && thenVal.Value.IsFunction)
                {
                    promise = JsPromise.Resolve(value, null);
                }
                else
                {
                    return value;
                }
            }
            else
            {
                return value;
            }

            if (promise.IsSettled)
            {
                return promise.IsFulfilled ? promise.Result : FenValue.FromError(promise.Result.ToString());
            }

            const int maxPumps = 5000;
            for (int i = 0; i < maxPumps; i++)
            {
                try
                {
                    EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                if (promise.IsSettled)
                {
                    break;
                }

                if (EventLoopCoordinator.Instance.HasPendingTasks)
                {
                    try
                    {
                        EventLoopCoordinator.Instance.ProcessNextTask();
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }
                }

                if (promise.IsSettled)
                {
                    break;
                }
            }

            if (promise.IsSettled)
            {
                return promise.IsFulfilled ? promise.Result : FenValue.FromError(promise.Result.ToString());
            }

            return FenValue.Undefined;
        }
        
        private FenValue ExecuteAdd(FenValue left, FenValue right)
        {
            // ES Spec: if either is string, concat
            if (left.IsString || right.IsString)
            {
                return FenValue.FromString(left.ToString() + right.ToString());
            }
            // else numeric addition
            return FenValue.FromNumber(left.ToNumber() + right.ToNumber());
        }

        /// <summary>
        /// Lazily resolves and caches the prototype object for a primitive constructor (String, Number, Boolean).
        /// Used by LoadProp to implement property access on primitive values.
        /// </summary>
        private FenObject GetPrimitivePrototype(CallFrame frame, string constructorName)
        {
            if (_primitivePrototypeCache.TryGetValue(constructorName, out var cached))
            {
                return cached;
            }

            var ctor = ResolveVariable(frame, constructorName);
            var ctorObj = ctor.AsObject();
            if (ctorObj != null)
            {
                var proto = ctorObj.Get("prototype");
                if (proto.IsObject)
                {
                    var protoObj = proto.AsObject() as FenObject;
                    _primitivePrototypeCache[constructorName] = protoObj;
                    return protoObj;
                }
            }

            return null;
        }

        /// <summary>
        /// Lazily sets RegExp.prototype as the prototype of a regex object created from a literal.
        /// This allows methods like test/exec/compile to be found via the prototype chain.
        /// </summary>
        private void EnsureRegExpPrototype(FenObject regexObj, CallFrame frame)
        {
            var proto = GetPrimitivePrototype(frame, "RegExp");
            if (proto != null)
            {
                regexObj.SetPrototype(proto);
            }
        }

        /// <summary>
        /// Parses a string key as a non-negative integer index. Used for string character access.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryParseNonNegativeInt(string key, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            int value = 0;
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }

                int digit = c - '0';
                if (value > (int.MaxValue - digit) / 10)
                {
                    return false;
                }

                value = (value * 10) + digit;
            }

            index = value;
            return true;
        }
    }
}

