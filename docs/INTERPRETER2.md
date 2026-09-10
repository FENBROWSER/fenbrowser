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
receiver, callee and context record per activation. Nothing above the stack top
is traced, which is why a popped window needs no clearing and a pushed one does.

## The gate

`FrameLayout` decides, once per `BytecodeFunction` and cached on it, whether a
body can be represented that way. **The decision is total**: every opcode in the
body must be one the loop implements, and there is deliberately no mid-body
bailout. A loop that can abandon a half-executed frame has to rebuild the old
loop's state out of its own, and that reconstruction is where an engine of this
shape grows its subtlest bugs.

Refused, and run on the old loop unchanged: generators, async bodies, class
constructors, `eval` code, rest parameters, `with`, direct `eval`,
function-level `let`/`const`, and any body that reaches for `super` or
`new.target`. As of the last measurement that is **2.5% of the corpus**, of
which 1.3 points is the bodies that suspend.

Methods are not refused, though the note above said they were for a long time
and the numbers were read accordingly. A method differs from an ordinary
function in having a `[[HomeObject]]` and no `[[Construct]]`, and neither shows
up in the frame - what a home object is *for* is `super`, and the opcode gate
refuses that on its own.

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

So the question is not "does this body make a closure" but "which of its
variables could a closure it makes read". Those - and only those - move out of
the window into a record the closures share; every other variable stays a
register. That is what V8 and SpiderMonkey call **context allocation**, and it
costs one environment record per call to a body that is captured from, which is
what the old loop paid on every call to every body. A body nothing captures from
gets no record at all and is handed the environment it closed over itself.

The record is a `FunctionEnvironmentRecord`, not a plain declarative one, because
it is the call's *variable* environment rather than a block inside it, and code
walking the chain asks which it is - a direct `eval` declaring `var x` looks
outwards for the nearest variable environment and treats every declarative record
it passes as a block. An arrow is the exception: its record must be one that
`this` resolves straight through, so it gets a plain declarative one.

`this`, `arguments`, `new.target` and `super` are the same question about the
bindings that are not identifiers. A nested arrow that reads `this` makes the
enclosing body keep a record so there is something to find it on, and so does one
that reads `arguments` when the enclosing body has its own - this engine builds
the arguments object as a snapshot rather than as an alias of the parameter
bindings, so a parameter can still be a register in a body that has one.
`new.target` and `super` from an enclosing scope are still refused.

## Where a free identifier lives

Reading a variable a function does not declare was the most expensive ordinary
operation in the engine: **107ns**, against 27ns for a property read on an object
already in a register. The cost is not the walk - on a real page the chain
averages 1.55 links - it is that each link is a string-keyed dictionary lookup,
and the name has to be recovered from the slot index before any of it can begin.

Lexical scoping is static, so the answer does not move: a given identifier in a
given function resolves the same number of links out, in the same kind of record,
on every call. `FreeSlotSite` caches that per (function, slot) in two shapes:

- a binding in a record that numbers its variables by slot, where the cached
  index makes the read an array access; and
- a property of the global object, where the shape-guarded load cache the engine
  already uses for `o.x` does the work.

Neither is trusted. The slot form re-checks that the record at the cached depth
still numbers slots for the same function; the global form checks a lexical
version counter on the global record - so a later top-level `let` shadowing the
property invalidates it - and then runs the shape guard. A stale entry misses and
takes the walk; it never answers. The bodies that could change a chain's shape,
`with` and direct `eval`, are refused by the layout long before this.

The result is **+8ns** over the same read via a local, and it drags everything
else down with it, because reading a global was hiding inside most other
operations: the callee's name in a call, `Math` before `Math.floor`, the array
before `arr[i]`.

## try, catch and block scopes

Open `try` entries live as (catch ip, finally ip) pairs in one array shared by
every live frame, each frame recording where its own start and how many are open,
so entering a `try` is two array writes and a counter. Unwinding is the same
operation a return is: cut the frame stack and the value stack back to the frame
that carries on. The thrown value goes into register 0 of that frame, where the
compiler's catch block reads it, and which is already traced.

Every throw takes one path. A `throw` raises a CLR exception rather than routing
itself, so it arrives where an exception from a getter three frames down or from
the old loop arrives, and the dispatch carries no exception handling at all. The
handler region sits around the outermost entry rather than around the dispatch
loop, which is worth about 5% of it.

A block scope is a slot in the window rather than a record, when the block cannot
be told apart from a straight-line assignment. `EnterScope` carries the slot it
introduces, whether it is a `const`, and whether it starts initialised, so the
layout can check that the scopes nest, that no two nested scopes share a slot,
that every instruction naming the slot falls inside the block, and that the
binding is initialised before it can be read. That last one stands in for the
temporal dead zone, which a register cannot represent - and it is exactly the
shape the compiler emits for a catch binding.

## Guard rails

An instruction budget, a wall-clock deadline and an embedder interrupt are
sampled every 4096 dispatched instructions rather than tested on every one. The
countdown lives on the loop, not in the dispatch's locals, and is carried across
frames and re-entries - a native builtin calling a short JavaScript callback (an
array predicate, a proxy trap, a sort comparator) re-enters the loop for every
call, and a per-entry countdown would restart at the full interval each time and
never fire. Call depth is bounded by `MaxCallDepth` exactly as on the old loop,
the value stack by `Interp2Options.MaxValueStackSlots`, and one frame's window by
`MaxFrameWindow`; each raises a catchable RangeError rather than growing.

Every host call in the dispatch re-reads the value stack afterwards. A property
read can run a getter, an operator can run a `valueOf`, a delegated call runs
whatever it likes, and any of them can re-enter deeply enough to grow the stack -
which replaces the array the loop was holding in a local.

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

`FEN_JS_INTERP2_LOG=1` prints coverage, the bailout table ranked both by bodies
and by the calls those bodies cost, and what the property caches could not answer - named misses by where the name actually
lived, and `o[k]` misses by receiver and key together, because that pair is what
a new cache form would have to guard. It works from the JS shell and from the
test262 runner. The
runner is the honest measurement - the benchmarks all reach 100% eligibility
because each was written to isolate one cost and they avoid what is hard.

## Where it stands

Measured on `test/language` (118k function bodies, 23,730 tests):

| | eligible functions | calls staying in the loop |
|---|---|---|
| first working version | 7.6% | 2.0% |
| + property writes, `typeof`, `throw`, `new` | 29.5% | 22.8% |
| + closures that capture nothing | 44.6% | — |
| + context allocation for the ones that do | 72.2% | — |
| + arrow functions | 75.9% | 9.8% |
| + `arguments` | 77.5% | 21.9% |
| + `try`/`catch`/`finally` and block scopes | 77.5% | 21.9% |
| + `instanceof`, `in`, `delete` | 83.8% | 27.3% |
| + `for-in`, `for-of` | 85.8% | 28.9% |
| + a block's slot shared with its siblings | 86.9% | — |
| + methods | 90.1% | — |
| + private fields | **97.5%** | **30.4%** |

Speed, against the old loop **with its JIT enabled**:

| | old loop | new loop |
|---|---|---|
| empty call | 438ns | **136ns** |
| + 2 arguments | +146ns | +31ns |
| + 110 declared locals | +180ns | +38ns |
| + a `Math.floor` call | +280ns | +108ns |
| free-variable read | +107ns | **+8ns** |
| array element read | +218ns | +123ns |
| whole call ladder | 1048ns | **324ns** |
| `b_bundle_calls` (bundle-shaped mix) | 670ns/call | **245ns/call** |

**Where the new loop loses**: tight single-frame loops, because there the old
loop's JIT compiles the body and the new loop has no tier-up.
`b_property_read`'s bare loop is 41ns old against 80ns new. The new loop wins
wherever calls or free variables are involved and loses where a JIT-compiled
loop runs uninterrupted - to the compiler, not to the old interpreter, which it
beats on every benchmark measured here (see "Against Chrome" below).

**Correctness**: `FenBrowser.Js.Tests` 3423 passing on both loops, including
`Interpreter2ParityTests` - 34 programs run on both loops in the same process and
compared against each other and against the expected answer. Identical test262
result sets on both for the whole of `test/language` (22663/23730),
`language/statements/{class,try,for-in,for-of,function}`,
`language/arguments-object`, `language/global-code`,
`language/expressions/{instanceof,in,delete,object,assignment}` and
`built-ins/{Object,Array,String,Promise,Function,Proxy,Reflect,Map,Set,Date,
Math,Number,JSON,Symbol,TypedArray,eval}`.

## Against Chrome

Everything above compares this engine to its own previous build. These three
benchmarks run unmodified in the FenJS shell, in node and in a browser, and each
carries a check value so a run that did different work shows up as wrong rather
than as fast - all three engines returned identical checks. Chrome
headless and node agreed to within 20%, so the column is V8 either way.
Repeated twice per engine; the numbers below moved by under 4% between runs,
unlike anything measured on the live page.

| | Chrome (V8) | old loop | new loop | new vs old | new vs V8 |
|---|---|---|---|---|---|
| `x_simple_loop` - tight integer loop | 3.6 ns/iter | 52.0 | **110.8** | **2.1x slower** | 31x |
| `x_medium_calls` - allocate, call a method, read a free variable | 8.5 ns/iter | 2231 | **1575** | 1.42x faster | 185x |
| `x_hard_mixed` - strings, arrays, a dictionary, a sort comparator | 10.9 ms/round | 124.9 | **111.3** | 1.12x faster | 10.2x |

That table compares what actually ships: the old loop **with its JIT**, against
the new loop, which never tiers up. It is the right comparison for a user and
the wrong one for the question "is the new loop faster". Turning the JIT off on
both sides asks that one instead:

| | old loop, no JIT | new loop | new vs old |
|---|---|---|---|
| `x_simple_loop` | 382.4 ns/iter | **111.0** | **3.4x faster** |
| `x_medium_calls` | 3376.5 ns/iter | **1572.0** | **2.1x faster** |
| `x_hard_mixed` | 186.1 ms/round | **111.8** | **1.7x faster** |

So the new loop beats the old interpreter on every one of them, by 1.7x to
3.4x. What it loses to is the old loop's **JIT**, which on a tight integer loop
is worth 7.4x (382ns to 51ns) and is the whole of the 2.1x deficit in the first
table. The same flag confirms the new loop never uses the JIT at all: 110.5ns
against 111.0ns, 1592 against 1572, 114.4 against 111.8 - within noise of
itself.

The three benchmarks say three different things, and the middle one says the
most.

**Simple** is the known loss, and it is a loss to a compiler rather than to an
interpreter: a single frame running a hot loop is what a JIT is for and what
this loop has no tier-up for. Against the old *interpreter* it is 3.4x faster.

**Hard** is the honest whole-workload number - 1.12x, because most of its time
is inside `split`, `sort` and string building, which are natives that neither
loop touches. A page made of library calls will move about this much.

**Medium** is where the new loop should have shone and it only manages 1.42x,
so it was decomposed. A bare iteration is ~177ns, `o.x` adds nothing,
`o.len2()` ~297ns, an `{x, y}` literal ~337ns - and **`new Point()` ~1050ns,
near-identical on both loops**. Two thirds of that benchmark is construction,
which this work never touched: the frame, the call and the free-variable read
all got faster underneath a cost that did not move. It is the largest single
gap to V8 in ordinary object-oriented JavaScript, and nothing in the coverage
or caching work was ever going to close it.

## What the live page said

`FenBrowser.Tooling.exe captcha https://www.google.com/recaptcha/api2/demo
35000 25000`, two runs per configuration, `slowestJob` normalised per million
instructions because raw milliseconds are too noisy to compare:

| | old loop | new loop |
|---|---|---|
| blocking job | 6396ms | **4302ms** |
| per instruction | 79.9 ms/Minstr | **41.3 ms/Minstr** |
| microtask total | 7926ms | 5378ms |
| heap frames, anchor realm | 1,619,752 | **51,077** |
| register bytes, anchor realm | 3727MB | **87MB** |
| widget's own give-up point | 18419ms | 15441ms |

**Coverage on that bundle is 90.4%** of 4174 function bodies, and its bailout
table is nothing like test262's. `NotOrdinaryFunction` is **zero** - the bundle
contains no generators and no async bodies at all, so the item that is 12.9% of
test262 and all of what remains there is worth nothing here. What blocked it was
named function expressions (403 bodies) and regex literals (146), both since
implemented. What remains is block scopes, at 378.

Of 6.80M calls in one run: 2.06M stay in the loop, 1.02M go to a JavaScript body
the loop declined, and **3.72M go to a native** - 55% of every call the page
makes, which is not a coverage problem at all.

## What measured zero, and why it is recorded

Three changes were built on this workload and did not move it. They are listed
because the reason each one failed is more useful than the change would have
been.

- **Specialising variable access at layout time.** `LoadVar` is 29% of every
  instruction the page executes, so each one was rewritten into a form carrying
  the window index outright - no slot-table read, no branch. Ten opcodes and a
  rewrite pass, and the page did not move. Per-opcode self time then said why:
  `LoadConst` and `Move`, the two simplest instructions there are, cost the same
  as `LoadVar` to within 2ns. The count was 29%; the cost was not. Reverted.
- **A shared (shape, name) table for megamorphic sites.** A site holds four
  programs and then gives up, answering nothing; the table answered those.
  It served 0.2% of misses, because the sites were not megamorphic. Reverted.
- **Calling natives straight from the window**, skipping a second resolution of
  the callee and the pinning of arguments the collector can already see through
  the window. Measured zero. Kept anyway - it removes work that is provably
  redundant on this loop, and the path it replaces did the same lookup twice.
- **Checking the guards at back edges and calls** instead of on every
  instruction. The browser always sets an instruction budget, so the check ran on
  all 104M of them; moving it to the ~10% of instructions that can begin
  unbounded work should have been free. It measured slightly *worse* and added
  sixteen call sites. Reverted, and the guarantee it was moving around now has
  tests: `Interpreter2GuardTests`.

Four attempts, three reverted, and between them they moved the page by nothing.
That is the finding, not a failure of the attempts: after the frame, call,
variable and allocation costs were taken out, **the job's remaining 4.3 seconds
is 104M instructions at ~41ns with no dominant term left in it.** The two
nameable items still in the property-miss table - string-primitive receivers and
prototype loads - price out at 30-60ms each. Nothing on this workload is worth
more than about 100ms any more.

The lesson is the one the perf handoff already states and this work had to learn
again: **a count is not a cost.** `FEN_FENJS_OPTIME=1` now works on this loop
too, and the property-miss classification under `FEN_JS_INTERP2_LOG=1` names
what a cache could not answer instead of leaving it to be guessed at.

## Property caching, which is shared with the old loop

Two kinds of read the cache could not describe, both found by classifying where
it missed rather than by guessing:

- **A dense array's `length`** is the vector's count, synthesised on demand and
  in no shape. 1.76M of 4.16M missed reads on the page were that one name.
- **Anything on the prototype.** A method call on a class instance, an array or
  an object literal reads the callee off the prototype; the cache refused
  everything that was not the receiver's own. 940k more.

Both now attach, and the named-property hit rate on reCAPTCHA's bundle went
**36.0% to 73.2%**. Prototype reads cost what own reads cost - 111ns against
111ns on the new loop.

The prototype form guards three things, because there are three ways the answer
can move: the receiver's shape (an own property appearing that shadows the
name), the receiver's `[[Prototype]]` (the chain being reassigned - shapes here
do not encode it), and the holder's shape (the property moving on the prototype
itself). It refuses a receiver that has the name at all, however it has it: an
own *accessor* shadows the prototype and must be called, which four tests in
`built-ins/Array/prototype` say plainly.

An exotic object answers own-property lookups from somewhere the shape does not
describe, so a shape guard cannot see one appear - but that is true per name,
not per object. An array can grow a `2` and a `length` outside its shape and can
never grow a `push`. `JsObject.MayGainOwnPropertyOutsideShape` asks per name,
which is the difference between caching 2% of a page's prototype loads and 83%.

## A receiver that is not an object

A string is not an object. It has no shape, so every read off one missed every
site and fell through the whole `[[Get]]` switch - and on this page those were
**80% of everything that still missed** after the object forms went in. Two
names: `length` (682k) and `charCodeAt` (677k).

What makes them cacheable is that a string primitive's own properties are
exactly `length` and its integer indices, and that set can never grow. Once the
key is neither, there is no receiver state left to guard - only which realm's
`%String.prototype%` answered and whether that object still holds the name in
the same slot. So the two forms guard no receiver shape at all: one yields the
value's length without flattening it, the other a prototype slot behind those
two guards. A key beginning with a digit is refused outright, which is wider
than the canonical-index rule and errs the safe way.

The realm guard is the part a string cannot supply for itself. An object
receiver discriminates realms by its own prototype handle; a string in one frame
is indistinguishable from a string in another, so a site warmed in the first
would otherwise hand the second the first one's methods. Resolving that
prototype was also two dictionary lookups through `globalThis.String` on every
string method read - and it is an intrinsic, fixed for the realm's life
(7.1.18), so rebinding the global cannot move it. It is now resolved once and
kept, which is both the fix and what the guard compares against.

Named reads on the bundle went **73.2% to 94.6% cached**; string-receiver misses
went from 1,403,537 to 579. `s.charCodeAt` costs 288ns before and 146ns after on
the new loop, 320ns to 232ns on the old; `b_string`, the obfuscated-bundle decode
loop, 1265ms to 1060ms. The blocking job moved 4740ms to 4593ms across two runs
each - consistent in direction, but that is a 3% move against a 130ms spread
within each pair, so treat the magnitude as approximate.

**The `length` form measured nothing** - 119ns before, 120ns after. It was
already answered without flattening, so the cache only replaces a miss that was
cheap. It stays because without it every `s.length` read would now attempt an
attach and be refused, which is work the old code did not do.

## An array that is not full

Two thirds of the page's missed `o[k]` reads - 1,985,174 of 3,044,000 - were
indexed reads on an array that had stopped keeping its elements in the dense
vector. The count said the cost was there; only the reason said whether it was
fixable, so `Materialise` records that too. **10,736 arrays** account for all of
it, and they named it: `LengthUnrepresentable=7505`, `WritePastEnd=3231`,
`UnrepresentableDescriptor=43`.

The first is `new Array(n)` and `a.length = n`. The vector could not hold a
length above its element count, so the array was materialised **before it held
anything**, and every read of it afterwards built a string from the index and
looked it up in the property table.

It does not have to describe every index below the length. The vector holds
`0..Count-1`, hole-free, and everything from the count up to the length is a
trailing hole - **absent, not `undefined`**, which is the whole of the
difference: it reads through to the prototype, and `Object.keys`, `in`, `delete`
and enumeration all have to keep it invisible. An interior hole is still what it
cannot represent, so a write past the end gives the vector up as before. The
length becomes its own field rather than the element count, which also fixes a
delete: `var a = [1,2,3]; delete a[2]` reported `a.length` as 2, and deleting
never shortens an array.

| | before | after |
|---|---|---|
| `o[k]` reads cached | 77.3% | **91.9%** |
| indexed reads on a non-dense array | 1,985,174 | **22,359** |
| blocking job | 4593ms | **3644ms** |
| read a pre-sized array | 194ns | **134ns** (a literal costs 132ns) |
| build a pre-sized array | 289ns | **130ns** |

Arrays that give the vector up afterwards are `WritePastEnd=3237` and
`UnrepresentableDescriptor=4408` - the second having been 43 before, because
those arrays used to die at the length and now live long enough to reach an
`Object.freeze`, `Object.seal` or a partial `Object.defineProperty`, which are
the three things that produce it. Those are not fixable: a sealed array's
elements are non-configurable, which is exactly what the vector promises they
are not.

Which is 23% off the blocking job for the two changes together - 4740ms to
3644ms - and unlike the string cache the gap is far larger than the spread
between runs.

## Three programs that described the wrong thing

The `o[k]` table said 87.7% of what still missed was one name, `push`, at
megamorphic sites. Profiling those sites said the rest: each read **one key off
one shape**. Nothing about that traffic is polymorphic, so the sites should
never have given up - which pointed at the programs, and there were three
defects in them, two of them wrong answers rather than slow ones.

- **The array-length program had no key guard at an `o[k]` site.** It is the
  program that guards no shape, because the length is not in one; at a site
  reading a literal `.length` the name is in the bytecode and that is enough.
  At a varying-key site it is not, and once a site had read `a["length"]` it
  answered the element count for every other name: **`a["0"]` returned 3**.
- **The prototype program was recognised by an exact sequence of five ops.** A
  varying-key site guards the key too, which makes six, so those programs
  matched no form, ran as unrecognised, and answered nothing. The site attached
  a fresh dead one on every miss and turned megamorphic in five. That is all
  941,330 `push` reads.
- **A compiled body read a prototype program's slot off the receiver.** The
  baseline compiler writes the guards out inline: check the shape the program
  reports, read the slot the program reports off the receiver. A prototype
  program reports the receiver's shape and a slot on the *holder*, so `p.m`
  returned whichever of `p`'s own slots shared that index - correct until the
  body was hot enough to compile, wrong after. The interpreter runs the same
  program correctly, which is why no test saw it.

`o[k]` reads on the bundle go **77.3% to 99.0%** cached across this and the
array work, and misses at a megamorphic site 944,251 to 4,292. The blocking job
does not move: interleaved run-for-run against the same build it is 4052ms
against 4137ms over three pairs with the pairwise deltas changing sign. **A
million prototype-chain walks are worth less than this harness can see**, which
is the same lesson as the four changes above that measured zero, arriving from
the other direction - this time the count was real and the cost still was not.

## Coverage, and what a bailout table is for

The bailout table ranks *bodies*, and that turned out to be the wrong queue.
Two reasons stood behind almost all of the page's declined bodies:

- **332 bodies: a block-scoped slot named from outside its block.** Nothing was
  escaping. The compiler numbers a body's bindings in one space and reuses a
  number across blocks that cannot both be open, so two sibling `{ let x }`
  blocks share one - and checking each block on its own failed on the other
  block's instructions every time. Checking the slot against every block that
  declares it took eligibility from **89.0% to 96.8%**.
- It also moved the delegated calls by **0.7%**. Those 332 bodies are barely
  entered. So the table now counts calls as well, and the two disagree
  completely: 103 bodies against 2,913 calls for one reason, **33 bodies against
  1,009,494 calls** for another.

Those 33 are `PreResolveVar` on a name the body does not declare - `x++` on an
outer variable or a global. The resolution genuinely has to be carried, because
`ToNumeric` runs between the two instructions and a `valueOf` there can delete
the property the name resolved to; walking again then throws in strict code
where the spec says the write lands. Held on the interpreter and matched by name
at the store, which is where and how the old loop holds it.

| | before | after |
|---|---|---|
| eligible functions | 89.0% | **97.5%** |
| calls staying in the loop | 30.4% | **40.5%** |
| calls to a declined body | 1,012,408 | **2,761** |

The A/B gate earned its keep here: admitting those bodies broke two tests, a
named function expression whose own name an arrow assigns. Inside the body the
immutable binding is enforced at the store, which knows the slot; from a closure
it resolves through a record whose slots are all mutable, so the write landed
where the spec drops it. That body is refused rather than given a home that
cannot say no.

**And the blocking job does not resolve either way.** Interleaved run-for-run
the two builds are 3984ms and 4230ms, pairwise deltas changing sign, against a
within-build spread of 1100ms. Two of three pairs say the coverage win cost
time, which would be the missing tier-up - those 33 bodies take 30,000 calls
each and were being JIT-compiled - but disabling the JIT on both did not
separate them either. The honest statement is that this machine could not see a
difference of this size on the day, and the counts above are what the change is
known to have done.

## How to measure this loop, after getting it wrong

The timings in this document above the coverage section were taken run-for-run
in one sitting. The ones in this section could not be, and the reason is worth
recording: **the machine drifted 450ms between the morning and the afternoon**,
which is larger than every change measured that day. Comparing a number taken
now against one written down earlier says nothing.

So: build both engines, keep both DLLs, and alternate them run by run -
before, after, before, after. Report the pairwise deltas and their signs, not
the means. If the deltas change sign, the answer is "not resolvable", and that
is a real answer: it bounds the change from above, which is often what you
needed.

## What is left on the page, which is not the loop

With 97.5% of bodies eligible and 40.5% of calls staying in the loop, the
anchor realm's blocking job is ~3.6s of ~104M instructions, and the terms in it
are all small. The one that is not the interpreter at all is the collector:
**154 minor collections costing around 1 second**, and that count is identical
in every run of every build measured here - the one number all day that did not
drift.

`FEN_FENJS_GC_NURSERY` exists to split its fixed cost from its variable one.
Quadrupling the budget takes the collections from 154 to 38 and the total from
~979ms to ~758ms, which answers it: solving `154F + VC = 979` against
`38F + VC = 758` puts the fixed cost at **~1.9ms per collection** (~290ms of
root scanning in total) and the rest, **~685ms, proportional to the ~1.26M young
cells themselves**. So a bigger nursery is not the fix - and the budget comment
above `YoungAllocationsPerMinorGc` records why it was made small in the first
place. Allocating less is.

Neither half is worth more than a few hundred milliseconds, which is the shape
of everything left here. **The page's remaining blocker is no longer
performance**: the widget gives up around 15s, the job is 3.6s, the click is
delivered and seen, and the challenge frame's `POST /api2/reload` returns 200 -
but the worker realm runs 779k instructions and never replies, so no token is
ever issued. That is a functional bug, and it is where the demo now stops.

## What is next, in order of measured value

The unimplemented-opcode table is effectively empty: 537 bodies across twenty
opcodes, none over 160. What is left is one thing and then a different kind of
work.

1. **Construction.** `new Point()` costs ~1050ns against ~337ns for the
   equivalent object literal and ~297ns for a method call, and it is the same
   on both loops - so it is the one number in the cross-engine table that this
   work never moved. Ordinary object-oriented code is mostly constructors.
2. **Bodies that suspend** - `Generator=585`, `AsyncGenerator=590`, `Async=310`
   on the language corpus, and still zero on the page. This is now genuinely
   what `NotOrdinaryFunction` means, the name having covered 13,966 ordinary
   methods until the kind was counted instead of the bailout. Suspending means
   copying a window out at a yield or an await and back at the resume, which is
   the one piece of machinery this loop has never needed.
3. **Elements that are not on a dense array**: `TypedArrayIndex` 70k,
   `ObjectIndexKey` 26k, `SparseArrayIndex` 22k. Nothing here is above 0.6% of
   the page's element reads, and the whole table is now 1.0% of them.

   **Tier-up has come off this list.** It was item 1, on the reasoning that the
   loop gives up the JIT on hot single-frame loops. But only 2,761 calls now
   reach a JavaScript body on the old loop at all, so on this workload the JIT
   has nearly nothing left to compile and cannot be what is missing. It belongs
   back on the list when a benchmark, not a page, says a compiled body would
   win.
4. **`super` and the class shape around it** - `SetHomeObject=141`,
   `LoadSuperProperty=45`, `LoadSuperElement=29`, `LoadSuperConstructor=13`,
   plus `ClassConstructor=44` bodies. Small, and the only remaining reason a
   method is turned away.
5. **Function-level `let`/`const`** (254 bodies) needs a hole value distinct
   from `undefined` so the temporal dead zone stays observable.
6. **Block scopes a closure captures** - the case these notes expected to
   matter, measured at **zero bodies** on the page once the bailout was split
   apart. It still needs a fresh record per entry to the block for a corpus that
   uses it.

Three of the four largest items on this list turned out to be misnamed rather
than large: a block scope that escaped nothing, a free-variable resolution that
had somewhere to go, and 13,966 methods filed under generators. Each was found
the same way - by counting the thing the fix would have to change, rather than
the thing the label said.

Two defects in the **old** loop that this work surfaced and did not fix:

- An uncatchable error - the instruction budget, the wall-clock deadline, the
  embedder interrupt - loses that flag when it is routed through a frame's
  handler stack, so `try`/`catch` can swallow it. It is why
  `built-ins/Array/prototype/slice/create-proxied-array-invalid-len` passes there
  and timed out here: both loops spin the proxy trap identically, and only one of
  them could be stopped.
- `ArraySpeciesCreate` does not raise the RangeError that test expects, on either
  loop, so the array methods walk a 2^32-length proxy element by element.
