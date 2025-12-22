---
trigger: always_on
---

Logging is mandatory for all implemented features.

Logs must be meaningful and diagnostic.
Each log should answer:

What failed?

Where?

Why?

How to fix?

Logging must be modular.

Enable/disable per feature or module

No global spam logging

Logging must be toggleable with a single control.
One switch to turn logs on/off.

Do not remove logs during development.
No log removal until: All major bugs are fixed and Project is production-ready

If logging framework lacks features, improve it first.
Logging system upgrades take priority over feature work.

If a feature generates excessive logs, optimize logging—not remove it.
