# FenBrowser Missing API Tracker

Status: TESTED for schema-v2 runtime classification, assignment-before-read evidence, concrete HTML receiver identity, checked-in-WebIDL receiver evidence, bounded rich-record `debug-site` export, and fresh Google reclassification; STUBBED for prototype/descriptor operation collection. Snapshot date: 2026-07-16.

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
| `Document.compareDocumentPosition` | `STANDARD_API` | no evidence | 2 | TESTED | Receiver-matched inherited `Node` member |
| `CharacterData.childNodes` | `STANDARD_API` | no evidence | 2 | TESTED | Receiver-matched inherited `Node` member |
| `HTMLScriptElement.async`, `HTMLScriptElement.fetchPriority` | `STANDARD_API` | no evidence | 2 | TESTED | Concrete receiver and selected checked-in HTML IDL agree |
| `HTMLLinkElement.as`, `HTMLLinkElement.fetchPriority` | `STANDARD_API` | no evidence | 2 | TESTED | Concrete receiver and selected checked-in HTML IDL agree |
| `Location.toString` | `UNCLASSIFIED` | no evidence | 3 | RESEARCHED | Checked-in metadata does not yet describe stringifier behavior |
| `Navigator.geolocation` | `UNCLASSIFIED` | no evidence | 3 | RESEARCHED | Optional capability probe; no checked-in receiver evidence |
| `Navigator.msPointerEnabled` | `LEGACY_PROBE` | no | 4 | DEFERRED_SPEC_COMPLIANCE | Explicit legacy Microsoft inventory entry |
| `HTML*Element.closure_*`, `Document.closure_*`, `HTMLDivElement.$goog_Thenable` | `UNCLASSIFIED` | no evidence | 5 | RESEARCHED | Names suggest page bookkeeping, but no assignment-before-read or descriptor evidence was captured |
| `Document.className`, `Document.getAttribute` | `WRONG_RECEIVER` | no evidence | 3 | TESTED | Checked-in IDL defines these members on `Element`, not `Document` |

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

`MissingApiTrackerTests` and `DebugSiteMissingApiClassificationTests` are compiled and discovered. The focused 2026-07-16 command passes `10/10`, including concrete `HTMLScriptElement`, `HTMLLinkElement`, and `HTMLImageElement` assignment receivers, the 512-record bound, cross-navigation identity, and bundle projection. Local evidence remains `logs/real-site/file_c_users_udayk_videos_fenbrowser-test_fenbrowser.tests_fixtures_diagnostics_missing_api_classification.html/20260716T105848Z/`.

Fresh Google evidence is `logs/real-site/www.google.com/20260716T110854Z/`. It retains 25/25 records: 9 `STANDARD_API`, 2 `WRONG_RECEIVER`, 1 `LEGACY_PROBE`, and 13 `UNCLASSIFIED`. The previously generic `Element.async`, `Element.fetchPriority`, and `Element.as` writes are now receiver-matched `HTMLScriptElement`/`HTMLLinkElement` standards records. Callback failures and exceptions remain zero, `first_blocker.json` reports `none`, logger drain succeeds, the main UI is visible, and all 26 manifest entries exist. The unclassified Closure-style names require descriptor/prototype assignment evidence; they are not promoted by name pattern.
