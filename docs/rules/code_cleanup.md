# FenBrowser Engineering Handbook

Version: 1.0  
Purpose: turn FenBrowser from an impressive prototype into a maintainable, testable, future-ready browser engine.

This handbook is the working rulebook for FenBrowser development. It assumes the project is AI-assisted, but it does not allow AI-generated code to become trusted engineering until it is understood, tested, reviewed, and documented.

---

## 1. Core philosophy

FenBrowser should be built like a browser engine, not like a screenshot demo.

The goal is not to make one large website look better by adding special cases. The goal is to make the engine pipeline more correct, one verified behavior at a time.

### The only progress that counts

A change counts as real progress only when it satisfies all of these:

1. The behavior is clearly defined.
2. A minimal test reproduces the old failure.
3. The affected pipeline stage is identified.
4. The patch is small and local.
5. The test fails before the fix and passes after the fix.
6. The limitation is documented honestly.
7. The change does not add fake completeness.

### What does not count as progress

- A screenshot that looks better but has no test.
- A large rewrite that nobody can explain.
- A feature folder that is not wired end-to-end.
- A stub that makes tests pass without behavior.
- A site-specific hack hidden inside the normal engine path.
- A README claim that is not backed by tests.

---

## 2. Non-negotiable rules

These rules should be followed for every prompt, every patch, and every pull request.

### 2.1 One behavior per change

Each patch should improve one small browser-engine behavior. Do not mix layout, rendering, JavaScript, networking, and UI cleanup in one change.

Good:

```text
Fix percentage width resolution for normal-flow block children.
```

Bad:

```text
Improve layout engine, add flexbox fixes, cleanup renderer, and make Google look better.
```

### 2.2 No site-specific hacks in core engine code

Do not add logic like this inside the main rendering path:

```text
if host contains google.com, rewrite the DOM
if host contains x.com, hide this node
if YouTube, skip script errors
```

If a temporary compatibility workaround is unavoidable, it must be isolated under a dedicated quirks area and documented with a removal condition.

Recommended location:

```text
FenBrowser.Core/Compatibility/Quirks/
```

Every quirk must include:

- URL or feature pattern affected
- Reason for the quirk
- Test case or reproduction
- Risk
- Owner
- Removal condition

### 2.3 No fake tests

Never allow tests like:

```csharp
Assert.True(true);
```

A useful test must prove observable behavior.

Good tests assert facts like:

- DOM node count
- computed style values
- layout box positions
- paint commands
- hit-test target
- event dispatch order
- navigation result

### 2.4 No unsupported feature claims

Do not say FenBrowser supports a feature unless tests prove the supported subset.

Bad:

```text
FenBrowser supports Flexbox.
```

Better:

```text
FenBrowser has experimental partial Flexbox support for simple row and column layouts. Wrapping, min-content sizing, baseline alignment, fragmentation, and many edge cases are not complete.
```

### 2.5 Delete weak code before adding more code

If a subsystem contains duplicated fallbacks, half-wired classes, fake abstractions, or untested special cases, cleanup comes before expansion.

The default question should be:

```text
Can this code be deleted, simplified, or isolated?
```

### 2.6 Keep the engine forgiving, not fake

Browsers are forgiving. They do not stop rendering because one CSS property or HTML tag is unsupported.

Correct fallback style:

```text
Unsupported CSS property: ignore that declaration.
Unknown HTML element: create a generic element and continue parsing children.
Unsupported display value: fall back according to CSS rules or a documented default.
Script error: report error, keep document usable where possible.
```

Incorrect fake fallback:

```text
Unsupported feature: pretend it worked.
Unsupported node: drop entire subtree silently.
Layout failed: return zero size without diagnostic.
```

### 2.7 No god files

A file that controls many unrelated engine stages becomes impossible to trust.

A file is suspicious if it handles more than two of these:

- navigation
- parsing
- DOM construction
- style resolution
- layout
- rendering
- input events
- JavaScript execution
- security policy
- cookies/storage
- website compatibility
- host UI integration

If a file becomes a god object, split it gradually with tests.

### 2.8 Every AI patch must be reviewed as untrusted code

AI output is a draft. It is not implementation until verified.

Before accepting AI code, answer:

1. Can I explain every important line?
2. Does a test prove the behavior?
3. Did it avoid site-specific hacks?
4. Did it avoid fake stubs?
5. Did it reduce or contain complexity?
6. Can I debug it later without asking AI to rewrite it?

If the answer is no, do not merge it.

---

## 3. Browser pipeline ownership

Every bug and feature must be mapped to a pipeline stage. This prevents random changes across the codebase.

```text
Navigation / Loader
    -> bytes, URL, redirects, content type
HTML parser
    -> tokens, tree construction, error recovery
DOM tree
    -> nodes, attributes, text, document structure
CSS parser
    -> rules, selectors, declarations
Cascade
    -> origin, specificity, source order, inheritance
Computed style
    -> resolved display, font, colors, lengths, visibility
Layout tree
    -> which nodes generate boxes
Layout
    -> box sizes and positions
Paint tree / display list
    -> draw commands and ordering
Raster / compositor
    -> pixels on screen
Hit testing
    -> element under pointer
Events
    -> focus, click, keyboard, dispatch path
JavaScript / DOM APIs
    -> dynamic behavior and mutations
Host integration
    -> window, input, timers, navigation UI
```

### Rule

When fixing a bug, name the broken stage first. If the stage is unknown, investigate before coding.

---

## 4. Definition of done

A FenBrowser change is done only when all relevant boxes are checked.

### Required for every code change

- [ ] The bug or behavior is described in one sentence.
- [ ] The affected pipeline stage is identified.
- [ ] A minimal test exists.
- [ ] The test fails before the fix or covers a previously missing behavior.
- [ ] The patch is limited to the correct area.
- [ ] No site-specific hack was added to core code.
- [ ] No fake test was added.
- [ ] No unsupported claim was added to docs.
- [ ] Limitations are documented if the feature is partial.

### Required for rendering/layout changes

- [ ] Computed style expectations are tested where relevant.
- [ ] Layout box positions/sizes are tested where relevant.
- [ ] Paint order or display-list output is tested where relevant.
- [ ] At least one reduced HTML fixture exists.
- [ ] The change does not rely on Google/X/YouTube-specific behavior.

### Required for JavaScript/DOM changes

- [ ] DOM mutation behavior is tested.
- [ ] Event order is tested if events are involved.
- [ ] Exceptions are surfaced, not swallowed silently.
- [ ] Unsupported APIs fail honestly.

### Required for documentation changes

- [ ] Claims are marked experimental/partial when appropriate.
- [ ] Tests or current limitations are referenced.
- [ ] Marketing language is removed or softened.

---

## 5. Testing strategy

Testing should move from small deterministic checks to larger compatibility tests.

### 5.1 Test pyramid

```text
Unit tests
    -> parser, selector matching, cascade, computed style, layout math
Reduced engine tests
    -> tiny HTML/CSS/JS files that reproduce bugs
Reftests / visual tests
    -> compare FenBrowser output against expected rendering
Integration tests
    -> navigation, input, events, forms, script-driven DOM updates
Conformance suites
    -> WPT, test262, selected CSS/HTML tests
Manual website checks
    -> only after smaller tests pass
```

Manual website checks are useful, but they must not replace reduced tests.

### 5.2 Start with small pages, not Google

Before targeting complex websites, make these reliable:

```html
<div>Hello</div>
```

```html
<div style="display:none">Hidden</div>
<div>Visible</div>
```

```html
<div style="margin:20px;padding:10px;border:5px solid black">Box</div>
```

```html
<button id="b">Click</button>
<script>
document.getElementById('b').addEventListener('click', () => document.body.append('Clicked'));
</script>
```

If simple pages are unstable, complex pages are not meaningful evidence.

### 5.3 Required test categories

FenBrowser should maintain focused tests for:

- HTML parsing and tree construction
- DOM node creation and mutation
- CSS selector matching
- Cascade specificity and source order
- Inheritance and initial values
- Computed style conversion
- Layout tree generation
- Block layout
- Inline layout
- Absolute/fixed positioning
- Tables
- Flexbox subset
- Paint order
- Backgrounds, borders, text painting
- Hit testing
- Focus and input events
- Anchor navigation
- Form submission basics
- JavaScript DOM integration
- Error recovery

---

## 6. AI-assisted development rules

AI is allowed to accelerate FenBrowser, but it must be constrained.

### 6.1 Never ask AI for broad changes

Do not ask:

```text
Make FenBrowser render modern websites better.
```

Ask:

```text
This reduced test fails. Find the broken pipeline stage, propose the smallest fix, and add a test. Do not modify unrelated systems.
```

### 6.2 AI prompt contract

Every AI coding prompt should include:

- observed bug
- expected behavior
- minimal test case
- relevant files
- forbidden changes
- required tests
- output format

### 6.3 Master prompt

Use this before asking AI to modify code:

```text
You are working on FenBrowser, an experimental C# browser engine.

Your job is not to add impressive-looking features.
Your job is to make one small browser-engine behavior correct, testable, and maintainable.

Rules:
1. Do not add new folders, new architecture layers, or new public claims unless absolutely required.
2. Do not add site-specific hacks for Google, X, YouTube, or other websites.
3. Do not silently skip unsupported HTML/CSS/JS. Use documented fallback behavior.
4. Do not create stubs that pretend a feature works.
5. Do not use Assert.True(true), fake tests, or screenshot-only validation.
6. Every change must include or update a focused test.
7. Prefer deleting weak code over adding workaround code.
8. Keep files small. If a file is becoming a god object, propose a split before modifying it.
9. Explain the exact pipeline stage affected: parsing, DOM, cascade, computed style, layout, paint, hit testing, events, JavaScript, networking, or host.
10. Output the smallest safe patch.

Before writing code:
- Identify the suspected root cause.
- Identify the files that need changes.
- Identify the test that will prove the fix.
- State what you will not change.

After writing code:
- Explain why the fix is correct.
- Explain what test proves it.
- List remaining limitations honestly.
```

---

## 7. Prompt templates

### 7.1 Bug investigation prompt

```text
FenBrowser bug investigation.

Observed bug:
[Describe exactly what is wrong]

Expected behavior:
[Describe browser-like expected result]

Minimal test case:
[Paste small HTML/CSS/JS]

Relevant files:
[Paste file names or code snippets]

Task:
Find the most likely root cause in the browser pipeline.
Do not write code yet.

Return:
1. Broken pipeline stage
2. Why this bug happens
3. Smallest code area to inspect
4. Test that should fail before the fix
5. Smallest safe fix strategy
6. What not to change
```

### 7.2 Implementation prompt

```text
Now implement the smallest safe fix.

Rules:
- No new architecture.
- No site-specific hacks.
- No broad rewrites.
- Add or update a focused test.
- Show the patch only.
- Explain why the test proves the fix.
```

### 7.3 Cleanup prompt

```text
Review this file for AI-generated or unmaintainable code.

Goal:
Make the code easier to own, not bigger.

Look for:
- duplicated logic
- fake fallback behavior
- dead code
- TODOs pretending to be implementation
- site-specific hacks
- mixed responsibilities
- comments that sound like AI notes
- code that hides failures instead of exposing them
- tests that do not prove behavior

Return:
1. Specific suspicious sections
2. Why each section is risky
3. Whether to delete, isolate, test, or rewrite
4. Safe cleanup order
5. Tests required before cleanup
6. Do not modify code yet
```

### 7.4 Feature implementation prompt

```text
Implement only the minimum useful part of [feature] in FenBrowser.

Feature:
[Example: CSS display:block normal-flow width calculation]

Scope:
Only support:
- [specific behavior 1]
- [specific behavior 2]
- [specific behavior 3]

Do not support yet:
- [advanced behavior 1]
- [advanced behavior 2]

Requirements:
1. Follow browser-style fallback behavior.
2. Unsupported cases must degrade safely.
3. Add focused tests.
4. Do not touch unrelated systems.
5. Do not add site-specific hacks.
6. Do not claim full feature support.

Return:
1. Files to change
2. Tests to add
3. Implementation plan
4. Limitations
5. Patch
```

### 7.5 Documentation claim audit prompt

```text
Audit this claim against the current code.

Claim:
"[Paste claim]"

Code evidence:
[Paste relevant files or summaries]

Task:
Decide whether this claim is:
- accurate
- partially accurate
- misleading
- unsupported

Rewrite the claim honestly.

Rules:
- Do not make marketing claims.
- Do not say spec-compliant unless tests prove it.
- Say partial/experimental when appropriate.
- Mention known limitations.
```

### 7.6 Pull request review prompt

```text
Review this FenBrowser patch as a strict browser-engine maintainer.

Check for:
1. Correct pipeline ownership
2. Hidden site-specific hacks
3. Fake tests or weak assertions
4. Broad rewrites unrelated to the bug
5. Silent failure handling
6. Unsupported feature claims
7. New allocations or hot-path performance risks
8. Missing limitations
9. Missing regression tests
10. Code that should be deleted instead of expanded

Return:
- Verdict: accept, request changes, or reject
- Blocking issues
- Non-blocking issues
- Tests required
- Suggested smaller patch if needed
```

---

## 8. Feature status discipline

Create and maintain `ENGINE_STATUS.md` in the repo root.

Template:

```markdown
# FenBrowser Engine Status

Last updated: YYYY-MM-DD

## Rendering pipeline

| Area | Status | Evidence | Known limitations |
|---|---|---|---|
| HTML parser | Partial | Tests: ... | ... |
| DOM tree | Partial | Tests: ... | ... |
| CSS parser | Partial | Tests: ... | ... |
| Cascade | Partial | Tests: ... | ... |
| Computed style | Partial | Tests: ... | ... |
| Block layout | Partial | Tests: ... | ... |
| Inline layout | Experimental | Tests: ... | ... |
| Flexbox | Experimental subset | Tests: ... | ... |
| Tables | Partial | Tests: ... | ... |
| Paint | Partial | Tests: ... | ... |
| Hit testing | Incomplete | Tests: ... | ... |
| DOM events | Incomplete | Tests: ... | ... |
| JavaScript engine | Experimental | Tests: ... | ... |

## Known hacks / quirks

| Quirk | Location | Reason | Removal condition |
|---|---|---|---|
| ... | ... | ... | ... |

## Things not supported yet

- ...
```

Status labels:

- Not started
- Stub only
- Experimental
- Partial
- Usable subset
- Mostly complete
- Conformance-backed

Do not use `complete` unless broad tests prove it.

---

## 9. Quirk and hack policy

### 9.1 Allowed temporary quirk format

```csharp
// Compatibility quirk: [name]
// Affected: [URL pattern or feature]
// Reason: [why required]
// Risk: [what it can break]
// Test: [test name]
// Removal condition: [what must be fixed before deleting]
```

### 9.2 Disallowed hidden hack examples

```csharp
if (url.Contains("google")) return BuildFallbackDom();
```

```csharp
catch { return defaultStyle; }
```

```csharp
if (layoutFailed) box.Width = 0;
```

These hide the real issue. Use diagnostics and tests instead.

---

## 10. Refactoring rules

Refactoring is allowed only when it improves ownership and preserves behavior.

### Safe refactor pattern

1. Add characterization tests for current behavior.
2. Extract one responsibility.
3. Keep public behavior unchanged.
4. Run tests.
5. Repeat.

### Do not refactor like this

```text
Rewrite the entire renderer into a new architecture.
```

### Refactor target examples

Split a giant HTML/rendering file into:

```text
DocumentLoader
HtmlParserAdapter
DomBuilder
StylePipeline
LayoutScheduler
PaintScheduler
ScriptExecutionCoordinator
InputEventBridge
CompatibilityQuirkRegistry
```

But do this gradually. One extraction per patch.

---

## 11. Performance rules

Correctness comes first, but do not add obvious hot-path problems.

### Watch hot paths

- style resolution
- selector matching
- layout tree construction
- layout loops
- text measurement
- paint command generation
- event dispatch
- JavaScript property lookup

### Avoid in hot paths

- unnecessary `Dictionary<string, object>` allocations
- repeated string interpolation for disabled logs
- repeated LINQ in tight loops
- boxing value types into `object`
- broad exception handling
- repeated parsing of the same CSS values
- allocating temporary lists per node unless necessary

### Logging rule

Do not build expensive log values before checking whether the log level is enabled.

Preferred direction:

- static message templates
- source-generated logging where possible
- lazy debug payloads
- no string interpolation in hot loops unless guarded

---

## 12. Documentation standards

FenBrowser documentation must be honest and precise.

### Good wording

```text
FenBrowser is an experimental browser engine prototype.
```

```text
This subsystem currently supports a limited subset and is under active development.
```

```text
This behavior is covered by focused tests listed below.
```

### Bad wording

```text
Fully spec-compliant browser.
```

```text
Production-ready secure browser.
```

```text
Supports modern web standards.
```

Those claims require broad evidence.

---

## 13. Suggested repo files to add

### `ENGINE_STATUS.md`

Truth table of supported, partial, experimental, and missing systems.

### `CONTRIBUTING_ENGINE.md`

Rules for patches, tests, AI use, and browser-engine standards.

### `QUIRKS.md`

List of temporary compatibility quirks and removal conditions.

### `TESTING.md`

How to run unit tests, layout tests, reduced tests, WPT subsets, and screenshot comparisons.

### `RISK_REGISTER.md`

Known technical debt and severity.

Example:

```markdown
| Risk | Severity | Area | Why it matters | Fix plan |
|---|---|---|---|---|
| Hit testing incomplete | High | Input/events | Pages render but cannot interact reliably | Add layout-box-backed hit testing tests |
```

---

## 14. Development roadmap

### Phase 1: Truth and cleanup

Goal: stop fake completeness and make the project understandable.

Tasks:

1. ~~Add `ENGINE_STATUS.md`.~~
2. ~~Add `QUIRKS.md`.~~
3. ~~Search and remove AI-note comments.~~
4. ~~Search for fake tests.~~
5. ~~Identify god files.~~
6. ~~Quarantine site-specific logic.~~
7. ~~Make build/test commands reliable.~~

### Phase 2: Rendering core stability

Goal: make small pages deterministic.

Priority:

1. default styles
2. cascade order
3. inheritance
4. computed lengths
5. layout tree construction
6. block layout
7. text painting
8. `display:none`
9. paint order
10. scroll basics

### Phase 3: Interaction basics

Goal: make pages usable, not just visible.

Priority:

1. hit testing
2. focus
3. mouse events
4. keyboard events
5. input text editing basics
6. buttons
7. links
8. forms
9. DOM event dispatch
10. script-triggered DOM updates

### Phase 4: Compatibility growth

Goal: grow feature coverage with tests.

Priority:

1. inline formatting
2. tables
3. flexbox subset
4. images
5. CSS positioning
6. media queries subset
7. DOM APIs used by common sites
8. JavaScript conformance improvements
9. WPT subset tracking
10. screenshot/reftest harness

---

## 15. Weekly operating system

Use this rhythm every week.

### Monday

Pick three reduced failing tests. No website-scale tasks.

### Tuesday to Thursday

Fix one behavior at a time. Every patch must include tests.

### Friday

Run quality checks:

- build
- unit tests
- layout tests
- reduced rendering tests
- fake-test scan
- TODO/hack scan
- README/ENGINE_STATUS update

### Weekend optional

Manual website screenshots are allowed only as discovery, not proof.

---

## 16. Quality gates for pull requests

A pull request should be rejected if it:

- adds a broad rewrite without tests
- adds site-specific code to core engine paths
- adds fake tests
- hides errors silently
- expands a god file
- claims full feature support without evidence
- fixes a screenshot but not the underlying reduced case
- changes multiple unrelated pipeline stages
- creates new folders just to look complete

A pull request should be accepted only if it:

- fixes a clear behavior
- has focused tests
- documents limitations
- is small enough to review
- improves correctness or maintainability

---

## 17. Red flag scanner

Regularly search the repo for these terms:

```text
Assert.True(true)
TODO
HACK
temporary
fallback
placeholder
stub
google
youtube
twitter
x.com
not implemented
catch { }
catch (Exception)
return null
return default
```

Not every match is bad, but every match should be reviewed.

---

## 18. Personal rule for the maintainer

Before merging anything, say this out loud:

```text
I understand why this code exists.
I can prove what behavior it changes.
I know which test protects it.
I know what it does not support.
I am not hiding a hack as an engine feature.
```

If that statement is not true, do not merge.

---

## 19. The right way to use complex websites

Complex websites are diagnostic tools, not the main proof.

Use Google, X, YouTube, or other large sites to discover missing systems. Then reduce the failure into a small test.

Correct workflow:

```text
Website looks broken
    -> inspect what kind of failure it is
    -> reduce to tiny HTML/CSS/JS
    -> add test
    -> fix pipeline stage
    -> verify tiny test
    -> re-check website only as a broad signal
```

Wrong workflow:

```text
Website looks broken
    -> patch code until screenshot improves
    -> add no reduced test
    -> repeat
```

---

## 20. Final standard

FenBrowser becomes future-ready when every major claim can be answered with:

```text
Here is the behavior.
Here is the test.
Here is the code path.
Here is the limitation.
Here is what still fails.
```

That is the difference between an our browser engine and other browser-engine project.
