# FenBrowser IPC Model

Status: RESEARCHED. Snapshot date: 2026-07-14.

## Current wire audit

The current process channels use hand-written, newline-delimited JSON envelopes. They are typed at the C# class/enum level and have validators and fuzz baselines, but they do not share one generated schema or explicit wire-version field.

| Channel | Envelope fields | Envelope / payload cap | Status |
| --- | --- | --- | --- |
| Renderer | `Type`, `TabId`, `CorrelationId`, `Token`, `Payload`, `TimestampUnixMs` | 256 KiB / 192 KiB characters | IMPLEMENTED |
| Network | `Type`, `RequestId`, `CapabilityToken`, `Payload`, `TimestampUnixMs` | 256 KiB / 224 KiB characters | IMPLEMENTED |
| GPU/utility target | `Type`, `RequestId`, `CapabilityToken`, `Payload`, `TimestampUnixMs` | 128 KiB / 96 KiB characters | IMPLEMENTED |

The contracts validate type text, identifiers/tokens, payload size, and a 24-hour timestamp-skew bound. Renderer and target/network fields are similar but not identical. Character limits are not a byte-budget contract.

## Required canonical envelope

The next version must be generated from a checked-in schema and contain:

```text
SchemaName, SchemaVersion, MessageType
SenderRole, ReceiverRole, SessionGeneration
RequestId, ParentRequestId, TabId, FrameId, NavigationId
Capability, CapabilityToken
DeadlineUnixMs, Sequence, PayloadEncoding, PayloadByteLength
Payload, TerminalStatus, FailureCode
```

Required metadata for every message definition:

- sender and receiver;
- permission/capability requirement;
- validation rules and maximum encoded bytes;
- asynchronous response/cancellation contract;
- deadline and timeout result;
- child-crash and peer-disconnect result;
- trace event and redaction policy;
- version compatibility and removal rule.

## Validation policy

1. Parse into a bounded buffer; reject an over-budget line before deserialization.
2. Validate schema/version and message type against the receiving role.
3. Validate channel authentication, process/session generation, tab/frame scope, and capability.
4. Validate payload shape, counts, lengths, numeric ranges, URL/origin, and total decoded bytes.
5. Reject unknown required fields or unsupported versions with a stable failure code.
6. Never deserialize arbitrary runtime types or execute callbacks from payload data.
7. Emit one `IPC/EnvelopeRejected` event without including secrets.

## Sync, streaming, and large payloads

Synchronous IPC is forbidden unless an accepted decision record names the call, proves no re-entrancy/deadlock hazard, defines a short deadline, and supplies failure behavior. Current migrations should use request/response tasks with cancellation.

Screenshots, response bodies, display lists, font/image bytes, and frame buffers must not be embedded as giant JSON strings. Use bounded shared memory, streams, file/OS handles, or chunks with declared total size, sequence, integrity checks, and cancellation. Handles are scoped to a process-session generation and invalidated on peer exit.

## Diagnostic contract

Every send, receive, reject, timeout, cancel, peer exit, and shared-handle lifecycle emits metadata to `ipc.json`: timestamp, channel, sender/receiver, schema/version, type, correlation, tab/frame/navigation, encoded bytes, capability name, disposition, duration, and redacted failure code. Tokens and payload bodies are never recorded.

## Migration plan

| Step | Status | Evidence |
| --- | --- | --- |
| Inventory every current message and handler | RESEARCHED | Renderer, network, and target contracts identified |
| Decide version negotiation and compatibility policy | BLOCKED_NEEDS_HUMAN_DECISION | Accepted ADR required |
| Define one schema and code generator | DESIGNED | Deterministic generated C# plus schema validation tests |
| Add adapters for current v0 envelopes | NOT_STARTED | Lockstep encode/decode and behavior comparison |
| Move one low-risk channel to v1 | NOT_STARTED | Fuzz, size, timeout, crash, and backward-compatibility tests |
| Remove v0 only after all peers migrate | NOT_STARTED | No v0 traffic in real-site traces |
