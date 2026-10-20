using System.Globalization;
using System.Text.Json.Nodes;
using CoverageService.Models;

namespace CoverageService.Services;

/// <summary>Who a projected Coverage names as its payor.</summary>
public sealed class FhirCoverageProjectionOptions
{
    public const string SectionName = "Fhir:Coverage";

    /// <summary>
    /// Display text for <c>Coverage.payor</c>. When empty the tenant id is used,
    /// so a projection never names a payer the deployment did not configure.
    /// </summary>
    public string? PayorDisplay { get; set; }

    /// <summary>Optional literal reference for the payor, e.g. <c>Organization/plan-1</c>.</summary>
    public string? PayorReference { get; set; }
}

public interface IFhirCoverageProjector
{
    JsonObject Project(Coverage coverage);
}

/// <summary>
/// Hand-built FHIR R4 Coverage projection of a CHO coverage record (ADR 018),
/// in the same style as member-service's Patient projector (no Hl7.Fhir
/// dependency). Populates status, the CHO status as an extension, type (line
/// of business), beneficiary as <c>Patient/{memberId}</c> — the id every CHO
/// resource uses for a patient — period, payor and the group and plan classes.
///
/// Not populated, because coverage-service does not hold them: subscriber,
/// subscriberId, relationship and dependent (they live on the member record).
/// </summary>
public sealed class FhirCoverageProjector : IFhirCoverageProjector
{
    public const string CanonicalBase = "http://fhir.cloudhealthoffice.com/";
    public const string CoverageStatusExtension = CanonicalBase + "StructureDefinition/cho-coverage-status";
    public const string LineOfBusinessSystem = CanonicalBase + "CodeSystem/cho-line-of-business";
    public const string CoverageClassSystem = "http://terminology.hl7.org/CodeSystem/coverage-class";

    private readonly FhirCoverageProjectionOptions _options;

    public FhirCoverageProjector(FhirCoverageProjectionOptions? options = null)
    {
        _options = options ?? new FhirCoverageProjectionOptions();
    }

    public JsonObject Project(Coverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);

        var resource = new JsonObject
        {
            ["resourceType"] = "Coverage",
            ["id"] = coverage.Id,
            ["meta"] = new JsonObject { ["lastUpdated"] = AsUtc(coverage.LastUpdatedDate).ToString("o", CultureInfo.InvariantCulture) },
            ["extension"] = new JsonArray(new JsonObject
            {
                ["url"] = CoverageStatusExtension,
                ["valueCode"] = coverage.Status.ToString(),
            }),
            ["status"] = FhirStatus(coverage.Status),
            ["type"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = LineOfBusinessSystem,
                    ["code"] = coverage.LineOfBusiness.ToString(),
                    ["display"] = coverage.LineOfBusiness.ToString(),
                }),
            },
            ["beneficiary"] = new JsonObject { ["reference"] = $"Patient/{coverage.MemberId}" },
            ["period"] = Period(coverage),
            ["payor"] = new JsonArray(Payor(coverage)),
        };

        var classes = new JsonArray();
        if (!string.IsNullOrWhiteSpace(coverage.GroupNumber))
            classes.Add(CoverageClass("group", "Group", coverage.GroupNumber));
        if (!string.IsNullOrWhiteSpace(coverage.PlanId))
            classes.Add(CoverageClass("plan", "Plan", coverage.PlanId));
        if (classes.Count > 0)
            resource["class"] = classes;

        return resource;
    }

    /// <summary>
    /// CHO coverage status → FHIR financial resource status. Coverage that is in
    /// force (Active, COBRA) or temporarily suspended is still an active record;
    /// the exact CHO status travels in the <see cref="CoverageStatusExtension"/>
    /// and the period says when it applies. Pending is not yet in force (draft);
    /// Terminated is cancelled.
    /// </summary>
    public static string FhirStatus(CoverageStatus status) => status switch
    {
        CoverageStatus.Pending => "draft",
        CoverageStatus.Terminated => "cancelled",
        _ => "active",
    };

    /// <summary>The CHO statuses a FHIR status covers; the inverse of <see cref="FhirStatus"/>.</summary>
    public static IReadOnlyList<CoverageStatus> ChoStatuses(string fhirStatus) => fhirStatus switch
    {
        "active" => [CoverageStatus.Active, CoverageStatus.COBRA, CoverageStatus.Suspended],
        "draft" => [CoverageStatus.Pending],
        "cancelled" => [CoverageStatus.Terminated],
        _ => [],
    };

    private static JsonObject Period(Coverage coverage)
    {
        var period = new JsonObject { ["start"] = coverage.EffectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        if (coverage.TerminationDate is { } end)
            period["end"] = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return period;
    }

    private JsonObject Payor(Coverage coverage)
    {
        var payor = new JsonObject
        {
            ["display"] = string.IsNullOrWhiteSpace(_options.PayorDisplay) ? coverage.TenantId : _options.PayorDisplay.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(_options.PayorReference))
            payor["reference"] = _options.PayorReference.Trim();
        return payor;
    }

    private static JsonObject CoverageClass(string code, string display, string value) => new()
    {
        ["type"] = new JsonObject
        {
            ["coding"] = new JsonArray(new JsonObject
            {
                ["system"] = CoverageClassSystem,
                ["code"] = code,
                ["display"] = display,
            }),
        },
        ["value"] = value,
    };

    private static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime()
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
