# FenBrowser DOM API Tracker

Status: INTEGRATED. Snapshot date: 2026-07-15.

This tracker separates implementation presence from browser-like binding behavior. A manual host member can be INTEGRATED while its WebIDL conversions, descriptors, brand checks, liveness, or exception behavior remain STUBBED.

## Current capability view

| API family | Status | Evidence | Highest-value next proof |
| --- | --- | --- | --- |
| `Node`, `Element`, `Document`, text/comment | INTEGRATED | Google built 572 DOM nodes and booted scripts | Included DOM/binding tests and selected WPT |
| Attributes, `classList`, `dataset` | INTEGRATED | Active manual bridge and real-site use | Descriptor/conversion/live-token tests |
| Selector APIs | INTEGRATED | Style/real-site paths depend on selectors | WPT selector API slice plus `--inspect-selector` command |
| Collections and liveness | INTEGRATED | Host-backed `HTMLCollection` iteration is tested through direct, `for...of`, and spread paths; mutation liveness is unverified | NodeList coverage, mutation liveness, descriptors, and selected WPT |
| `EventTarget` and propagation | INTEGRATED | Browser/input/event paths exist | Capture/target/bubble/cancel/default-action matrix |
| Mouse/keyboard/input/focus events | INTEGRATED | Host input routing exists | Automated Google click/type/submit trace |
| `MutationObserver` | IMPLEMENTED | Source surface exists | Microtask delivery/order/disconnect reductions |
| Custom elements | IMPLEMENTED | Source surface exists | Reaction-stack and upgrade timing WPT |
| Shadow DOM | IMPLEMENTED | Source surface exists | Tree-scope, event retargeting, style boundary tests |
| Templates/fragments | IMPLEMENTED | Parser/DOM sources exist | Clone/adoption/template-content WPT |
| Forms and activation | INTEGRATED | Form/input paths and focused tests exist | Real-site submit and successful-controls matrix |
| CSSOM/getComputedStyle/measurements | INTEGRATED | Google style/layout probe succeeded | Browser-compatible computed and geometry WPT |

## Google missing-member disposition

The current Google bundle is not proof that all emitted names are browser APIs:

| Observation class | Examples | Disposition | Status |
| --- | --- | --- | --- |
| Site-library expando | `closure_uid`, `closure_listenable_*`, `$goog_Thenable` | Exclude from missing-standard-API counts | RESEARCHED |
| Wrong receiver/probe | `Document.className`, `Document.getAttribute` | Record receiver/source, not an API implementation task | RESEARCHED |
| Legacy feature detection | `Navigator.msPointerEnabled` | Record as optional/legacy probe | DEFERRED_SPEC_COMPLIANCE |
| Standards candidate | `Document.compareDocumentPosition`, `Location.toString`, `CharacterData.childNodes`, `Navigator.geolocation` | Confirm descriptor/receiver and causal use before tasking | RESEARCHED |

## Priority rule

A DOM task becomes Priority 1 only when an attributed standards-defined member causes the first fatal exception, prevents a lifecycle/render/input milestone, or reproduces in local WPT/reduction. Silent fake values are forbidden; incomplete compatibility surfaces remain STUBBED.
