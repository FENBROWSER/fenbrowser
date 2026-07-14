# FenBrowser Human-Decision Blockers

Snapshot date: 2026-07-14. These blockers do not prevent local diagnostic work. They do prevent agents from silently changing memory ownership, security fallbacks, public IPC, or production process policy.

## BLOCK-MEM-001

- Area: JS/DOM memory ownership
- Status: BLOCKED_NEEDS_HUMAN_DECISION
- Decision required: Choose the authoritative wrapper/DOM ownership and cross-heap cycle strategy.
- Current evidence: `HostObjectTable` holds strong host-object references; `_hostHandleCache` holds reference-keyed handles; listener/property caches use `ConditionalWeakTable`; automatic browser minor GC is disabled because transient roots are incomplete.
- Conflict: `HostObjectEntry` comments describe a weak-reference slot that the current table does not implement.
- Choices that require an ADR: strong table with explicit document teardown; weak host rows plus JS-root retention; an ephemeron/bridge tracer; or another explicit model.
- Work allowed before decision: diagnostics, leak measurement, teardown probes, and GC stress reductions.
- Work blocked: broad generated binding rollout or a change to wrapper lifetime semantics.

## BLOCK-PROC-001

- Area: production process default
- Status: BLOCKED_NEEDS_HUMAN_DECISION
- Decision required: Decide whether production defaults to brokered mode, in-process mode, or a platform-qualified policy.
- Current evidence: an unset environment value selects in-process mode; brokered mode is opt-in and has renderer/network/GPU/utility code.
- Security consequence: the default determines whether a malicious page shares the UI process.
- Work allowed before decision: brokered smoke tests, crash recovery tests, and trace export.
- Work blocked: changing the default or claiming renderer isolation is the production security boundary.

## BLOCK-NET-001

- Area: network privilege and fallback
- Status: BLOCKED_NEEDS_HUMAN_DECISION
- Decision required: Define whether network-process startup or request failure may fall back to in-process network access.
- Current evidence: `NetworkProcessCoordinator` explicitly uses a fallback `HttpClient`; no active resource caller of `SendAsync` was found.
- Security consequence: a permissive fallback defeats renderer network denial.
- Work allowed before decision: route a controlled request through the child and capture policy evidence.
- Work blocked: treating the current child process as the enforced network boundary.

## BLOCK-IPC-001

- Area: IPC public contract
- Status: BLOCKED_NEEDS_HUMAN_DECISION
- Decision required: Approve schema/version compatibility, sender/receiver identity, permission identifiers, timeout semantics, and migration strategy.
- Current evidence: renderer, network, and GPU/utility messages use separate JSON envelopes with validation and size caps but without a shared version field.
- Work allowed before decision: read-only audit, `ipc.json` trace export, fuzzing existing validators, and size-limit regression tests.
- Work blocked: incompatible envelope changes or adopting a new serializer/generator as architecture policy.
