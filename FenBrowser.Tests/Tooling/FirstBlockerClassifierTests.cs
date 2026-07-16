using FenBrowser.Tooling;

namespace FenBrowser.Tests.Tooling;

public sealed class FirstBlockerClassifierTests
{
    public static TheoryData<string, FirstBlockerInput, string, string, string> Cases => new()
    {
        { "navigation failure", BootInput() with { ResponseReceived = false, Evidence = [Required("navigation-1", "Navigation", "Core/Network", "ResponseReceived", 1)] }, "blocker", "Navigation", "ResponseReceived" },
        { "resource failure", BootInput() with { RequiredResourcesFetched = false, Evidence = [Required("resource-1", "Network", "Core/Network", "RequiredResourcesFetched", 7)] }, "blocker", "Network", "RequiredResourcesFetched" },
        { "parser-blocking script", BootInput() with { RequiredScriptsExecuted = false, Evidence = [Required("script-1", "ECMAScriptSemantics", "FenJS", "RequiredScriptsExecuted", 8)] }, "blocker", "ECMAScriptSemantics", "RequiredScriptsExecuted" },
        { "missing standard API throw", BootInput() with { RequiredScriptsExecuted = false, Evidence = [Required("missing-api-1", "DOMAPI", "Core/DOM", "RequiredScriptsExecuted", 8)] }, "blocker", "DOMAPI", "RequiredScriptsExecuted" },
        { "non-fatal feature probe", BootInput() with { Evidence = [Optional("probe-1", "DOMAPI", "Core/DOM", 8)] }, "none", "none", "" },
        { "lifecycle contradiction", BootInput() with { ContradictoryArtifactWarnings = ["lifecycle complete but event loop readyState loading"] }, "insufficient-evidence", "Lifecycle", "LifecycleTruth" },
        { "zero-size root", BootInput() with { MainUiLaidOut = false, Evidence = [Required("layout-root-1", "Layout", "FenEngine/Layout", "MainUiLaidOut", 12)] }, "blocker", "Layout", "MainUiLaidOut" },
        { "successful page", BootInput(), "none", "none", "" },
        { "post-load callback failure", BootInput() with { NonFatalFailures = ["callback:callback-1 timer-fixture-boom"] }, "none", "none", "" },
        { "input failure", BootInput() with { InputTargetFound = false }, "blocker", "InputEventDefaultAction", "InputTargetFound" },
        { "submit failure", BootInput() with { InputTargetFound = true, FocusAcquired = true, TextAccepted = true, SubmitCompleted = false }, "blocker", "InputEventDefaultAction", "SubmitDefaultActionCompleted" }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classify_ProducesDeterministicCausalResult(
        string _,
        FirstBlockerInput input,
        string expectedResult,
        string expectedBucket,
        string expectedMilestone)
    {
        var result = FirstBlockerClassifier.Classify(input);

        Assert.Equal(expectedResult, result.Result);
        Assert.Equal(expectedBucket, result.FailureBucket);
        Assert.Equal(expectedMilestone, result.MilestoneBlocked);
        Assert.Equal(19, result.Milestones.Count);
    }

    [Fact]
    public void Classify_PostLoadCallbackFailureIsOrderedAndNonFatal()
    {
        var input = BootInput() with
        {
            NonFatalFailures = ["callback:callback-2 second", "callback:callback-1 first"]
        };

        var result = FirstBlockerClassifier.Classify(input);

        Assert.Equal("none", result.Result);
        Assert.Equal(input.NonFatalFailures, result.NonFatalRemainingFailures);
        Assert.Equal(
            new[] { "InputTargetFound", "FocusAcquired", "TextAccepted", "SubmitDefaultActionCompleted", "ResultRequestNavigationCompleted" },
            result.UnverifiedMilestones);
    }

    [Fact]
    public void Classify_ClockInversionIsVerificationInsufficiency()
    {
        var input = BootInput() with
        {
            Evidence =
            [
                new FirstBlockerEvidence(
                    "navigation-response",
                    "Navigation",
                    "Core/Network",
                    "ResponseReceived",
                    2,
                    "2026-07-16T12:00:05.0000000Z",
                    false,
                    "response observed"),
                new FirstBlockerEvidence(
                    "document-created",
                    "Lifecycle",
                    "FenEngine/Lifecycle",
                    "DocumentCreated",
                    3,
                    "2026-07-16T11:59:55.0000000Z",
                    false,
                    "document observed")
            ]
        };

        var result = FirstBlockerClassifier.Classify(input);

        Assert.Equal("insufficient-evidence", result.Result);
        Assert.Equal("VerificationInfrastructure", result.FailureBucket);
        Assert.Equal("EvidenceTimeline", result.MilestoneBlocked);
        Assert.Single(result.EvidenceQualityWarnings);
        Assert.Contains("Evidence clock inversion", result.EvidenceQualityWarnings[0]);
    }

    [Fact]
    public void Classify_MissingRequiredArtifactIsVerificationInsufficiency()
    {
        var result = FirstBlockerClassifier.Classify(BootInput() with
        {
            MissingRequiredArtifacts = ["trace.jsonl"],
            ContradictoryArtifactWarnings = ["lifecycle sample disagrees"]
        });

        Assert.Equal("insufficient-evidence", result.Result);
        Assert.Equal("VerificationInfrastructure", result.FailureBucket);
        Assert.Equal("Tooling/Diagnostics", result.SubsystemOwner);
        Assert.Equal("ArtifactCompleteness", result.MilestoneBlocked);
        Assert.Equal(["Required artifact missing: trace.jsonl."], result.EvidenceQualityWarnings);
        Assert.Equal(["lifecycle sample disagrees"], result.ContradictoryArtifactWarnings);
    }

    private static FirstBlockerInput BootInput() => new(
        NavigationRequested: true,
        ResponseReceived: true,
        DocumentCreated: true,
        ParsingStarted: true,
        RequiredStylesDiscovered: true,
        RequiredScriptsDiscovered: true,
        RequiredResourcesFetched: true,
        RequiredScriptsExecuted: true,
        ParsingCompleted: true,
        DomContentLoadedFired: true,
        LoadFired: true,
        MainUiLaidOut: true,
        MainUiPainted: true,
        FrameSubmitted: true);

    private static FirstBlockerEvidence Required(
        string id,
        string bucket,
        string owner,
        string milestone,
        long sequence) => new(id, bucket, owner, milestone, sequence, "", true, id);

    private static FirstBlockerEvidence Optional(
        string id,
        string bucket,
        string owner,
        long sequence) => new(id, bucket, owner, "", sequence, "", false, id);
}
