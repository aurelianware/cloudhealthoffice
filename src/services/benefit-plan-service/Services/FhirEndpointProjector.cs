using System.Text.Json.Nodes;
using BenefitPlanService.Models;
using static BenefitPlanService.Services.FhirExtensionBuilder;

namespace BenefitPlanService.Services;

/// <summary>
/// Hand-built FHIR R4 <c>Endpoint</c> projector (capability BP 5.9 — Plan
/// Documents → FHIR Endpoint projection). Mirrors
/// <see cref="FhirInsurancePlanProjector"/>: stateless, deterministic, no
/// Hl7.Fhir.R4 dependency.
///
/// <para>
/// Source-of-truth: <see cref="BenefitPlan.Documents"/>. One Endpoint per
/// <see cref="PlanDocumentReference"/> whose <c>Location</c> is an
/// external HTTPS URL (Decision 4 — internal
/// <c>documentreference/{id}</c> references skip projection).
/// </para>
/// </summary>
internal sealed class FhirEndpointProjector : IFhirEndpointProjector
{
    /// <summary>
    /// The reserved internal-reference prefix (Phase 2 forward-compat).
    /// Documents whose <c>Location</c> starts with this string are not
    /// projected to an Endpoint — Endpoints require an external address.
    /// Matches the docstring shape on
    /// <see cref="PlanDocumentReference.Location"/>.
    /// </summary>
    public const string InternalReferencePrefix = "documentreference/";

    public JsonObject? Project(BenefitPlan plan, PlanDocumentReference document)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(document);

        if (!IsPlanProjectable(plan)) return null;
        if (!IsDocumentProjectable(document)) return null;

        var status = ResolveStatus(plan, document);

        var resource = new JsonObject
        {
            ["resourceType"] = "Endpoint",
            ["id"] = document.Id,
            ["status"] = status,
        };

        // ── connectionType (Decision 1) ──────────────────────────────
        // Plan-Net's Endpoint profile binds connectionType to a slot the
        // HL7 endpoint-connection-type CodeSystem can't fill for static
        // documents — CHO publishes one code, "static-document".
        resource["connectionType"] = Coding(
            ChoBenefitPlanFhirUrls.EndpointConnectionTypeSystem,
            ChoBenefitPlanFhirUrls.EndpointConnectionTypeStaticDocument,
            "Static downloadable document");

        // ── name ─────────────────────────────────────────────────────
        var name = ResolveName(document);
        if (!string.IsNullOrEmpty(name))
        {
            resource["name"] = name;
        }

        // ── payloadType (Decision 3) ─────────────────────────────────
        resource["payloadType"] = new JsonArray
        {
            CodeableConcept(
                Coding(
                    ChoBenefitPlanFhirUrls.PlanDocumentTypeSystem,
                    PlanDocumentTypeCode(document.DocType),
                    PlanDocumentTypeDisplay(document.DocType)),
                text: PlanDocumentTypeDisplay(document.DocType)),
        };

        // ── payloadMimeType (Decision 6 — pass-through) ──────────────
        if (!string.IsNullOrWhiteSpace(document.ContentType))
        {
            resource["payloadMimeType"] = new JsonArray { document.ContentType };
        }

        // ── address (Decision 4 — operator-authored Location) ────────
        resource["address"] = document.Location;

        // ── period — track the document's effective window ──────────
        if (document.EffectiveDate.HasValue)
        {
            resource["period"] = new JsonObject
            {
                ["start"] = ToFhirDate(document.EffectiveDate.Value),
            };
        }

        // ── meta ─────────────────────────────────────────────────────
        resource["meta"] = new JsonObject
        {
            ["lastUpdated"] = ToFhirInstant(ResolveLastUpdated(plan)),
            ["profile"] = new JsonArray(
                ChoBenefitPlanFhirUrls.PlanNetEndpointProfile),
        };

        return resource;
    }

    public JsonArray ProjectAll(BenefitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var array = new JsonArray();
        if (!IsPlanProjectable(plan)) return array;

        foreach (var doc in OrderedProjectableDocuments(plan))
        {
            var projected = Project(plan, doc);
            if (projected is not null) array.Add(projected);
        }
        return array;
    }

    public IReadOnlyList<PlanDocumentReference> OrderedProjectableDocuments(BenefitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!IsPlanProjectable(plan) || plan.Documents is null || plan.Documents.Count == 0)
        {
            return Array.Empty<PlanDocumentReference>();
        }

        return plan.Documents
            .Where(d => d is not null && IsDocumentProjectable(d))
            .OrderBy(d => DocTypeOrdinal(d.DocType))
            .ThenByDescending(d => d.EffectiveDate ?? DateTime.MinValue)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
    }

    // ── projection gates ────────────────────────────────────────────────

    /// <summary>
    /// A plan with a non-Published version state has no public FHIR
    /// projection (mirrors <see cref="FhirInsurancePlanProjector"/>'s
    /// stance). Future-effective plans likewise — projecting before the
    /// plan starts is not part of the BP 5.9 contract.
    /// </summary>
    private static bool IsPlanProjectable(BenefitPlan plan)
    {
        if (plan.VersionState != PlanVersionState.Published) return false;
        if (ToUtc(plan.EffectiveDate) > DateTime.UtcNow) return false;
        return true;
    }

    /// <summary>
    /// Endpoints require an external address (Decision 4). Internal
    /// <c>documentreference/{id}</c> references are deferred to Phase 2;
    /// they are not projected to an Endpoint.
    /// Empty / whitespace locations also don't project — but those should
    /// have been rejected by
    /// <see cref="PlanDocumentValidation.ValidateLocation(string?, string)"/>
    /// at the producer boundary.
    /// </summary>
    private static bool IsDocumentProjectable(PlanDocumentReference document)
    {
        if (string.IsNullOrWhiteSpace(document.Location)) return false;
        if (document.Location.StartsWith(InternalReferencePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    // ── status (Decision 5) ─────────────────────────────────────────────

    private static string ResolveStatus(BenefitPlan plan, PlanDocumentReference document)
    {
        var now = DateTime.UtcNow;

        if (document.EffectiveDate.HasValue && ToUtc(document.EffectiveDate.Value) > now)
        {
            return "off";
        }

        if (plan.TerminationDate.HasValue && ToUtc(plan.TerminationDate.Value) < now)
        {
            // A retired parent plan retires its endpoints too. The
            // document's URL may still resolve, but the operator's
            // authored intent is "no longer current member-facing
            // material" — surfacing it as off keeps Plan-Net consumers
            // honest.
            return "off";
        }

        return "active";
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static string PlanDocumentTypeCode(PlanDocumentType docType) => docType switch
    {
        PlanDocumentType.SBC                     => "sbc",
        PlanDocumentType.EOC                     => "eoc",
        PlanDocumentType.Formulary               => "formulary",
        PlanDocumentType.SPD                     => "spd",
        PlanDocumentType.MachineReadableRateFile => "mrf",
        PlanDocumentType.Other                   => "other",
        _                                        => "other",
    };

    private static string PlanDocumentTypeDisplay(PlanDocumentType docType) => docType switch
    {
        PlanDocumentType.SBC                     => "Summary of Benefits and Coverage",
        PlanDocumentType.EOC                     => "Evidence of Coverage",
        PlanDocumentType.Formulary               => "Drug Formulary",
        PlanDocumentType.SPD                     => "Summary Plan Description",
        PlanDocumentType.MachineReadableRateFile => "Machine-Readable Rate File",
        PlanDocumentType.Other                   => "Other Plan Document",
        _                                        => "Other Plan Document",
    };

    /// <summary>
    /// Decision 8 ordering — SBC consumer-facing first; matches member-app
    /// consumer expectations. Exposed (assembly-internal) so
    /// <see cref="Controllers.FhirEndpointController"/> can apply the same
    /// ordering across plans when sorting the cross-plan search bundle.
    /// </summary>
    internal static int DocTypeOrdinal(PlanDocumentType docType) => docType switch
    {
        PlanDocumentType.SBC                     => 1,
        PlanDocumentType.EOC                     => 2,
        PlanDocumentType.Formulary               => 3,
        PlanDocumentType.SPD                     => 4,
        PlanDocumentType.MachineReadableRateFile => 5,
        PlanDocumentType.Other                   => 6,
        _                                        => 99,
    };

    private static string? ResolveName(PlanDocumentReference document)
    {
        if (!string.IsNullOrWhiteSpace(document.DisplayName)) return document.DisplayName;
        return PlanDocumentTypeDisplay(document.DocType);
    }

    /// <summary>
    /// FHIR <c>meta.lastUpdated</c> is "the instant the resource was last
    /// updated by the server" — NOT the document's effective window date.
    /// Future-dated documents (status=off per Decision 5) would otherwise
    /// produce a future <c>lastUpdated</c>, which violates the FHIR
    /// contract and breaks consumer cache-freshness logic. Mirror the
    /// InsurancePlan projector: prefer the plan's modify/publish/create
    /// timestamps. Copilot review BP 5.9.
    /// </summary>
    private static DateTime ResolveLastUpdated(BenefitPlan plan)
    {
        if (plan.ModifiedDate.HasValue) return plan.ModifiedDate.Value;
        if (plan.PublishedAt.HasValue) return plan.PublishedAt.Value;
        if (plan.UpdatedAt != default) return plan.UpdatedAt;
        return plan.CreatedAt;
    }

    private static DateTime ToUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string ToFhirDate(DateTime value)
        => ToUtc(value).ToString("yyyy-MM-dd");

    private static string ToFhirInstant(DateTime value)
        => ToUtc(value).ToString("o");
}
