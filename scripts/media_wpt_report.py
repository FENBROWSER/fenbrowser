"""Summarise media WPT runs into docs/media_wpt_results.md.

Usage (from the repo root):
    python scripts/media_wpt_report.py Results/wpt_20260917_115227 [Results/wpt_... ...]

Each argument is a Results/wpt_<stamp> directory written by `FenBrowser.Tooling wpt`.
The raw mozlog (wpt.raw.json) is read for test_status (subtests) and test_end
(files) and grouped by the test's directory, the same model as docs/test262_results.md:
one table, one row per directory, the file counted as a subtest only when it had none
of its own (a harness ERROR/TIMEOUT with no subtests).
"""
import collections
import datetime
import json
import os
import sys

DOC = os.path.join("docs", "media_wpt_results.md")


def read_run(directory):
    raw = os.path.join(directory, "wpt.raw.json")
    files = {}
    subtests = collections.defaultdict(list)
    with open(raw, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            record = json.loads(line)
            action = record.get("action")
            if action == "test_status":
                subtests[record["test"]].append(record["status"])
            elif action == "test_end":
                files[record["test"]] = record["status"]
    return files, subtests


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2

    per_dir = collections.defaultdict(lambda: collections.Counter())
    file_counts = collections.defaultdict(lambda: collections.Counter())
    for directory in argv[1:]:
        files, subtests = read_run(directory)
        for test, status in files.items():
            group = test.rsplit("/", 1)[0]
            file_counts[group][status] += 1
            statuses = subtests.get(test)
            if statuses:
                per_dir[group].update(statuses)
            else:
                per_dir[group][status] += 1

    lines = [
        "# Media WPT results",
        "",
        f"Generated {datetime.date.today().isoformat()} by `scripts/media_wpt_report.py` from "
        + ", ".join(f"`{os.path.basename(d)}`" for d in argv[1:])
        + ". Subtests are counted per directory; a file without subtests counts once with its harness status.",
        "",
        "| Directory | Files | Pass | Fail | Timeout | Other | Pass % |",
        "|---|---:|---:|---:|---:|---:|---:|",
    ]
    total = collections.Counter()
    total_files = 0
    for group in sorted(per_dir):
        counts = per_dir[group]
        passed = counts.get("PASS", 0)
        failed = counts.get("FAIL", 0)
        timeout = counts.get("TIMEOUT", 0) + counts.get("NOTRUN", 0)
        other = sum(counts.values()) - passed - failed - timeout
        n = sum(counts.values())
        files = sum(file_counts[group].values())
        total.update(counts)
        total_files += files
        lines.append(f"| `{group}` | {files} | {passed} | {failed} | {timeout} | {other} | {100.0 * passed / n if n else 0:.1f}% |")

    passed = total.get("PASS", 0)
    n = sum(total.values())
    lines.append(f"| **Total** | {total_files} | {passed} | {total.get('FAIL', 0)} | {total.get('TIMEOUT', 0) + total.get('NOTRUN', 0)} | {n - passed - total.get('FAIL', 0) - total.get('TIMEOUT', 0) - total.get('NOTRUN', 0)} | {100.0 * passed / n if n else 0:.1f}% |")
    lines.append("")

    with open(DOC, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines))
    print("\n".join(lines))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
