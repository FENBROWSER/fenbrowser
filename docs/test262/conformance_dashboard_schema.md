# Conformance Dashboard Schema

```json
{
  "engine": "FenJS",
  "timestampUtc": "2026-05-19T00:00:00Z",
  "test262Commit": "<pinned-commit>",
  "mode": "parser|runtime|full",
  "summary": {
    "passed": 0,
    "failed": 0,
    "unsupported": 0,
    "skipped": 0,
    "regressions": 0,
    "crashes": 0,
    "unknownFailures": 0
  },
  "categories": [
    {
      "name": "parser",
      "passed": 0,
      "failed": 0,
      "unsupported": 0,
      "crashes": 0
    }
  ],
  "failures": [
    {
      "path": "test/language/...",
      "classification": "expected|regression|unsupported|crash|unknown",
      "owner": "<owner>",
      "milestone": "0.x",
      "notes": ""
    }
  ]
}
```
