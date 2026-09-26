using FenBrowser.Js.Heap;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// Array.fromAsync ( asyncItems [ , mapfn [ , thisArg ] ] ) - ECMA-262 23.1.2.2
// (ES2026). The body is an async abstract closure: every step that the
// specification writes as Await suspends until a promise job resumes it, so an
// endless iterator is drained one microtask at a time - never in one loop that
// only the process ending can stop - and an async mapfn's rejection is seen,
// closes the iterator and rejects the result.
public sealed partial class BytecodeInterpreter
{
    private JsValue ArrayFromAsync(JsValue constructor, IReadOnlyList<JsValue> args)
    {
        var capability = NewPromiseCapability();
        var job = new ArrayFromAsyncJob(
            this,
            capability,
            constructor,
            args.Count > 0 ? args[0] : JsValue.Undefined,
            args.Count > 1 ? args[1] : JsValue.Undefined,
            args.Count > 2 ? args[2] : JsValue.Undefined);
        job.Start();
        return capability.Promise;
    }

    /// <summary>
    /// The closure's state between awaits. Everything it holds is also held by
    /// the reactions of the promise it is waiting on (their captured roots), so
    /// nothing it refers to can be collected while it is suspended.
    /// </summary>
    private sealed class ArrayFromAsyncJob
    {
        private readonly BytecodeInterpreter _vm;
        private readonly PromiseCapability _capability;
        private readonly JsValue _constructor;
        private readonly JsValue _items;
        private readonly JsValue _mapfn;
        private readonly JsValue _thisArg;
        private bool _mapping;

        // The iterator form; _iterator is undefined for the array-like form.
        private JsValue _iterator = JsValue.Undefined;
        private JsValue _nextMethod = JsValue.Undefined;
        private bool _syncIterator;

        // The array-like form.
        private JsValue _arrayLike = JsValue.Undefined;
        private long _length;

        private JsValue _result = JsValue.Undefined;
        private long _index;

        public ArrayFromAsyncJob(
            BytecodeInterpreter vm, PromiseCapability capability, JsValue constructor, JsValue items, JsValue mapfn, JsValue thisArg)
        {
            _vm = vm;
            _capability = capability;
            _constructor = constructor;
            _items = items;
            _mapfn = mapfn;
            _thisArg = thisArg;
        }

        // Every abrupt completion of the closure rejects the promise
        // (AsyncFunctionStart). An uncatchable stop - an interrupt, a deadline,
        // an exhausted budget - is not a completion of the closure and is never
        // turned into one.
        public void Start()
        {
            try
            {
                Begin();
            }
            catch (JsThrownException ex) when (!ex.IsUncatchableByScript)
            {
                Reject(ex.Value);
            }
        }

        private void Begin()
        {
            if (_mapfn.Tag != JsValueTag.Undefined)
            {
                if (!_vm.IsCallable(_mapfn))
                {
                    throw new JsThrownException(_vm.CreateTypeError("Array.fromAsync: mapfn is not callable."));
                }

                _mapping = true;
            }

            // Steps 3.c-3.g: @@asyncIterator, else @@iterator driven with
            // async-from-sync semantics, else an array-like.
            var usingAsyncIterator = GetSymbolMethod(_items, "asyncIterator");
            var usingSyncIterator = usingAsyncIterator.Tag == JsValueTag.Undefined
                ? GetSymbolMethod(_items, "iterator")
                : JsValue.Undefined;
            if (usingAsyncIterator.Tag != JsValueTag.Undefined || usingSyncIterator.Tag != JsValueTag.Undefined)
            {
                _syncIterator = usingAsyncIterator.Tag == JsValueTag.Undefined;
                var method = _syncIterator ? usingSyncIterator : usingAsyncIterator;
                _iterator = _vm.CallFunction(method, Array.Empty<JsValue>(), _items);
                if (_iterator.Tag != JsValueTag.Object)
                {
                    throw new JsThrownException(_vm.CreateTypeError("Array.fromAsync: the iterator is not an object."));
                }

                _nextMethod = _vm.GetReceiverProperty(_iterator, "next");
                _result = IsConstructor(_constructor)
                    ? _vm.ConstructFunction(_constructor, Array.Empty<JsValue>(), _constructor)
                    : NewArray(0);
                IteratorStep();
                return;
            }

            // Step 3.i: an array-like.
            _arrayLike = _vm.ToObjectValue(_items);
            _length = (long)_vm.LengthOfArrayLikeAsDouble(_vm._heap.GetObject(_vm.ResolveObjectHandle(_arrayLike)), _arrayLike);
            _result = IsConstructor(_constructor)
                ? _vm.ConstructFunction(_constructor, new[] { JsValue.FromNumber(_length) }, _constructor)
                : NewArray(_length);
            ArrayLikeStep();
        }

        // ------------------------------------------------ iterator form

        private void IteratorStep()
        {
            if (_index >= MaxSafeLength)
            {
                CloseIteratorThenReject(_vm.CreateTypeError("Array.fromAsync: too many elements."));
                return;
            }

            var nextResult = _vm.CallFunction(_nextMethod, Array.Empty<JsValue>(), _iterator);
            if (_syncIterator)
            {
                // Async-from-sync (27.1.4.2.1): done is read from the sync
                // result, and only the value is awaited.
                OnNextResult(nextResult, fromSyncIterator: true);
                return;
            }

            Await(nextResult, awaited => OnNextResult(awaited, fromSyncIterator: false), Reject);
        }

        private void OnNextResult(JsValue nextResult, bool fromSyncIterator)
        {
            if (nextResult.Tag != JsValueTag.Object)
            {
                throw new JsThrownException(_vm.CreateTypeError("Array.fromAsync: iterator result is not an object."));
            }

            if (_vm.IteratorResultDone(nextResult))
            {
                SetLengthAndResolve(_index);
                return;
            }

            var nextValue = _vm.GetReceiverProperty(nextResult, "value");
            if (fromSyncIterator)
            {
                // A rejected value closes the sync iterator (27.1.4.4 step 9).
                Await(nextValue, MapAndStore, error => CloseIteratorThenReject(error));
                return;
            }

            MapAndStore(nextValue);
        }

        private void MapAndStore(JsValue value)
        {
            if (!_mapping)
            {
                StoreIteratorValue(value);
                return;
            }

            JsValue mapped;
            try
            {
                mapped = _vm.CallFunction(_mapfn, new[] { value, JsValue.FromNumber(_index) }, _thisArg);
            }
            catch (JsThrownException ex) when (!ex.IsUncatchableByScript)
            {
                CloseIteratorThenReject(ex.Value);
                return;
            }

            // IfAbruptCloseAsyncIterator(Await(mappedValue)).
            Await(mapped, StoreIteratorValue, error => CloseIteratorThenReject(error));
        }

        private void StoreIteratorValue(JsValue value)
        {
            try
            {
                _vm.DefineOwnDataProperty(_result, IndexKey(_index), value);
            }
            catch (JsThrownException ex) when (!ex.IsUncatchableByScript)
            {
                CloseIteratorThenReject(ex.Value);
                return;
            }

            _index++;
            IteratorStep();
        }

        /// <summary>
        /// AsyncIteratorClose (or IteratorClose for the sync form) with a throw
        /// completion: the iterator's return is called and, for an async
        /// iterator, awaited - but whatever it does, the original error wins.
        /// </summary>
        private void CloseIteratorThenReject(JsValue error)
        {
            JsValue closeResult;
            try
            {
                var returnMethod = _vm.GetReceiverProperty(_iterator, "return");
                if (returnMethod.Tag is JsValueTag.Undefined or JsValueTag.Null)
                {
                    Reject(error);
                    return;
                }

                closeResult = _vm.CallFunction(returnMethod, Array.Empty<JsValue>(), _iterator);
            }
            catch (JsThrownException ex) when (!ex.IsUncatchableByScript)
            {
                Reject(error);
                return;
            }

            if (_syncIterator)
            {
                Reject(error);
                return;
            }

            Await(closeResult, _ => Reject(error), _ => Reject(error));
        }

        // ------------------------------------------------ array-like form

        private void ArrayLikeStep()
        {
            if (_index >= _length)
            {
                SetLengthAndResolve(_length);
                return;
            }

            var element = _vm.GetReceiverProperty(_arrayLike, IndexKey(_index));
            Await(element, ArrayLikeMap, Reject);
        }

        private void ArrayLikeMap(JsValue value)
        {
            if (!_mapping)
            {
                ArrayLikeStore(value);
                return;
            }

            var mapped = _vm.CallFunction(_mapfn, new[] { value, JsValue.FromNumber(_index) }, _thisArg);
            Await(mapped, ArrayLikeStore, Reject);
        }

        private void ArrayLikeStore(JsValue value)
        {
            _vm.DefineOwnDataProperty(_result, IndexKey(_index), value);
            _index++;
            ArrayLikeStep();
        }

        // ------------------------------------------------ shared

        private void SetLengthAndResolve(long length)
        {
            // Set(A, "length", 𝔽(len), true).
            var resultHandle = _vm.ResolveObjectHandle(_result);
            _vm.SetOrThrow(resultHandle, _vm._heap.GetObject(resultHandle), "length", JsValue.FromNumber(length));
            _ = _vm.CallFunction(_capability.Resolve, new[] { _result }, JsValue.Undefined);
        }

        private void Reject(JsValue reason)
            => _ = _vm.CallFunction(_capability.Reject, new[] { reason }, JsValue.Undefined);

        /// <summary>
        /// Await(value): the continuation runs from a promise job. A step that
        /// completes abruptly there rejects the closure's promise, like one that
        /// does so before the first await.
        /// </summary>
        private void Await(JsValue value, Action<JsValue> onFulfilled, Action<JsValue> onRejected)
        {
            var promise = _vm.PromiseResolveStatic(value);
            var roots = new[]
            {
                promise, _capability.Promise, _capability.Resolve, _capability.Reject, _constructor, _items,
                _mapfn, _thisArg, _iterator, _nextMethod, _arrayLike, _result,
            };
            var fulfilled = _vm.AllocateNativeCallback((_, callArgs) =>
            {
                Resume(onFulfilled, callArgs.Count > 0 ? callArgs[0] : JsValue.Undefined);
                return JsValue.Undefined;
            }, roots);
            var rejected = _vm.AllocateNativeCallback((_, callArgs) =>
            {
                Resume(onRejected, callArgs.Count > 0 ? callArgs[0] : JsValue.Undefined);
                return JsValue.Undefined;
            }, roots);

            var handle = promise.AsObjectHandle();
            var instance = (PromiseInstance)_vm._heap.GetObject(handle);
            _ = _vm.PerformPromiseThen(handle, instance.Promise, fulfilled, rejected, _vm.GetDummyCapability());
        }

        private void Resume(Action<JsValue> continuation, JsValue value)
        {
            try
            {
                continuation(value);
            }
            catch (JsThrownException ex) when (!ex.IsUncatchableByScript)
            {
                Reject(ex.Value);
            }
        }

        private JsValue GetSymbolMethod(JsValue target, string wellKnown)
        {
            // GetMethod(asyncItems, @@name): GetV, so a primitive's prototype
            // answers; undefined or null is no method; anything else must be
            // callable.
            var symbol = _vm.GetWellKnownSymbolId(wellKnown);
            if (symbol == 0 || target.Tag is JsValueTag.Undefined or JsValueTag.Null)
            {
                return JsValue.Undefined;
            }

            var method = _vm.GetReceiverSymbolProperty(target, symbol);
            if (method.Tag is JsValueTag.Undefined or JsValueTag.Null)
            {
                return JsValue.Undefined;
            }

            if (!_vm.IsCallable(method))
            {
                throw new JsThrownException(_vm.CreateTypeError($"Array.fromAsync: Symbol.{wellKnown} is not callable."));
            }

            return method;
        }

        private bool IsConstructor(JsValue value)
            => value.Tag == JsValueTag.Object && _vm.IsConstructableTarget(value.AsObjectHandle());

        private JsValue NewArray(long length)
        {
            // ArrayCreate: a length past 2^32 - 1 is a RangeError.
            if (length > uint.MaxValue)
            {
                throw new JsThrownException(_vm.CreateRangeError("Invalid array length."));
            }

            var array = _vm.CreateEmptyArray(0);
            _ = array.SetProperty("length", JsValue.FromNumber(length));
            return JsValue.FromObject(_vm._heap.AllocateObject(array, AllocationSite.Current()));
        }

        private static string IndexKey(long index)
            => index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private const long MaxSafeLength = 9007199254740991;
    }
}
