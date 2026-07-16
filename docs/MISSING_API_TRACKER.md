# FenBrowser Missing API Tracker

Status: TESTED for schema-v2 runtime classification and the compact `debug-site` projection; STUBBED for assignment/prototype/IDL evidence collection and rich-record bundle merging. Snapshot date: 2026-07-16.

## Evidence model

A missing host property observation must be classified before it becomes an engine task.

| Disposition | Meaning |
| --- | --- |
| `STANDARD_API` | Checked-in WebIDL evidence confirms the member on the receiver/interface |
| `WRONG_RECEIVER` | Checked-in WebIDL evidence confirms the member, but not on the observed receiver brand |
| `SITE_EXPANDO` | Assignment-before-read evidence identifies page-owned bookkeeping state |
| `LEGACY_PROBE` | The owner/member pair is in the explicit legacy API inventory |
| `UNCLASSIFIED` | Evidence is insufficient; this is the default for unknown reads |

Only `STANDARD_API` records set `standardPriorityEligible: true`. Fatality still requires a causal exception or blocked milestone.

## Target record format

```json
{
  "schema": "fenbrowser.missing-apis.v2",
  "apiName": "Document.compareDocumentPosition",
  "objectOrPrototype": "Document",
  "receiverType": "Document",
  "propertyName": "compareDocumentPosition",
  "siteUrl": "https://example.test/",
  "scriptUrl": "https://example.test/app.js",
  "scriptId": "script-10",
  "line": 33,
  "column": 21079,
  "navigationId": "1",
  "firstSeenTraceId": "missing-api-...",
  "firstSeenUtc": "...",
  "lastSeenUtc": "...",
  "encounterCount": 6,
  "exceptionText": "",
  "classification": "STANDARD_API",
  "operationKind": "READ",
  "classificationReason": "known-webidl-member-defined-on-Node",
  "standardPriorityEligible": true,
  "assignmentBeforeRead": false,
  "knownWebIdlMember": true,
  "definedInterface": "Node"
}
```

The compact bundle now preserves classification, operation kind, reason, and standards-priority eligibility. Merging the runtime tracker's script, navigation, source, receiver, first-seen, last-seen, reason, and exception fields into that bundle remains open.

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

`MissingApiTrackerTests` and `DebugSiteMissingApiClassificationTests` are compiled and discovered. The focused 2026-07-16 command passes `4/4`. Fresh local evidence is `logs/real-site/file_c_users_udayk_videos_fenbrowser-test_fenbrowser.tests_fixtures_diagnostics_missing_api_classification.html/20260716T103201Z/`: `Document.applicationSpecificMarker` is `UNCLASSIFIED`, `Navigator.msPointerEnabled` is `LEGACY_PROBE`, both are `READ`, and neither is standards-priority eligible. Required next proof is assignment-before-read and checked-in-IDL receiver instrumentation followed by a fresh Google rerun.
