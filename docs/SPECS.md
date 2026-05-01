# FenBrowser Spec Ownership Map

State as of: 2026-04-30
Owner: Architecture Track

## Purpose

This document maps engine subsystems to normative specifications and code owners.
It is the source of truth for:
- spec-to-code ownership
- capability tracking IDs
- compliance accountability during implementation and review

## Required Source Header Contract

For core runtime files in `FenBrowser.Core`, `FenBrowser.FenEngine`, and process-isolation contracts in `FenBrowser.Host`, use this header form:

```csharp
// SpecRef: WHATWG HTML 8.1.7 Event loops
// CapabilityId: EVENTLOOP-MICROTASK-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
```

Rules:
- `SpecRef` must name the normative section.
- `CapabilityId` must exist in `docs/COMPLIANCE_MATRIX.md`.
- `Determinism` is `strict` or `best-effort`.
- `FallbackPolicy` is `clean-unsupported` or `spec-defined`.

## Subsystem Specification Map

| Subsystem | Normative Spec | Primary Owner Project | Owner Entry Point | Status |
| --- | --- | --- | --- | --- |
| HTML Tokenization + Tree Construction | WHATWG HTML, Parsing | `FenBrowser.Core` | `Core/Parsing/*` | Partial |
| DOM Core + Mutation | DOM Living Standard | `FenBrowser.Core` + `FenBrowser.FenEngine` | `Core/Dom/V2/*`, `FenEngine/DOM/*` | Partial |
| Event Loop + Task/Microtask | WHATWG HTML, Web App APIs | `FenBrowser.FenEngine` | `FenEngine/Core/EventLoop/*` | Partial |
| CSS Syntax + Parsing | CSS Syntax Level 3 | `FenBrowser.FenEngine` | `FenEngine/Rendering/Css/CssSyntaxParser.cs` | Partial |
| CSS Tokenization | CSS Syntax Level 3 tokenization | `FenBrowser.FenEngine` | `FenEngine/Rendering/Css/CssTokenizer.cs` | Partial |
| CSS Values + Computation | CSS Values and Units Level 4 | `FenBrowser.FenEngine` | `FenEngine/Rendering/Css/CssLoaderValueParsing.cs` | Partial |
| CSS Cascade + Inheritance | CSS Cascade Level 4/5 | `FenBrowser.FenEngine` | `FenEngine/Rendering/Css/CascadeEngine.cs` | Partial |
| CSS Cascade Layers | CSS Cascade Level 5 | `FenBrowser.FenEngine` | `FenEngine/Rendering/Css/CascadeKey.cs` | Partial |
| Selectors | Selectors Level 4 | `FenBrowser.FenEngine` | `FenEngine/Rendering/Css/SelectorMatcher.cs` | Partial |
| Advanced Selectors | Selectors Level 4 functional pseudo classes | `FenBrowser.FenEngine` | `FenEngine/Rendering/Css/CssSelectorAdvanced.cs` | Provisional |
| CSSOM Stylesheet APIs | CSSOM | `FenBrowser.FenEngine` | `FenEngine/DOM/DocumentWrapper.cs`, `FenEngine/DOM/ElementWrapper.cs` | Partial |
| Containment + Container Queries | CSS Containment Level 3 + Conditional Rules Level 5 | `FenBrowser.FenEngine` | `FenEngine/Rendering/Css/CssLoader.cs` | Provisional |
| Layout (Block/Inline/Flex/Grid/Table) | CSS2.1 + Flexbox + Grid | `FenBrowser.FenEngine` | `FenEngine/Layout/*` | Partial |
| Layout (Multi-column + Positioning) | CSS Multi-column + CSS2.2 positioned layout | `FenBrowser.FenEngine` | `FenEngine/Layout/MultiColumnLayoutComputer.cs`, `FenEngine/Layout/Contexts/BlockFormattingContext.cs` | Partial |
| Painting + Compositing | CSS2.1 painting order + compositing model | `FenBrowser.FenEngine` | `FenEngine/Rendering/PaintTree/*`, `SkiaDomRenderer.cs` | Partial |
| Animations + Transitions | CSS Animations Level 1 + CSS Transitions Level 2 | `FenBrowser.FenEngine` | `FenEngine/Rendering/Css/CssAnimationEngine.cs` | Provisional |
| Typed OM | CSS Typed OM Level 1 | `FenBrowser.FenEngine` | `FenEngine/DOM/*` + runtime surface | Unsupported |
| Houdini Worklets | CSS Painting API / Houdini worklets | `FenBrowser.FenEngine` + `FenBrowser.Host` | worklet runtime/bridge surfaces | Unsupported |
| JavaScript Runtime + Built-ins | ECMAScript (ECMA-262) | `FenBrowser.FenEngine` | `FenEngine/Core/*`, `Scripting/JavaScriptEngine.cs` | Partial |
| WebIDL Binding Generation | Web IDL Living Standard | `FenBrowser.Core` | `Core/WebIDL/WebIdlBindingGenerator.cs` | Partial |
| Fetch/CORS/CSP/Cookies | WHATWG Fetch + RFC6265 + CSP | `FenBrowser.Core` + `FenBrowser.Host` process targets | `Core/Resource/*`, `Host/ProcessIsolation/Network/*` | Partial |
| Process Isolation + Sandbox Policy | Project security contract + process model | `FenBrowser.Host` + `FenBrowser.Core` | `Host/ProcessIsolation/*`, `Core/Security/*` | Partial |

## Ownership Rules

- A capability has exactly one primary owner project.
- Cross-project capabilities must designate one primary owner and one integration owner.
- No capability may be marked `Complete` without:
  - a linked deterministic test
  - a linked implementation owner file
  - a linked spec reference
