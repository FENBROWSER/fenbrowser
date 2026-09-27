"""Print the failing subtests of a Results/wpt_*/wpt.failures.json (latest by default)."""
import glob
import json
import os
import sys

path = sys.argv[1] if len(sys.argv) > 1 else sorted(glob.glob("Results/wpt_*/wpt.failures.json"), key=os.path.getmtime)[-1]
data = json.load(open(path, encoding="utf-8"))
for failure in data.get("Failures", []):
    if failure.get("Level") != "subtest":
        continue
    print(f"{failure['Test'].split('/')[-1]} | {failure['Subtest'][:70]} | {failure['Status']} | {(failure.get('Message') or '')[:140]}")
for result in data.get("TestResults", []):
    if result.get("Status") != "OK":
        print(f"{result['Test'].split('/')[-1]} | <file> | {result['Status']} | {(result.get('Message') or '')[:140]}")
