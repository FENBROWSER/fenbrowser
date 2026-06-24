using System.Text.Json.Serialization;

namespace FenBrowser.Js.Test262;

/// <summary>
/// Source-generated JSON serializer context for all Test262 result types.
/// Replaces reflection-based JsonSerializer.Serialize calls, making the
/// project fully trimmable and Native-AOT-compatible.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Test262RunResult))]
[JsonSerializable(typeof(Test262CategoryBreakdown))]
[JsonSerializable(typeof(Test262RunSummary))]
[JsonSerializable(typeof(Test262FailureEntry))]
[JsonSerializable(typeof(Test262UnexpectedPassEntry))]
[JsonSerializable(typeof(Test262DryRunResult))]
[JsonSerializable(typeof(Test262DashboardResult))]
[JsonSerializable(typeof(Test262DashboardSource))]
[JsonSerializable(typeof(Test262DashboardMetrics))]
[JsonSerializable(typeof(TopFailingDirectory))]
[JsonSerializable(typeof(Test262GateVerificationPayload))]
[JsonSerializable(typeof(Test262GateSummary))]
[JsonSerializable(typeof(Test262GateSummaryValues))]
[JsonSerializable(typeof(ShellErrorPayload))]
[JsonSerializable(typeof(ShellErrorInfo))]
[JsonSerializable(typeof(TestEntry))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
public partial class Test262JsonContext : JsonSerializerContext
{
}
