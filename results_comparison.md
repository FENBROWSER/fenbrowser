# JIT vs Interpreter Performance Comparison

This document compares the performance of the FenEngine JIT compiler (threshold: 50) against the baseline interpreter (threshold: 10,000,000).

| Benchmark              | Interpreter (Baseline) | JIT Enabled | Improvement             |
| ---------------------- | ---------------------- | ----------- | ----------------------- |
| **Loop Math**          | 1792.16 ms             | 1429.66 ms  | ~20% faster             |
| **Recursion (Fib 25)** | 464.70 ms              | 694.19 ms   | ~49% slower\*           |
| **Object Allocation**  | 4074.88 ms             | 3884.84 ms  | ~5% faster              |
| **Function Calls**     | 3122.10 ms             | 1466.21 ms  | **>100% faster (2.1x)** |

## Analysis

- **Function Calls**: The JIT compiler significantly outperforms the interpreter for function calls. This is due to the jitted code directly managing the call stack efficiently once a function is "hot".
- **Loop Math**: Shows a solid 20% improvement. JIT helps by optimizing the inner loop operations.
- **Recursion**: Surprisingly, recursion is currently slower in the JIT. This is likely due to the overhead of entering/exiting the jitted delegate for each recursive call, as each call still goes through a boundary that checks for jitted status or involves delegate invocation overhead.
- **Object Allocation**: Shows marginal improvement as the actual allocation (memory management) is still handled by the underlying .NET runtime and `FenObject` logic.

> [!NOTE]
> The recursion slowdown is a known trade-off in simple JIT implementations where the transition between the interpreter/runtime and jitted code has non-zero cost. For deep recursion with small bodies, this overhead can dominate.

## JIT Fixes Applied

- Fixed `NullReferenceException` in `EvalIfExpression` when `else` block is missing.
- Added support for `long` (integer), `float`, and `null` literals in JIT IL emission.
- Standardized `MethodInfo` resolution with rigorous startup validation.
- Improved error reporting with full stack traces in `FenRuntime`.
