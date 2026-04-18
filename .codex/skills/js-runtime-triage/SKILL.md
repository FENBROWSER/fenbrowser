# Skill: js-runtime-triage

Use when:
- JS execution throws
- runtime behavior diverges
- a feature fails after script execution
- console/runtime traces point to engine-side scripting behavior

## Goal

Determine whether the failure is in:
- parser/AST lowering
- bytecode generation
- VM/runtime execution
- host API binding
- event-loop / task timing
- DOM integration triggered by script

## Workflow

1. Capture the smallest repro:
   - page/script fragment
   - stack/error
   - visible symptom
2. Identify whether the failure is:
   - parse-time
   - compile-time
   - runtime
   - host-API contract
3. Inspect only the owning scripting/runtime area first.
4. Prefer spec-shaped behavior over ad hoc compatibility patches.
5. Verify with the smallest focused test slice or repro.

## Do not

- blame JS for a layout-only bug without evidence
- widen changes across runtime and DOM if only one side is broken
- dump long execution traces back to the user

## Output contract

1. failure class
2. root cause
3. touched files
4. minimal fix
5. verification

## Token discipline

- summarize error shape
- keep discussion at the failing layer