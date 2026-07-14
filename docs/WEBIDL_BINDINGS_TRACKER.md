# FenBrowser WebIDL and Bindings Tracker

Status: IMPLEMENTED generator, STUBBED runtime integration. Snapshot date: 2026-07-14.

## Current truth

`FenBrowser.WebIdlGen` contains a parser/generator and checked-in IDL inputs. `FenBrowser.FenEngine.csproj` explicitly removes `Bindings/Generated/**/*.cs` from compilation, so generated classes are not the active browser binding layer. The current runtime uses manual host dispatch in FenEngine.

## Binding pipeline

| Stage | Status | Evidence | Closure condition |
| --- | --- | --- | --- |
| Parse WebIDL | IMPLEMENTED | Generator project and IDL inputs exist | Parser fixtures for required grammar |
| Generate C# surfaces | IMPLEMENTED | Generator code exists | Deterministic output snapshot/build |
| Compile generated bindings | NOT_STARTED | Generated path is excluded from FenEngine | One selected family compiles behind an explicit integration seam |
| Interface/prototype objects | STUBBED | Manual runtime exposes browser objects | Descriptor/prototype/constructor tests |
| Type conversion and overload resolution | STUBBED | Per-member manual conversion | WebIDL conversion matrix and exception tests |
| Brand checks and `this` validation | STUBBED | Manual receiver handling | WPT/local wrong-receiver reductions |
| Optional/nullable/default/dictionary/enum | STUBBED | No generated integrated proof | Generated test matrix |
| Callbacks/promises/sequences/records | STUBBED | Manual host callbacks exist | Lifetime plus conversion/rejection tests |
| Iterable/maplike/setlike | NOT_STARTED | No integrated generated proof | WPT and generated surface tests |
| Wrapper identity/lifetime | BLOCKED_NEEDS_HUMAN_DECISION | Current strong handle/cache model | Accepted memory ADR and teardown tests |

## API-family workflow

For each family:

1. select the standards-defined IDL and record source/spec revision;
2. generate deterministic C# descriptors, conversions, overloads, brand checks, exceptions, and operation stubs;
3. map implementation calls to the existing Core/FenEngine owner without duplicating DOM logic;
4. run generated binding unit tests plus selected local WPT;
5. run the same real-site reduction in manual and generated modes;
6. activate only after wrapper/lifetime rules for that family are satisfied;
7. update this tracker and the missing API disposition.

## First integration candidate

Do not begin with all DOM interfaces. After `BLOCK-MEM-001` is resolved, choose one low-lifetime-risk value/interface family with current real-site relevance and existing implementation. The candidate must avoid callbacks, observers, cross-realm identity, and complex collections. Selection itself remains DESIGNED until source/WPT evidence names the family.

## Required binding record

```text
Interface:
IDL source:
Generated surface:
Implementation owner:
Conversions:
Overloads:
Brand/this checks:
Descriptors/prototype:
Wrapper identity:
Lifetime roots:
WPT/local tests:
Real-site link:
Status:
Evidence:
```
