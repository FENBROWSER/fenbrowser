# FenBrowser Missing API Tracker

Status: TESTED at the runtime recorder, STUBBED at the `debug-site` bundle boundary. Snapshot date: 2026-07-14.

## Evidence model

A missing host property observation must be classified before it becomes an engine task.

| Disposition | Meaning |
| --- | --- |
| `standards-member` | Confirmed interface member on the receiver/prototype |
| `wrong-receiver` | Standards member may exist, but not on the observed receiver brand |
| `site-expando` | Page-owned bookkeeping property; returning `undefined` is normal until assigned |
| `feature-detection` | Optional or legacy probe whose absence is non-fatal |
| `binding-mismatch` | Member exists in C# but JS exposure/conversion/descriptor is wrong |
| `unclassified` | Needs spec/source/reduction evidence |

Only `standards-member` and `binding-mismatch` entries can be promoted directly to implementation tasks. Fatality still requires a causal exception or blocked milestone.

## Target record format

```json
{
  "schema": "fenbrowser.missing-api.v2",
  "api_name": "Node.prototype.compareDocumentPosition",
  "object_or_prototype": "Node.prototype",
  "observed_receiver": "Document",
  "property_name": "compareDocumentPosition",
  "site_url": "https://www.google.com/",
  "script_url": "https://...",
  "script_id": "script-10",
  "line": 33,
  "column": 21079,
  "navigation_id": "1",
  "first_seen_trace_id": "missing-api-...",
  "first_seen_utc": "...",
  "last_seen_utc": "...",
  "encounter_count": 6,
  "exception_text": "",
  "disposition": "standards-member",
  "fatal": false,
  "failure_bucket": "F",
  "priority": 2,
  "linked_wpt": [],
  "owner": "DOM bindings",
  "status": "RESEARCHED",
  "evidence": ["bundle path and reduction"],
  "notes": "Inherited from Node; confirm JS prototype exposure."
}
```

The bundle must preserve the runtime tracker's script, navigation, source, first-seen, last-seen, reason, and exception fields. The current simplified bundle record drops those fields.

## Current Google observations

| Observed property | Proposed disposition | Fatal | Priority | Status | Reason |
| --- | --- | --- | --- | --- | --- |
| `Document.compareDocumentPosition` | standards-member candidate | no evidence | 2 | RESEARCHED | `Document` inherits `Node`; reduce prototype exposure |
| `Location.toString` | standards-member candidate | no evidence | 2 | RESEARCHED | Confirm WebIDL/stringifier behavior |
| `CharacterData.childNodes` | standards-member candidate | no evidence | 2 | RESEARCHED | `CharacterData` inherits `Node`; reduce inherited binding |
| `Navigator.geolocation` | standards-member candidate | no evidence | 3 | RESEARCHED | Optional capability probe; no visible blocker |
| `Navigator.msPointerEnabled` | feature-detection | no | 4 | DEFERRED_SPEC_COMPLIANCE | Legacy Microsoft probe |
| `Element.closure_*`, `Document.closure_*` | site-expando candidate | no evidence | 5 | RESEARCHED | Page bookkeeping names, not WebIDL names |
| `Element.$goog_Thenable` | site-expando candidate | no evidence | 5 | RESEARCHED | Framework marker |
| `Document.className`, `Document.getAttribute` | wrong-receiver candidate | no evidence | 3 | RESEARCHED | Element members probed on Document |

No Google observation is currently confirmed as the first fatal blocker.

## Implementation rules

1. Record undefined reads without changing JS semantics.
2. Deduplicate by site, receiver brand, member, script, and navigation; preserve first seen.
3. Do not suppress arbitrary names by regex alone; classify with provenance so genuine framework-used APIs remain visible.
4. Join an observation to the subsequent exception/task/script record when possible.
5. Link selected local WPT paths only after verifying them in the local WPT checkout.
6. Never return a fake success value to quiet a site. A compatibility stub remains `STUBBED`.
7. Redact sensitive URLs and data while retaining stable hashes/correlation IDs.

## Verification

`MissingApiTrackerTests` is discovered and passed in the 44-test focused Release filter on 2026-07-14. Required next proof is a local expando-versus-standard-member reduction followed by a fresh Google bundle whose first missing API is classified rather than merely listed.
