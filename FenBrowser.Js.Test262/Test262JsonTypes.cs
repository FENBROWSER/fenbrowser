// Concrete types for JSON serialization via System.Text.Json source generators.
// These replace the anonymous-type payloads previously used in Test262ResultWriter,
// Test262DashboardWriter, Test262GateVerifier, and Test262Runner, making the
// project trimmable and Native-AOT-compatible.

namespace FenBrowser.Js.Test262;

// ── Result writer types ────────────────────────────────────────────────

public sealed class Test262RunResult
{
    public string Engine { get; set; } = "";
    public string Mode { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public long DurationMs { get; set; }
    public string Test262Commit { get; set; } = "";
    public string FenbrowserCommit { get; set; } = "unknown";
    public string SpecTarget { get; set; } = "ECMA-262 pinned snapshot";
    public string? Expectations { get; set; }
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Crashed { get; set; }
    public int TimedOut { get; set; }
    public int Skipped { get; set; }
    public int Unsupported { get; set; }
    public int ExpectedFailures { get; set; }
    public int UnexpectedPasses { get; set; }
    public int HarnessUnsupported { get; set; }
    public int InvalidTestConfiguration { get; set; }
    public Test262CategoryBreakdown Categories { get; set; } = new();
    public Test262RunSummary Summary { get; set; } = new();
    public List<Test262FailureEntry> Failures { get; set; } = new();
    public List<Test262UnexpectedPassEntry> UnexpectedPassesList { get; set; } = new();
    public List<TestEntry> Tests { get; set; } = new();
}

public sealed class Test262CategoryBreakdown
{
    public int ParserMissing { get; set; }
    public int ParserBug { get; set; }
    public int EarlyErrorBug { get; set; }
    public int RuntimeMissing { get; set; }
    public int RuntimeSemanticBug { get; set; }
    public int BuiltinMissing { get; set; }
    public int BuiltinSemanticBug { get; set; }
    public int ModuleMissing { get; set; }
    public int PromiseMissing { get; set; }
    public int RegexpMissing { get; set; }
    public int IntlMissing { get; set; }
    public int ProxyMissing { get; set; }
    public int TypedArrayMissing { get; set; }
    public int HostNotApplicable { get; set; }
    public int Crash { get; set; }
    public int Timeout { get; set; }
}

public sealed class Test262RunSummary
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Unsupported { get; set; }
    public int ParserErrors { get; set; }
    public int RuntimeErrors { get; set; }
    public int Crashes { get; set; }
    public int TimedOut { get; set; }
    public int HarnessUnsupported { get; set; }
    public int InvalidTestConfiguration { get; set; }
    public int ExpectedFailures { get; set; }
    public int UnexpectedPasses { get; set; }
}

public sealed class Test262FailureEntry
{
    public string Path { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Classification { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Feature { get; set; }
    public string? Location { get; set; }
    public bool Expected { get; set; }
    public string? ExpectedReason { get; set; }
    public string? ExpectedOwner { get; set; }
    public string? ExpectedArea { get; set; }
    public string? ExpiresAtMilestone { get; set; }
    public string? Include { get; set; }
    public string? Details { get; set; }
}

public sealed class Test262UnexpectedPassEntry
{
    public string Path { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string ExpectedStatus { get; set; } = "";
    public string ExpectedReason { get; set; } = "";
    public string ExpectedOwner { get; set; } = "";
    public string ExpectedArea { get; set; } = "";
    public string ExpiresAtMilestone { get; set; } = "";
}

// ── Dry-run result ──────────────────────────────────────────────────────

public sealed class Test262DryRunResult
{
    public string Engine { get; set; } = "";
    public string Mode { get; set; } = "dry-run";
    public DateTime TimestampUtc { get; set; }
    public string Test262Commit { get; set; } = "";
    public int Discovered { get; set; }
    public string[] Files { get; set; } = Array.Empty<string>();
}

// ── Dashboard types ─────────────────────────────────────────────────────

public sealed class Test262DashboardResult
{
    public DateTime GeneratedAtUtc { get; set; }
    public Test262DashboardSource Source { get; set; } = new();
    public Test262DashboardMetrics Metrics { get; set; } = new();
    public TopFailingDirectory[] TopFailingDirectories { get; set; } = Array.Empty<TopFailingDirectory>();
    public string[] CrashList { get; set; } = Array.Empty<string>();
    public string[] NewRegressions { get; set; } = Array.Empty<string>();
    public string[] FixedTests { get; set; } = Array.Empty<string>();
}

public sealed class Test262DashboardSource
{
    public string Current { get; set; } = "";
    public string? Previous { get; set; }
}

public sealed class Test262DashboardMetrics
{
    public int TotalTests { get; set; }
    public int EnabledTests { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Crashed { get; set; }
    public int TimedOut { get; set; }
    public int Unsupported { get; set; }
    public int ExpectedFailures { get; set; }
    public int UnexpectedPasses { get; set; }
    public double PassRateEnabled { get; set; }
    public double PassRateExcludingUnsupported { get; set; }
    public string PassRateContext { get; set; } = "";
}

public sealed class TopFailingDirectory
{
    public string Directory { get; set; } = "";
    public int Count { get; set; }
}

// ── Gate-verifier types ─────────────────────────────────────────────────

public sealed class Test262GateVerificationPayload
{
    public DateTime GeneratedAtUtc { get; set; }
    public Test262DashboardSource Source { get; set; } = new();
    public Test262GateSummary Summary { get; set; } = new();
    public string[] NewFailures { get; set; } = Array.Empty<string>();
    public int UnknownCrashes { get; set; }
    public int UncategorizedFailures { get; set; }
    public int ExpectationMetadataViolations { get; set; }
    public bool Passed { get; set; }
    public List<string> Violations { get; set; } = new();
}

public sealed class Test262GateSummary
{
    public Test262GateSummaryValues Current { get; set; } = new();
    public Test262GateSummaryValues? Previous { get; set; }
}

public sealed class Test262GateSummaryValues
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Unsupported { get; set; }
    public int ParserErrors { get; set; }
    public int Crashes { get; set; }
    public int ExpectedFailures { get; set; }
    public int UnexpectedPasses { get; set; }
}

// ── Shell error output type (used by Program.cs WriteError) ─────────────

public sealed class ShellErrorPayload
{
    public ShellErrorInfo Error { get; set; } = new();
}

public sealed class ShellErrorInfo
{
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public string Source { get; set; } = "";
}
