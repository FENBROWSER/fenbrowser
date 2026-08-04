# FenBrowser HTML tokenizer language benchmark

This tool compares FenBrowser's production C# HTML tokenizer with equivalent
common-state benchmark implementations in C#, Rust, and C++ over the same
deterministic ASCII corpus.

The benchmark exists to answer a narrow question: how much steady-state
tokenization throughput changes when the common HTML tokenizer states are
implemented in C#, Rust, or C++ on the same machine.

## Scope

The C# executable has two modes:

- `production` calls `FenBrowser.Core.Parsing.HtmlTokenizer` with its production
  token pool. Its input and emission guards are raised to `int.MaxValue` so the
  generated performance corpus is measured rather than rejected by normal
  browser hardening limits.
- `common` uses the same benchmark algorithm and ownership contract as the Rust
  and C++ common-state ports. This is the language-to-language control.

The three common-state implementations cover the states used by the checked-in
corpus:

- data and batched character runs
- start and end tags
- quoted, unquoted, and boolean attributes
- self-closing tags
- common named and ASCII numeric character references
- comments
- simple doctypes
- ASCII lower-casing and source-position advancement

The common-state ports are benchmark implementations, not production-complete
WHATWG tokenizers and not browser integration candidates. Timing is rejected
unless all four benchmark modes emit the same token count and normalized 64-bit
checksum.

## Run

From the repository root:

```powershell
.\scripts\run-html-tokenizer-language-bench.ps1
```

Optional controls:

```powershell
.\scripts\run-html-tokenizer-language-bench.ps1 `
  -CorpusMiB 16 `
  -Iterations 15 `
  -WarmupIterations 3
```

The script:

1. repeats the checked-in seed into a generated corpus under `Results/`;
2. publishes the C# benchmark in Release mode;
3. builds Rust with the release/LTO profile;
4. builds C++ with MSVC `/O2`, `/GL`, and `/LTCG`;
5. runs production C#, common-state C#, Rust, and C++ warmup/measured
   iterations sequentially;
6. rejects mismatched token output;
7. writes both speedup-versus-production and speedup-versus-common-C# values to
   `Results/html-tokenizer-bench/summary.json`.

File loading, compilation, corpus generation, and JSON serialization are
outside the timed region. Median steady-state time and MiB/s are the primary
metrics. The C# executable additionally reports managed bytes allocated on the
benchmark thread. Token consumption and checksum calculation are inside the
timed region for every implementation so output work cannot be optimized away.

## Interpretation limits

- This measures one controlled corpus, not complete browser page-load time.
- C# reads the corpus as a UTF-16 `string`; Rust and C++ use byte strings.
- Compare Rust and C++ primarily with `csharp-common`; comparison with
  `csharp-production` includes algorithm, token-model, and pooling differences.
- FFI overhead is not measured. A native production experiment would need a
  batched boundary and complete tokenizer conformance before browser adoption.
- Do not treat a native throughput win as proof that replacing the production
  tokenizer will improve end-to-end rendering.
