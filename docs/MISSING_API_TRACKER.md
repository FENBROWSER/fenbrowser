# FenBrowser Missing API Tracker

Status: TESTED for schema-v2 runtime classification, ordered read/write/descriptor/property-check evidence, descriptor target identity, boolean function-prototype marker evidence, concrete HTML receiver identity, checked-in-WebIDL receiver evidence, bounded rich-record `debug-site` export, and fresh Google reclassification; STUBBED for explicit prototype-operation collection and stringifier metadata. Snapshot date: 2026-07-16.

## Evidence model

A missing host property observation must be classified before it becomes an engine task.

| Disposition | Meaning |
| --- | --- |
| `STANDARD_API` | Checked-in WebIDL evidence confirms the member on the receiver/interface |
| `WRONG_RECEIVER` | Checked-in WebIDL evidence confirms the member, but not on the observed receiver brand |
| `SITE_EXPANDO` | A page-owned host assignment or boolean marker defined on a script function's instance prototype identifies bookkeeping/protocol state |
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
      "descriptorTargetIsPrototype": false,
      "functionPrototypeMarkerObserved": false,
      "knownWebIdlMember": true,
      "definedInterface": "Node",
      "receiverMatchesDefinedInterface": true
    }
  ]
}
```

The bundle snapshots the runtime tracker after the logger drain, retains the first 512 ordered records, reports total/retained/truncated counts, and does not retain receiver object graphs. Each record preserves the first operation plus the bounded ordered set of observed operation kinds, assignment timing, descriptor target identity, and boolean function-prototype-marker evidence. Missing host checks distinguish `READ`, `IN_CHECK`, and `DESCRIPTOR_OPERATION`; `Object.getOwnPropertyDescriptor` observes the miss without invoking a getter or changing its `undefined` result. A known WebIDL member queried as an own descriptor on an instance remains `UNCLASSIFIED` unless the target is proven to be the defining prototype. The FenJS side marks prototype objects without rooting them; the browser host retains at most 2,048 keys of at most 256 characters per document and accepts only `true` marker values. Stable identity includes site, receiver brand, member, script, and navigation, so the same observation in a replacement navigation remains distinct. Tracker export failures are logged and isolated from page execution.

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
| `HTML*Element.closure_lm_*`, `HTML*Element.closure_uid_*`, `Document.closure_lm_*` | `SITE_EXPANDO` | no | 5 | TESTED | Fresh records preserve ordinary read-then-write page assignment evidence |
| `HTML*Element.closure_listenable_*`, `Document.closure_listenable_*`, `HTMLDivElement.$goog_Thenable` | `SITE_EXPANDO` | no | 5 | TESTED | Matching page script defined the same key with boolean `true` on a function instance prototype; no name-pattern rule is used |
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

`MissingApiTrackerTests` and `DebugSiteMissingApiClassificationTests` are compiled and discovered. The focused 2026-07-16 tracker command passes `15/15`; the combined tracker/export command lists and passes `17/17`. Coverage includes descriptor and `in`/own-property-check operation attribution, instance-versus-prototype descriptor classification, boolean function-prototype marker attribution, negative controls for ordinary-object writes and non-boolean prototype methods, read-then-write preservation, WebIDL/wrong-receiver precedence, concrete specialized HTML receivers, the 512-record bound, and cross-navigation identity. Local operation evidence `logs/real-site/file_c_users_udayk_videos_fenbrowser-test_fenbrowser.tests_fixtures_diagnostics_missing_api_operation_kinds.html/20260716T114816Z/` renders `true|false|false` and retains distinct `DESCRIPTOR_OPERATION`, `IN_CHECK`, and `DESCRIPTOR_OPERATION` misses, with zero callback failures/exceptions, blocker `none`, and 26/26 artifacts.

Fresh Google evidence is `logs/real-site/www.google.com/20260716T114852Z/`. It retains 25/25 records: 9 `STANDARD_API`, 11 `SITE_EXPANDO`, 2 `WRONG_RECEIVER`, 1 `LEGACY_PROBE`, and 2 `UNCLASSIFIED`. Six `closure_listenable_*`/`$goog_Thenable` records carry boolean prototype-marker evidence; `closure_uid_*` now preserve `[DESCRIPTOR_OPERATION, WRITE]`, while `closure_lm_*` preserve `[READ, WRITE]`. Neither path uses a property-name pattern. `Location.toString` and `Navigator.geolocation` remain unclassified plain reads. Callback failures and exceptions are zero, `first_blocker.json` reports `none`, lifecycle is complete, the main UI is visible, and all 26 manifest entries exist.
