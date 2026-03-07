# FenBrowser Source Layout

This `/src/` directory defines the **logical architecture** of FenBrowser, mapping guide concepts
to the actual .NET project files. Each sub-directory contains a `MANIFEST.md` listing the
canonical source files that implement that layer.

> **Note**: The build system uses the `.csproj` projects at the root. The `src/` tree is an
> architectural reference layer — files live in their .NET projects but are symbolically
> catalogued here for navigation and ownership clarity.

## Directory Map

| Directory | Layer | Source Project | Description |
|-----------|-------|---------------|-------------|
| `PAL/` | Platform Abstraction | `FenBrowser.Core` | OS-agnostic interfaces for file I/O, threading, networking primitives |
| `SAL/` | Security Abstraction | `FenBrowser.Core` | Capability model, sandbox policy, privilege separation |
| `IPC/` | Inter-Process Comms | `FenBrowser.Host` | Named-pipe channels, capability tokens, message envelopes |
| `Engine/` | JS Engine | `FenBrowser.FenEngine` | Bytecode VM, JIT, builtins, execution contexts |
| `DOM/` | DOM + HTML Parser | `FenBrowser.FenEngine`, `FenBrowser.Core` | HTML tokenizer, tree builder, DOM V2 nodes |
| `CSS/` | CSS Pipeline | `FenBrowser.FenEngine` | Tokenizer, parser, cascade, style computation |
| `Layout/` | Layout Engine | `FenBrowser.FenEngine` | Box model, incremental layout, dirty bits |
| `Network/` | Network Process | `FenBrowser.Host` | Fetch API, CORS, referrer policy, MIME sniffing |
| `GPU/` | GPU / Compositor | `FenBrowser.Host` | Display list, rasterisation, vsync, frame pacing |
| `Utility/` | Utility Processes | `FenBrowser.Host` | Image/font/media/PDF decoders (isolated) |
| `Accessibility/` | A11y Tree | `FenBrowser.Core` | ARIA roles, AccName, AT-SPI/UIA/NSAccessibility bridges |
| `Storage/` | Storage | `FenBrowser.Core` | Cookies, localStorage, HTTP cache (partitioned) |
| `Security/` | Security Hardening | `FenBrowser.Core` | CORB, OOPIF, storage partitioning, CSP |
| `WebIDL/` | WebIDL Bindings | `FenBrowser.Core`, `FenBrowser.WebIdlGen` | IDL source files, generator, generated C# stubs |
| `Tests/` | Conformance | `FenBrowser.Tests`, `FenBrowser.Test262`, `FenBrowser.WPT` | Test suites, conformance runners |

## Architectural Rules

1. **PAL ← everything**: all OS calls go through PAL interfaces. No raw `System.IO`, `System.Threading`,
   or P/Invoke except inside PAL implementations.
2. **SAL gates capability grants**: no process spawning, network access, or file creation without a
   SAL capability check.
3. **IPC is the only inter-process channel**: renderer, network, GPU, utility processes communicate
   exclusively through the typed IPC envelopes in `IPC/`.
4. **Engine is isolated from DOM wrappers**: the bytecode VM has no direct DOM dependency;
   DOM objects reach the VM only through WebIDL bindings.
5. **No shared mutable state across process boundaries**: all data crossing IPC is serialised and validated.

## Adding a New Module

1. Identify which layer it belongs to (PAL/SAL/IPC/Engine/DOM/CSS/Layout/…)
2. Implement in the correct .NET project
3. Add an entry to the relevant `src/<layer>/MANIFEST.md`
4. Update this README if a new layer is needed
5. Follow the DoD checklist in `docs/DEFINITION_OF_DONE.md`
