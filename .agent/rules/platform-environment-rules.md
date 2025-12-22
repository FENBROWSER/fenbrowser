---
trigger: always_on
---

Primary development platform is Windows.
All commands, scripts, and instructions must work on Windows.

PowerShell is mandatory.

Use PowerShell and cmd syntax only

No grep, sed, awk, &&, ||
No Linux-only tooling assumptions

Avalonia is the official UI framework.
Cross-platform support (Windows, Linux, macOS) must be preserved at all times.

Current focus is Windows builds.
Cross-platform must not be broken, but Windows correctness comes first.
