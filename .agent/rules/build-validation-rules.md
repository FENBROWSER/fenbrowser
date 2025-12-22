---
trigger: always_on
---

Before every build, terminate all Fen Browser processes.
Use PowerShell process management commands.

Every feature must be built locally.
No unbuilt code is considered valid.

Every feature must be runtime-tested.
Build → Run → Observe logs → Fix → Repeat.

You must ask the user to verify functionality.
Only proceed after the user confirms:

“working”
“ok”
"done"
