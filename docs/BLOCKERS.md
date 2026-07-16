# FenBrowser Human-Decision Blockers

Snapshot date: 2026-07-16. These blockers do not prevent local diagnostic work. They do prevent agents from silently changing memory ownership, security fallbacks, public IPC, or production process policy.

## BLOCK-MEM-001

- Area: JS/DOM memory ownership
- Status: BLOCKED_NEEDS_HUMAN_DECISION
- Decision required: Choose the authoritative wrapper/DOM ownership and cross-heap cycle strategy.
- Current evidence: `HostObjectTable` holds strong host-object references; `_hostHandleCache` holds reference-keyed handles; listener/property caches use `ConditionalWeakTable`; automatic browser minor GC is disabled because transient roots are incomplete. At commit `6a208540`, same-session lookup reuses one handle, retaining 32 detached nodes grows live/cache/slot counts from 6 to 38, and six document/session resets return all counts to 6 with a stable two-listener window baseline.
- Conflict: `HostObjectEntry` comments describe a weak-reference slot that the current table does not implement.
- Choices that require an ADR: strong table with explicit document teardown; weak host rows plus JS-root retention; an ephemeron/bridge tracer; or another explicit model.
- Work allowed before decision: diagnostics, leak measurement, teardown probes, and GC stress reductions.
- Work blocked: broad generated binding rollout or a change to wrapper lifetime semantics.
- Remaining decision evidence gap: within-document detached-node reclamation, DOM-to-JS cycle collection, callback/observer roots, cross-realm identity, and post-reset managed-graph collection are not proven by the session-reset measurement.

## BLOCK-PROC-001

- Area: production process default
- Status: BLOCKED_NEEDS_HUMAN_DECISION
- Decision required: Decide whether production defaults to brokered mode, in-process mode, or a platform-qualified policy.
- Current evidence: an unset environment value selects in-process mode; brokered mode is opt-in and has renderer/network/GPU/utility code.
- Security consequence: the default determines whether a malicious page shares the UI process.
- Work allowed before decision: brokered smoke tests, crash recovery tests, and trace export.
- Work blocked: changing the default or claiming renderer isolation is the production security boundary.

## BLOCK-PROC-002

- Area: renderer AppContainer runtime-file access
- Status: BLOCKED_NEEDS_HUMAN_DECISION
- Decision required: Choose how the installed/development `FenBrowser.Host` apphost and its runtime dependencies receive read/execute access for the `FenBrowser.RendererMinimal` AppContainer identity.
- Current evidence: strict direct launch resolves the correct `FenBrowser.Host.exe` but `CreateProcessW` fails with native error 2 from the current checkout path. The output directory has no renderer-profile ACE. Temporarily granting the exact profile SID execute traversal on parents and read/execute on the runtime directory removes the immediate spawn error, but the full acceptance still did not complete within 30 seconds. All temporary ACEs were removed and verified absent.
- Security consequence: granting too broad a directory exposes unrelated user/workspace files to a compromised renderer; runtime ACL mutation also creates ownership, upgrade, concurrency, and cleanup obligations.
- Choices requiring approval: installer-owned ACLs on a dedicated runtime directory; a packaged/AppContainer deployment layout; a broker-prepared dedicated child-runtime directory with narrowly managed ACLs; or another reviewed mechanism.
- Work allowed before decision: executable/environment component tests, fail-closed startup attribution, IPC validator tests, and read-only packaging/ACL design.
- Work blocked: automatic ACL mutation, unsandboxed fallback, brokered Google execution, or claiming authenticated renderer startup/navigation/frame/crash acceptance.

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
