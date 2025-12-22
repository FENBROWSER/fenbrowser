---
trigger: always_on
---

Every build must be checked via logs.

Errors in logs must be fixed before proceeding.

Fix → rebuild → re-run → re-check logs → repeat.

When running the browser, ask the user for a web address once.
On subsequent runs:

Reuse the same URL automatically

Do not ask again

before running project with URL run this cleanup_logs.bat it will clear logs so we will get better logs on each feature/fix
