# FenBrowser WebIDL and Bindings Tracker

Status: IMPLEMENTED generator and TESTED inventory, STUBBED runtime integration. Snapshot date: 2026-07-16.

## Current truth

`FenBrowser.WebIdlGen` contains a parser/generator and checked-in IDL inputs. `FenBrowser.FenEngine.csproj` explicitly removes `Bindings/Generated/**/*.cs` from compilation, so generated classes are not the active browser binding layer. The current runtime uses manual host dispatch in FenEngine. Core now embeds those IDL inputs for missing-property classification only; this metadata lookup does not activate generated bindings, create wrappers, or change host dispatch.

`FenBrowser.Tooling webidl-inventory` now produces the deterministic offline audit under `Results/webidl/manual-binding-inventory/`. At commit `d95f74e0a5f725675a66f96652bb81a339d6ca15`, it reports 55 definition records, 402 members, 255 members with bounded manual-source evidence, 300 with name-correlated active tests, 73 with name-correlated selected-WPT coverage, zero generated outputs present, and zero generated outputs compiled. Source, test, and WPT correlations are candidates for behavioral review, not proof of full conformance.

## Binding pipeline

| Stage | Status | Evidence | Closure condition |
| --- | --- | --- | --- |
| Parse WebIDL | IMPLEMENTED | Generator project and IDL inputs exist | Parser fixtures for required grammar |
| Generate C# surfaces | IMPLEMENTED | Generator code exists | Deterministic output snapshot/build |
| Inventory manual/generated overlap | TESTED | Deterministic JSON and Markdown audit; compiled fixture; two identical repo-scale hashes | Review one candidate against the active call path |
| Compile generated bindings | NOT_STARTED | Generated path is excluded from FenEngine; inventory reports zero compiled generated outputs | One selected family compiles behind an explicit integration seam |
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

Do not begin with all DOM interfaces. The deterministic inventory names `EventInit` as the first low-lifetime-risk candidate because it is a value-only dictionary, has no wrapper identity, and has active source/test/WPT name correlations. This is an inventory result, not approval to activate generated bindings. Before side-by-side work, confirm the manual dictionary-conversion call path and add default-value, conversion, and exception tests. Broad activation remains blocked by `BLOCK-MEM-001`.

## Inventory command and artifacts

```powershell
dotnet run --project FenBrowser.Tooling/FenBrowser.Tooling.csproj -c Release --no-build -- webidl-inventory --output-dir Results/webidl/manual-binding-inventory --wpt-root C:/Users/udayk/Videos/wpt --selected-wpt dom/lists/DOMTokenList-stringifier.html,dom/lists/DOMTokenList-value.html,html/semantics/forms/the-input-element/checkbox-click-events.html
```

- `Results/webidl/manual-binding-inventory/webidl_manual_binding_inventory.json`
- `Results/webidl/manual-binding-inventory/webidl_manual_binding_inventory.md`
- Current IDL input SHA-256: `51310A030262A071F7E5DEE8C0012529DFE1FFEC8D47054C569B68D6891D0745`
- Real-site usage remains explicitly `not measured by offline source inventory`; the tool does not turn a source-name match into runtime evidence.

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
