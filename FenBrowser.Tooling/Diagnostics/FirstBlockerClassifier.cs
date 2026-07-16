namespace FenBrowser.Tooling;

public sealed record FirstBlockerInput(
    bool NavigationRequested,
    bool ResponseReceived,
    bool DocumentCreated,
    bool ParsingStarted,
    bool RequiredStylesDiscovered,
    bool RequiredScriptsDiscovered,
    bool RequiredResourcesFetched,
    bool RequiredScriptsExecuted,
    bool ParsingCompleted,
    bool DomContentLoadedFired,
    bool LoadFired,
    bool MainUiLaidOut,
    bool MainUiPainted,
    bool FrameSubmitted)
{
    public bool? InputTargetFound { get; init; }
    public bool? FocusAcquired { get; init; }
    public bool? TextAccepted { get; init; }
    public bool? SubmitCompleted { get; init; }
    public bool? ResultNavigationCompleted { get; init; }
    public IReadOnlyList<FirstBlockerEvidence> Evidence { get; init; } = Array.Empty<FirstBlockerEvidence>();
    public IReadOnlyList<string> NonFatalFailures { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ContradictoryArtifactWarnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingRequiredArtifacts { get; init; } = Array.Empty<string>();
}

public sealed record FirstBlockerEvidence(
    string EvidenceId,
    string FailureBucket,
    string SubsystemOwner,
    string MilestoneBlocked,
    long Sequence,
    string TimestampUtc,
    bool Required,
    string Explanation);

public sealed record FirstBlockerMilestone(string Name, string Status);

public sealed record FirstBlockerResult(
    int SchemaVersion,
    string Result,
    string FailureBucket,
    string SubsystemOwner,
    string MilestoneBlocked,
    long? FirstCausalSequence,
    string FirstCausalTimestampUtc,
    IReadOnlyList<string> EvidenceIds,
    double Confidence,
    string Explanation,
    IReadOnlyList<string> AlternativeCandidates,
    IReadOnlyList<string> NonFatalRemainingFailures,
    IReadOnlyList<string> EvidenceQualityWarnings,
    IReadOnlyList<string> ContradictoryArtifactWarnings,
    IReadOnlyList<string> UnverifiedMilestones,
    IReadOnlyList<FirstBlockerMilestone> Milestones);

public static class FirstBlockerClassifier
{
    private const string None = "none";

    public static FirstBlockerResult Classify(FirstBlockerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var milestones = BuildMilestones(input);
        var contradictions = input.ContradictoryArtifactWarnings?.ToList() ?? new List<string>();
        var unverified = milestones
            .Where(static milestone => string.Equals(milestone.Status, "unverified", StringComparison.Ordinal))
            .Select(static milestone => milestone.Name)
            .ToList();
        var nonFatal = input.NonFatalFailures?.ToList() ?? new List<string>();
        var evidence = (input.Evidence ?? Array.Empty<FirstBlockerEvidence>())
            .OrderBy(static item => item.Sequence <= 0 ? long.MaxValue : item.Sequence)
            .ThenBy(static item => item.TimestampUtc, StringComparer.Ordinal)
            .ThenBy(static item => item.EvidenceId, StringComparer.Ordinal)
            .ToList();
        var missingArtifacts = (input.MissingRequiredArtifacts ?? Array.Empty<string>())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Take(64)
            .ToList();
        var evidenceQualityWarnings = BuildEvidenceQualityWarnings(evidence, missingArtifacts);

        nonFatal.AddRange(evidence
            .Where(static item => !item.Required)
            .Select(static item => item.EvidenceId + ": " + item.Explanation));

        if (evidenceQualityWarnings.Count > 0)
        {
            var missingArtifactEvidence = missingArtifacts.Count > 0;
            return Create(
                "insufficient-evidence",
                "VerificationInfrastructure",
                "Tooling/Diagnostics",
                missingArtifactEvidence ? "ArtifactCompleteness" : "EvidenceTimeline",
                null,
                Array.Empty<string>(),
                0,
                missingArtifactEvidence
                    ? "One or more required diagnostic artifacts are missing."
                    : "Evidence timestamps contradict their causal sequence.",
                evidenceQualityWarnings,
                nonFatal,
                evidenceQualityWarnings,
                contradictions,
                unverified,
                milestones);
        }

        if (contradictions.Count > 0)
        {
            return Create(
                "insufficient-evidence",
                "Lifecycle",
                "FenEngine/Lifecycle",
                "LifecycleTruth",
                null,
                Array.Empty<string>(),
                0,
                "Artifacts disagree about terminal lifecycle state.",
                contradictions,
                nonFatal,
                evidenceQualityWarnings,
                contradictions,
                unverified,
                milestones);
        }

        var causal = evidence.FirstOrDefault(static item => item.Required);
        if (causal != null)
        {
            var alternatives = evidence
                .Where(item => item.Required && !ReferenceEquals(item, causal))
                .Select(static item => item.EvidenceId + ": " + item.Explanation)
                .ToList();
            return Create(
                "blocker",
                causal.FailureBucket,
                causal.SubsystemOwner,
                causal.MilestoneBlocked,
                causal,
                new[] { causal.EvidenceId },
                0.95,
                causal.Explanation,
                alternatives,
                nonFatal,
                evidenceQualityWarnings,
                contradictions,
                unverified,
                milestones);
        }

        var failed = milestones.FirstOrDefault(static milestone => string.Equals(milestone.Status, "failed", StringComparison.Ordinal));
        if (failed != null)
        {
            var interactionFailure = IsInteractionMilestone(failed.Name);
            return Create(
                interactionFailure ? "blocker" : "insufficient-evidence",
                interactionFailure ? "InputEventDefaultAction" : BucketForMilestone(failed.Name),
                interactionFailure ? "Host/Input" : OwnerForMilestone(failed.Name),
                failed.Name,
                null,
                Array.Empty<string>(),
                interactionFailure ? 0.9 : 0.35,
                interactionFailure
                    ? "The interaction milestone was attempted and failed."
                    : "A required milestone is absent, but no causal evidence record identifies why.",
                Array.Empty<string>(),
                nonFatal,
                evidenceQualityWarnings,
                contradictions,
                unverified,
                milestones);
        }

        return Create(
            None,
            None,
            None,
            string.Empty,
            null,
            Array.Empty<string>(),
            1,
            unverified.Count == 0
                ? "All modeled acceptance milestones completed and no causal blocker was recorded."
                : "Boot and rendering completed; interaction milestones remain unverified.",
            Array.Empty<string>(),
            nonFatal,
            evidenceQualityWarnings,
            contradictions,
            unverified,
            milestones);
    }

    private static FirstBlockerResult Create(
        string result,
        string bucket,
        string owner,
        string milestone,
        FirstBlockerEvidence causal,
        IReadOnlyList<string> evidenceIds,
        double confidence,
        string explanation,
        IReadOnlyList<string> alternatives,
        IReadOnlyList<string> nonFatal,
        IReadOnlyList<string> evidenceQualityWarnings,
        IReadOnlyList<string> contradictions,
        IReadOnlyList<string> unverified,
        IReadOnlyList<FirstBlockerMilestone> milestones) => new(
            SchemaVersion: 1,
            Result: result,
            FailureBucket: bucket,
            SubsystemOwner: owner,
            MilestoneBlocked: milestone,
            FirstCausalSequence: causal?.Sequence > 0 ? causal.Sequence : null,
            FirstCausalTimestampUtc: causal?.TimestampUtc ?? string.Empty,
            EvidenceIds: evidenceIds,
            Confidence: confidence,
            Explanation: explanation,
            AlternativeCandidates: alternatives,
            NonFatalRemainingFailures: nonFatal,
            EvidenceQualityWarnings: evidenceQualityWarnings,
            ContradictoryArtifactWarnings: contradictions,
            UnverifiedMilestones: unverified,
            Milestones: milestones);

    private static List<string> BuildEvidenceQualityWarnings(
        IReadOnlyList<FirstBlockerEvidence> evidence,
        IReadOnlyList<string> missingArtifacts)
    {
        var warnings = missingArtifacts
            .Select(static name => $"Required artifact missing: {name}.")
            .ToList();
        var timeline = evidence
            .Where(static item => item.Sequence > 0 &&
                                  TryParseTimestamp(item.TimestampUtc, out _))
            .OrderBy(static item => item.Sequence)
            .ThenBy(static item => item.EvidenceId, StringComparer.Ordinal)
            .ToList();

        for (var index = 1; index < timeline.Count && warnings.Count < 64; index++)
        {
            var previous = timeline[index - 1];
            var current = timeline[index];
            if (current.Sequence <= previous.Sequence ||
                !TryParseTimestamp(previous.TimestampUtc, out var previousTimestamp) ||
                !TryParseTimestamp(current.TimestampUtc, out var currentTimestamp) ||
                currentTimestamp >= previousTimestamp)
            {
                continue;
            }

            warnings.Add(
                $"Evidence clock inversion: sequence {current.Sequence} ({current.EvidenceId}) at " +
                $"{currentTimestamp:O} precedes sequence {previous.Sequence} ({previous.EvidenceId}) at {previousTimestamp:O}.");
        }

        return warnings;
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out timestamp);

    private static List<FirstBlockerMilestone> BuildMilestones(FirstBlockerInput input) =>
    [
        Required("NavigationRequested", input.NavigationRequested),
        Required("ResponseReceived", input.ResponseReceived),
        Required("DocumentCreated", input.DocumentCreated),
        Required("ParsingStarted", input.ParsingStarted),
        Required("RequiredStylesDiscovered", input.RequiredStylesDiscovered),
        Required("RequiredScriptsDiscovered", input.RequiredScriptsDiscovered),
        Required("RequiredResourcesFetched", input.RequiredResourcesFetched),
        Required("RequiredScriptsExecuted", input.RequiredScriptsExecuted),
        Required("ParsingCompleted", input.ParsingCompleted),
        Required("DOMContentLoadedFired", input.DomContentLoadedFired),
        Required("LoadFired", input.LoadFired),
        Required("MainUiLaidOut", input.MainUiLaidOut),
        Required("MainUiPainted", input.MainUiPainted),
        Required("FrameSubmitted", input.FrameSubmitted),
        Optional("InputTargetFound", input.InputTargetFound),
        Optional("FocusAcquired", input.FocusAcquired),
        Optional("TextAccepted", input.TextAccepted),
        Optional("SubmitDefaultActionCompleted", input.SubmitCompleted),
        Optional("ResultRequestNavigationCompleted", input.ResultNavigationCompleted)
    ];

    private static FirstBlockerMilestone Required(string name, bool value) =>
        new(name, value ? "completed" : "failed");

    private static FirstBlockerMilestone Optional(string name, bool? value) =>
        new(name, !value.HasValue ? "unverified" : value.Value ? "completed" : "failed");

    private static bool IsInteractionMilestone(string name) => name is
        "InputTargetFound" or "FocusAcquired" or "TextAccepted" or
        "SubmitDefaultActionCompleted" or "ResultRequestNavigationCompleted";

    private static string BucketForMilestone(string name) => name switch
    {
        "NavigationRequested" or "ResponseReceived" => "Navigation",
        "DocumentCreated" or "ParsingStarted" or "ParsingCompleted" => "Lifecycle",
        "RequiredStylesDiscovered" => "CSSStyle",
        "RequiredScriptsDiscovered" or "RequiredScriptsExecuted" => "ScriptDiscoveryLoading",
        "RequiredResourcesFetched" => "Network",
        "DOMContentLoadedFired" or "LoadFired" => "Lifecycle",
        "MainUiLaidOut" => "Layout",
        "MainUiPainted" or "FrameSubmitted" => "PaintRaster",
        _ => "VerificationInfrastructure"
    };

    private static string OwnerForMilestone(string name) => name switch
    {
        "NavigationRequested" or "ResponseReceived" or "RequiredResourcesFetched" => "Core/Network",
        "RequiredStylesDiscovered" or "MainUiLaidOut" or "MainUiPainted" or "FrameSubmitted" => "FenEngine/Rendering",
        "RequiredScriptsDiscovered" or "RequiredScriptsExecuted" => "FenEngine/Scripting",
        _ => "FenEngine/Lifecycle"
    };
}
