# FenJS Threat Model

Attacker capabilities:
- arbitrary JavaScript input
- parser edge cases and deep recursion
- memory pressure and object graph abuse
- coercion/prototype/getter/setter reentrancy abuse
- host-wrapper lifetime and stale-handle abuse

Security invariants:
- script failure may terminate script execution
- fatal JS engine failure may terminate renderer process
- no native pointer exposure from JS values
- no stale host handle reuse after navigation epoch changes
- no bypass of realm/origin/permission checks
- no sandbox escape through JS runtime paths

Required validations for host operations:
- handle index and generation
- realm and origin
- frame/document alive state
- navigation epoch
- permission checks
