using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.DOM;
using FenBrowser.FenEngine.Errors;
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
        private const int DirectEvalAllowNewTargetFlag = 0x1;
        private const int DirectEvalForceUndefinedNewTargetFlag = 0x2;
        private const int DirectEvalAllowSuperPropertyFlag = 0x4;
        private const int LongRunTraceIntervalMs = 5000;
        private static int s_nativeTraceCount;
        private static readonly string[] s_cachedIndexKeys = BuildCachedIndexKeys();
        private static readonly ConditionalWeakTable<CodeBlock, string[]> s_stringConstantCache = new ConditionalWeakTable<CodeBlock, string[]>();
        private static readonly ConditionalWeakTable<CodeBlock, Dictionary<int, PolymorphicInlineCache>> s_loadPropertyInlineCaches = new ConditionalWeakTable<CodeBlock, Dictionary<int, PolymorphicInlineCache>>();
        private static readonly ConditionalWeakTable<CodeBlock, Dictionary<int, PolymorphicInlineCache>> s_storePropertyInlineCaches = new ConditionalWeakTable<CodeBlock, Dictionary<int, PolymorphicInlineCache>>();

        private static bool IsLongRunTraceEnabled()
        {
            var value = Environment.GetEnvironmentVariable("FEN_VM_TRACE_LONGRUN");
            return !string.IsNullOrWhiteSpace(value) &&
                   !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNativeCallTraceEnabled()
        {
            var value = Environment.GetEnvironmentVariable("FEN_VM_TRACE_NATIVE");
            return !string.IsNullOrWhiteSpace(value) &&
                   !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
        }

        private static void TraceNativeCall(string phase, FenFunction func, int argCount, FenValue thisValue, long elapsedMs = -1)
        {
            if (!IsNativeCallTraceEnabled())
            {
                return;
            }

            var count = Interlocked.Increment(ref s_nativeTraceCount);
            if (count > 512)
            {
                return;
            }

            var elapsedPart = elapsedMs >= 0 ? $" elapsed={elapsedMs}ms" : string.Empty;
            var funcName = func?.Name ?? "<anonymous>";
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[VM-NATIVECALL] {phase} name={funcName} args={argCount} thisType={thisValue.Type}{elapsedPart}",
                FenBrowser.Core.Logging.LogCategory.JavaScript);
        }

        private FenValue InvokeNativeFunction(FenFunction func, FenValue[] args, FenValue thisValue)
        {
            TraceNativeCall("begin", func, args?.Length ?? 0, thisValue);
            var stopwatch = IsNativeCallTraceEnabled() ? Stopwatch.StartNew() : null;
            var result = func.Invoke(args, null, thisValue);
            if (stopwatch != null)
            {
                TraceNativeCall("end", func, args?.Length ?? 0, thisValue, stopwatch.ElapsedMilliseconds);
            }

            return result;
        }

        private sealed class PropertyInlineCacheEntry
        {
            public string Key;
            public Shape Shape;
            public int SlotIndex;
        }

        /// <summary>
        /// Polymorphic inline cache: caches up to 4 shape?slot mappings per property access site.
        /// When more than 4 distinct shapes are seen, the site goes megamorphic (no caching).
        /// </summary>
        private sealed class PolymorphicInlineCache
        {
            public const int MaxEntries = 4;
            public readonly PropertyInlineCacheEntry[] Entries = new PropertyInlineCacheEntry[MaxEntries];
            public int Count;      // 0?4: valid entries
            public bool Megamorphic; // true ? too many shapes, skip caching
        }

        // Preserve original JS thrown values when crossing host/native frames.
        private sealed class JsUncaughtException : Exception
        {
            public FenValue ThrownValue { get; }

            public JsUncaughtException(FenValue thrownValue)
                : base("Uncaught JS Exception")
            {
                ThrownValue = thrownValue;
            }
        }

        private static bool TryExtractThrownValue(Exception ex, out FenValue thrownValue)
        {
            return JsThrownValueException.TryExtract(ex, out thrownValue);
        }

        private sealed class BytecodeArrayObject : FenObject
        {
            private const int MaxDenseCapacity = 65536;
            private const int MaxDenseGap = 1024;
            private FenValue[] _elements = new FenValue[8];
            private bool[] _present = new bool[8];
            private readonly SortedDictionary<int, FenValue> _sparseElements = new SortedDictionary<int, FenValue>();
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

                if (ShouldStoreSparse(index))
                {
                    _sparseElements[index] = value;
                    if (index >= _length)
                    {
                        _length = index + 1;
                    }

                    return;
                }

                EnsureCapacity(index + 1);
                _sparseElements.Remove(index);
                _elements[index] = value;
                _present[index] = true;
                if (index >= _length)
                {
                    _length = index + 1;
                }
            }

            public bool TryGetElement(int index, out FenValue value)
            {
                if ((uint)index < (uint)_elements.Length && _present[index])
                {
                    value = _elements[index];
                    return true;
                }

                if ((uint)index < (uint)_length && _sparseElements.TryGetValue(index, out value))
                {
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
                    int denseClearEnd = Math.Min(_length, _elements.Length);
                    for (int i = Math.Min(newLength, denseClearEnd); i < denseClearEnd; i++)
                    {
                        _present[i] = false;
                        _elements[i] = FenValue.Undefined;
                    }

                    if (_sparseElements.Count > 0)
                    {
                        var sparseKeysToRemove = new List<int>();
                        foreach (var sparseKey in _sparseElements.Keys)
                        {
                            if (sparseKey >= newLength)
                            {
                                sparseKeysToRemove.Add(sparseKey);
                            }
                        }

                        foreach (var sparseKey in sparseKeysToRemove)
                        {
                            _sparseElements.Remove(sparseKey);
                        }
                    }
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

            public override FenValue GetWithReceiver(string key, FenValue receiver, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                if (string.Equals(key, "length", StringComparison.Ordinal))
                {
                    return FenValue.FromNumber(_length);
                }

                if (TryParseArrayIndex(key, out int index))
                {
                    return TryGetElement(index, out var value) ? value : FenValue.Undefined;
                }

                return base.GetWithReceiver(key, receiver, context);
            }

            public override FenValue GetWithReceiver(FenValue key, FenValue receiver, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                if (key.IsString)
                {
                    return GetWithReceiver(key.AsString(), receiver, context);
                }

                return base.GetWithReceiver(key, receiver, context);
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

            public override void Set(string key, FenValue value, bool strict)
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

                base.Set(key, value, strict);
            }

            public override bool Has(string key, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                if (string.Equals(key, "length", StringComparison.Ordinal))
                {
                    return true;
                }

                if (TryParseArrayIndex(key, out int index))
                {
                    return ((uint)index < (uint)_elements.Length && _present[index])
                        || ((uint)index < (uint)_length && _sparseElements.ContainsKey(index));
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
                    if ((uint)index < (uint)_elements.Length)
                    {
                        _present[index] = false;
                        _elements[index] = FenValue.Undefined;
                    }

                    _sparseElements.Remove(index);

                    return true;
                }

                return base.Delete(key, context);
            }

            public override IEnumerable<string> Keys(FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                int denseCount = Math.Min(_length, _elements.Length);
                for (int i = 0; i < denseCount; i++)
                {
                    if (_present[i])
                    {
                        yield return IndexKey(i);
                    }
                }

                foreach (var sparseKey in _sparseElements.Keys)
                {
                    if (sparseKey < _length)
                    {
                        yield return IndexKey(sparseKey);
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
                int denseCount = Math.Min(_length, _elements.Length);
                for (int i = 0; i < denseCount; i++)
                {
                    if (_present[i])
                    {
                        yield return IndexKey(i);
                    }
                }

                foreach (var sparseKey in _sparseElements.Keys)
                {
                    if (sparseKey < _length)
                    {
                        yield return IndexKey(sparseKey);
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

            private bool ShouldStoreSparse(int index)
            {
                if (index < _elements.Length)
                {
                    return false;
                }

                int gapFromLogicalEnd = index - _length;
                return index >= MaxDenseCapacity || gapFromLogicalEnd > MaxDenseGap;
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

        private sealed class VmOperandStack
        {
            private readonly FenValue[] _items;

            public VmOperandStack(int capacity)
            {
                _items = new FenValue[capacity];
            }

            public FenValue[] Items => _items;

            public ref FenValue this[int index]
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref GetReference(index);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private ref FenValue GetReference(int index)
            {
                if ((uint)index >= (uint)_items.Length)
                {
                    throw CreateOperandStackBoundsException(index, _items.Length);
                }

                return ref _items[index];
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

        /// <summary>
        /// Iterates an object that follows the JS iterator protocol: calls obj.next() each step
        /// and reads {value, done} from the result. Works for GeneratorObjects and any native iterator.
        /// </summary>
        private sealed class JsProtocolIteratorEnumerator : IEnumerator<FenValue>
        {
            private readonly FenBrowser.FenEngine.Core.Interfaces.IObject _iterator;
            private readonly FenFunction _nextFn;
            private bool _done;

            public JsProtocolIteratorEnumerator(FenBrowser.FenEngine.Core.Interfaces.IObject iterator, FenFunction nextFn)
            {
                _iterator = iterator;
                _nextFn = nextFn;
            }

            public FenValue Current { get; private set; } = FenValue.Undefined;
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_done) return false;

                var resultVal = _nextFn.Invoke(Array.Empty<FenValue>(), null, FenValue.FromObject(_iterator));
                if (!resultVal.IsObject)
                {
                    _done = true;
                    throw new FenTypeError("TypeError: Iterator result is not an object");
                }
                var resultObj = resultVal.AsObject();

                var doneVal = resultObj?.Get("done");
                if (doneVal.HasValue && doneVal.Value.ToBoolean())
                {
                    _done = true;
                    return false;
                }

                var valueVal = resultObj?.Get("value");
                Current = valueVal.HasValue ? valueVal.Value : FenValue.Undefined;
                return true;
            }

            public void Reset() { }
            public void Dispose()
            {
                if (_done || _iterator == null)
                {
                    return;
                }

                _done = true;
                var returnMethod = _iterator.Get("return");
                if (!returnMethod.IsFunction)
                {
                    return;
                }

                var returnFn = returnMethod.AsFunction() as FenFunction;
                if (returnFn == null)
                {
                    throw new FenTypeError("TypeError: Iterator .return() is not callable");
                }

                var returnResult = returnFn.Invoke(Array.Empty<FenValue>(), null, FenValue.FromObject(_iterator));
                if (!returnResult.IsObject)
                {
                    throw new FenTypeError("TypeError: Iterator .return() must return an object");
                }
            }
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
                char c = _source[_index];
                // ES spec: string iteration yields Unicode code points (handle surrogate pairs)
                if (char.IsHighSurrogate(c) && _index + 1 < _source.Length && char.IsLowSurrogate(_source[_index + 1]))
                {
                    int codePoint = char.ConvertToUtf32(c, _source[_index + 1]);
                    Current = FenValue.FromString(char.ConvertFromUtf32(codePoint));
                    _index++; // consume the low surrogate too
                }
                else
                {
                    Current = FenValue.FromString(c.ToString());
                }
                return true;
            }

            public void Reset()
            {
                _index = -1;
                Current = FenValue.Undefined;
            }

            public void Dispose() { }
        }

        private sealed class SuperReferenceObject : FenObject
        {
            private readonly FenBrowser.FenEngine.Core.Interfaces.IObject _baseObject;
            private readonly FenValue _receiver;

            public SuperReferenceObject(FenBrowser.FenEngine.Core.Interfaces.IObject baseObject, FenValue receiver)
            {
                _baseObject = baseObject;
                _receiver = receiver;
            }

            public override FenValue Get(string key, FenBrowser.FenEngine.Core.Interfaces.IExecutionContext context = null)
            {
                if (_baseObject == null)
                {
                    return FenValue.Undefined;
                }

                FenValue value = _baseObject is FenObject fenBase
                    ? fenBase.GetWithReceiver(key, _receiver, context)
                    : _baseObject.Get(key, context);

                if (value.IsFunction)
                {
                    var targetFunction = value.AsFunction();
                    if (targetFunction != null)
                    {
                        var boundFunction = new FenFunction(targetFunction.Name, (args, thisVal) =>
                            targetFunction.Invoke(args, context, _receiver))
                        {
                            IsConstructor = false
                        };
                        return FenValue.FromFunction(boundFunction);
                    }
                }

                return value;
            }
        }

        /// <summary>
        /// Represents a suspended generator. Holds its own private VirtualMachine so that
        /// the generator's stack/frames are fully isolated from the caller.
        /// </summary>
        private sealed class GeneratorObject : FenObject
        {
            [ThreadStatic]
            private static int _generatorResumeDepth;
            private const int MaxGeneratorResumeDepth = 32;

            private readonly VirtualMachine _vm = new VirtualMachine();
            private readonly FenFunction _func;
            private FenValue[] _initialArgs;

            public bool IsStarted;
            public bool IsDone;

            public GeneratorObject(FenFunction func, FenValue[] args)
            {
                _func = func;
                _initialArgs = args;
                InternalClass = "Generator";

                // next(value?) => resume / start the generator
                var nextFn = new FenFunction("next", (fnArgs, thisVal) =>
                {
                    var gen = (thisVal.IsObject ? thisVal.AsObject() : null) as GeneratorObject ?? this;
                    var sentValue = fnArgs.Length > 0 ? fnArgs[0] : FenValue.Undefined;
                    return gen.Next(sentValue);
                });

                // return(value?) => close the generator
                var returnFn = new FenFunction("return", (fnArgs, thisVal) =>
                {
                    var gen = (thisVal.IsObject ? thisVal.AsObject() : null) as GeneratorObject ?? this;
                    gen.IsDone = true;
                    var retValue = fnArgs.Length > 0 ? fnArgs[0] : FenValue.Undefined;
                    return MakeIteratorResult(retValue, true);
                });

                // throw(error) => inject an exception into the generator
                var throwFn = new FenFunction("throw", (fnArgs, thisVal) =>
                {
                    var gen = (thisVal.IsObject ? thisVal.AsObject() : null) as GeneratorObject ?? this;
                    gen.IsDone = true;
                    var errVal = fnArgs.Length > 0 ? fnArgs[0] : FenValue.Undefined;
                    throw new FenTypeError($"TypeError: {FormatGeneratorThrowValue(errVal)}");
                });

                Set("next", FenValue.FromFunction(nextFn));
                Set("return", FenValue.FromFunction(returnFn));
                Set("throw", FenValue.FromFunction(throwFn));

                // [Symbol.iterator]() => return this (generators are their own iterators)
                var selfIterFn = new FenFunction("[Symbol.iterator]", (fnArgs, thisVal) => thisVal);
                Set("[Symbol.iterator]", FenValue.FromFunction(selfIterFn));
            }

            private static string FormatGeneratorThrowValue(FenValue v)
            {
                if (v.IsObject)
                {
                    var obj = v.AsObject();
                    var msg = obj?.Get("message");
                    if (msg.HasValue && !msg.Value.IsUndefined) return msg.Value.AsString();
                }
                return v.AsString();
            }

            private static FenValue MakeIteratorResult(FenValue value, bool done)
            {
                var result = new FenObject();
                result.Set("value", value);
                result.Set("done", FenValue.FromBoolean(done));
                return FenValue.FromObject(result);
            }

            public FenValue Next(FenValue sentValue)
            {
                if (++_generatorResumeDepth > MaxGeneratorResumeDepth)
                {
                    _generatorResumeDepth--;
                    throw new FenResourceError("RangeError: Maximum call stack size exceeded");
                }

                try
                {
                    if (IsDone)
                        return MakeIteratorResult(FenValue.Undefined, true);

                    if (!IsStarted)
                {
                    IsStarted = true;
                    _vm._sp = 0;
                    _vm._frameCount = 0;
                    _vm._generatorYielded = false;

                    var newEnv = new FenEnvironment(_func.Env);
                    InitializeFunctionFastStore(_func, newEnv);
                    if (!string.IsNullOrEmpty(_func.Name))
                        SetFunctionBinding(_func, newEnv, _func.Name, FenValue.FromFunction(_func));
                    if (!_func.IsArrowFunction)
                        SetFunctionBinding(_func, newEnv, "this", FenValue.Undefined);
                    BindFunctionArguments(_func, newEnv, _initialArgs);
                    _initialArgs = null; // free args after first use

                    _vm.PushFrame(_func.BytecodeBlock, newEnv, 0);
                }
                else
                {
                    _vm._generatorYielded = false;
                    // Push sentValue: it becomes the result of the `yield` expression that suspended
                    _vm._stack[_vm._sp++] = sentValue;
                }

                FenValue runResult = _vm.RunLoop();

                    if (_vm._generatorYielded)
                    {
                        return MakeIteratorResult(_vm._generatorYieldValue, false);
                    }
                    else
                    {
                        IsDone = true;
                        return MakeIteratorResult(runResult, true);
                    }
                }
                finally
                {
                    _generatorResumeDepth--;
                }
            }
        }

        // Cached primitive prototype lookups (lazily resolved from the environment on first use)
        private readonly Dictionary<string, FenObject> _primitivePrototypeCache = new Dictionary<string, FenObject>(StringComparer.Ordinal);

        // Generator yield state ? set by the Yield opcode to signal RunLoop() to exit
        private bool _generatorYielded;
        private FenValue _generatorYieldValue;

        // Fixed-size fast heap for operands. The wrapper preserves the existing indexing shape
        // while making overflow/underflow explicit JS-visible failures instead of CLR array faults.
        private const int STACK_SIZE = 16384;
        private readonly VmOperandStack _stack = new VmOperandStack(STACK_SIZE);
        private int _sp = 0; // Stack pointer
        private FenValue _completionValue = FenValue.Undefined; // Stores the result of the last evaluated expression

        // Call stack managed entirely on the heap to prevent .NET StackOverflowException
        private const int MAX_FRAMES = 1024;
        private readonly CallFrame[] _callFrames = new CallFrame[MAX_FRAMES];
        private int _frameCount = 0;

        // Cooperative cancellation: checked every CANCEL_CHECK_INTERVAL instructions in RunLoop
        private CancellationToken _cancellationToken;
        private const int CANCEL_CHECK_INTERVAL = 4096;
        private int _instructionsSinceCancelCheck;

        // Resource limits — instruction count and memory cap (checked in the amortized interval, zero per-instruction overhead)
        private long _totalInstructionCount;
        private Security.IResourceLimits _limits;

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsArrayIndexKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return true;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowJsError(string errorType, string message)
        {
            throw errorType switch
            {
                "TypeError" => new FenTypeError($"{errorType}: {message}"),
                "RangeError" => new FenRangeError($"{errorType}: {message}"),
                "ReferenceError" => new FenReferenceError($"{errorType}: {message}"),
                "SyntaxError" => new FenSyntaxError($"{errorType}: {message}"),
                _ => new FenInternalError($"{errorType}: {message}")
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowTypeError(string message)
        {
            ThrowJsError("TypeError", message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowReferenceError(string message)
        {
            ThrowJsError("ReferenceError", message);
        }

        /// <summary>
        /// Converts a FenValue.Error (returned by native functions) into a real thrown JS exception.
        /// ECMA-262: native built-ins must throw — returning an Error-typed value is a legacy
        /// internal convention that we normalize at every native call-site boundary.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfNativeError(in FenValue result)
        {
            if (result.Type == JsValueType.Throw)
            {
                throw new JsUncaughtException(result.GetThrownValue());
            }

            if (result.Type != JsValueType.Error) return;
            var msg = result.AsString() ?? string.Empty;
            if (TryParseKnownErrorPrefix(msg, out var errorType, out var errorMessage))
            {
                ThrowJsError(errorType, errorMessage);
                return;
            }
            ThrowJsError("Error", msg);
        }

        private static bool TryParseKnownErrorPrefix(string rawMessage, out string errorType, out string errorMessage)
        {
            errorType = null;
            errorMessage = rawMessage ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawMessage))
            {
                return false;
            }

            var colonIdx = rawMessage.IndexOf(':');
            if (colonIdx <= 0)
            {
                return false;
            }

            var prefix = rawMessage.Substring(0, colonIdx);
            if (!string.Equals(prefix, "Error", StringComparison.Ordinal) &&
                !string.Equals(prefix, "TypeError", StringComparison.Ordinal) &&
                !string.Equals(prefix, "RangeError", StringComparison.Ordinal) &&
                !string.Equals(prefix, "ReferenceError", StringComparison.Ordinal) &&
                !string.Equals(prefix, "SyntaxError", StringComparison.Ordinal) &&
                !string.Equals(prefix, "URIError", StringComparison.Ordinal) &&
                !string.Equals(prefix, "EvalError", StringComparison.Ordinal) &&
                !string.Equals(prefix, "SecurityError", StringComparison.Ordinal))
            {
                return false;
            }

            errorType = prefix;
            errorMessage = rawMessage.Substring(colonIdx + 1).TrimStart();
            return true;
        }

        private static Exception CreateOperandStackBoundsException(int index, int capacity)
        {
            if (index < 0)
            {
                return new FenInternalError("VM Error: Operand stack underflow.");
            }

            return new FenResourceError($"RangeError: Maximum operand stack size exceeded ({capacity}).");
        }

        private FenValue ResolveNonStrictThisBinding(CallFrame frame)
        {
            if (frame != null)
            {
                var globalThis = ResolveVariableSafe(frame, "globalThis");
                if (globalThis.IsObject)
                {
                    return globalThis;
                }

                var window = ResolveVariableSafe(frame, "window");
                if (window.IsObject)
                {
                    return window;
                }

                var self = ResolveVariableSafe(frame, "self");
                if (self.IsObject)
                {
                    return self;
                }
            }

            return FenValue.Undefined;
        }

        private static void BindSuperReference(FenFunction func, FenEnvironment env, FenValue thisValue)
        {
            if (func?.HomeObject == null || env == null)
            {
                return;
            }

            var superBase = func.HomeObject.GetPrototype();
            if (superBase == null)
            {
                return;
            }

            env.Set("super", FenValue.FromObject(new SuperReferenceObject(superBase, thisValue)));
        }

        private static void BindSuperConstructorIfPresent(FenFunction func, FenEnvironment env)
        {
            if (func == null || env == null)
            {
                return;
            }

            var superCtor = func.Get("__super_ctor__");
            if (superCtor.IsFunction || superCtor.IsObject)
            {
                env.Set("super", superCtor);
            }
        }

        private FenValue ExecuteDirectEval(FenValue sourceValue, CallFrame frame, int directEvalFlags)
        {
            if (!sourceValue.IsString)
            {
                return sourceValue;
            }

            var code = sourceValue.AsString() ?? string.Empty;
            var activeRuntime = FenRuntime.GetActiveRuntime();
            var activeContext = activeRuntime?.Context;

            if (activeContext != null && !activeContext.Permissions.Check(FenBrowser.FenEngine.Security.JsPermissions.Eval))
            {
                activeContext.Permissions.LogViolation(
                    FenBrowser.FenEngine.Security.JsPermissions.Eval,
                    "direct eval",
                    "direct eval blocked by permission policy");
                ThrowJsError(
                    "EvalError",
                    "Refused to evaluate a string as JavaScript because 'unsafe-eval' is not an allowed source of script in the current security policy.");
            }

            if (code.Length > 1_000_000)
            {
                ThrowJsError("EvalError", "eval() input exceeds maximum allowed size (1 MB).");
            }

            bool inheritStrict = (frame.Block != null && frame.Block.IsStrict) || frame.Environment.StrictMode;
            bool allowNewTarget = (directEvalFlags & DirectEvalAllowNewTargetFlag) != 0;
            bool forceUndefinedNewTarget = (directEvalFlags & DirectEvalForceUndefinedNewTargetFlag) != 0;
            bool allowSuperProperty = (directEvalFlags & DirectEvalAllowSuperPropertyFlag) != 0;

            Program program;
            Parser parser;
            try
            {
                var lexer = new Lexer(code);
                parser = new Parser(
                    lexer,
                    allowReturnOutsideFunction: false,
                    initialStrictMode: inheritStrict,
                    allowNewTargetOutsideFunction: allowNewTarget,
                    allowSuperOutsideClass: allowSuperProperty,
                    allowSuperInClassFieldInitializer: allowSuperProperty,
                    allowRecovery: false);
                program = parser.ParseProgram();
            }
            catch (FenSyntaxError)
            {
                throw;
            }
            catch (Exception ex)
            {
                ThrowJsError("SyntaxError", ex.Message);
                return FenValue.Undefined;
            }

            if (parser.Errors.Count > 0)
            {
                ThrowJsError("SyntaxError", parser.Errors[0]);
            }

            CodeBlock compiledBlock;
            try
            {
                var compiler = new Bytecode.Compiler.BytecodeCompiler(isEval: true);
                compiledBlock = compiler.Compile(program);
            }
            catch (FenSyntaxError)
            {
                throw;
            }
            catch (Exception ex)
            {
                ThrowJsError("SyntaxError", ex.Message);
                return FenValue.Undefined;
            }

            // Non-strict direct eval gets a fresh lexical environment whose var-declaration
            // environment is still the caller's enclosing var scope. Strict direct eval keeps
            // var declarations local to the eval itself.
            var evalEnvironment = inheritStrict
                ? new FenEnvironment(frame.Environment)
                : new FenEnvironment(frame.Environment, isLexicalScope: true);
            evalEnvironment.StrictMode = inheritStrict;

            var thisBinding = ResolveVariableSafe(frame, "this");
            evalEnvironment.Set("this", thisBinding);

            if (allowSuperProperty && thisBinding.IsObject)
            {
                var receiverObject = thisBinding.AsObject();
                var homeObject = receiverObject?.GetPrototype();
                var superBase = homeObject?.GetPrototype();
                if (superBase != null)
                {
                    evalEnvironment.Set("super", FenValue.FromObject(new SuperReferenceObject(superBase, thisBinding)));
                }
            }

            // ECMA-262 Annex B §B.3.3.1: Pre-initialize block-scoped function names in the enclosing
            // variable-declaration environment (outer scope) before the eval code runs.
            // This handles cases like: eval("if (true) { function f() {} }") — 'f' must exist in the
            // outer var scope as undefined before eval executes, and be updated to the function value after.
            var annexBNames = compiledBlock.AnnexBBlockFunctionNames;
            if (annexBNames != null && annexBNames.Count > 0 && !inheritStrict)
            {
                var outerVarEnv = frame.Environment.GetVarDeclarationEnvironment();
                foreach (var blockFnName in annexBNames)
                {
                    if (!outerVarEnv.HasLocalBinding(blockFnName))
                    {
                        outerVarEnv.Set(blockFnName, FenValue.Undefined);
                    }
                }
            }

            var nestedVm = new VirtualMachine();
            var evalNewTarget = forceUndefinedNewTarget ? FenValue.Undefined : frame.NewTarget;
            var evalResult = nestedVm.Execute(compiledBlock, evalEnvironment, evalNewTarget);

            // ECMA-262 Annex B §B.3.3.1: After eval execution, propagate the final values of
            // block-scoped function bindings up to the outer variable-declaration scope.
            if (annexBNames != null && annexBNames.Count > 0 && !inheritStrict)
            {
                var outerVarEnv = frame.Environment.GetVarDeclarationEnvironment();
                foreach (var blockFnName in annexBNames)
                {
                    var blockFnValue = evalEnvironment.Get(blockFnName);
                    if (!blockFnValue.IsUndefined)
                    {
                        outerVarEnv.Set(blockFnName, blockFnValue);
                    }
                }
            }

            return evalResult;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RequireObjectCoercible(FenValue v, string opName)
        {
            if (v.IsNull || v.IsUndefined)
                ThrowTypeError($"{opName} on {(v.IsNull ? "null" : "undefined")}");
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
        private static Dictionary<int, PolymorphicInlineCache> GetLoadPropertyInlineCache(CodeBlock block)
        {
            return s_loadPropertyInlineCaches.GetOrCreateValue(block);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Dictionary<int, PolymorphicInlineCache> GetStorePropertyInlineCache(CodeBlock block)
        {
            return s_storePropertyInlineCaches.GetOrCreateValue(block);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryLoadPropertyInlineCache(
            Dictionary<int, PolymorphicInlineCache> cache,
            int instructionOffset,
            FenObject obj,
            string key,
            out FenValue value)
        {
            value = FenValue.Undefined;
            if (!cache.TryGetValue(instructionOffset, out var pic) || pic.Megamorphic)
            {
                return false;
            }

            var shape = obj.GetShape();
            for (int i = 0; i < pic.Count; i++)
            {
                var entry = pic.Entries[i];
                if (entry.Shape != shape || !string.Equals(entry.Key, key, StringComparison.Ordinal))
                {
                    continue;
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

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void PopulatePropertyInlineCache(
            Dictionary<int, PolymorphicInlineCache> cache,
            int instructionOffset,
            FenObject obj,
            string key,
            bool writableRequired)
        {
            var shape = obj.GetShape();
            if (!shape.TryGetPropertyOffset(key, out int slotIndex))
            {
                return;
            }

            var storage = obj.GetPropertyStorage();
            if ((uint)slotIndex >= (uint)storage.Length)
            {
                return;
            }

            var descriptor = storage[slotIndex];
            if (descriptor.IsAccessor || !descriptor.Value.HasValue)
            {
                return;
            }

            if (writableRequired && descriptor.Writable == false)
            {
                return;
            }

            if (!cache.TryGetValue(instructionOffset, out var pic))
            {
                pic = new PolymorphicInlineCache();
                cache[instructionOffset] = pic;
            }

            if (pic.Megamorphic) return;

            // Update existing entry for this shape if already cached
            for (int i = 0; i < pic.Count; i++)
            {
                if (pic.Entries[i].Shape == shape && string.Equals(pic.Entries[i].Key, key, StringComparison.Ordinal))
                {
                    pic.Entries[i].SlotIndex = slotIndex;
                    return;
                }
            }

            // Add new entry or go megamorphic
            if (pic.Count < PolymorphicInlineCache.MaxEntries)
            {
                if (pic.Entries[pic.Count] == null)
                    pic.Entries[pic.Count] = new PropertyInlineCacheEntry();
                pic.Entries[pic.Count].Key = key;
                pic.Entries[pic.Count].Shape = shape;
                pic.Entries[pic.Count].SlotIndex = slotIndex;
                pic.Count++;
            }
            else
            {
                pic.Megamorphic = true; // Too many shapes at this site ? go megamorphic
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static FenValue ResolveVariableSafe(CallFrame frame, string varName)
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

            if (TryResolveGlobalObjectProperty(frame, varName, out var globalValue))
            {
                return globalValue;
            }

            if (frame.Environment.HasBinding(varName))
            {
                return frame.Environment.Get(varName);
            }

            return FenValue.Undefined; // Safe: return undefined without throwing
        }

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

            if (TryResolveGlobalObjectProperty(frame, varName, out var globalValue))
            {
                return globalValue;
            }

            if (frame.Environment.HasBinding(varName))
            {
                return frame.Environment.Get(varName);
            }

            throw new FenReferenceError($"ReferenceError: {varName} is not defined");
        }

        private static bool TryResolveGlobalObjectProperty(CallFrame frame, string varName, out FenValue value)
        {
            value = FenValue.Undefined;
            if (string.IsNullOrEmpty(varName) ||
                string.Equals(varName, "globalThis", StringComparison.Ordinal) ||
                string.Equals(varName, "window", StringComparison.Ordinal) ||
                string.Equals(varName, "self", StringComparison.Ordinal))
            {
                return false;
            }

            static bool TryGetGlobalObj(FenEnvironment env, string bindingName, out FenObject globalObj)
            {
                globalObj = null;
                var bindingEnv = env.ResolveBindingEnvironment(bindingName);
                if (bindingEnv != null && bindingEnv.TryGetLocal(bindingName, out var bound) && bound.IsObject)
                {
                    globalObj = bound.AsObject() as FenObject;
                    return globalObj != null;
                }

                return false;
            }

            FenObject rootObj = null;
            if (!TryGetGlobalObj(frame.Environment, "globalThis", out rootObj) &&
                !TryGetGlobalObj(frame.Environment, "window", out rootObj) &&
                !TryGetGlobalObj(frame.Environment, "self", out rootObj))
            {
                return false;
            }

            if (!rootObj.Has(varName))
            {
                return false;
            }

            value = rootObj.Get(varName);
            return true;
        }
        private static bool TryResolveGlobalOwnProperty(CallFrame frame, string varName, out FenValue value)
        {
            value = FenValue.Undefined;
            if (string.IsNullOrEmpty(varName))
            {
                return false;
            }

            static bool TryGetGlobalObj(FenEnvironment env, string bindingName, out FenObject globalObj)
            {
                globalObj = null;
                var bindingEnv = env.ResolveBindingEnvironment(bindingName);
                if (bindingEnv != null && bindingEnv.TryGetLocal(bindingName, out var bound) && bound.IsObject)
                {
                    globalObj = bound.AsObject() as FenObject;
                    return globalObj != null;
                }

                return false;
            }

            FenObject rootObj = null;
            if (!TryGetGlobalObj(frame.Environment, "globalThis", out rootObj) &&
                !TryGetGlobalObj(frame.Environment, "window", out rootObj) &&
                !TryGetGlobalObj(frame.Environment, "self", out rootObj))
            {
                return false;
            }

            if (!rootObj.GetOwnPropertyDescriptor(varName).HasValue)
            {
                return false;
            }

            value = rootObj.Get(varName);
            return true;
        }
        private static bool TryResolveGlobalPrototypeProxyBinding(CallFrame frame, string varName, out FenValue value)
        {
            value = FenValue.Undefined;
            if (string.IsNullOrEmpty(varName))
            {
                return false;
            }

            static bool TryGetGlobalObj(FenEnvironment env, string bindingName, out FenObject globalObj)
            {
                globalObj = null;
                var bindingEnv = env.ResolveBindingEnvironment(bindingName);
                if (bindingEnv != null && bindingEnv.TryGetLocal(bindingName, out var bound) && bound.IsObject)
                {
                    globalObj = bound.AsObject() as FenObject;
                    return globalObj != null;
                }

                return false;
            }

            FenObject globalRoot = null;
            if (!TryGetGlobalObj(frame.Environment, "globalThis", out globalRoot) &&
                !TryGetGlobalObj(frame.Environment, "window", out globalRoot) &&
                !TryGetGlobalObj(frame.Environment, "self", out globalRoot))
            {
                return false;
            }

            var proto = globalRoot.GetPrototype() as FenObject;
            if (proto == null)
            {
                return false;
            }

            var isProxyDesc = proto.GetOwnPropertyDescriptor("__isProxy__");
            if (!isProxyDesc.HasValue || !isProxyDesc.Value.Value.HasValue || !isProxyDesc.Value.Value.Value.ToBoolean())
            {
                return false;
            }

            var hasTrapDesc = proto.GetOwnPropertyDescriptor("__proxyHas__");
            if (!hasTrapDesc.HasValue || !hasTrapDesc.Value.Value.HasValue || !hasTrapDesc.Value.Value.Value.IsFunction)
            {
                return false;
            }

            var getTrapDesc = proto.GetOwnPropertyDescriptor("__proxyGet__");
            if (!getTrapDesc.HasValue || !getTrapDesc.Value.Value.HasValue || !getTrapDesc.Value.Value.Value.IsFunction)
            {
                return false;
            }

            var targetDesc = proto.GetOwnPropertyDescriptor("__proxyTarget__") ?? proto.GetOwnPropertyDescriptor("__target__");
            var proxyTarget = targetDesc.HasValue && targetDesc.Value.Value.HasValue
                ? targetDesc.Value.Value.Value
                : FenValue.Undefined;

            var hasTrap = hasTrapDesc.Value.Value.Value.AsFunction();
            var hasResult = hasTrap.Invoke(new[] { proxyTarget, FenValue.FromString(varName) }, null, FenValue.Undefined);
            if (!hasResult.ToBoolean())
            {
                return false;
            }

            var getTrap = getTrapDesc.Value.Value.Value.AsFunction();
            value = getTrap.Invoke(new[] { proxyTarget, FenValue.FromString(varName), FenValue.FromObject(globalRoot) }, null, FenValue.Undefined);
            return true;
        }
        private static bool TryAssignUndeclaredToGlobal(CallFrame frame, string varName, FenValue value)
        {
            if (string.IsNullOrEmpty(varName))
            {
                return false;
            }

            static bool TryGetGlobalObj(FenEnvironment env, string bindingName, out FenObject globalObj)
            {
                globalObj = null;
                var bindingEnv = env.ResolveBindingEnvironment(bindingName);
                if (bindingEnv != null && bindingEnv.TryGetLocal(bindingName, out var bound) && bound.IsObject)
                {
                    globalObj = bound.AsObject() as FenObject;
                    return globalObj != null;
                }

                return false;
            }

            FenObject globalRoot = null;
            if (!TryGetGlobalObj(frame.Environment, "globalThis", out globalRoot) &&
                !TryGetGlobalObj(frame.Environment, "window", out globalRoot) &&
                !TryGetGlobalObj(frame.Environment, "self", out globalRoot))
            {
                return false;
            }

            var proto = globalRoot.GetPrototype() as FenObject;
            if (proto != null)
            {
                var isProxyDesc = proto.GetOwnPropertyDescriptor("__isProxy__");
                if (isProxyDesc.HasValue && isProxyDesc.Value.Value.HasValue && isProxyDesc.Value.Value.Value.ToBoolean())
                {
                    var setTrapDesc = proto.GetOwnPropertyDescriptor("__proxySet__");
                    if (setTrapDesc.HasValue && setTrapDesc.Value.Value.HasValue && setTrapDesc.Value.Value.Value.IsFunction)
                    {
                        var targetDesc = proto.GetOwnPropertyDescriptor("__proxyTarget__") ?? proto.GetOwnPropertyDescriptor("__target__");
                        var proxyTarget = targetDesc.HasValue && targetDesc.Value.Value.HasValue
                            ? targetDesc.Value.Value.Value
                            : FenValue.Undefined;
                        var setTrap = setTrapDesc.Value.Value.Value.AsFunction();
                        setTrap.Invoke(new[] { proxyTarget, FenValue.FromString(varName), value, FenValue.FromObject(globalRoot) }, null, FenValue.Undefined);
                        return true;
                    }
                }
            }

            globalRoot.Set(varName, value);
            return true;
        }
        private static bool TryResolveNamedGlobalById(CallFrame frame, string varName, out FenValue value)
        {
            value = FenValue.Undefined;
            if (string.IsNullOrEmpty(varName))
            {
                return false;
            }

            var root = frame.Environment;
            while (root?.Outer != null)
            {
                root = root.Outer;
            }

            if (root == null)
            {
                return false;
            }

            var docVal = root.Get("document");
            if (!docVal.IsObject)
            {
                var windowVal = root.Get("window");
                if (windowVal.IsObject)
                {
                    docVal = windowVal.AsObject().Get("document");
                }
            }

            if (!docVal.IsObject)
            {
                return false;
            }

            var docObj = docVal.AsObject();
            if (docObj == null)
            {
                return false;
            }

            var getById = docObj.Get("getElementById");
            if (!getById.IsFunction)
            {
                return false;
            }

            var found = getById.AsFunction().Invoke(new[] { FenValue.FromString(varName) }, null, docVal);
            if (found.IsNull || found.IsUndefined)
            {
                return false;
            }

            root.Set(varName, found);
            value = found;
            return true;
        }

        public FenValue Execute(CodeBlock initialBlock, FenEnvironment initialEnv)
        {
            return ExecuteInternal(initialBlock, initialEnv, FenValue.Undefined, CancellationToken.None, null);
        }

        public FenValue Execute(CodeBlock initialBlock, FenEnvironment initialEnv, CancellationToken cancellationToken)
        {
            return ExecuteInternal(initialBlock, initialEnv, FenValue.Undefined, cancellationToken, null);
        }

        public FenValue Execute(CodeBlock initialBlock, FenEnvironment initialEnv, CancellationToken cancellationToken, Security.IResourceLimits limits)
        {
            return ExecuteInternal(initialBlock, initialEnv, FenValue.Undefined, cancellationToken, limits);
        }

        public FenValue Execute(CodeBlock initialBlock, FenEnvironment initialEnv, FenValue initialNewTarget)
        {
            return ExecuteInternal(initialBlock, initialEnv, initialNewTarget, CancellationToken.None, null);
        }

        private FenValue ExecuteInternal(
            CodeBlock initialBlock,
            FenEnvironment initialEnv,
            FenValue initialNewTarget,
            CancellationToken cancellationToken,
            Security.IResourceLimits limits)
        {
            _cancellationToken = cancellationToken;
            _instructionsSinceCancelCheck = 0;
            _totalInstructionCount = 0;
            _limits = limits;
            _generatorYielded = false;
            _generatorYieldValue = FenValue.Undefined;
            Interlocked.Exchange(ref s_nativeTraceCount, 0);
            FenObject.ResetAllocatedBytes();
            _sp = 0;
            _completionValue = FenValue.Undefined;
            _frameCount = 0;
            
            // Push initial frame
            var initialFrame = PushFrame(initialBlock, initialEnv, 0);
            initialFrame.NewTarget = initialNewTarget;

            return RunLoop();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private CallFrame PushFrame(CodeBlock block, FenEnvironment env, int stackBase)
        {
            if (_frameCount >= MAX_FRAMES)
                throw new FenResourceError($"RangeError: Maximum call stack size exceeded ({MAX_FRAMES}).");

            if (block != null && block.LocalSlotCount > 0)
            {
                env.InitializeFastStore(block.LocalSlotCount);

                // Seed fast slots from already-resolved bindings (global/env) so optimized local loads
                // observe existing values instead of defaulting to undefined.
                for (int slotIndex = 0; slotIndex < block.LocalSlotCount; slotIndex++)
                {
                    var slotName = block.GetLocalSlotName(slotIndex);
                    if (string.IsNullOrEmpty(slotName))
                    {
                        continue;
                    }

                    if (env.TryGetLocal(slotName, out var localValue))
                    {
                        env.SetFast(slotIndex, localValue);
                    }
                    else if (env.HasBinding(slotName))
                    {
                        env.SetFast(slotIndex, env.Get(slotName));
                    }
                }
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
            var longRunTrace = IsLongRunTraceEnabled() ? Stopwatch.StartNew() : null;
            long nextLongRunTraceMs = LongRunTraceIntervalMs;

run_loop_restart:
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
                        // Cooperative cancellation + resource limit check (amortized: once per CANCEL_CHECK_INTERVAL instructions)
                        if (++_instructionsSinceCancelCheck >= CANCEL_CHECK_INTERVAL)
                        {
                            _instructionsSinceCancelCheck = 0;
                            if (_cancellationToken.IsCancellationRequested)
                                throw new OperationCanceledException(_cancellationToken);

                            if (_limits != null)
                            {
                                _totalInstructionCount += CANCEL_CHECK_INTERVAL;
                                if (_totalInstructionCount > _limits.MaxInstructionCount)
                                    throw new Errors.FenResourceError(
                                        $"Error: Script exceeded maximum instruction count ({_limits.MaxInstructionCount:N0}). Possible infinite loop.");

                                long allocatedBytes = FenObject.GetAllocatedBytes();
                                if (allocatedBytes > _limits.MaxTotalMemory)
                                    throw new Errors.FenResourceError(
                                        $"Error: Script exceeded memory limit ({_limits.MaxTotalMemory / (1024 * 1024)}MB). Allocated: {allocatedBytes / (1024 * 1024)}MB.");
                            }
                        }

                        OpCode op = (OpCode)instructions[frame.IP++];
                        int instructionOffset = frame.IP - 1;

                        if (longRunTrace != null &&
                            longRunTrace.ElapsedMilliseconds >= nextLongRunTraceMs)
                        {
                            nextLongRunTraceMs += LongRunTraceIntervalMs;
                            FenBrowser.Core.EngineLogCompat.Warn(
                                $"[VM-LONGRUN] elapsed={longRunTrace.ElapsedMilliseconds}ms ip={instructionOffset}/{instructions.Length} op={op} frames={_frameCount} sp={_sp} pendingTasks={EventLoopCoordinator.Instance.TaskCount} pendingMicrotasks={EventLoopCoordinator.Instance.MicrotaskCount}",
                                FenBrowser.Core.Logging.LogCategory.JavaScript);
                        }

                        switch (op)
                        {
                            case OpCode.LoadConst:
                            {
                                int constIndex = ReadInt32(instructions, ref frame);
                                var constant = constants[constIndex];
                                if (constant.IsObject && constant.AsObject() is FenObject constantObject &&
                                    constantObject.InternalClass == "RegExp")
                                {
                                    EnsureRegExpPrototype(constantObject, frame);
                                }

                                _stack[_sp++] = constant;
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
                                if (right.IsObject || right.IsFunction)
                                {
                                    _stack[_sp++] = FenValue.FromBoolean(right.AsObject().Has(PropertyKey(left)));
                                }
                                else
                                {
                                    ThrowTypeError($"Cannot use 'in' operator to search for '{PropertyKey(left)}' in {right.AsString()}");
                                }
                                break;
                            }
                            case OpCode.InstanceOf:
                            {
                                var right = _stack[--_sp];
                                var left = _stack[--_sp];

                                if (!right.IsObject && !right.IsFunction)
                                {
                                    ThrowTypeError("Right-hand side of 'instanceof' is not an object");
                                }

                                if (!right.IsFunction)
                                {
                                    ThrowTypeError("Right-hand side of 'instanceof' is not callable");
                                }

                                if (!left.IsObject && !left.IsFunction)
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
                                if (value.Type == JsValueType.Error)
                                {
                                    throw new FenInternalError(value.ToString());
                                }
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.LoadVarSafe:
                            {
                                // Like LoadVar but still throws for TDZ bindings; it only suppresses undeclared-name errors for typeof.
                                int nameIndex = ReadInt32(instructions, ref frame);
                                string varName = GetStringConstant(frame.Block, constants, nameIndex);
                                var value = ResolveVariableSafe(frame, varName);
                                if (value.Type == JsValueType.Error)
                                {
                                    throw new FenInternalError(value.ToString());
                                }
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.StoreVar:
                            {
                                int nameIndex = ReadInt32(instructions, ref frame);
                                string varName = GetStringConstant(frame.Block, constants, nameIndex);
                                var value = _stack[--_sp];
                                var declarationEnv = frame.Environment.GetDeclarationEnvironment();
                                declarationEnv.Set(varName, value);
                                if (CanUseBindingCache(frame))
                                {
                                    frame.CacheBindingEnvironment(varName, declarationEnv);
                                }
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.StoreVarDeclaration:
                            {
                                int nameIndex = ReadInt32(instructions, ref frame);
                                string varName = GetStringConstant(frame.Block, constants, nameIndex);
                                var value = _stack[--_sp];
                                var declarationEnv = frame.Environment.GetVarDeclarationEnvironment();
                                declarationEnv.Set(varName, value);
                                declarationEnv.SyncGlobalDeclarationBinding(varName, value);
                                if (CanUseBindingCache(frame))
                                {
                                    frame.RemoveCachedBindingEnvironment(varName);
                                }
                                _stack[_sp++] = value;
                                break;
                            }
                            case OpCode.DeclareTdz:
                            {
                                int nameIndex = ReadInt32(instructions, ref frame);
                                string varName = GetStringConstant(frame.Block, constants, nameIndex);
                                frame.Environment.GetDeclarationEnvironment().DeclareTDZ(varName);
                                if (CanUseBindingCache(frame))
                                {
                                    frame.RemoveCachedBindingEnvironment(varName);
                                }
                                break;
                            }
                            case OpCode.DeclareVar:
                            {
                                int nameIndex = ReadInt32(instructions, ref frame);
                                string varName = GetStringConstant(frame.Block, constants, nameIndex);
                                var declarationEnv = frame.Environment.GetVarDeclarationEnvironment();
                                if (!declarationEnv.HasLocalBinding(varName))
                                {
                                    declarationEnv.Set(varName, FenValue.Undefined);
                                }
                                declarationEnv.SyncGlobalDeclarationBinding(varName, declarationEnv.Get(varName));
                                if (CanUseBindingCache(frame))
                                {
                                    frame.RemoveCachedBindingEnvironment(varName);
                                }
                                break;
                            }
                            case OpCode.UpdateVar:
                            {
                                // Assignment: walks scope chain to update an existing binding,
                                // or creates in current scope if not found (implicit global in non-strict mode).
                                int nameIndex = ReadInt32(instructions, ref frame);
                                string varName = GetStringConstant(frame.Block, constants, nameIndex);
                                var value = _stack[--_sp];
                                bool strictAssignment = (frame.Block != null && frame.Block.IsStrict) || frame.Environment.StrictMode;
                                bool hasResolvedBinding = frame.Environment.ResolveBindingEnvironment(varName) != null;
                                if (!strictAssignment && !hasResolvedBinding && TryAssignUndeclaredToGlobal(frame, varName, value))
                                {
                                    if (CanUseBindingCache(frame))
                                    {
                                        frame.RemoveCachedBindingEnvironment(varName);
                                    }

                                    _stack[_sp++] = value;
                                    break;
                                }

                                var updateResult = frame.Environment.Update(varName, value, strictAssignment);
                                if (updateResult.Type == JsValueType.Error)
                                {
                                    throw new FenInternalError(updateResult.ToString());
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
                                var localValue = frame.Environment.GetFast(localSlot);
                                _stack[_sp++] = localValue;
                                break;
                            }
                            case OpCode.StoreLocalDeclaration:
                            {
                                int localSlot = ReadInt32(instructions, ref frame);
                                var value = _stack[--_sp];
                                frame.Environment.SetFast(localSlot, value);
                                string localName = frame.Block.GetLocalSlotName(localSlot);
                                if (!string.IsNullOrEmpty(localName))
                                {
                                    var declarationEnv = frame.Environment.GetVarDeclarationEnvironment();
                                    declarationEnv.Set(localName, value);
                                    if (CanUseBindingCache(frame))
                                    {
                                        frame.RemoveCachedBindingEnvironment(localName);
                                    }
                                }

                                _stack[_sp++] = value;
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
                                newFunc.IsMethodDefinition = templateFunc.IsMethodDefinition;
                                newFunc.HasOwnNameBinding = templateFunc.HasOwnNameBinding;
                                newFunc.Source = templateFunc.Source;
                                newFunc.NeedsArgumentsObject = templateFunc.NeedsArgumentsObject;
                                newFunc.LocalMap = templateFunc.LocalMap;
                                newFunc.HomeObject = templateFunc.HomeObject;

                                if (!newFunc.IsArrowFunction)
                                {
                                    var fnPrototype = new FenObject();
                                    fnPrototype.SetBuiltin("constructor", FenValue.FromFunction(newFunc));
                                    newFunc.Prototype = fnPrototype;
                                    newFunc.DefineOwnProperty("prototype", new PropertyDescriptor
                                    {
                                        Value = FenValue.FromObject(fnPrototype),
                                        Writable = true,
                                        Enumerable = false,
                                        Configurable = false
                                    });
                                }

                                _stack[_sp++] = FenValue.FromFunction(newFunc);
                                break;
                            }
                            case OpCode.Call:
                            {
                                int argCount = ReadInt32(instructions, ref frame);
                                int argStart = _sp - argCount;
                                var callee = _stack[argStart - 1];

                                var func = (callee.IsFunction || callee.IsObject) ? callee.AsObject() as FenFunction : null;
                                if (func == null) ThrowTypeError($"{callee.AsString()} is not a function");

                                if (func.IsNative)
                                {
                                    var args = new FenValue[argCount];
                                    if (argCount > 0)
                                    {
                                        Array.Copy(_stack.Items, argStart, args, 0, argCount);
                                    }

                                    _sp = argStart - 1; // Pop callee + args
                                    // Route native calls through FenFunction.Invoke so the owning runtime
                                    // can activate the correct realm and preserve built-in semantics.
                                    var callResult = InvokeNativeFunction(func, args, FenValue.Undefined);
                                    ThrowIfNativeError(callResult);
                                    _stack[_sp++] = callResult;
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    if (func.IsGenerator)
                                    {
                                        // Generator call: capture args and return a suspended GeneratorObject
                                        var genArgs = new FenValue[argCount];
                                        if (argCount > 0) Array.Copy(_stack.Items, argStart, genArgs, 0, argCount);
                                        _sp = argStart - 1;
                                        _stack[_sp++] = FenValue.FromObject(new GeneratorObject(func, genArgs));
                                        break;
                                    }

                                    var newEnv = new FenEnvironment(func.Env);
                                    if (func.BytecodeBlock != null && func.BytecodeBlock.IsStrict)
                                    {
                                        newEnv.StrictMode = true;
                                    }
                                    InitializeFunctionFastStore(func, newEnv);
                                    if (func.HasOwnNameBinding && !string.IsNullOrEmpty(func.Name))
                                    {
                                        SetFunctionBinding(func, newEnv, func.Name, FenValue.FromFunction(func));
                                    }
                                    // Bind 'this': undefined for strict-mode-like behaviour; arrow functions inherit from closure
                                    if (!func.IsArrowFunction)
                                    {
                                        var thisValue = func.BytecodeBlock != null && func.BytecodeBlock.IsStrict ? FenValue.Undefined : ResolveNonStrictThisBinding(frame);
                                        SetFunctionBinding(func, newEnv, "this", thisValue);
                                        BindSuperReference(func, newEnv, thisValue);
                                        BindSuperConstructorIfPresent(func, newEnv);
                                    }
                                    BindFunctionArgumentsFromStack(func, newEnv, argCount, argStart);

                                    _sp = argStart - 1; // Pop callee + args

                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.NewTarget = FenValue.Undefined;
                                    newFrame.IsAsyncFunction = func.IsAsync;
                                    goto fetch_frame; // Break out of inner loop to process new frame
                                }
                                break;
                            }
                            case OpCode.CallFromArray:
                            {
                                var argsArrayVal = _stack[--_sp];
                                var callee = _stack[--_sp];

                                var func = (callee.IsFunction || callee.IsObject) ? callee.AsObject() as FenFunction : null;
                                if (func == null) ThrowTypeError($"{callee.AsString()} is not a function");
                                var argsObject = argsArrayVal.IsObject ? argsArrayVal.AsObject() : null;
                                int argCount = GetArrayLikeLength(argsObject);

                                if (func.IsNative)
                                {
                                    var args = ExtractArrayLikeValues(argsArrayVal);
                                    var callFromArrayResult = InvokeNativeFunction(func, args, FenValue.Undefined);
                                    ThrowIfNativeError(callFromArrayResult);
                                    _stack[_sp++] = callFromArrayResult;
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    if (func.IsGenerator)
                                    {
                                        var genArgs = ExtractArrayLikeValues(argsArrayVal);
                                        _stack[_sp++] = FenValue.FromObject(new GeneratorObject(func, genArgs));
                                        break;
                                    }

                                    var newEnv = new FenEnvironment(func.Env);
                                    if (func.BytecodeBlock != null && func.BytecodeBlock.IsStrict)
                                    {
                                        newEnv.StrictMode = true;
                                    }
                                    InitializeFunctionFastStore(func, newEnv);
                                    if (func.HasOwnNameBinding && !string.IsNullOrEmpty(func.Name))
                                    {
                                        SetFunctionBinding(func, newEnv, func.Name, FenValue.FromFunction(func));
                                    }
                                    if (!func.IsArrowFunction)
                                    {
                                        var thisValue = func.BytecodeBlock != null && func.BytecodeBlock.IsStrict ? FenValue.Undefined : ResolveNonStrictThisBinding(frame);
                                        SetFunctionBinding(func, newEnv, "this", thisValue);
                                        BindSuperReference(func, newEnv, thisValue);
                                        BindSuperConstructorIfPresent(func, newEnv);
                                    }
                                    BindFunctionArgumentsFromArrayLike(func, newEnv, argsObject, argCount);

                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.NewTarget = FenValue.Undefined;
                                    newFrame.IsAsyncFunction = func.IsAsync;
                                    goto fetch_frame;
                                }
                                break;
                            }
                            case OpCode.CallMethod:
                            {
                                int argCount = ReadInt32(instructions, ref frame);
                                int argStart = _sp - argCount;
                                var callee = _stack[argStart - 1];
                                var receiver = _stack[argStart - 2];

                                var func = (callee.IsFunction || callee.IsObject) ? callee.AsObject() as FenFunction : null;
                                if (func == null) ThrowTypeError($"{callee.AsString()} is not a function");

                                if (func.IsNative)
                                {
                                    var args = new FenValue[argCount];
                                    if (argCount > 0)
                                    {
                                        Array.Copy(_stack.Items, argStart, args, 0, argCount);
                                    }

                                    _sp = argStart - 2; // Pop receiver + callee + args
                                    var callMethodResult = InvokeNativeFunction(func, args, receiver);
                                    ThrowIfNativeError(callMethodResult);
                                    _stack[_sp++] = callMethodResult;
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    if (func.IsGenerator)
                                    {
                                        var genArgs = new FenValue[argCount];
                                        if (argCount > 0)
                                        {
                                            Array.Copy(_stack.Items, argStart, genArgs, 0, argCount);
                                        }
                                        _sp = argStart - 2;
                                        _stack[_sp++] = FenValue.FromObject(new GeneratorObject(func, genArgs));
                                        break;
                                    }

                                    var newEnv = new FenEnvironment(func.Env);
                                    if (func.BytecodeBlock != null && func.BytecodeBlock.IsStrict)
                                    {
                                        newEnv.StrictMode = true;
                                    }
                                    InitializeFunctionFastStore(func, newEnv);
                                    if (func.HasOwnNameBinding && !string.IsNullOrEmpty(func.Name))
                                    {
                                        SetFunctionBinding(func, newEnv, func.Name, FenValue.FromFunction(func));
                                    }
                                    if (!func.IsArrowFunction)
                                    {
                                        var thisVal = receiver;
                                        SetFunctionBinding(func, newEnv, "this", thisVal);
                                        BindSuperReference(func, newEnv, thisVal);
                                        BindSuperConstructorIfPresent(func, newEnv);
                                    }
                                    BindFunctionArgumentsFromStack(func, newEnv, argCount, argStart);

                                    _sp = argStart - 2; // Pop receiver + callee + args

                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.NewTarget = FenValue.Undefined;
                                    newFrame.IsAsyncFunction = func.IsAsync;
                                    goto fetch_frame;
                                }
                                break;
                            }
                            case OpCode.CallMethodFromArray:
                            {
                                var argsArrayVal = _stack[--_sp];
                                var callee = _stack[--_sp];
                                var receiver = _stack[--_sp];

                                var func = (callee.IsFunction || callee.IsObject) ? callee.AsObject() as FenFunction : null;
                                if (func == null) ThrowTypeError($"{callee.AsString()} is not a function");
                                var argsObject = argsArrayVal.IsObject ? argsArrayVal.AsObject() : null;
                                int argCount = GetArrayLikeLength(argsObject);

                                if (func.IsNative)
                                {
                                    var args = ExtractArrayLikeValues(argsArrayVal);
                                    var callMethodFromArrayResult = InvokeNativeFunction(func, args, receiver);
                                    ThrowIfNativeError(callMethodFromArrayResult);
                                    _stack[_sp++] = callMethodFromArrayResult;
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    if (func.IsGenerator)
                                    {
                                        var genArgs = ExtractArrayLikeValues(argsArrayVal);
                                        _stack[_sp++] = FenValue.FromObject(new GeneratorObject(func, genArgs));
                                        break;
                                    }

                                    var newEnv = new FenEnvironment(func.Env);
                                    if (func.BytecodeBlock != null && func.BytecodeBlock.IsStrict)
                                    {
                                        newEnv.StrictMode = true;
                                    }
                                    InitializeFunctionFastStore(func, newEnv);
                                    if (func.HasOwnNameBinding && !string.IsNullOrEmpty(func.Name))
                                    {
                                        SetFunctionBinding(func, newEnv, func.Name, FenValue.FromFunction(func));
                                    }
                                    if (!func.IsArrowFunction)
                                    {
                                        var thisVal = receiver;
                                        SetFunctionBinding(func, newEnv, "this", thisVal);
                                        BindSuperReference(func, newEnv, thisVal);
                                        BindSuperConstructorIfPresent(func, newEnv);
                                    }
                                    BindFunctionArgumentsFromArrayLike(func, newEnv, argsObject, argCount);

                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.NewTarget = FenValue.Undefined;
                                    newFrame.IsAsyncFunction = func.IsAsync;
                                    goto fetch_frame;
                                }
                                break;
                            }
                            case OpCode.Construct:
                            {
                                int argCount = ReadInt32(instructions, ref frame);
                                int argStart = _sp - argCount;
                                var constructorVal = _stack[argStart - 1];

                                var func = (constructorVal.IsFunction || constructorVal.IsObject) ? constructorVal.AsObject() as FenFunction : null;
                                if (func == null) ThrowTypeError($"{constructorVal.AsString()} is not a constructor");
                                if (func.IsArrowFunction || !func.IsConstructor)
                                {
                                    ThrowTypeError(func.Name + " is not a constructor");
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
                                        Array.Copy(_stack.Items, argStart, args, 0, argCount);
                                    }

                                    _sp = argStart - 1; // Pop constructor + args

                                    // Native constructors usually ignore 'this' passed in and return their own newly created object,
                                    // or we pass newObj as 'this' depending on FenRuntime design.
                                    var result = func.NativeImplementation(args, FenValue.FromObject(newObj));
                                    ThrowIfNativeError(result);
                                    if (result.IsObject || result.IsFunction) _stack[_sp++] = result;
                                    else _stack[_sp++] = FenValue.FromObject(newObj);
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    var newEnv = new FenEnvironment(func.Env);
                                    if (func.BytecodeBlock != null && func.BytecodeBlock.IsStrict)
                                    {
                                        newEnv.StrictMode = true;
                                    }

                                    // Bind 'this' to newObj
                                    InitializeFunctionFastStore(func, newEnv);
                                    if (func.HasOwnNameBinding && !string.IsNullOrEmpty(func.Name))
                                    {
                                        SetFunctionBinding(func, newEnv, func.Name, FenValue.FromFunction(func));
                                    }
                                    SetFunctionBinding(func, newEnv, "this", FenValue.FromObject(newObj));
                                    BindSuperReference(func, newEnv, FenValue.FromObject(newObj));
                                    BindSuperConstructorIfPresent(func, newEnv);
                                    BindFunctionArgumentsFromStack(func, newEnv, argCount, argStart);

                                    _sp = argStart - 1; // Pop constructor + args

                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.IsConstruct = true;
                                    newFrame.ConstructedObject = newObj;
                                    newFrame.NewTarget = constructorVal;

                                    goto fetch_frame;
                                }
                                break;
                            }
                            case OpCode.ConstructFromArray:
                            {
                                var argsArrayVal = _stack[--_sp];
                                var constructorVal = _stack[--_sp];

                                var func = (constructorVal.IsFunction || constructorVal.IsObject) ? constructorVal.AsObject() as FenFunction : null;
                                if (func == null) ThrowTypeError($"{constructorVal.AsString()} is not a constructor");
                                if (func.IsArrowFunction || !func.IsConstructor)
                                {
                                    ThrowTypeError(func.Name + " is not a constructor");
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
                                    ThrowIfNativeError(result);
                                    if (result.IsObject || result.IsFunction) _stack[_sp++] = result;
                                    else _stack[_sp++] = FenValue.FromObject(newObj);
                                }
                                else if (func.BytecodeBlock != null)
                                {
                                    var newEnv = new FenEnvironment(func.Env);
                                    if (func.BytecodeBlock != null && func.BytecodeBlock.IsStrict)
                                    {
                                        newEnv.StrictMode = true;
                                    }
                                    InitializeFunctionFastStore(func, newEnv);
                                    if (func.HasOwnNameBinding && !string.IsNullOrEmpty(func.Name))
                                    {
                                        SetFunctionBinding(func, newEnv, func.Name, FenValue.FromFunction(func));
                                    }
                                    SetFunctionBinding(func, newEnv, "this", FenValue.FromObject(newObj));
                                    BindSuperReference(func, newEnv, FenValue.FromObject(newObj));
                                    BindSuperConstructorIfPresent(func, newEnv);
                                    BindFunctionArgumentsFromArrayLike(func, newEnv, argsObject, argCount);

                                    var newFrame = PushFrame(func.BytecodeBlock, newEnv, _sp);
                                    newFrame.IsConstruct = true;
                                    newFrame.ConstructedObject = newObj;
                                    newFrame.NewTarget = constructorVal;

                                    goto fetch_frame;
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
                                var propertyKey = PropertyKey(prop);
                                RequireObjectCoercible(obj, $"LoadProp '{propertyKey}'");
                                var objectRef = obj.AsObject();
                                if (objectRef != null)
                                {
                                    var key = propertyKey;
                                    if (objectRef is FenObject fenObj)
                                    {
                                        // Keep regex literals linked to the active realm's RegExp.prototype.
                                        if (fenObj.InternalClass == "RegExp")
                                        {
                                            EnsureRegExpPrototype(fenObj, frame);
                                        }

                                        var cache = GetLoadPropertyInlineCache(frame.Block);
                                        bool mustUseDynamicEventGetter =
                                            fenObj is FenBrowser.FenEngine.DOM.DomEvent &&
                                            (string.Equals(key, "eventPhase", StringComparison.Ordinal) ||
                                             string.Equals(key, "currentTarget", StringComparison.Ordinal) ||
                                             string.Equals(key, "defaultPrevented", StringComparison.Ordinal) ||
                                             string.Equals(key, "returnValue", StringComparison.Ordinal) ||
                                             string.Equals(key, "cancelBubble", StringComparison.Ordinal) ||
                                             string.Equals(key, "isTrusted", StringComparison.Ordinal));
                                        if (!mustUseDynamicEventGetter &&
                                            TryLoadPropertyInlineCache(cache, instructionOffset, fenObj, key, out var cachedValue))
                                        {
                                            _stack[_sp++] = cachedValue;
                                            break;
                                        }

                                        var value = fenObj.GetWithReceiver(key, obj);
                                        _stack[_sp++] = value;
                                        if (!mustUseDynamicEventGetter)
                                        {
                                            PopulatePropertyInlineCache(cache, instructionOffset, fenObj, key, writableRequired: false);
                                        }
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
                                else if (obj.IsSymbol)
                                {
                                    var key = PropertyKey(prop);
                                    if (string.Equals(key, "description", StringComparison.Ordinal))
                                    {
                                        var sym = obj.AsSymbol();
                                        _stack[_sp++] = sym != null && sym.Description != null
                                            ? FenValue.FromString(sym.Description)
                                            : FenValue.Undefined;
                                    }
                                    else
                                    {
                                        var proto = GetPrimitivePrototype(frame, "Symbol");
                                        _stack[_sp++] = proto != null ? proto.Get(key) : FenValue.Undefined;
                                    }
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
                                RequireObjectCoercible(obj, "StoreProp");
                                var objectRef = obj.AsObject();
                                if (objectRef != null)
                                {
                                    var key = PropertyKey(prop);
                                    if (objectRef is FenObject fenObj && !string.Equals(key, "__proto__", StringComparison.Ordinal))
                                    {
                                        const string getterMarker = "__get_";
                                        const string setterMarker = "__set_";
                                        if (key.StartsWith(getterMarker, StringComparison.Ordinal) ||
                                            key.StartsWith(setterMarker, StringComparison.Ordinal))
                                        {
                                            bool isGetter = key.StartsWith(getterMarker, StringComparison.Ordinal);
                                            string actualKey = key.Substring(isGetter ? getterMarker.Length : setterMarker.Length);
                                            var existingDesc = fenObj.GetOwnPropertyDescriptor(actualKey);
                                            FenFunction existingGetter = existingDesc.HasValue ? existingDesc.Value.Getter : null;
                                            FenFunction existingSetter = existingDesc.HasValue ? existingDesc.Value.Setter : null;
                                            var incomingFn = value.IsFunction ? value.AsFunction() : null;
                                            var accessorDesc = PropertyDescriptor.Accessor(
                                                isGetter ? incomingFn : existingGetter,
                                                isGetter ? existingSetter : incomingFn,
                                                enumerable: true,
                                                configurable: true);
                                            fenObj.DefineOwnProperty(actualKey, accessorDesc);
                                            _stack[_sp++] = value;
                                            break;
                                        }

                                        bool storePropStrict = (frame.Block != null && frame.Block.IsStrict) || frame.Environment.StrictMode;
                                        bool mustUseCustomEventSetter =
                                            fenObj is FenBrowser.FenEngine.DOM.DomEvent &&
                                            (string.Equals(key, "cancelBubble", StringComparison.Ordinal) ||
                                             string.Equals(key, "returnValue", StringComparison.Ordinal));
                                        bool arrayLengthSensitiveStore =
                                            fenObj is BytecodeArrayObject &&
                                            (string.Equals(key, "length", StringComparison.Ordinal) || IsArrayIndexKey(key));
                                        var cache = GetStorePropertyInlineCache(frame.Block);
                                        bool inlineCacheHit = false;
                                        if (!mustUseCustomEventSetter &&
                                            !arrayLengthSensitiveStore &&
                                            cache.TryGetValue(instructionOffset, out var pic) &&
                                            !pic.Megamorphic)
                                        {
                                            var shape = fenObj.GetShape();
                                            for (int idx = 0; idx < pic.Count; idx++)
                                            {
                                                var entry = pic.Entries[idx];
                                                if (entry.Shape == shape && string.Equals(entry.Key, key, StringComparison.Ordinal))
                                                {
                                                    var storage = fenObj.GetPropertyStorage();
                                                    if ((uint)entry.SlotIndex < (uint)storage.Length)
                                                    {
                                                        var descriptor = storage[entry.SlotIndex];
                                                        if (!descriptor.IsAccessor && descriptor.Writable != false)
                                                        {
                                                            descriptor.Value = value;
                                                            storage[entry.SlotIndex] = descriptor;
                                                            _stack[_sp++] = value;
                                                            inlineCacheHit = true;
                                                        }
                                                        else if (!descriptor.IsAccessor && descriptor.Writable == false && storePropStrict)
                                                        {
                                                            // ECMA-262 §9.1.9.1 step 4.a: strict-mode TypeError for non-writable
                                                            throw new FenTypeError($"TypeError: Cannot assign to read only property '{key}'");
                                                        }
                                                    }
                                                    break;
                                                }
                                            }
                                        }

                                        if (inlineCacheHit)
                                        {
                                            break;
                                        }

                                        fenObj.Set(key, value, storePropStrict);
                                        if (!mustUseCustomEventSetter && !arrayLengthSensitiveStore)
                                        {
                                            PopulatePropertyInlineCache(cache, instructionOffset, fenObj, key, writableRequired: true);
                                        }
                                    }
                                    else
                                    {
                                        if (objectRef is HTMLCollectionWrapper htmlCollection)
                                        {
                                            bool strictMode = (frame.Block != null && frame.Block.IsStrict) || frame.Environment.StrictMode;
                                            htmlCollection.SetFromVm(key, value, strictMode);
                                        }
                                        else
                                        {
                                            objectRef.Set(key, value);
                                        }
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

                                if ((targetValue.IsObject || targetValue.IsFunction) && (sourceValue.IsObject || sourceValue.IsFunction))
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
                                RequireObjectCoercible(obj, "DeleteProp");
                                bool deleted = true;
                                var objectRef = obj.AsObject();
                                if (objectRef != null)
                                {
                                    deleted = objectRef.Delete(PropertyKey(prop));
                                }

                                bool strictDelete = (frame.Block != null && frame.Block.IsStrict) || frame.Environment.StrictMode;
                                if (!deleted && strictDelete)
                                {
                                    ThrowTypeError("Cannot delete property");
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
                                else if (objVal.IsObject || objVal.IsFunction)
                                {
                                    var obj = objVal.AsObject();
                                    // for...in must enumerate all enumerable properties in the prototype chain
                                    // per ECMA-262 §14.7.5.9, not just own properties.
                                    iterObj.NativeObject = obj != null
                                        ? (obj is FenObject fenObj
                                            ? new KeyIteratorEnumerator(fenObj.EnumerableKeys().GetEnumerator())
                                            : new KeyIteratorEnumerator(obj.Keys().GetEnumerator()))
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
                                RequireObjectCoercible(objVal, "GetIterator");

                                var symIterMethod = FenValue.Undefined;
                                if (objVal.IsObject || objVal.IsFunction)
                                {
                                    symIterMethod = objVal.AsObject().Get("[Symbol.iterator]");
                                }
                                else if (objVal.IsString)
                                {
                                    var strProto = GetPrimitivePrototype(frame, "String");
                                    if (strProto != null) symIterMethod = strProto.Get("[Symbol.iterator]");
                                }

                                if (symIterMethod.IsFunction)
                                {
                                    var iteratorFn = symIterMethod.AsFunction() as FenFunction;
                                    var iteratorResult = iteratorFn.Invoke(Array.Empty<FenValue>(), null, objVal);
                                    if (!iteratorResult.IsObject)
                                    {
                                        ThrowTypeError("Symbol.iterator must return an object");
                                    }
                                    var iteratorActualObj = iteratorResult.AsObject();
                                    var nextMethod = iteratorActualObj.Get("next");
                                    if (!nextMethod.IsFunction)
                                    {
                                        ThrowTypeError("Iterator must have a next() method");
                                    }
                                    iterObj.NativeObject = new JsProtocolIteratorEnumerator(iteratorActualObj, nextMethod.AsFunction() as FenFunction);
                                }
                                else if (objVal.IsString)
                                {
                                    iterObj.NativeObject = new StringIteratorEnumerator(objVal.AsString());
                                }
                                else if (objVal.IsObject || objVal.IsFunction)
                                {
                                    var obj = objVal.AsObject();
                                    iterObj.NativeObject = obj != null
                                        ? new ValueIteratorEnumerator(obj)
                                        : (IEnumerator<FenValue>)EmptyFenValueEnumerator.Instance;
                                }
                                else
                                {
                                    ThrowTypeError($"{objVal.AsString()} is not iterable");
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
                            case OpCode.IteratorClose:
                            {
                                var obj = (FenObject)_stack[--_sp].AsObject();
                                var iter = (IEnumerator<FenValue>)obj.NativeObject;
                                iter.Dispose();
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
                                if (val.IsHtmlDdaObject)
                                {
                                    _stack[_sp++] = FenValue.FromString("undefined");
                                    break;
                                }

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
                            case OpCode.SetFunctionHomeObject:
                            {
                                var homeObjectValue = _stack[--_sp];
                                var functionValue = _stack[--_sp];
                                if ((functionValue.IsFunction || functionValue.IsObject) &&
                                    functionValue.AsObject() is FenFunction function &&
                                    homeObjectValue.IsObject)
                                {
                                    function.HomeObject = homeObjectValue.AsObject();
                                }

                                _stack[_sp++] = functionValue;
                                break;
                            }
                            case OpCode.DirectEval:
                            {
                                int directEvalFlags = ReadInt32(instructions, ref frame);
                                var sourceValue = _stack[--_sp];
                                _stack[_sp++] = ExecuteDirectEval(sourceValue, frame, directEvalFlags);
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
                                var withObject = ToObjectForWith(withObjectValue, frame);
                                if (withObject == null)
                                {
                                    throw new FenTypeError("TypeError: Cannot convert undefined or null to object");
                                }

                                var withEnv = new FenEnvironment(frame.Environment, withObject);
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
                                // A return inside a finally block suppresses the pending exception (JS spec)
                                frame.PendingException = null;
                                _frameCount--;
                                _sp = frame.StackBase;

                                // If this was a constructor call, returning a primitive yields the constructed object.
                                // Returning an object yields that object.
                                if (frame.IsConstruct && !result.IsObject && !result.IsFunction)
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
                            case OpCode.PushScope:
                            {
                                // Create a new child environment for block-level bindings (let/const/class)
                                var childEnv = new FenEnvironment(frame.Environment, isLexicalScope: true);
                                childEnv.InheritFastSlots(frame.Environment);
                                frame.SetEnvironment(childEnv);
                                break;
                            }
                            case OpCode.PopScope:
                            {
                                // Restore the parent environment, discarding block-level bindings
                                var parentEnv = frame.Environment.Outer;
                                if (parentEnv != null)
                                {
                                    frame.SetEnvironment(parentEnv);
                                }
                                break;
                            }
                            case OpCode.EnterFinally:
                            {
                                // Marker: finally block begins. In normal flow, execution falls through.
                                // Exception-path finally is handled by HandleException.
                                break;
                            }
                            case OpCode.ExitFinally:
                            {
                                // If this finally was entered due to an exception, re-throw it now
                                if (frame.PendingException.HasValue)
                                {
                                    var pending = frame.PendingException.Value;
                                    frame.PendingException = null;
                                    HandleException(pending, ref frame);
                                    goto fetch_frame;
                                }
                                break;
                            }
                            case OpCode.Yield:
                            {
                                // Suspend execution and expose yielded value to the caller.
                                // GeneratorObject.Next() still uses _generatorYielded/_generatorYieldValue,
                                // while direct bytecode evaluation receives the yielded value directly.
                                var yieldedValue = _stack[--_sp];
                                _generatorYielded = true;
                                _generatorYieldValue = yieldedValue;
                                return yieldedValue;
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
                                throw new FenInternalError($"VM Error: Unhandled OpCode {op} at IP {frame.IP - 1}");
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
                if (ex is FenError fenError)
                {
                    var thrownValue = CreateHostExceptionValue(fenError);
                    if (_frameCount > 0)
                    {
                        var top = _callFrames[_frameCount - 1];
                        HandleException(thrownValue, ref top);
                        goto run_loop_restart;
                    }

                    throw CreateUncaughtHostException(thrownValue, fenError);
                }

                if (TryExtractThrownValue(ex, out var thrown))
                {
                    if (_frameCount > 0)
                    {
                        var top = _callFrames[_frameCount - 1];
                        HandleException(thrown, ref top);
                        goto run_loop_restart;
                    }

                    if (IsThrownValueBoundaryException(ex))
                    {
                        throw;
                    }

                    throw CreateUncaughtHostException(thrown, ex);
                }

                // Unwind and handle .NET exceptions gracefully
                var errorObj = CreateHostExceptionValue(ex);

                if (_frameCount > 0)
                {
                    var topFrame = _callFrames[_frameCount - 1];
                    HandleException(errorObj, ref topFrame);
                    // Resume execution at the installed JS catch/finally handler without recursive RunLoop calls.
                    goto run_loop_restart;
                }
                else
                {
                    throw new global::System.Exception($"Uncaught JS Exception: {FormatExceptionValue(errorObj)}", ex);
                }
            }

            return FenValue.Undefined;
        }

        private FenValue CreateHostExceptionValue(Exception ex)
        {
            string rawMsg = ex?.Message ?? "Error";
            string errType = "Error";
            string errMsg = rawMsg;
            int? domExceptionCode = null;

            if (ex is FenBrowser.Core.Dom.V2.DomException domException)
            {
                errType = domException.Name;
                errMsg = domException.Message ?? string.Empty;
                domExceptionCode = domException.Code;
            }
            else if (TryParseKnownErrorPrefix(rawMsg, out var prefixedErrorType, out var prefixedErrorMessage))
            {
                errType = prefixedErrorType;
                errMsg = prefixedErrorMessage;
            }
            else if (ex is FenError fenError)
            {
                errType = fenError.Type switch
                {
                    ErrorType.Type => "TypeError",
                    ErrorType.Range => "RangeError",
                    ErrorType.Reference => "ReferenceError",
                    ErrorType.Syntax => "SyntaxError",
                    ErrorType.Security => "SecurityError",
                    _ => "Error"
                };
            }

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
                        if (protoVal.IsObject)
                        {
                            errObj.SetPrototype(protoVal.AsObject());
                        }

                        errObj.Set("name", FenValue.FromString(errType));
                        errObj.Set("message", FenValue.FromString(errMsg));
                        if (domExceptionCode.HasValue)
                        {
                            errObj.Set("code", FenValue.FromNumber(domExceptionCode.Value));
                            errObj.Set("Symbol(Symbol.toStringTag)", FenValue.FromString("DOMException"));
                        }
                        errObj.Set("stack", FenValue.FromString($"{errType}: {errMsg}\n    at <anonymous>"));
                        return FenValue.FromObject(errObj);
                    }
                }
                catch
                {
                }
            }

            var plainErr = new FenObject();
            plainErr.Set("name", FenValue.FromString(errType));
            plainErr.Set("message", FenValue.FromString(errMsg));
            if (domExceptionCode.HasValue)
            {
                plainErr.Set("code", FenValue.FromNumber(domExceptionCode.Value));
                plainErr.Set("Symbol(Symbol.toStringTag)", FenValue.FromString("DOMException"));
            }
            return FenValue.FromObject(plainErr);
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

        private Exception CreateUncaughtHostException(FenValue thrownValue, Exception innerException = null)
        {
            return JsThrownValueException.CreateBoundaryException(
                $"Uncaught JS Exception: {FormatExceptionValue(thrownValue)}",
                thrownValue,
                innerException);
        }

        private static bool IsThrownValueBoundaryException(Exception exception)
        {
            return exception?.GetType() == typeof(Exception) &&
                   exception.Data?[JsThrownValueException.ThrownValueDataKey] is FenValue;
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
                        // Store pending exception: ExitFinally will re-throw it after finally runs
                        frame.PendingException = exceptionValue;
                        return; // Resume execution in RunLoop
                    }
                }

                // Async functions capture uncaught exceptions as rejected promise results,
                // rather than propagating host exceptions through callers.
                if (frame.IsAsyncFunction)
                {
                    var rejection = FenValue.FromObject(JsPromise.Reject(exceptionValue, null));
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
            throw CreateUncaughtHostException(exceptionValue);
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
                // Keep dictionary-backed lexical updates and fast-slot loads coherent.
                // Without this mapping, closure writes via Environment.Update() can leave
                // captured locals stale in FastStore.
                env.ConfigureFastSlots(func.LocalMap);
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

                // arguments[@@iterator] = Array.prototype.values (ECMA-262 §10.4.4.7)
                var arrayProtoValues = FenObject.DefaultArrayPrototype?.Get("values");
                if (arrayProtoValues.HasValue && arrayProtoValues.Value.IsFunction)
                {
                    argumentsObj.Set("@@iterator", arrayProtoValues.Value);
                    argumentsObj.SetSymbol(Types.JsSymbol.Iterator, arrayProtoValues.Value);
                }

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
                if (promise.IsFulfilled)
                {
                    return promise.Result;
                }

                throw new JsThrownValueException(promise.Result);
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
                    if (EventLoopCoordinator.Instance.HasPendingTasksFor(FenBrowser.FenEngine.Core.EventLoop.TaskSource.Messaging))
                    {
                        break;
                    }

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
                if (promise.IsFulfilled)
                {
                    return promise.Result;
                }

                throw new JsThrownValueException(promise.Result);
            }

            return FenValue.Undefined;
        }
        

        private FenBrowser.FenEngine.Core.Interfaces.IObject ToObjectForWith(FenValue value, CallFrame frame)
        {
            if (value.IsUndefined || value.IsNull)
            {
                return null;
            }

            if (value.IsObject || value.IsFunction)
            {
                return value.AsObject();
            }

            var wrapper = new FenObject();
            if (value.IsString)
            {
                wrapper.InternalClass = "String";
                wrapper.Set("__value__", value);
                var proto = GetPrimitivePrototype(frame, "String");
                if (proto != null) wrapper.SetPrototype(proto);
                return wrapper;
            }

            if (value.IsNumber)
            {
                wrapper.InternalClass = "Number";
                wrapper.Set("__value__", value);
                var proto = GetPrimitivePrototype(frame, "Number");
                if (proto != null) wrapper.SetPrototype(proto);
                return wrapper;
            }

            if (value.IsBoolean)
            {
                wrapper.InternalClass = "Boolean";
                wrapper.Set("__value__", value);
                var proto = GetPrimitivePrototype(frame, "Boolean");
                if (proto != null) wrapper.SetPrototype(proto);
                return wrapper;
            }

            if (value.IsBigInt || value.IsSymbol)
            {
                wrapper.Set("__value__", value);
                wrapper.Set("valueOf", FenValue.FromFunction(new FenFunction("valueOf", (args, thisVal) => value)));
                return wrapper;
            }

            return wrapper;
        }

        private FenValue ExecuteAdd(FenValue left, FenValue right)
        {
            // ES2023 AdditiveExpression: apply ToPrimitive(default) first.
            var ap = left.IsObject || left.IsFunction ? left.ToPrimitive(null, "default") : left;
            var bp = right.IsObject || right.IsFunction ? right.ToPrimitive(null, "default") : right;


            // String concatenation path is selected before numeric/BigInt addition.
            if (ap.IsString || bp.IsString)
            {
                return FenValue.FromString(ap.AsString() + bp.AsString());
            }

            if (ap.IsSymbol || bp.IsSymbol)
            {
                throw new FenTypeError("TypeError: Cannot convert a Symbol value to a number");
            }

            // BigInt addition requires both operands to be BigInt.
            if (ap.IsBigInt || bp.IsBigInt)
            {
                if (!ap.IsBigInt || !bp.IsBigInt)
                {
                    throw new FenTypeError("TypeError: Cannot mix BigInt and other types, use explicit conversions");
                }

                var leftBigInt = ap.AsBigInt();
                var rightBigInt = bp.AsBigInt();
                return FenValue.FromBigInt(JsBigInt.Add(leftBigInt, rightBigInt));
            }

            // Number addition fallback.
            return FenValue.FromNumber(ap.ToNumber() + bp.ToNumber());
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

            var ctor = ResolveVariableSafe(frame, constructorName);
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
            if (proto != null && !ReferenceEquals(regexObj.GetPrototype(), proto))
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




































