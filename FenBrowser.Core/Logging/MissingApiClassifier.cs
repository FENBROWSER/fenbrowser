using System;
using System.Collections.Generic;
using FenBrowser.Core.WebIDL;

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
    bool? ReceiverMatchesDefinedInterface = null,
    bool AssignmentObserved = false,
    bool FunctionPrototypeMarkerObserved = false,
    bool DescriptorTargetIsPrototype = false);

public sealed record MissingApiClassificationResult(
    MissingApiClassification Classification,
    MissingApiOperationKind OperationKind,
    bool StandardPriorityEligible,
    string Reason,
    bool KnownWebIdlMember = false,
    string DefinedInterface = "",
    bool? ReceiverMatchesDefinedInterface = null);

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
        if (LegacyProbeInventory.Contains(owner + "." + property))
        {
            return new MissingApiClassificationResult(
                MissingApiClassification.LegacyProbe,
                input.OperationKind,
                false,
                "known-legacy-api-inventory");
        }

        var knownWebIdlMember = input.KnownWebIdlMember;
        var definedInterface = input.DefinedInterface?.Trim() ?? string.Empty;
        var receiverMatchesDefinedInterface = input.ReceiverMatchesDefinedInterface;
        if (!knownWebIdlMember)
        {
            var catalogResolution = WebIdlMemberCatalog.Resolve(owner, property);
            knownWebIdlMember = catalogResolution.KnownMember;
            definedInterface = catalogResolution.DefinedInterface;
            receiverMatchesDefinedInterface = catalogResolution.ReceiverMatchesDefinedInterface;
        }

        if (knownWebIdlMember)
        {
            if (receiverMatchesDefinedInterface == false)
            {
                return new MissingApiClassificationResult(
                    MissingApiClassification.WrongReceiver,
                    input.OperationKind,
                    false,
                    string.IsNullOrWhiteSpace(definedInterface)
                        ? "known-webidl-member-on-wrong-receiver"
                        : "known-webidl-member-defined-on-" + definedInterface,
                    true,
                    definedInterface,
                    false);
            }

            if (receiverMatchesDefinedInterface != true)
            {
                return new MissingApiClassificationResult(
                    MissingApiClassification.Unclassified,
                    input.OperationKind,
                    false,
                    "known-webidl-member-receiver-unresolved",
                    true,
                    definedInterface,
                    null);
            }

            if (input.OperationKind == MissingApiOperationKind.DescriptorOperation &&
                !input.DescriptorTargetIsPrototype)
            {
                return new MissingApiClassificationResult(
                    MissingApiClassification.Unclassified,
                    input.OperationKind,
                    false,
                    "known-webidl-member-descriptor-target-is-instance",
                    true,
                    definedInterface,
                    true);
            }

            return new MissingApiClassificationResult(
                MissingApiClassification.StandardApi,
                input.OperationKind,
                true,
                string.IsNullOrWhiteSpace(definedInterface)
                    ? "known-webidl-member"
                    : "known-webidl-member-defined-on-" + definedInterface,
                true,
                definedInterface,
                true);
        }

        if (input.AssignmentBeforeRead || input.AssignmentObserved)
        {
            return new MissingApiClassificationResult(
                MissingApiClassification.SiteExpando,
                input.OperationKind,
                false,
                input.AssignmentBeforeRead
                    ? "assignment-before-read"
                    : "page-assignment-observed");
        }

        if (input.FunctionPrototypeMarkerObserved)
        {
            return new MissingApiClassificationResult(
                MissingApiClassification.SiteExpando,
                input.OperationKind,
                false,
                "page-function-prototype-marker");
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
