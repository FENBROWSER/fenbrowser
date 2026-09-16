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

### 0.1 An inactive page is instantiated and painted

**Corrected 2026-09-16.** This was filed as "Polymer/ShadyCSS component styles never
reach the document". That premise was wrong, and the way it was wrong is worth
keeping: only 9 `<style>` elements totalling 6.7 KB reach `document.head`, and the
inference was that component styles were being lost somewhere in the style-module
pipeline. They are not being lost. **There are none to lose.**

What the evidence actually says:

- `ShadyCSS.disableRuntime === true`. YouTube ships CSS pre-scoped by its build, so
  the runtime shim is deliberately switched off. Driving `ShadyCSS.prepareTemplate`
  and `ShadyCSS.styleElement` by hand confirms it: both run clean and emit nothing,
  and no scoping class is added.
- `customElements.get('ytd-watch-flexy').prototype._template` has 24 children and
  **zero** `<style>` elements. The components genuinely carry no per-component CSS;
  it lives in the external stylesheets, which the engine does load and apply — the
  masthead renders styled.

So the real defect is upstream of styling: **`ytd-watch-flexy`, the watch page, is
instantiated and laid out on the home page.** It computes `display: inline`, which is
the UA default for an unknown element and means no rule matched it — because the
watch page's CSS is not loaded on the home page, because in a browser that element is
never created there. `ytd-page-manager` should only have the active page.

That is what paints the large black `#full-bleed-container` rectangle beside the home
feed. It is a page-manager / `dom-if` instantiation question, not a CSS one.

**First move:** find why `ytd-page-manager` instantiates a non-active page — most
likely a `dom-if`/`dom-repeat` condition evaluating truthy when it should not, which
is testable in isolation rather than against the whole site.

### 0.2 `document.styleSheets` — DONE (ca00f96b)

Was a hardcoded empty list, with an existing test pinning that shape. Now live, in
tree order, stable identity, and backed by `CssSyntaxParser` — the same parser the
cascade uses — instead of the JavaScript shim that split stylesheet text on `}`. On
youtube.com the collection goes from 0 sheets to 17.

Still open in this area: `<link>` sheets expose `href`/`ownerNode`/`media` but an empty
rule list, because the fetched bytes are not retained per element; and
`adoptedStyleSheets` / constructable stylesheets remain absent, which is Lit's default
styling path.

### 0.3 Compilation is eager, and almost none of it is ever used

**Measured 2026-09-17 (b156e190).** The 12.7 s script batch breaks down. For
youtube.com's main bundle — 10.8M characters, the single largest thing on the page:

```
parse+compile 2248 ms | verify 0 ms | execute 5760 ms
```

And `FEN_JS_COMPILED_CODE_COVERAGE=1` says what that compile bought:

```
functions compiled=107601  entered=257            (0.2% used)
instructions compiled=5493888  reachable=1364979  (24.8% reachable)
```

The compiler is eager. Every function in a script is parsed and compiled to
bytecode when the script is compiled, whether or not it is ever called — there is no
lazy path anywhere in `FenBrowser.Js/Parser/` or `Bytecode/`. So the engine compiles
a hundred thousand functions to run a few hundred, and holds ~5.5M instructions of
bytecode of which three quarters is unreachable.

**The fix is lazy function compilation**, which is what every production engine does:
pre-parse a function body only far enough to find its end and record its source
extent, then parse and compile it on first call. It is worth being clear about what
that does and does not buy:

- It attacks the **2,248 ms**, not the 5,760 ms. Pre-parsing is not free — expect to
  keep perhaps a quarter to a third of the compile time — so the realistic saving on
  this bundle is on the order of **1.5 s**, not 2.2 s.
- The memory saving is larger in proportion and may matter more: ~5.5M instructions
  of bytecode, plus 107K `BytecodeFunction` objects, most of which are never touched.
- It does nothing for `execute`, which is the bigger half. That is interpreter
  throughput and is the separate, harder problem `docs/INTERPRETER2.md` targets.

It is a substantial change — the parser needs a pre-parse mode, `BytecodeFunction`
needs a not-yet-compiled state, and closure creation and `Function.prototype.toString`
both have to cope with it — so it wants its own run rather than being squeezed in
beside something else.

**Re-measure with the same command**, so the claim stays honest:

```bash
FEN_JS_COMPILED_CODE_COVERAGE=1 ./FenBrowser.Tooling/bin/Release/net10.0/FenBrowser.Tooling.exe debug-site <url> 20000
```

A caveat on the numbers: `entered` counts functions reached through the ordinary call
path, so generators, async resumption and construct paths may be undercounted. The
instruction ratio is the sturdier of the two figures. Even allowing an order of
magnitude, the conclusion does not move.

The roadmap's earlier note still stands: large-bundle throughput is the metric this
work should be judged by, and the per-script `parse+compile / verify / execute` line
the engine already logs is the cheapest way to see the split.

### 0.4 Cascade cost on interaction — pseudo-element matching (fixed, ddc6602b)

Found from a live session: every click on youtube.com's watch page triggered an
incremental recascade costing 70–100 ms, and the counters said where it went.

The pseudo-element rule index was one flat list per pseudo-element, and the guard
that decided whether to run a pseudo pass was **document-wide** — one `::before` rule
anywhere meant every element ran a full `::before` match against every `::before`
rule on the page. On youtube.com:

```
mainPass=8477  pseudoPass=23723  pseudoCollectMs=973  collectMs=1395
```

70% of all selector-matching time, almost all of it finding nothing. Keying each
pseudo-element's rules the way the main index is keyed — ID, class, attribute, tag,
universal — fixed it:

```
pseudo matching   973 ms -> 18 ms
total selector   1395 ms -> 468 ms
per recascade   104.6 ms -> 75.8 ms   (same page, same method)
```

Page *load* time is unchanged; it is dominated by JS, not the cascade. This is an
interaction-responsiveness win.

**Still open in the cascade:** `cacheHits=0` for a whole page load. `_styleCache` is
keyed per element and skipped whenever the element is StyleDirty, which during a
subtree recascade is every element in the subtree — so the cache never earns its
keep. Whether that is right depends on what actually invalidates, and the answer
wants the explicit invalidation set the Firefox note below argues for.

---

## Tier 1 — capability gaps confirmed absent

Measured directly in this checkout:

| Surface | State | Why it matters |
| --- | --- | --- |
| `WebAssembly` | `undefined` | Feature-detected by ~5% of top sites; several fall back to nothing rather than to JS |
| `document.adoptedStyleSheets` | `undefined` | Constructable stylesheets; Lit's default styling path |
| ~~`HTMLCanvasElement.prototype.getContext`~~ | **fixed (a08ae1ac)** | See below |
| `speechSynthesis` | `undefined` | Low priority, listed for completeness |
| `getComputedStyle(el).top` | returns the **string** `"undefined"` | Should be `auto` or a used px value; any code doing arithmetic on it gets `NaN` |

The canvas one was a whole class of bug: **an API that works when called but cannot
be detected is worse than one that is absent**, because the site takes the "no
support" branch anyway and you get no error to trace.

**Swept and fixed (a08ae1ac, 03e0a9ca).** Across 15 interfaces the sweep found **67
members answering on an instance but missing from the prototype**, including
`Element.innerHTML`, `Element.getBoundingClientRect`, `Node.textContent`,
`Document.body`, `HTMLAnchorElement.href` and `HTMLTemplateElement.content`. That is
exactly what made the web-components polyfill patch nothing: it decides what it can
wrap with `Object.getOwnPropertyDescriptor(Element.prototype, name)`, and every one
came back undefined. Node, Element, Document, CharacterData and forty-odd element
interfaces now publish their members; the sweep reports zero instance-only members.

Three things that fell out and are worth remembering:

- **Do not publish what the host does not implement.** The mirror-image lie is just as
  bad. Each member is checked against a probe element, so the 19 genuinely missing
  ones (`HTMLMediaElement.play`, input validation, selection) stay absent.
- **Ask, do not read.** Probing by fetching the value cost fourteen layout flushes per
  document, because reading `clientWidth` resolves layout. `in` was also invoking
  accessors during the chain walk, which is an ECMA-262 violation in its own right.
- **The engine probing itself is not a site hitting a gap.** Unsuppressed, the probe
  filed 122 missing-API reports per document and drowned the tracker.

`HTMLElement.prototype` is deliberately excluded — it is host-backed already and its
members install lazily elsewhere.

Still open in this area: `HTMLInputElement.checked`, `select`, `setSelectionRange`,
form validation, and the whole `HTMLMediaElement` surface are genuinely unimplemented.
`FenJsFingerprintProbeTests` remains the right shape for the `typeof window.X` half;
`InterfacePrototypeMemberTests` now covers the prototype half.

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

1. ~~**0.2 CSSOM over the engine's parsed stylesheets**~~ — done (ca00f96b). It did
   not unlock 0.1, because 0.1 was not a styling problem; see the correction there.
2. **0.1 an inactive page being instantiated** — what actually paints the watch page
   over the home feed.
3. ~~**Prototype-level feature-detection sweep**~~ — done (a08ae1ac); 67 members were
   invisible to detection and now are not.
4. **0.3 lazy function compilation** — measured at 0.2% of compiled functions ever
   entered; worth ~1.5 s on youtube's bundle plus a large memory saving. Wants its
   own run.
5. **WebAssembly**, once 1–4 have stopped producing blank pages.
