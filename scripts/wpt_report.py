#!/usr/bin/env python3
"""
Generate a WPT results summary from per-category result bundles.
Reads Results/wpt/categories/*/wpt.summary.json and writes a Markdown report.
Usage: python scripts/wpt_report.py [--results-dir Results/wpt/categories]
"""
import json, os, sys, glob
from datetime import datetime

RESULTS_DIR = sys.argv[2] if len(sys.argv) > 2 and sys.argv[1] == "--results-dir" else "Results/wpt/categories"
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

abs_dir = os.path.join(REPO_ROOT, RESULTS_DIR) if not os.path.isabs(RESULTS_DIR) else RESULTS_DIR

if not os.path.isdir(abs_dir):
    print(f"Results directory not found: {abs_dir}")
    print("Run a WPT sweep first: pwsh scripts/run-wpt-category-sweep.ps1")
    sys.exit(1)

categories = []
total_test_start = 0
total_test_end = 0
total_statuses = {}
grand_total_subtests = 0
grand_passing_subtests = 0

for cat_dir in sorted(os.listdir(abs_dir)):
    summary_path = os.path.join(abs_dir, cat_dir, "wpt.summary.json")
    if not os.path.isfile(summary_path):
        continue
    try:
        with open(summary_path) as f:
            data = json.load(f)
    except Exception:
        continue

    # cat_dir like "wpt_dom" or "wpt_dom_lists"
    cat_name = cat_dir.removeprefix("wpt_").replace("_", "/")

    tests = data.get("Tests", [])
    cat_label = tests[0] if tests else cat_name
    test_start = data.get("TestStart", 0)
    test_end = data.get("TestEnd", 0)
    statuses = data.get("StatusCounts", {})

    # Try to get subtest details from the raw log
    subtest_pass = 0
    subtest_total = 0
    raw_path = os.path.join(abs_dir, cat_dir, "wpt.raw.json")
    if os.path.isfile(raw_path):
        try:
            with open(raw_path) as rf:
                for line in rf:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        entry = json.loads(line)
                    except Exception:
                        continue
                    if entry.get("action") == "test_status":
                        subtest_total += 1
                        if entry.get("status") == "PASS":
                            subtest_pass += 1
                    elif entry.get("action") == "test_end":
                        pass  # harness-level status is in StatusCounts
        except Exception:
            pass

    categories.append({
        "name": cat_label,
        "test_start": test_start,
        "test_end": test_end,
        "statuses": statuses,
        "subtest_pass": subtest_pass,
        "subtest_total": subtest_total,
    })
    total_test_start += test_start
    total_test_end += test_end
    for s, c in statuses.items():
        total_statuses[s] = total_statuses.get(s, 0) + c
    grand_total_subtests += subtest_total
    grand_passing_subtests += subtest_pass

# Write report
report_path = os.path.join(REPO_ROOT, "docs", "wpt_results.md")
now = datetime.utcnow().strftime("%Y-%m-%d %H:%M UTC")

with open(report_path, "w", encoding="utf-8") as f:
    f.write(f"# WPT Results ({now})\n\n")
    f.write(f"**Harness files:** {total_test_start} started, {total_test_end} completed\n")
    f.write(f"**Harness statuses:** ")
    f.write(", ".join(f"{s}: {c}" for s, c in sorted(total_statuses.items())) if total_statuses else "none")
    f.write("\n")

    if grand_total_subtests > 0:
        pct = (grand_passing_subtests / grand_total_subtests * 100) if grand_total_subtests > 0 else 0
        f.write(f"**Subtests:** {grand_passing_subtests}/{grand_total_subtests} = {pct:.1f}%\n")
    f.write("\n")

    f.write("| Category | Files | Harness OK | Subtest Pass | Subtest Total | Pass % |\n")
    f.write("|----------|-------|------------|--------------|---------------|--------|\n")

    for cat in sorted(categories, key=lambda c: -c["test_start"]):
        ok = cat["statuses"].get("OK", 0)
        sub_pct = (cat["subtest_pass"] / cat["subtest_total"] * 100) if cat["subtest_total"] > 0 else 0
        f.write(f"| {cat['name']} | {cat['test_start']} | {ok} | {cat['subtest_pass']} | {cat['subtest_total']} | {sub_pct:.1f}% |\n")

    f.write(f"\n*Report generated {now} from `{RESULTS_DIR}`*\n")

print(f"Report written: {report_path}")
print(f"Total: {total_test_start} files, " +
      f"{' '.join(f'{s}={c}' for s,c in sorted(total_statuses.items()))}" +
      (f", subtest_pass={grand_passing_subtests}/{grand_total_subtests}" if grand_total_subtests > 0 else ""))
