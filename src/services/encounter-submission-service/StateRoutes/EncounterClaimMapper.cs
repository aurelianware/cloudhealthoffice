using System.Text.Json;
using System.Text.Json.Serialization;

namespace EncounterSubmissionService.StateRoutes;

/// <summary>
/// Maps the claims-service, member-service and provider-service records into an
/// <see cref="EncounterClaim"/>. The DTOs below read only the fields an encounter
/// needs; the services' own models stay the source of truth.
/// </summary>
public static class EncounterClaimMapper
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static EncounterClaim Map(
        string submissionId, ClaimRecord claim, MemberRecord? member, ProviderRecord? billing)
    {
        var form = claim.ClaimType switch
        {
            ClaimRecordType.Institutional => EncounterFormType.Institutional,
            ClaimRecordType.Dental => EncounterFormType.Dental,
            _ => EncounterFormType.Professional
        };

        var adjudicated = claim.AdjudicatedDate ?? claim.PaidDate ?? claim.LastUpdatedDate;

        var lines = claim.ClaimLines.OrderBy(l => l.LineNumber).Select(l => new EncounterLine
        {
            LineNumber = l.LineNumber,
            ProcedureQualifier = "HC",
            ProcedureCode = l.ProcedureCode ?? string.Empty,
            Modifiers = l.Modifiers ?? new List<string>(),
            RevenueCode = l.RevenueCode,
            Charge = l.ChargeAmount,
            Units = l.Units,
            UnitType = string.IsNullOrWhiteSpace(l.UnitType) ? "UN" : l.UnitType!,
            DiagnosisPointers = l.DiagnosisPointers ?? new List<int>(),
            ServiceFrom = DateOnly.FromDateTime(l.ServiceDateFrom == default ? claim.ServiceDateFrom : l.ServiceDateFrom),
            ServiceTo = DateOnly.FromDateTime(l.ServiceDateTo == default ? claim.ServiceDateTo : l.ServiceDateTo),
            PlaceOfService = l.PlaceOfServiceCode,
            NationalDrugCode = l.NationalDrugCode,
            DrugQuantity = l.DrugQuantity,
            DrugUnit = l.DrugUnitOfMeasure,
            PaidAmount = l.AdjudicationResult?.PaidAmount ?? 0m,
            Adjustments = (l.AdjudicationResult?.AdjustmentReasons ?? new List<AdjustmentRecord>())
                .Select(a => new EncounterAdjustment(a.GroupCode, a.ReasonCode, a.Amount))
                .ToList()
        }).ToList();

        var renderingName = SplitName(claim.RenderingProviderName);

        return new EncounterClaim
        {
            SubmissionId = submissionId,
            ClaimId = claim.Id,
            PatientControlNumber = string.IsNullOrWhiteSpace(claim.ClaimNumber) ? claim.Id : claim.ClaimNumber,
            Form = form,
            FrequencyCode = "1",
            TotalCharge = claim.TotalChargeAmount,
            PlaceOfService = claim.PlaceOfServiceCode,
            FacilityTypeCode = claim.Institutional?.FacilityTypeCode,
            ServiceFrom = DateOnly.FromDateTime(claim.Institutional?.StatementFromDate ?? claim.ServiceDateFrom),
            ServiceTo = DateOnly.FromDateTime(claim.Institutional?.StatementToDate ?? claim.ServiceDateTo),
            AdmissionDate = claim.Institutional?.AdmissionDate is { } ad ? DateOnly.FromDateTime(ad) : null,
            AdmissionHour = claim.Institutional?.AdmissionHour,
            AdmissionTypeCode = claim.Institutional?.AdmissionTypeCode,
            AdmissionSourceCode = claim.Institutional?.AdmissionSourceCode,
            PatientStatusCode = claim.Institutional?.PatientStatusCode,
            PriorAuthorizationNumber = claim.PriorAuthorizationNumber,
            Diagnoses = claim.DiagnosisCodes
                .OrderBy(d => d.PointerNumber == 0 ? int.MaxValue : d.PointerNumber)
                .Select(d => new EncounterDiagnosis(d.Code, d.PresentOnAdmission))
                .ToList(),
            Member = new EncounterMember
            {
                MemberId = claim.SubscriberId ?? member?.MemberId ?? claim.MemberId,
                GroupNumber = member?.GroupNumber,
                LastName = member?.LastName ?? claim.PatientLastName ?? claim.SubscriberLastName ?? string.Empty,
                FirstName = member?.FirstName ?? claim.PatientFirstName ?? claim.SubscriberFirstName,
                DateOfBirth = member is { DateOfBirth: var dob } && dob != default ? DateOnly.FromDateTime(dob) : null,
                Gender = NormalizeGender(member?.Gender),
                AddressLine = member?.Address,
                City = member?.City,
                State = member?.State,
                PostalCode = member?.ZipCode
            },
            BillingProvider = new EncounterProvider
            {
                Npi = claim.BillingProviderNPI,
                EntityType = claim.BillingProviderEntityType == "1"
                    || (billing is not null && string.IsNullOrWhiteSpace(billing.OrganizationName)
                        && !string.IsNullOrWhiteSpace(billing.LastName))
                    ? "1" : "2",
                LastOrOrganizationName = FirstNonEmpty(
                    billing?.OrganizationName, billing?.LastName, claim.BillingProviderName) ?? string.Empty,
                FirstName = billing?.FirstName,
                TaxId = billing?.TaxId,
                TaxonomyCode = billing?.TaxonomyCode,
                AddressLine = billing?.Address,
                City = billing?.City,
                State = billing?.State,
                PostalCode = billing?.ZipCode
            },
            RenderingNpi = claim.RenderingProviderNPI,
            RenderingLastName = renderingName.Last,
            RenderingFirstName = renderingName.First,
            PlanPaidAmount = claim.AdjudicationResult?.PayerPayment ?? lines.Sum(l => l.PaidAmount),
            PlanAdjudicationDate = DateOnly.FromDateTime(adjudicated),
            Lines = lines
        };
    }

    private static string NormalizeGender(string? gender) =>
        (gender ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "M" or "MALE" => "M",
            "F" or "FEMALE" => "F",
            _ => "U"
        };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>Claims carry the rendering provider as one string, "Last, First" or "First Last".</summary>
    private static (string? Last, string? First) SplitName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, null);
        var comma = name.IndexOf(',');
        if (comma > 0) return (name[..comma].Trim(), name[(comma + 1)..].Trim());
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1 ? (parts[0], null) : (parts[^1], string.Join(' ', parts[..^1]));
    }
}

// ── Read models (subset of the owning services' records) ─────────────────

public enum ClaimRecordType
{
    Professional = 1,
    Institutional = 2,
    Dental = 3
}

public sealed class ClaimRecord
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string? SubscriberId { get; set; }
    public string? SubscriberFirstName { get; set; }
    public string? SubscriberLastName { get; set; }
    public string? PatientFirstName { get; set; }
    public string? PatientLastName { get; set; }
    public string BillingProviderNPI { get; set; } = string.Empty;
    public string? BillingProviderName { get; set; }
    public string? BillingProviderEntityType { get; set; }
    public string? RenderingProviderNPI { get; set; }
    public string? RenderingProviderName { get; set; }
    public string? PlaceOfServiceCode { get; set; }
    public ClaimRecordType ClaimType { get; set; } = ClaimRecordType.Professional;
    public string? ClaimFrequencyCode { get; set; }
    public decimal TotalChargeAmount { get; set; }
    public DateTime ServiceDateFrom { get; set; }
    public DateTime ServiceDateTo { get; set; }
    public List<DiagnosisRecord> DiagnosisCodes { get; set; } = new();
    public List<ClaimLineRecord> ClaimLines { get; set; } = new();
    public InstitutionalRecord? Institutional { get; set; }
    public DateTime? AdjudicatedDate { get; set; }
    public DateTime? PaidDate { get; set; }
    public DateTime LastUpdatedDate { get; set; }
    public ClaimAdjudicationRecord? AdjudicationResult { get; set; }
    public string? PriorAuthorizationNumber { get; set; }
}

public sealed class DiagnosisRecord
{
    public string Code { get; set; } = string.Empty;
    public int PointerNumber { get; set; }
    public string? PresentOnAdmission { get; set; }
}

public sealed class ClaimLineRecord
{
    public int LineNumber { get; set; }
    public string? ProcedureCode { get; set; }
    public List<string>? Modifiers { get; set; }
    public List<int>? DiagnosisPointers { get; set; }
    public decimal Units { get; set; } = 1;
    public string? UnitType { get; set; }
    public decimal ChargeAmount { get; set; }
    public DateTime ServiceDateFrom { get; set; }
    public DateTime ServiceDateTo { get; set; }
    public string? PlaceOfServiceCode { get; set; }
    public string? RevenueCode { get; set; }
    public string? NationalDrugCode { get; set; }
    public decimal? DrugQuantity { get; set; }
    public string? DrugUnitOfMeasure { get; set; }
    public LineAdjudicationRecord? AdjudicationResult { get; set; }
}

public sealed class InstitutionalRecord
{
    public string? FacilityTypeCode { get; set; }
    public DateTime? AdmissionDate { get; set; }
    public string? AdmissionHour { get; set; }
    public DateTime? StatementFromDate { get; set; }
    public DateTime? StatementToDate { get; set; }
    public string? AdmissionTypeCode { get; set; }
    public string? AdmissionSourceCode { get; set; }
    public string? PatientStatusCode { get; set; }
}

public sealed class ClaimAdjudicationRecord
{
    public decimal PayerPayment { get; set; }
    public List<AdjustmentRecord> AdjustmentReasons { get; set; } = new();
}

public sealed class LineAdjudicationRecord
{
    public decimal PaidAmount { get; set; }
    public List<AdjustmentRecord> AdjustmentReasons { get; set; } = new();
}

public sealed class AdjustmentRecord
{
    public string GroupCode { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public sealed class MemberRecord
{
    public string MemberId { get; set; } = string.Empty;
    public string? GroupNumber { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
}

public sealed class ProviderRecord
{
    public string NPI { get; set; } = string.Empty;
    public string? TaxId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? OrganizationName { get; set; }
    public string? TaxonomyCode { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
}
