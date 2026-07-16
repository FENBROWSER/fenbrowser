# FenBrowser Missing API Tracker

Status: TESTED for schema-v2 runtime classification, assignment-before-read evidence, checked-in-WebIDL receiver evidence, and bounded rich-record `debug-site` export; STUBBED for prototype/descriptor operation collection and a fresh Google reclassification. Snapshot date: 2026-07-16.

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
  "schemaVersion": 2,
  "schema": "fenbrowser.missing-apis.v2",
  "totalRecordCount": 1,
  "retainedRecordCount": 1,
  "truncated": false,
  "records": [
    {
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
      "definedInterface": "Node",
      "receiverMatchesDefinedInterface": true
    }
  ]
}
```

The bundle snapshots the runtime tracker after the logger drain, retains the first 512 ordered records, reports total/retained/truncated counts, and does not retain receiver object graphs. Stable identity includes site, receiver brand, member, script, and navigation, so the same observation in a replacement navigation remains distinct. Tracker export failures are logged and isolated from page execution.

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

`MissingApiTrackerTests` and `DebugSiteMissingApiClassificationTests` are compiled and discovered. The focused 2026-07-16 command passes `9/9`, including the 512-record bound, cross-navigation identity, and bundle projection. Fresh local evidence is `logs/real-site/file_c_users_udayk_videos_fenbrowser-test_fenbrowser.tests_fixtures_diagnostics_missing_api_classification.html/20260716T105848Z/`: the bundle retains four rich records, including `Document.applicationState` as `WRITE`/`SITE_EXPANDO` and `Document.charset` as a receiver-matched `STANDARD_API`; logger drain succeeds, first blocker is `none`, screenshot capture succeeds, and all 26 manifest entries exist. Required next proof is a fresh Google rerun.
