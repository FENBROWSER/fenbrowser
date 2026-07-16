using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Logging;

public enum MissingApiClassification
{
    StandardApi,
    SiteExpando,
    WrongReceiver,
    LegacyProbe,
    Unclassified
}

public enum MissingApiOperationKind
{
    Read,
    Write,
    Delete,
    InCheck,
    PrototypeAccess,
    Call,
    Construct,
    DescriptorOperation
}

public sealed record MissingApiClassificationInput(
    string ObjectOrPrototype,
    string PropertyName,
    MissingApiOperationKind OperationKind = MissingApiOperationKind.Read,
    bool AssignmentBeforeRead = false,
    bool KnownWebIdlMember = false,
    string DefinedInterface = "",
    bool? ReceiverMatchesDefinedInterface = null);

public sealed record MissingApiClassificationResult(
    MissingApiClassification Classification,
    MissingApiOperationKind OperationKind,
    bool StandardPriorityEligible,
    string Reason);

public static class MissingApiClassifier
{
    private static readonly HashSet<string> LegacyProbeInventory = new(StringComparer.Ordinal)
    {
        "Navigator.msPointerEnabled"
    };

    public static MissingApiClassificationResult Classify(MissingApiClassificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var owner = input.ObjectOrPrototype?.Trim() ?? string.Empty;
        var property = input.PropertyName?.Trim() ?? string.Empty;
        if (input.AssignmentBeforeRead)
        {
            return new MissingApiClassificationResult(
                MissingApiClassification.SiteExpando,
                input.OperationKind,
                false,
                "assignment-before-read");
        }

        if (LegacyProbeInventory.Contains(owner + "." + property))
        {
            return new MissingApiClassificationResult(
                MissingApiClassification.LegacyProbe,
                input.OperationKind,
                false,
                "known-legacy-api-inventory");
        }

        if (input.KnownWebIdlMember)
        {
            if (input.ReceiverMatchesDefinedInterface == false)
            {
                return new MissingApiClassificationResult(
                    MissingApiClassification.WrongReceiver,
                    input.OperationKind,
                    false,
                    string.IsNullOrWhiteSpace(input.DefinedInterface)
                        ? "known-webidl-member-on-wrong-receiver"
                        : "known-webidl-member-defined-on-" + input.DefinedInterface.Trim());
            }

            if (input.ReceiverMatchesDefinedInterface != true)
            {
                return new MissingApiClassificationResult(
                    MissingApiClassification.Unclassified,
                    input.OperationKind,
                    false,
                    "known-webidl-member-receiver-unresolved");
            }

            return new MissingApiClassificationResult(
                MissingApiClassification.StandardApi,
                input.OperationKind,
                true,
                string.IsNullOrWhiteSpace(input.DefinedInterface)
                    ? "known-webidl-member"
                    : "known-webidl-member-defined-on-" + input.DefinedInterface.Trim());
        }

        return new MissingApiClassificationResult(
            MissingApiClassification.Unclassified,
            input.OperationKind,
            false,
            "insufficient-classification-evidence");
    }

    public static string ToToken(MissingApiClassification classification)
        => classification switch
        {
            MissingApiClassification.StandardApi => "STANDARD_API",
            MissingApiClassification.SiteExpando => "SITE_EXPANDO",
            MissingApiClassification.WrongReceiver => "WRONG_RECEIVER",
            MissingApiClassification.LegacyProbe => "LEGACY_PROBE",
            _ => "UNCLASSIFIED"
        };

    public static string ToToken(MissingApiOperationKind operationKind)
        => operationKind switch
        {
            MissingApiOperationKind.Write => "WRITE",
            MissingApiOperationKind.Delete => "DELETE",
            MissingApiOperationKind.InCheck => "IN_CHECK",
            MissingApiOperationKind.PrototypeAccess => "PROTOTYPE_ACCESS",
            MissingApiOperationKind.Call => "CALL",
            MissingApiOperationKind.Construct => "CONSTRUCT",
            MissingApiOperationKind.DescriptorOperation => "DESCRIPTOR_OPERATION",
            _ => "READ"
        };
}
