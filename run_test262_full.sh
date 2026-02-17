#!/bin/bash
# =============================================================================
# Full Test262 Suite Runner
# Runs all chunks of 1000 tests with process isolation.
# Per-test timeout: 3 minutes (set in Program.cs)
# Per-chunk process timeout: 45 minutes (safety net)
# =============================================================================

set -e

PROJECT="FenBrowser.Test262Benchmark/FenBrowser.Test262Benchmark.csproj"
EXE="FenBrowser.Test262Benchmark/bin/Release/net9.0/FenBrowser.Test262Benchmark.exe"
RESULTS="test262_results.md"
CHUNK_TIMEOUT=2700  # 45 minutes per chunk (safety net)

echo "=== Test262 Full Suite Runner ==="
echo "Per-test timeout: 3 minutes"
echo "Per-chunk process timeout: ${CHUNK_TIMEOUT}s"
echo ""

# Get chunk count
echo "Discovering tests..."
CHUNK_OUTPUT=$("$EXE" get_chunk_count 2>&1)
CHUNK_COUNT=$(echo "$CHUNK_OUTPUT" | grep -E '^[0-9]+$' | tail -1)
echo "Total chunks: $CHUNK_COUNT"
echo ""

# Initialize results file (truncate old results)
TIMESTAMP=$(date '+%Y-%m-%d %H:%M:%S')
cat > "$RESULTS" << 'HEADER'
HEADER
printf "\n# Test262 Full Results - %s\n\n" "$TIMESTAMP" >> "$RESULTS"
printf "Per-test timeout: 180s (3 minutes)\nChunk size: 1000\n\n" >> "$RESULTS"
printf "| Chunk | Range | Time (ms) | Tests | Passed | Failed | Pass %% | Avg/Test (ms) |\n" >> "$RESULTS"
printf "|-------|-------|-----------|-------|--------|--------|--------|---------------|\n" >> "$RESULTS"

echo "Results file: $RESULTS"
echo ""

TOTAL_PASSED=0
TOTAL_FAILED=0
TOTAL_TESTS=0
CRASHED_CHUNKS=""

for i in $(seq 1 $CHUNK_COUNT); do
    echo "============================================"
    echo "  CHUNK $i / $CHUNK_COUNT"
    echo "============================================"

    # Run chunk with process timeout
    START_TIME=$(date +%s)

    if timeout $CHUNK_TIMEOUT "$EXE" run_chunk $i 2>&1; then
        END_TIME=$(date +%s)
        ELAPSED=$((END_TIME - START_TIME))
        echo "  Chunk $i completed in ${ELAPSED}s"
    else
        EXIT_CODE=$?
        END_TIME=$(date +%s)
        ELAPSED=$((END_TIME - START_TIME))

        if [ $EXIT_CODE -eq 124 ]; then
            echo "  CHUNK $i KILLED (process timeout after ${CHUNK_TIMEOUT}s)"
            echo "| $i | - | TIMEOUT | 1000 | 0 | 1000 | 0% | timeout |" >> "$RESULTS"
            CRASHED_CHUNKS="$CRASHED_CHUNKS $i(timeout)"
        else
            echo "  CHUNK $i CRASHED (exit code $EXIT_CODE) after ${ELAPSED}s"
            echo "| $i | - | CRASH | 1000 | 0 | 1000 | 0% | crash |" >> "$RESULTS"
            CRASHED_CHUNKS="$CRASHED_CHUNKS $i(crash:$EXIT_CODE)"
        fi
    fi

    echo ""

    # Brief pause for OS resource recovery
    sleep 1
done

echo ""
echo "============================================"
echo "  ALL CHUNKS COMPLETE"
echo "============================================"

if [ -n "$CRASHED_CHUNKS" ]; then
    echo "Crashed/timed-out chunks:$CRASHED_CHUNKS"
fi

echo "Results saved to: $RESULTS"
echo "Done!"
