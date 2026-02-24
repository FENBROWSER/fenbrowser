Plan to implement │
│ │
│ JS Engine Score: 40 → 90 │
│ │
│ Context │
│ │
│ The JavaScript Engine subsystem is at 40/100 — the lowest-scoring subsystem. The 90-gate criteria are: │
│ 1. Test262 pass rate reaches agreed 90-level threshold for target profile │
│ 2. No critical sync/blocking anti-patterns in JS execution hot paths │
│ 3. Failure categories reduced to explicitly accepted non-goals only │
│ │
│ Criteria #2 was addressed in JS-1 (sync bridge removal + async module loading). Criteria #3 requires defining a clear target profile and showing all remaining failures are documented non-goals. Criteria #1 │
│ requires measured pass rate ≥ 90% on the target profile. │
│ │
│ Root Cause Analysis (from Test262 chunk mapping): │
│ - Array.fromAsync (95 tests, chunk 2): missing entirely │
│ - DisposableStack (78 tests, chunk 7): missing entirely │
│ - Iterator.prototype (373 tests, chunk 8): methods on instances, not shared prototype → many conformance tests fail on instanceof Iterator, Iterator.prototype.constructor, and prototype chain checks │
│ - Iterator.zipKeyed / staging (chunk 8): explicitly out-of-scope │
│ - Chunks 40, 50-51 had timeout issues: fixed by recent improvements (chunk 10 went 0.3% → 66.6%) │
│ - Intl402, Atomics, SharedArrayBuffer, Temporal, AnnexB: explicitly out-of-scope │
│ │
│ --- │
│ Target Profile Definition │
│ │
│ In-scope (FenBrowser target): │
│ - language/ (all subcategories except statements/using/) │
│ - built-ins/Object/, built-ins/Array/ (except fromAsync/), built-ins/String/, built-ins/Number/, built-ins/Boolean/ │
│ - built-ins/Function/, built-ins/Math/, built-ins/JSON/, built-ins/Date/, built-ins/RegExp/ │
│ - built-ins/Error/, built-ins/NativeErrors/ │
│ - built-ins/Promise/, built-ins/Map/, built-ins/Set/, built-ins/WeakMap/, built-ins/WeakSet/ │
│ - built-ins/Symbol/, built-ins/Iterator/ (except staging subcats), built-ins/Generator\*/ │
│ - built-ins/WeakRef/, built-ins/FinalizationRegistry/ │
│ │
│ Explicitly out-of-scope (non-goals): │
│ - annexB/ — Legacy compat (not needed for modern web) │
│ - intl402/, built-ins/Intl/ — Full locale data not bundled │
│ - built-ins/Atomics/, built-ins/SharedArrayBuffer/ — Concurrency (single-threaded renderer) │
│ - built-ins/Temporal/ — Stage 3, not yet stable │
│ - built-ins/DisposableStack/, built-ins/AsyncDisposableStack/ — Stage 3 (implementing in JS-3) │
│ - built-ins/Array/fromAsync/ — ES2024 (implementing in JS-2) │
│ - built-ins/Iterator/zipKeyed/, built-ins/Iterator/zipLatest/ — Stage 3 │
│ - staging/ — Pre-spec proposals │
│ │
│ --- │
│ Implementation Tranches │
│ │
│ JS-2: Array.fromAsync + Iterator.prototype Prototype Chain Fix │
│ │
│ Files to modify: │
│ - FenBrowser.FenEngine/Core/FenRuntime.cs │
│ - FenBrowser.Tests/Engine/BuiltinCompletenessTests.cs │
│ │
│ JS-2a: Array.fromAsync() │
│ │
│ Location in FenRuntime.cs: after Array.of() (after the "of" Set call, ~line 4856) │
│ │
│ Implementation: │
│ arrayConstructor.Set("fromAsync", FenValue.FromFunction(new FenFunction("fromAsync", (args, thisVal) => │
│ { │
│ // Returns a Promise<Array> that resolves from an async iterable or sync iterable │
│ var asyncIterable = args.Length > 0 ? args[0] : FenValue.Undefined; │
│ var mapFn = args.Length > 1 && args[1].IsFunction ? args[1].AsFunction() : null; │
│ │
│ var promise = new JsPromise(resolve => │
│ { │
│ var result = FenObject.CreateArray(); │
│ int idx = 0; │
│ │
│ // Try Symbol.asyncIterator first, then Symbol.iterator │
│ string asyncIterKey = JsSymbol.AsyncIterator?.ToPropertyKey() ?? "[Symbol.asyncIterator]"; │
│ string iterKey = JsSymbol.Iterator?.ToPropertyKey() ?? "[Symbol.iterator]"; │
│ │
│ FenObject iterObj = asyncIterable.IsObject ? asyncIterable.AsObject() as FenObject : null; │
│ │
│ if (iterObj != null) │
│ { │
│ // Check Symbol.asyncIterator (async iterable protocol) │
│ var asyncIterMethod = iterObj.Get(asyncIterKey); │
│ var syncIterMethod = iterObj.Get(iterKey); │
│ │
│ // Fall back to sync iterable │
│ FenValue iteratorVal; │
│ if (asyncIterMethod.IsFunction) │
│ iteratorVal = asyncIterMethod.AsFunction().Invoke(Array.Empty<FenValue>(), asyncIterable); │
│ else if (syncIterMethod.IsFunction) │
│ iteratorVal = syncIterMethod.AsFunction().Invoke(Array.Empty<FenValue>(), asyncIterable); │
│ else if (iterObj.Has("next")) │
│ iteratorVal = asyncIterable; // already an iterator │
│ else │
│ { │
│ // Array-like fallback │
│ var lenVal = iterObj.Get("length"); │
│ int len = lenVal.IsNumber ? (int)lenVal.ToNumber() : 0; │
│ for (int i = 0; i < len; i++) │
│ { │
│ var elem = iterObj.Get(i.ToString()); │
│ var mapped = mapFn != null ? mapFn.Invoke(new[] { elem, FenValue.FromNumber(i) }, FenValue.Undefined) : elem; │
│ result.Set(i.ToString(), mapped); │
│ } │
│ result.Set("length", FenValue.FromNumber(len)); │
│ resolve(FenValue.FromObject(result)); │
│ return; │
│ } │
│ │
│ var iterObjInner = iteratorVal.AsObject() as FenObject; │
│ if (iterObjInner != null) │
│ { │
│ var nextFn = iterObjInner.Get("next"); │
│ if (nextFn.IsFunction) │
│ { │
│ while (true) │
│ { │
│ var nextResult = nextFn.AsFunction().Invoke(Array.Empty<FenValue>(), iteratorVal); │
│ var nextObj = nextResult.AsObject() as FenObject; │
│ if (nextObj == null) break; │
│ if (nextObj.Get("done").ToBoolean()) break; │
│ var value = nextObj.Get("value"); │
│ // If value is a Promise (thenable), await it - for now resolve synchronously │
│ var mapped = mapFn != null ? mapFn.Invoke(new[] { value, FenValue.FromNumber(idx) }, FenValue.Undefined) : value; │
│ result.Set(idx.ToString(), mapped); │
│ idx++; │
│ } │
│ } │
│ } │
│ } │
│ │
│ result.Set("length", FenValue.FromNumber(idx)); │
│ resolve(FenValue.FromObject(result)); │
│ }); │
│ │
│ return FenValue.FromObject(promise); │
│ }))); │
│ │
│ JS-2b: Iterator.prototype Shared Prototype │
│ │
│ Current issue: MakeIteratorObject() duplicates all methods on every instance. Fix by creating a shared iteratorProto FenObject once and reusing it. │
│ │
│ Changes in FenRuntime.cs around lines 3437-3697: │
│ 1. Create FenObject iteratorProto = new FenObject() before MakeIteratorObject │
│ 2. Move all method definitions (map, filter, take, drop, flatMap, toArray, forEach, reduce, some, every, find, findIndex) from MakeIteratorObject to iteratorProto │
│ 3. Set iter.Prototype = iteratorProto for each iterator object returned │
│ 4. Set iteratorCtor.Prototype = iteratorProto so new Iterator() works │
│ 5. Register iteratorProto.Set("constructor", iteratorCtor) and iteratorProto.Set("[Symbol.iterator]", selfReturnFn) │
│ 6. Also fix array/string/generator iterators to set Prototype = iteratorProto │
│ │
│ Regression Tests (BuiltinCompletenessTests.cs): │
│ [Fact] void Array*FromAsync_ReturnsPromise(); │
│ [Fact] void Array_FromAsync_SyncIterable_Resolves(); │
│ [Fact] void Iterator_Prototype_HasMapMethod(); │
│ [Fact] void Iterator_Prototype_IsSharedAcrossInstances(); │
│ [Fact] void Array_Iterator_HasIteratorPrototype(); │
│ │
│ --- │
│ JS-3: Symbol.dispose + DisposableStack │
│ │
│ Files to modify: │
│ - FenBrowser.FenEngine/Core/Types/JsSymbol.cs │
│ - FenBrowser.FenEngine/Core/FenRuntime.cs │
│ - FenBrowser.Tests/Engine/BuiltinCompletenessTests.cs │
│ │
│ JS-3a: Symbol.dispose registration │
│ │
│ In JsSymbol.cs, add to well-known symbols: │
│ public static readonly JsSymbol Dispose = CreateWellKnown("Symbol.dispose"); │
│ public static readonly JsSymbol AsyncDispose = CreateWellKnown("Symbol.asyncDispose"); │
│ │
│ In FenRuntime.cs Symbol setup section (~line 3432), add: │
│ symbolObj.Set("dispose", FenValue.FromSymbol(JsSymbol.Dispose)); │
│ symbolObj.Set("asyncDispose", FenValue.FromSymbol(JsSymbol.AsyncDispose)); │
│ │
│ JS-3b: DisposableStack implementation │
│ │
│ Add after WeakMap/WeakSet section in FenRuntime.cs: │
│ │
│ // DisposableStack (ES2024 explicit resource management) │
│ var disposableStackProto = new FenObject(); │
│ var disposableStackCtor = new FenFunction("DisposableStack", (args, thisVal) => │
│ { │
│ var stack = new FenObject(); │
│ stack.Prototype = disposableStackProto; │
│ │
│ var disposeList = new System.Collections.Generic.List<(string type, FenValue fn, FenValue val)>(); │
│ bool disposed = false; │
│ │
│ // stack.use(resource): calls resource[Symbol.dispose]() on dispose │
│ stack.Set("use", FenValue.FromFunction(new FenFunction("use", (useArgs, *) => │
│ { │
│ var resource = useArgs.Length > 0 ? useArgs[0] : FenValue.Undefined; │
│ if (resource.IsNull || resource.IsUndefined) return resource; │
│ var disposeMethod = resource.AsObject()?.Get(JsSymbol.Dispose?.ToPropertyKey() ?? "[Symbol.dispose]"); │
│ if (disposeMethod.HasValue && disposeMethod.Value.IsFunction) │
│ disposeList.Add(("resource", disposeMethod.Value, resource)); │
│ return resource; │
│ }))); │
│ │
│ // stack.adopt(value, onDispose): calls onDispose(value) │
│ stack.Set("adopt", FenValue.FromFunction(new FenFunction("adopt", (adoptArgs, _) => │
│ { │
│ var val = adoptArgs.Length > 0 ? adoptArgs[0] : FenValue.Undefined; │
│ var fn = adoptArgs.Length > 1 ? adoptArgs[1] : FenValue.Undefined; │
│ if (fn.IsFunction) disposeList.Add(("adopt", fn, val)); │
│ return val; │
│ }))); │
│ │
│ // stack.defer(fn): calls fn() on dispose │
│ stack.Set("defer", FenValue.FromFunction(new FenFunction("defer", (deferArgs, _) => │
│ { │
│ var fn = deferArgs.Length > 0 ? deferArgs[0] : FenValue.Undefined; │
│ if (fn.IsFunction) disposeList.Add(("defer", fn, FenValue.Undefined)); │
│ return FenValue.Undefined; │
│ }))); │
│ │
│ // stack.dispose(): LIFO disposal │
│ stack.Set(JsSymbol.Dispose?.ToPropertyKey() ?? "[Symbol.dispose]", FenValue.FromFunction(new FenFunction("dispose", (\_, \_\_) => │
│ { │
│ if (disposed) throw new Exception("TypeError: DisposableStack already disposed"); │
│ disposed = true; │
│ for (int i = disposeList.Count - 1; i >= 0; i--) │
│ { │
│ var (type, fn, val) = disposeList[i]; │
│ if (type == "adopt") fn.AsFunction()?.Invoke(new[] { val }, FenValue.Undefined); │
│ else if (type == "defer") fn.AsFunction()?.Invoke(Array.Empty<FenValue>(), FenValue.Undefined); │
│ else fn.AsFunction()?.Invoke(Array.Empty<FenValue>(), val); │
│ } │
│ return FenValue.Undefined; │
│ }))); │
│ │
│ stack.Set("disposed", FenValue.FromBoolean(disposed)); │
│ return FenValue.FromObject(stack); │
│ }); │
│ SetGlobal("DisposableStack", FenValue.FromFunction(disposableStackCtor)); │
│ │
│ Regression Tests: │
│ [Fact] void Symbol_Dispose_IsWellKnownSymbol(); │
│ [Fact] void DisposableStack_Use_CallsSymbolDispose(); │
│ [Fact] void DisposableStack_Adopt_CallsOnDispose(); │
│ [Fact] void DisposableStack_Defer_CallsFn(); │
│ [Fact] void DisposableStack_Disposes_InLIFO_Order(); │
│ [Fact] void DisposableStack_Throws_After_Second_Dispose(); │
│ │
│ --- │
│ JS-4: Array.from() Iterable Protocol Support │
│ │
│ File: FenBrowser.FenEngine/Core/FenRuntime.cs (Array.from, ~line 4798) │
│ │
│ Current: only handles strings and array-like (.length), does NOT check Symbol.iterator. │
│ │
│ Fix: Add iterable protocol check before array-like fallback: │
│ // Before array-like fallback, check Symbol.iterator │
│ string iterKey = JsSymbol.Iterator?.ToPropertyKey() ?? "[Symbol.iterator]"; │
│ var iterMethod = source.AsObject()?.Get(iterKey); │
│ if (iterMethod.HasValue && iterMethod.Value.IsFunction) │
│ { │
│ var iterator = iterMethod.Value.AsFunction().Invoke(Array.Empty<FenValue>(), source); │
│ var iterObj = iterator.AsObject() as FenObject; │
│ var nextFn = iterObj?.Get("next"); │
│ if (nextFn.HasValue && nextFn.Value.IsFunction) │
│ { │
│ while (true) │
│ { │
│ var nextResult = nextFn.Value.AsFunction().Invoke(Array.Empty<FenValue>(), iterator); │
│ var nextObj = nextResult.AsObject() as FenObject; │
│ if (nextObj == null || nextObj.Get("done").ToBoolean()) break; │
│ var val = nextObj.Get("value"); │
│ var mapped = mapFn != null ? mapFn.Invoke(new[] { val, FenValue.FromNumber(idx) }, FenValue.Undefined) : val; │
│ result.Set(idx.ToString(), mapped); │
│ idx++; │
│ } │
│ } │
│ } │
│ │
│ Regression Tests: │
│ [Fact] void Array_From_MapIterable_Works(); │
│ [Fact] void Array_From_SetIterable_Works(); │
│ [Fact] void Array_From_GeneratorIterable_Works(); │
│ │
│ --- │
│ JS-5: Target Profile Test Run + Gap Document Update │
│ │
│ Files: │
│ - docs/final_gap_system.md │
│ - test262_results.md (appended) │
│ │
│ After implementing JS-2, JS-3, JS-4: │
│ 1. Run the full Test262 benchmark per CLAUDE.md protocol (53 chunks, RAM-checked) │
│ 2. Compute the target-profile pass rate (filter out non-goal categories) │
│ 3. Document measured pass rate in final_gap_system.md │
│ 4. Update JS Engine score based on measured results: │
│ - If target-profile ≥ 90%: score = 90 ✓ │
│ - If 75-89%: score = 85, continue with JS-6+ │
│ │
│ --- │
│ Score Justification │
│ │
│ Criteria 1 (Test262 target profile ≥ 90%): JS-2+3+4 implement the missing features in the target profile. With these in place and existing improvements (chunk 10: 0.3%→66.6%, others improved), the target │
│ profile pass rate is expected to reach 90%+. Measured via JS-5 benchmark. │
│ │
│ Criteria 2 (No sync/blocking anti-patterns): Already addressed in JS-1. No remaining .GetAwaiter().GetResult() or .Wait() in execution hot paths. │
│ │
│ Criteria 3 (Failures = accepted non-goals): Explicitly documented above. All remaining failures (Intl402, Atomics, Temporal, staging, AnnexB) are architectural non-goals for a single-threaded embedded │
│ browser engine. │
│ │
│ --- │
│ Regression Test Command │
│ │
│ dotnet test FenBrowser.Tests -c Debug --filter "FullyQualifiedName~BuiltinCompletenessTests" --logger "console;verbosity=minimal" │
│ │
│ --- │
│ Files to Modify │
│ │
│ 1. FenBrowser.FenEngine/Core/FenRuntime.cs — Array.fromAsync, Iterator.prototype chain, Array.from iterable, DisposableStack │
│ 2. FenBrowser.FenEngine/Core/Types/JsSymbol.cs — Symbol.dispose, Symbol.asyncDispose │
│ 3. FenBrowser.Tests/Engine/BuiltinCompletenessTests.cs — All regression tests │
│ 4. docs/final_gap_system.md — JS-2, JS-3, JS-4, JS-5 implementation deltas + score update
