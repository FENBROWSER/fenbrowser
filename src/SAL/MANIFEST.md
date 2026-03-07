# SAL — Security Abstraction Layer

Source: `FenBrowser.Core/Security/`, `FenBrowser.Host/ProcessIsolation/Sandbox/`

## Purpose
Defines security policy in OS-agnostic terms.
All privilege decisions go through SAL before any OS call.

## Key Files
- `FenBrowser.Core/SandboxPolicy.cs` — capability grant table
- `FenBrowser.Host/ProcessIsolation/Sandbox/AppContainerSandbox.cs` — Windows AppContainer
- `FenBrowser.Core/Security/Corb/CorbFilter.cs` — CORB read blocking
- `FenBrowser.Core/Security/Oopif/OopifPlanner.cs` — OOPIF site isolation policy

## Capability Model
Every sandboxed process is granted a minimal capability set:
- `Network` capability — only Network process
- `FileRead(path)` capability — only Utility process (for font loading)
- `GPU` capability — only GPU process
- `ChildProcess` capability — only Broker

## Security Properties
- W^X enforcement — pages are either writable or executable, never both
- Pointer authentication on ARM64 (PAC)
- Stack canaries on all native frames
- Arena poison (`0xCD`) on free, `0xFE` on realloc
