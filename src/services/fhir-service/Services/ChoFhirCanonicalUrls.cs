namespace FhirService.Services;

/// <summary>
/// Canonical URL constants for Cloud Health Office-authored FHIR artifacts.
/// These URLs are permanent: once a resource claims conformance via
/// <c>meta.profile</c>, the URL cannot change without invalidating every
/// persisted resource's profile declaration.
/// </summary>
public static class ChoFhirCanonicalUrls
{
    public const string Base                    = "http://fhir.cloudhealthoffice.com/";
    public const string StructureDefinitionBase = Base + "StructureDefinition/";
    public const string OperationDefinitionBase = Base + "OperationDefinition/";
    public const string CodeSystemBase          = Base + "CodeSystem/";
    public const string ValueSetBase            = Base + "ValueSet/";

    // ── CHO appeal profiles ─────────────────────────────────────────────────
    public const string AppealTask              = StructureDefinitionBase + "cho-appeal-task";
    public const string AppealCommunication     = StructureDefinitionBase + "cho-appeal-communication";
    public const string AppealDocumentReference = StructureDefinitionBase + "cho-appeal-document-reference";
    public const string AppealClaimResponse     = StructureDefinitionBase + "cho-appeal-claim-response";

    // ── CHO appeal extensions ───────────────────────────────────────────────
    public const string AppealLevelExt              = StructureDefinitionBase + "cho-appeal-level";
    public const string AppealLineOfBusinessExt     = StructureDefinitionBase + "cho-appeal-line-of-business";
    public const string AppealTargetResponseDateExt = StructureDefinitionBase + "cho-appeal-target-response-date";
    public const string AppealUrgentFlagExt         = StructureDefinitionBase + "cho-appeal-urgent-flag";
    public const string AppealX12ControlNumberExt   = StructureDefinitionBase + "cho-appeal-x12-275-control-number";
    public const string AppealX12TransmissionExt    = StructureDefinitionBase + "cho-appeal-x12-275-transmission-code";
    public const string AppealTaskReferenceExt      = StructureDefinitionBase + "cho-appeal-task-reference";

    // ── CHO appeal operations ───────────────────────────────────────────────
    public const string AppealSubmitOperation = OperationDefinitionBase + "cho-appeal-submit";

    /// <summary>All CHO appeal resource profile URLs (not extensions).</summary>
    public static readonly IReadOnlyList<string> AllAppealResourceProfiles =
    [
        AppealTask,
        AppealCommunication,
        AppealDocumentReference,
        AppealClaimResponse,
    ];

    /// <summary>All CHO appeal extension URLs.</summary>
    public static readonly IReadOnlyList<string> AllAppealExtensions =
    [
        AppealLevelExt,
        AppealLineOfBusinessExt,
        AppealTargetResponseDateExt,
        AppealUrgentFlagExt,
        AppealX12ControlNumberExt,
        AppealX12TransmissionExt,
        AppealTaskReferenceExt,
    ];

    /// <summary>All CHO-authored StructureDefinition URLs (profiles + extensions).</summary>
    public static readonly IReadOnlyList<string> AllStructureDefinitions =
        [.. AllAppealResourceProfiles, .. AllAppealExtensions];

    /// <summary>All CHO-authored OperationDefinition URLs.</summary>
    public static readonly IReadOnlyList<string> AllOperationDefinitions =
    [
        AppealSubmitOperation,
    ];
}
