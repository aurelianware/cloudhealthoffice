using System.Globalization;
using System.Text;
using ClaimsService.Models;

namespace ClaimsService.Extracts;

/// <summary>
/// The claims warehouse extract's two files: one row per claim version
/// (<see cref="Headers"/>) and one per line of that version
/// (<see cref="Lines"/>). Every version is a row, superseded ones included,
/// so the warehouse can rebuild a claim's history; the current version is
/// the one with an empty <c>SupersededAt</c>. The columns are fixed and named
/// here, in order, so a plan's loader can map them once.
/// </summary>
public static class ClaimExtractColumns
{
    public const string HeadersFile = "claims.csv";
    public const string LinesFile = "claim-lines.csv";
    public const string ManifestFile = "manifest.json";

    /// <summary>Bumped when a column is added, removed or changes meaning.</summary>
    public const int SchemaVersion = 1;

    public static readonly IReadOnlyList<string> Headers =
    [
        "ClaimId", "ClaimVersionId", "VersionNumber", "VersionState", "PredecessorVersionId", "SupersededAt",
        "ClaimNumber", "ClaimType", "ClaimFrequencyCode", "LineOfBusiness",
        "MemberId", "SubscriberId", "BenefitPlanId",
        "BillingProviderNpi", "RenderingProviderNpi", "FacilityNpi", "PlaceOfServiceCode",
        "ServiceDateFrom", "ServiceDateTo", "PrincipalDiagnosisCode", "DiagnosisCodes",
        "Status", "SubmittedDate", "ReceivedDate", "AdjudicatedDate", "PaidDate",
        "TotalChargeAmount", "AllowedAmount", "PayerPayment", "PatientResponsibility",
        "DeductibleAmount", "CopayAmount", "CoinsuranceAmount", "NetworkTier",
        "DenialReasonCode", "AdjustmentReasonCodes", "RemarkCodes", "CheckNumber", "PaymentDate",
        "PriorAuthorizationNumber", "SourceSystem", "SourceClaimId",
        "CreatedDate", "LastUpdatedDate",
    ];

    public static readonly IReadOnlyList<string> Lines =
    [
        "ClaimId", "ClaimVersionId", "LineNumber",
        "ProcedureCode", "Modifiers", "RevenueCode", "NationalDrugCode", "Units", "UnitType",
        "ServiceDateFrom", "ServiceDateTo", "PlaceOfServiceCode", "DiagnosisPointers",
        "ChargeAmount", "AllowedAmount", "PaidAmount", "PatientResponsibility",
        "PriorAuthorizationNumber",
    ];
}

/// <summary>Turns a claim version into its extract rows, as CSV fields in <see cref="ClaimExtractColumns"/> order.</summary>
public static class ClaimExtractRows
{
    /// <summary>The claim version's header row.</summary>
    public static IReadOnlyList<string> Header(Claim c)
    {
        var a = c.AdjudicationResult;
        var diagnoses = c.DiagnosisCodes.OrderBy(d => d.PointerNumber).Select(d => d.Code).ToList();
        return
        [
            c.Id, c.ClaimVersionId, Field(c.VersionNumber), c.VersionState.ToString(), c.PredecessorVersionId ?? "", Field(c.SupersededAt),
            c.ClaimNumber, c.ClaimType.ToString(), c.ClaimFrequencyCode, c.LineOfBusiness.ToString(),
            c.MemberId, c.SubscriberId ?? "", c.BenefitPlanId ?? "",
            c.BillingProviderNPI, c.RenderingProviderNPI ?? "", c.FacilityNPI ?? "", c.PlaceOfServiceCode,
            Date(c.ServiceDateFrom), Date(c.ServiceDateTo), diagnoses.FirstOrDefault() ?? "", string.Join('|', diagnoses),
            c.Status.ToString(), Field(c.SubmittedDate), Field(c.ReceivedDate), Field(c.AdjudicatedDate), Field(c.PaidDate),
            Field(c.TotalChargeAmount), Field(a?.AllowedAmount), Field(a?.PayerPayment), Field(a?.PatientResponsibility),
            Field(a?.DeductibleAmount), Field(a?.CopayAmount), Field(a?.CoinsuranceAmount), a?.NetworkTier ?? "",
            a?.DenialReasonCode ?? "",
            string.Join('|', (a?.AdjustmentReasons ?? []).Select(r => $"{r.GroupCode}-{r.ReasonCode}")),
            string.Join('|', a?.RemarkCodes ?? []), a?.CheckNumber ?? "", Field(a?.PaymentDate),
            c.PriorAuthorizationNumber ?? "", c.ExternalSource?.SourceSystem ?? "", c.ExternalSource?.SourceClaimId ?? "",
            Field(c.CreatedDate), Field(c.LastUpdatedDate),
        ];
    }

    /// <summary>The claim version's line rows, in line number order.</summary>
    public static IEnumerable<IReadOnlyList<string>> Lines(Claim c) =>
        c.ClaimLines.OrderBy(l => l.LineNumber).Select(l => (IReadOnlyList<string>)
        [
            c.Id, c.ClaimVersionId, Field(l.LineNumber),
            l.ProcedureCode, string.Join(':', l.Modifiers), l.RevenueCode ?? "", l.NationalDrugCode ?? "",
            Field(l.Units), l.UnitType ?? "",
            Date(l.ServiceDateFrom), Date(l.ServiceDateTo), l.PlaceOfServiceCode ?? c.PlaceOfServiceCode,
            string.Join(':', l.DiagnosisPointers),
            Field(l.ChargeAmount), Field(l.AdjudicationResult?.AllowedAmount), Field(l.AdjudicationResult?.PaidAmount),
            Field(l.AdjudicationResult?.PatientResponsibility),
            l.PriorAuthorizationNumber ?? "",
        ]);

    /// <summary>
    /// One RFC 4180 CSV record. Values are written as they are stored: a
    /// warehouse loader reads them, not a spreadsheet, so nothing is
    /// prefixed to defuse formulas (the portal's CSV downloads do that).
    /// </summary>
    public static string Record(IReadOnlyList<string> fields)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var f = fields[i];
            if (f.IndexOfAny([',', '"', '\n', '\r']) >= 0)
                sb.Append('"').Append(f.Replace("\"", "\"\"")).Append('"');
            else
                sb.Append(f);
        }
        return sb.ToString();
    }

    private static string Field(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Field(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Field(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>An instant, in UTC ISO 8601.</summary>
    private static string Field(DateTime? value) =>
        value is { } v ? v.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : "";

    /// <summary>A service date: a calendar date, no time.</summary>
    private static string Date(DateTime value) =>
        value == default ? "" : value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
