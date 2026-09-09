# The register-window interpreter (`FenBrowser.Js/Interpreter2/`)

A second execution loop for FenJS, built beside the original rather than in
place of it, selected by `FEN_JS_INTERPRETER=v2`. Off by default.

## Why there are two

`docs/HANDOFF_PERF_2026-09-09.md` measured an empty JavaScript call on the
original dispatch loop at ~400ns and found no single step in it worth more than
about 30ns. The cost is the sequence, not any one part of it:

| what a call does on the old loop |
|---|
| rent a register array from a size-keyed pool |
| rent or allocate a `FunctionEnvironmentRecord` |
| attach slot storage to it |
| bind `this` into it |
| rent an `InterpreterFrame` |
| push that frame onto the GC root stack |
| recurse into a CLR frame behind a `try`/`finally` |
| bind each formal parameter by slot |
| run three declaration-instantiation passes |

Picking those off one at a time does not work, which is why the new loop does
not have them.

## What the new loop does instead

A frame is a slice of one shared `JsValue[]`: bytecode registers in the low
half, the body's declared variables in the high half. Entering a call is a
bounds check, a clear of the window and a bump of two pointers. The callee runs
on the same loop, so **a JavaScript call is a `goto`, not a recursion** - no CLR
frame, no `try`/`finally`, no depth bookkeeping, and a call depth bounded by an
integer rather than by the host's native stack.

The collector sees the whole loop as one linear span (`[0, _stackTop)`) plus one
receiver and callee per activation. Nothing above the stack top is traced, which
is why a popped window needs no clearing and a pushed one does.

## The gate

`FrameLayout` decides, once per `BytecodeFunction` and cached on it, whether a
body can be represented that way. **The decision is total**: every opcode in the
body must be one the loop implements, and there is deliberately no mid-body
bailout. A loop that can abandon a half-executed frame has to rebuild the old
loop's state out of its own, and that reconstruction is where an engine of this
shape grows its subtlest bugs.

Refused, and run on the old loop unchanged: generators, async bodies, arrows,
class constructors, `eval` code, `arguments`, rest parameters, named function
expressions, `with`, direct `eval`, `let`/`const` (no hole value yet),
`try` (no handler stack yet), and any body whose variables a closure could
observe.

## Semantics live in one place

Nothing in `Interp2.cs` reimplements what an operator, coercion or property
access means. Everything beyond a register move goes through the same helper the
old loop calls, via the facade in `BytecodeInterpreter.Interp2Host.cs`. The two
loops share the heap, the builtins, the inline caches and the bytecode format, so
a call crosses between them in either direction at any depth, and they cannot
drift apart on semantics however far the new loop is taken.

## Closures without an environment record

`CaptureAnalysis` is why a body that creates functions can still keep its
variables in registers. A captured environment exists to resolve the names a
closure does not declare itself, and those names are readable off the bytecode: a
nested function's slot table lists every identifier it mentions, its declarations
are its parameters, vars and lexical names, and what is left is what it reaches
outwards for - closed transitively over its own nested functions.

So the question is not "does this body make a closure" but "could a closure it
makes ever read one of its variables". When the answer is no, the closure is
handed the environment the body itself closed over, and every name it does reach
for resolves exactly where it did before. `this`, `arguments`, `new.target` and
`super` are tracked alongside the names as the same question about the bindings
that are not identifiers.

This is the conservative half of what V8 and SpiderMonkey do. They go further:
when a variable *is* captured, only that variable moves to a heap context and the
rest stay in registers. Here one captured variable sends the whole body back to
the old loop.

## Running it

```bash
FEN_JS_INTERPRETER=v2   # select the new loop
FEN_JS_INTERP2_LOG=1    # print coverage and the ranked bailout table

# One test262 slice on both loops, diffed by test name (not by count).
bash scripts/interp2_ab.sh language/expressions/call
bash scripts/interp2_ab.sh language            # the whole language corpus
```

The A/B script exits non-zero when the new loop fails anything the old one
passed, so it can gate a commit. Comparing pass totals is not enough: a loop can
fix one test and break another and score the same.

`FEN_JS_INTERP2_LOG=1` works from the JS shell and from the test262 runner. The
runner is the honest measurement - the benchmarks all reach 100% eligibility
because each was written to isolate one cost and they avoid what is hard.

## Where it stands

Measured on `test/language` (118k function bodies, 23,730 tests):

| | eligible functions | calls staying in the loop |
|---|---|---|
| first working version | 7.6% | 2.0% |
| + property writes, `typeof`, `throw`, `new` | 29.5% | 22.8% |
| + closures that capture nothing | **44.6%** | 7.0%\* |

\* the ratio fell because the number of calls *made from inside* the loop grew
12x; the absolute count staying in the loop rose 1161 → 4161.

Speed, against the old loop **with its JIT enabled**:

| | old loop | new loop |
|---|---|---|
| empty call | 400-438ns | **128-142ns** |
| + 2 arguments | +116-146ns | +32-53ns |
| + 110 declared locals | +188-208ns | +34-48ns |
| whole call ladder | 1073-1100ns | **475-502ns** |
| `b_bundle_calls` (bundle-shaped mix) | 680-705ns/call | **331-353ns/call** |

Against the interpreter alone (`FEN_JIT_DISABLE=1`) the empty call goes
509ns → 137ns.

**Where the new loop loses**: tight single-frame loops, because there the old
loop's JIT compiles the body and the new loop has no tier-up.
`b_property_read`'s bare loop is 41ns old against 80ns new; `b_working_set` is
126ns against 143ns. The new loop wins wherever calls are involved and loses
where a JIT-compiled loop runs uninterrupted.

**Correctness**: `FenBrowser.Js.Tests` 3389 passing on both loops. Identical
test262 result sets on both for the whole of `test/language` (22663/23730), plus
`built-ins/{Object,Promise,JSON}`, `built-ins/String/prototype`,
`built-ins/Array/prototype/{map,filter,reduce,push}` and
`built-ins/Function/prototype/bind`.

## What is next, in order of measured value

1. **Context allocation** - `CapturedVariable` is 32.8k of the 118k bodies, by
   far the largest remaining decline. The captured names are already computed by
   `CaptureAnalysis`; the work is to give those slots a heap record per call and
   leave the rest in registers, rather than sending the whole body back.
2. **`try`/`catch`/`finally`** (`PushHandler`, 5.8k). Needs a per-frame handler
   stack, which the window model has room for. A `catch (e)` binding also needs
   `EnterScope`, so this and item 3 are the same piece of work.
3. **Block scopes and `let`/`const`** (`EnterScope`, `LexicalDeclarations`).
   Needs a hole value distinct from `undefined` so the temporal dead zone stays
   observable, and needs to know which slots belong to which scope - the one
   place where the compiler would have to say more than it does today.
4. **`for-in` / `for-of`** (`EnumerateKeys` 1.8k, `EnumerateValues` 543).
5. **Arrows** (part of `NotOrdinaryFunction`, 19.8k with generators and async).
   An arrow has no `this` of its own, so it needs the enclosing frame to supply
   one - reachable once item 1 exists.
6. **Tier-up**, once coverage is high enough that the loop is on the critical
   path: the loop currently gives up the JIT's win on hot single-frame loops.
