# Definition of Done (DoD) for FenBrowser Pull Requests

Every PR that lands on `main` **MUST** satisfy all criteria in the relevant tier before merge.
No exceptions. No "we'll fix it in follow-up". No partial credit.

---

## Tier 0 — ALL PRs (mandatory, no exceptions)

| # | Gate | Evidence required |
|---|------|------------------|
| 0-1 | **Builds clean** | `dotnet build` exits 0, zero warnings on new code |
| 0-2 | **Tests green** | `dotnet test` exits 0; no newly-failing test |
| 0-3 | **No secrets / credentials** | Automated scan + manual review confirms no keys, tokens, passwords |
| 0-4 | **No debug leftovers** | No `Console.WriteLine`, `TODO REMOVE`, or commented-out dead code on hot paths |
| 0-5 | **CI passes** | All required status checks green before merge |
| 0-6 | **Spec governance wired** | `docs/SPECS.md` + `docs/COMPLIANCE_MATRIX.md` are current for changed capabilities, source headers include `SpecRef`/`CapabilityId`, and `scripts/validate_spec_headers.ps1` passes |

---

## Tier 1 — Feature / Bug-fix PRs

### 1-A Spec Mapping

Every new or changed behaviour **must cite a normative spec reference** in the PR description or inline comment.

- DOM/HTML/CSS: cite the WHATWG spec section + algorithm name (e.g. `WHATWG DOM §4.2.2 "insert an element"`)
- JS engine: cite ECMA-262 section and algorithm (e.g. `ECMA-262 §20.4.1.1 OrdinaryCallEvaluateBody`)
- Network: cite WHATWG Fetch / RFC (e.g. `Fetch §4.6 HTTP-network fetch`)
- Security: cite relevant RFC or W3C spec (e.g. `CSP Level 3 §8.5`)

**Exceptions**: pure internal refactors with no observable behaviour change.

### 1-B Conformance Tests

| Area | Required test coverage |
|------|----------------------|
| HTML/DOM | WPT subtests exercising the changed path must pass |
| CSS | WPT css/CSS2/selectors + affected sub-suite must pass |
| JS engine | Relevant Test262 chapters must pass (no regressions) |
| Fetch/CORS | WPT fetch/ subtests must pass |
| Security headers | WPT content-security-policy/ if relevant |

**New conformance failures are a hard block.** New passes are highlighted in the PR.

### 1-C Security Review

Every PR that touches a security-sensitive surface **must** include a brief security analysis:

- **IPC**: new message types need threat model, size/rate limits, capability token validation
- **Parsers** (HTML, CSS, JS, image, font, media): input validation, fuzzing baseline updated
- **Sandboxing**: no new ambient authority granted without explicit policy update
- **Origins / CORS**: no relaxation without spec justification
- **Memory management**: no new unsafe blocks without bounds checks and pointer lifetime analysis

The analysis can be one paragraph in the PR description. A senior reviewer must sign off.

### 1-D Fuzzing (parsers and IPC only)

For any PR that:
- Adds or modifies a parser (tokenizer, tree builder, expression parser, IDL parser, etc.)
- Adds new IPC message types

The PR must:
1. Add or update a `IpcFuzzHarness` / `StructuredMutator` coverage path for the new surface
2. Run at least 10 000 fuzzing iterations locally with no crashes
3. Attach the fuzzing log summary to the PR (pass count, unique paths, coverage delta)

---

## Tier 2 — Architecture / Cross-cutting PRs

In addition to Tier 0 + 1:

| Gate | Description |
|------|-------------|
| **API stability** | Public API changes require a `BREAKING CHANGE:` note and version bump |
| **Benchmark regression** | No > 5% regression in any tracked benchmark (`dotnet run --project FenBrowser.Test262`) |
| **Memory baseline** | Arena slab high-water mark does not grow > 10% for standard workloads |
| **Thread safety** | Any shared-state change reviewed by a second engineer for races |
| **Docs updated** | VOLUME_*.md files updated to reflect architecture changes |

---

## PR Template Checklist

Copy into every PR description:

```markdown
## DoD Checklist

### Tier 0 (all PRs)
- [ ] `dotnet build` — zero warnings on new code
- [ ] `dotnet test` — all tests pass, no new failures
- [ ] No secrets/credentials in diff
- [ ] No debug leftovers
- [ ] `powershell -ExecutionPolicy Bypass -File scripts/validate_spec_headers.ps1` passes (for Core/FenEngine/Host process-isolation runtime changes)

### Tier 1 (feature/fix)
- [ ] Spec reference cited: <!-- WHATWG/ECMA-262/RFC link + section -->
- [ ] Conformance tests added/updated and passing
- [ ] Security analysis written: <!-- one paragraph or N/A -->
- [ ] Fuzzing updated (if parser or IPC change): <!-- log attached or N/A -->

### Tier 2 (architecture) — if applicable
- [ ] No benchmark regression (> 5%)
- [ ] Memory baseline stable
- [ ] Thread safety reviewed by second engineer
- [ ] VOLUME_*.md docs updated
```

---

## Reviewer Responsibilities

1. **Do not approve** a PR with unchecked Tier 0 boxes.
2. **Do not approve** a PR with unchecked Tier 1 boxes unless explicitly waived with justification.
3. **Leave a blocking comment** citing the specific DoD criterion violated.
4. **Merge only** after all required checks are green and all blocking comments resolved.

---

## Waiver Process

Waivers for individual DoD criteria require:

1. A comment in the PR: `WAIVER: <criterion> — <justification>`
2. Approval from the tech lead
3. A follow-up issue filed immediately (linked in the PR) to close the gap

Waivers are tracked and reviewed monthly. Persistent waivers indicate a systemic gap.

---

_This document is normative. All contributors must read it before submitting their first PR._
_Last updated: 2026-03-07_
