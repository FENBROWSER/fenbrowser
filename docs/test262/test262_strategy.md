# test262 Strategy (Test262-First)

Policy:
- test262 is a foundation subsystem from milestone 0.1
- expectations and categorization are maintained with pinned test262 commit metadata
- every feature change states enabled files, unsupported files, expected failures, regressions, and crashers

Milestone order:
1. 0.1 checkout/pin + runner skeleton + dry-run
2. 0.2 parser-only subset
3. 0.3 basic runtime semantic subset
4. 0.4 Object/Array/String/Number subset
5. 0.5 functions/closures/classes subset
6. 0.6 promises/modules subset
7. 0.8 daily conformance dashboard

CI gates:
- regressions blocked for enabled subset
- unknown crashes tracked and driven to zero
- pass/fail/unsupported/skipped counts emitted per run
