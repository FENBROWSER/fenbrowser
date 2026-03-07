# PAL — Platform Abstraction Layer

Source: `FenBrowser.Core/Platform/`

## Purpose
Provides OS-agnostic interfaces for all platform operations.
Engine components must call PAL, not OS APIs directly.

## Key Files
- `FenBrowser.Core/Platform/` — PAL interfaces and implementations
- `FenBrowser.Core/SandboxPolicy.cs` — Sandbox capability grants
- `FenBrowser.Host/ProcessIsolation/Sandbox/` — OS-specific sandbox backends

## Interfaces
- `IPalFileSystem` — file read/write/stat/watch
- `IPalNetwork` — socket creation (used by Network process only)
- `IPalThread` — thread creation and priority
- `IPalMemory` — virtual memory allocation (used by arena allocator)
- `IPalTimer` — high-resolution timers

## Platform Implementations
| Platform | Implementation |
|----------|---------------|
| Windows | `WindowsPal` using Win32 APIs |
| Linux | `LinuxPal` using libc P/Invoke |
| macOS | `MacOsPal` using libSystem P/Invoke |
