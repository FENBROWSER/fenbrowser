# Handoff — FenJS / FenBrowser performance, 2026-09-09

Paste the "Prompt for the next session" section below into a fresh session. The
rest of this file is the evidence behind it.

---

## Prompt for the next session

> Continue performance work on FenBrowser. Read
> `docs/HANDOFF_PERF_2026-09-09.md` first — it has the measurement rig, the
> numbers, and what has already been ruled out. Do not re-derive it.
>
> **Rule 1: never trust `dotnet-trace` leaf self-time in this engine.** .NET 10
> inlines `Thread.PollGC()` at nearly every safepoint, so unwalkable addresses
> resolve into `PollGCWorker` and it will tell you that is 47% of CPU. Two
> optimisations were built from that reading and both measured *exactly zero*.
> Use `scripts/bench/b_call_ladder.js` and the other ladders instead; use
> `scripts/profile_engine_self.py` for direction only, never as evidence.
>
> **Rule 2: never A/B on the live page.** Identical configs spread ±20%
> (86–107 ms/Minstr). Kill idle build servers first (`dotnet build-server
> shutdown`) — 14 were resident during one session. Use the deterministic
> benches; verify on the page only at the end.
>
> The work queue, in order of measured value, is in "What to do next" below.
> Start with item 1. Build clean, run `FenBrowser.Js.Tests` (3389 passing) plus
> a test262 slice, commit in small human-style chunks with **no AI/co-author
> trailers**, and push each verified unit before starting the next.

---

## What just landed (all pushed to `master`)

| commit | change | measured |
|---|---|---|
| `df97a5e7` | Renderer child no longer awaits `Navigate` in its IPC loop | **google.com first paint 5503ms → 941ms** |
| `f2f97164` | Per-native-call timing + name dictionary behind `FEN_FENJS_NATIVE_STATS` | native call 382ns → ~300ns; bundle-shaped mix 790 → 730 ns/call |
| `c132354f` | `CallArgs` is a 16-byte view over the caller's registers, not a 112-byte snapshot | time-neutral; memory traffic only |
| `a80a3e53`, `8c4951de` | The measurement rig (ladders + engine-self profiler) | — |
| `fec79f8e` | test262 refreshed to 49936/53483 = 93.37% | — |

The first-paint fix is the big one and it is **not** a JS fix. See
"The first-paint bug" below — the same class of bug may exist elsewhere.

---

## The measurement rig (use this, not a profiler)

```bash
# Deterministic engine benches — stable to a few percent.
./FenBrowser.Js.Shell/bin/Release/net10.0/FenBrowser.Js.Shell.exe --file scripts/bench/b_call_ladder.js
./FenBrowser.Js.Shell/bin/Release/net10.0/FenBrowser.Js.Shell.exe --file scripts/bench/b_property_read.js
./FenBrowser.Js.Shell/bin/Release/net10.0/FenBrowser.Js.Shell.exe --file scripts/bench/b_bundle_calls.js
./FenBrowser.Js.Shell/bin/Release/net10.0/FenBrowser.Js.Shell.exe --file scripts/bench/b_working_set.js
./FenBrowser.Js.Shell/bin/Release/net10.0/FenBrowser.Js.Shell.exe --file scripts/bench/b_shape_polymorphism.js
```

Each ladder rung adds exactly one thing to the rung below it, so the difference
between two rungs is that thing's cost and nothing else.

**Page-level paint timing** (this is what the user actually experiences):

```bash
FEN_LOG_PRESET=perf FEN_DIAGNOSTICS_DIR="$(pwd)/logs/run" \
  ./FenBrowser.Host/bin/Release/net10.0/FenBrowser.Host.exe "https://www.google.com"
# then parse logs/fenbrowser_<ts>.jsonl (NOT the _trace_ twin) and diff
# timestampUtc across: NavigationRequested -> "[DOC][INFO] First layout complete"
#                      -> "[DOC][INFO] First paint submitted"
```

- **Brokered (multi-process) is the default and is what the user runs.**
  `FEN_PROCESS_ISOLATION=in-process` switches modes.
- The headless `FenBrowser.Tooling.exe diagnose <url>` command **never paints**.
  Never draw a paint-timing conclusion from it.

**reCAPTCHA blocking job** (the other workload):
```bash
./FenBrowser.Tooling/bin/Release/net10.0/FenBrowser.Tooling.exe \
  captcha "https://www.google.com/recaptcha/api2/demo" 35000 25000
# read the [FenJsEngine] #1 line: slowestJob=<ms>/<instructions>
# normalise as ms per million instructions; raw ms is too noisy to compare
bash scripts/captcha_sample.sh <tag> 9 20   # attaches a sampler to the job window
```

---

## The cost table (everything in L1, so this is compute, not memory)

From `b_call_ladder.js` and `b_property_read.js`:

| operation | marginal cost |
|---|---|
| loop overhead | 42 ns |
| **empty JS call** | **~400 ns** |
| + 2 arguments | +140 ns (~70 ns each) |
| + 110 declared locals in the callee | +190 ns (~1.7 ns/var) |
| **one native call (`Math.floor`)** | **~300 ns** |
| property read, object in a local | +27 ns |
| property write | +24 ns |
| array element read | **+70 ns** |
| **read a free variable** | **+109 ns** |

The reCAPTCHA job runs 1.6M JS calls + 1.5M native calls ≈ **1.5 s of its ~7 s**.

---

## What has been ruled out (do not re-investigate)

- **Layout and paint are not in the reCAPTCHA blocking job.** It is 100% JS.
  (Layout on google.com is ~115 ms of a 10 s load.)
- **CLR GC is not a factor.** `dotnet-counters` reports `gc.pause.time` = **0**,
  and `DOTNET_GCgen0size=20000000` (512 MB gen0) changes nothing.
- **Not memory latency.** `b_working_set.js`: 64 objects → 132 ns, 1M objects →
  145 ns. Flat.
- **Not megamorphic inline caches.** `b_shape_polymorphism.js`: 1 shape → 198 ns,
  64 shapes → 228 ns.
- **Not the boxing of `CallArgs` on the native path**, and **not the `CallArgs`
  struct copy.** Both were the profiler lying; both fixes measured zero. The
  `CallArgs` view was kept anyway because it is simpler and smaller.
- **Not per-call array clearing.** Ablating the `Array.Clear` in
  `ReturnRegisterFile`/`ReturnSlotStorage` is worth ~35 ns of a ~1450 ns wide call.
- **Not `PinReturnValue`, `Invocations++`, `ActiveFrameScope`, or
  `ResolveFunctionOuterEnvironment`.** All ablated individually; all zero. The
  ~400 ns empty call is genuinely distributed across the whole call sequence.

---

## What to do next, in order of measured value

### 1. Scope-chain inline cache — the biggest named JS win (~0.5–0.9 s of the job)

**Where:** `FenBrowser.Js/Interpreter/BytecodeInterpreter.cs`, `LoadName(frame, slot)`
(around line 6960). There is a matching store path.

**The problem:** when a read misses the frame's slot fast path it falls through
to `SlotNameTable.GetName(frame.Function, slot)` and then walks
`env.OuterEnv` calling `env.TryLookupBinding(name, ...)` — **a string-keyed
dictionary lookup per level of the scope chain**. That is the 109 ns above. In a
minified bundle nearly every identifier that is not a parameter or local takes
this path.

**Evidence it is worth it:** `FEN_FENJS_PROFILE=1` on the reCAPTCHA page reports
`identifier reads=7,767,914 onGlobalObject=7.0% inLocalScope=93.0%
avgScopesWalked=1.55`. Those 7.77M are only the reads that reached the slow walk.

**The fix:** lexical scoping is static, so a site can resolve `(hops, slotIndex)`
once and thereafter walk `hops` `OuterEnv` links and read the slot directly.
Cache it per (function, slot) site. Guard it: any environment that is `with`,
direct `eval`, or has had a binding deleted must mark itself dynamic and force
the slow path. Global-object reads need a shape guard.

**Warning:** an earlier session dismissed this on a measurement of
`avgScopesWalked=1.56` / `shareOfExecution=0.60%`. That measurement only timed
the walk loop — it missed `GetName` and the dictionary lookups themselves. The
ladder is the correct number.

**Verify:** `b_property_read.js` rung "o.hit via free var" should fall from
~178 ns toward the "via local" rung at ~69 ns. Then the captcha job.

### 2. Array element read at 70 ns

`b_property_read.js` rung `arr[i & 3]` on a **four-element array, in L1**, costs
~70 ns over the free-variable read it also contains. That is roughly 200 cycles
for an indexed load. See memory `array-dense-storage-gap.md` — array indices may
still be going through shape-tracked string-keyed property lookup. Check
`GetElemWithKeyForJit` / `PerformSetElementByIndex` and whether the dense fast
path is actually being taken for the common case.

### 3. The ~400 ns empty JS call

Distributed across environment rent + stamp, frame rent, register rent, root
push, dispatch entry and teardown. No single step is worth more than ~30 ns, so
picking them off will not work — it needs a fused "enter frame" that rents the
environment, frame and register file in one step. Treat as a design task, and
only after items 1 and 2.

### 4. Look for more of the first-paint class of bug

`df97a5e7` was worth 4.5 s to the user and was not a JS problem at all — it was
one `await` on an IPC loop. The general shape: **anything on the brokered
renderer child's message loop that awaits long work blocks every frame request
behind it.** Audit the other handlers in `FenBrowser.Host/Program.cs`
(`Input`, and anything added later) for the same pattern. Compare in-process vs
brokered milestone *ordering* on a few real sites — an inversion is the tell.

---

## Known-open, lower priority

- google.com's scripts still take 4.9–25.8 s to finish (`ExecutePageScripts…
  DONE`). No longer blocks first paint, but still blocks interactivity. Items 1–3
  attack this.
- The reCAPTCHA challenge still does not appear; its own 20 s anchor watchdog
  fires during a ~7 s blocking job. Same underlying JS speed problem.
- `FenBrowser.Js/Interpreter/BytecodeInterpreter.cs` `AllocationSite.Current()`
  passes a 24-byte record struct with two string references into every heap
  allocation, purely for diagnostics. Not measured; likely small.
- `docs/test262_results.md` is current at 49936/53483 (93.37%). Follow the file,
  do not re-run the full suite.
