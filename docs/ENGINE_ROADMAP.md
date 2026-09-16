# Engine Roadmap — capability gaps ranked by site evidence

Snapshot date: 2026-09-16. Written from a youtube.com bring-up session; every item
below is backed by something observed in this checkout, not by spec coverage counting.

This complements the existing trackers rather than replacing them:

- `docs/KNOWN_GAPS.md` — process model, IPC, host-object ownership, test determinism.
- `docs/MISSING_API_TRACKER.md` — per-API inventory from real-site runs.
- `docs/NEXT_TASKS.md` — dependency-ready task queue.
- `docs/test262_results.md` — language conformance.

What is new here is the **ordering principle**. A missing API only matters in
proportion to what it takes down with it, and today's session was a clean
demonstration: seven defects sat in a chain where each one hid the next, and the
single highest-impact one — `document.all` — is a piece of Annex B legacy that no
conformance score would have flagged. Rank by blast radius, and measure blast radius
by loading real sites.

## How this session found what it found

The method matters more than the list, because the list will be stale in a month.

```bash
FEN_DEBUG_SITE_PRESCRIPT='<one-line JS whose return value gets printed>' \
FEN_LOG_PRESET=testrun \
./FenBrowser.Tooling/bin/Release/net10.0/FenBrowser.Tooling.exe debug-site <url> 20000
```

The prescript runs after the page settles and its return value is printed to stdout.
That is a complete probe loop in one command, and it is how every diagnosis below was
made. Two techniques are worth keeping:

1. **Serve the site's own bundle from a page you control.** `python -m http.server` in
   a scratch directory, then `XMLHttpRequest` the real script and `eval` it inside a
   try/catch. The failure reproduces with a stack you can instrument.
2. **Bisect a minified bundle by marker injection.** Scan for `;` at the top-level
   brace depth, insert `window.__mark=N;` after each, then read `__mark` from the
   catch block. It located a failure inside a 77 KB minified polyfill in two runs.

`scripts/fenlog.py` is the only correct way to read the bundles; the raw `trace.jsonl`
is 10 MB of mostly-null context.

---

## Tier 0 — open blockers with a known first move

### 0.1 Polymer/ShadyCSS component styles never reach the document

**Evidence.** On youtube.com every custom element is defined and upgraded,
`ShadyDOM.inUse === true`, `nativeShadow === false` — and `document.head` holds
only **9 `<style>` elements totalling 6.7 KB**. Component CSS is simply absent, so
`ytd-watch-flexy` computes `display: inline` and the watch page paints beside the
home page instead of being hidden by it.

**What is already ruled out.** The cascade is fine. All three dynamic-injection paths
were verified working against a controlled page: `style.textContent` before append,
after append, and `style.sheet.insertRule` all produce the expected computed value.
Template contents are correct too (`<template>` children land in `.content`, both from
the parser and from `innerHTML` on a JS-created template).

**So the break is upstream**, in Polymer's style-module pipeline — which is worth
fixing precisely because every web-components site on the platform shares it.

**First move:** instrument `ShadyCSS.prepareTemplate` / `styleElementForTemplate` the
same way the polymer-resin failure was bisected, and find where the scoped style is
computed but never appended.

### 0.2 `document.styleSheets` is hardcoded empty

`CreateEmptyStyleSheetListObject` in `BrowserScriptEngineRuntime.cs` returns
`{length: 0}` unconditionally. ShadyCSS, every CSS-in-JS runtime, and every theming
library reads this collection. It is very likely a contributing cause of 0.1, and it is
independently wrong.

The right shape is not another JS shim. `HTMLStyleElement.sheet` today is built by a
JS-side `__fenCreateStyleSheet` whose rule parser is `split('}')` — it will mangle
`@media`, `@supports`, and any `}` inside a string, and it never seeds rules from the
element's existing text. **The CSSOM should be a view over the engine's own parsed
stylesheets** (`FenBrowser.Core/Css`, `CssParser`, `CssLoader`), which already parse
these correctly for the cascade. That is the Engineering Constitution's own rule —
layout and style authority live in the engine — applied to the CSSOM.

Scope: `document.styleSheets` (live, tree order), `CSSStyleSheet` with real
`cssRules`, `CSSRule` subtypes for style/media/supports/keyframes/font-face,
`insertRule`/`deleteRule` writing back through the engine, `link.sheet`, and
`adoptedStyleSheets` + constructable stylesheets (currently `undefined`; Lit depends
on them).

### 0.3 One script batch takes 12.7 seconds

```
max 12734ms x72 total 22125ms   JS job ExecuteScriptBatchAsync
max 12015ms  x5 total 30047ms   JS job ExecuteScriptBatchAsync executing
```

YouTube ships a ~10 MB kevlar bundle and loads twice (its own `?themeRefresh=1`
round-trip, which Chrome also performs on a cookieless first visit). That is ~45 s of
wall clock where Chrome is under a second. Nothing else on this list changes the felt
experience as much.

This is the same lever `docs/INTERPRETER2.md` and the baseline-JIT work already
target. The roadmap point is only that **large-bundle throughput should be the metric
those efforts are judged by**, with a checked-in benchmark that runs a real bundle,
rather than microbenchmarks. Prior memory already warns that the FenJS profiler's
leaf self-time is unreliable — use the call ladder.

---

## Tier 1 — capability gaps confirmed absent

Measured directly in this checkout:

| Surface | State | Why it matters |
| --- | --- | --- |
| `WebAssembly` | `undefined` | Feature-detected by ~5% of top sites; several fall back to nothing rather than to JS |
| `document.adoptedStyleSheets` | `undefined` | Constructable stylesheets; Lit's default styling path |
| `HTMLCanvasElement.prototype.getContext` | `undefined` | `getContext` exists on instances but not the prototype — libraries feature-detect on the prototype and conclude canvas is missing |
| `speechSynthesis` | `undefined` | Low priority, listed for completeness |
| `getComputedStyle(el).top` | returns the **string** `"undefined"` | Should be `auto` or a used px value; any code doing arithmetic on it gets `NaN` |

The canvas one deserves emphasis because it is a whole class of bug: **an API that
works when called but cannot be detected is worse than one that is absent**, because
the site takes the "no support" branch anyway and you get no error to trace. Anywhere
the engine exposes a method as an instance-level host property that a browser exposes
on a prototype, feature detection silently fails. A sweep for that mismatch is cheap
and will find more than canvas.

The existing `FenJsFingerprintProbeTests` is the right regression shape for this and
should grow to cover prototype-level detection, not just `typeof window.X`.

---

## Tier 2 — the four properties, made concrete

The request was for work that is performance-oriented, modular, secure, and
test-driven. Those are properties of *how* the tiers above get built, so here is what
each means in this codebase specifically.

### Performance

- **Judge by real bundles.** A checked-in benchmark that runs a captured
  multi-megabyte site bundle end to end, reported as a distribution, not a single
  sample. `docs/KNOWN_GAPS.md` already flags that the current performance artifact is
  one diagnostic sample and explicitly not a repeatable benchmark.
- **Layout probes must not cost a second pass.** This session found a flex intrinsic
  probe leaking infinite width into control sizing; the sanitizer clamped it and the
  page still rendered, which is exactly why it survived — the cost was 1,388 error log
  lines and a wrong intrinsic size, with no visible failure. Probe results should be
  validated at the probe boundary, not clamped downstream.
- **Watch the double-load.** Any site that reloads itself pays the full JS cost twice.
  Nothing is wrong with our handling of it, but it doubles every number.

### Modularity

The Engineering Constitution's existing rule — HarfBuzz shapes, Skia draws, FenEngine
decides — should extend to the JS-side shims. Today a large amount of the DOM surface
is C# raw-string JavaScript inside `BrowserScriptEngineRuntime.cs`, a file well past
27,000 lines. The failures in this session were *specifically* made harder by that:

- Event constructors resolved their base class through `globalThis` at call time
  because they were defined as `globalThis.X = function X(...)` in a shared IIFE, so a
  page replacing `window.Event` replaced the base class of every built-in subclass.
- The EventTarget polyfill assumed its own constructor had run, so anything reaching
  its methods without that — the global, any subclass skipping `super()` — threw.

Both are the same bug class: **a shim that assumes it is the only thing in the realm.**
Two rules fall out, and they are worth writing into the constitution:

1. A shim never resolves another built-in through the global at call time. Capture a
   local binding at install time.
2. A shim never assumes its constructor ran on a receiver. Initialise lazily.

Longer term, this JavaScript belongs in versioned `.js` resources compiled in as
embedded resources, so it can be linted, unit-tested in isolation, and diffed — rather
than living inside C# string literals where none of that is possible.

### Security

The two fixes here that touched security-adjacent surface were both cases where being
*more* spec-correct is also safer:

- `document.all` is a legacy exotic, but implementing it correctly means the sanitizer
  guard sites write actually discriminates. The version without it caused polymer-resin
  to run its sanitizer over values that were never meant to reach it — a security shim
  firing on the wrong inputs is a security shim nobody can reason about.
- Root overflow propagation removed a clip that was swallowing the entire page. A
  rendering bug that hides content is a correctness bug, but the same class of bug in
  the other direction — content painting outside a clip that was supposed to contain
  it — is a spoofing primitive. Clip derivation deserves the same scrutiny as origin
  checks.

Still open from `docs/KNOWN_GAPS.md` and unchanged by this session: the default
runtime is in-process, so the ordinary path does not contain a compromised renderer;
IPC envelopes have no schema version or per-message timeout policy; host-object
ownership uses strong references with no cycle collection. Those outrank every
capability gap above on a security axis and are already flagged as needing a human
decision.

**Trusted Types** is worth a note because it came up and was misdiagnosed at first:
`window.trustedTypes` does exist here. The `zClosurez` strings on YouTube were *not*
a Trusted Types failure — polymer-resin's innocuous string is `zClosurez` in Chrome
too. The failure was `document.all`. Recording this so nobody re-derives it.

### Test-driven

The regression tests added today (`FenBrowser.Tests/Scripting/DocumentAllTests.cs`,
`DomEventInterfaceTests.cs`) pin the comparison table for `[[IsHTMLDDA]]`, the exact
Closure guard shape that depends on it, and the prototype links. That is the right
granularity: **pin the idiom the web actually writes, not just the API**.

One process note. `FenBrowser.Tests` has a documented nondeterministic failure pool
(~82–96 failures under parallelism, each passing focused). The only sound way to check
for regressions is to **diff the failure sets** before and after, then rerun any
newly-appearing test in isolation. Doing that caught a real regression today —
adding `TouchEvent` broke `FenJsFingerprintProbeTests`, which deliberately presents a
non-touch desktop and is self-consistent about it (`ontouchstart` absent,
`maxTouchPoints` 0). The interfaces were removed rather than flipping that contract as
a drive-by. A raw pass/fail count would have hidden this completely.

---

## What to take from other engines

Each of these is a specific mechanism, not an aspiration.

**From Chromium — the compositor/main-thread split as a correctness boundary.**
The thing worth copying is not the threading but the discipline that produces it: a
display list that is a real command stream with stable identity, so damage can be
computed rather than guessed. `docs/KNOWN_GAPS.md` already records that
`display_list.txt` is a flattened paint-tree proxy rather than a canonical command
stream. Making it canonical is what unlocks damage tracking, partial raster, and a
meaningful frame budget — in that order.

**From Firefox — style computation as a pure function with explicit invalidation.**
Stylo's win was making the cascade a side-effect-free computation over a rule tree with
sharing, so recomputation could be parallel *and* incremental. This checkout already
has `StyleCache` and incremental recascade; prior memory records a parallel-cascade
race that dropped styles and a hover-recascade debounce that dropped 86% of hover
crossings. Both are symptoms of invalidation being implicit. The lever is an explicit
invalidation set, not more parallelism.

**From Ladybird — the spec text is the source of truth, and the code should say so.**
Ladybird's practice of writing the algorithm with its spec steps inline is what makes
its correctness auditable by someone who is not the author. This codebase already
does this in places, and the commits from this session follow it. Where it is missing,
correctness claims cannot be checked without re-deriving them.

**Where to improve on all three: diagnosis.** None of them has an equivalent of
`debug-site` plus `fenlog.py` — a single command that loads a real site headlessly and
emits a bounded, deduplicated, machine-readable account of every blocker, with a
prescript hook for arbitrary probes. Seven distinct engine defects were localised in
one session with it, three of them inside minified third-party bundles. That is a
genuine advantage and it compounds: **it should be invested in as a product surface,
not treated as scaffolding.** Concretely — first-blocker classification that names the
spec algorithm it belongs to, and a diff mode that answers "what changed between these
two runs" without re-reading either bundle.

---

## Suggested order

1. **0.2 CSSOM over the engine's parsed stylesheets** — likely unlocks 0.1, and is a
   prerequisite for a large class of sites regardless.
2. **0.1 Polymer/ShadyCSS style pipeline** — one root cause behind every
   web-components site.
3. **Prototype-level feature-detection sweep** (Tier 1) — cheap, and each finding is a
   whole site behaving as though a working API were absent.
4. **0.3 large-bundle JS throughput**, judged by a checked-in real-bundle benchmark.
5. **WebAssembly**, once 1–4 have stopped producing blank pages.
