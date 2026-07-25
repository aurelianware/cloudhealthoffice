using System.Globalization;
using ClaimsService.Models;
using EngineModels = CloudHealthOffice.ClaimsScrubEngine.Models;

namespace ClaimsService.EDI.Inbound;

/// <summary>
/// Maps a parsed <see cref="EngineModels.X12837Claim"/> onto the
/// <see cref="AdapterClaim"/> shape <c>IClaimSubmissionService.SubmitAsync</c>
/// already accepts — the inbound counterpart of
/// <c>ClaimToX12837Mapper</c>. Deliberately does *not* try to resolve
/// BenefitPlanId/CoverageId: submission-time validation
/// (<c>ClaimSubmissionService.Validate</c>) doesn't require them, and
/// leaving them null lets a member/coverage CHO doesn't recognize surface
/// as a real pend/deny outcome during adjudication rather than being
/// silently papered over here.
/// </summary>
public static class X12837ClaimMapper
{
    public static AdapterClaim Map(EngineModels.X12837Claim source, string tenantId)
    {
        // A dependent-as-patient claim must resolve against the
        // dependent's own MemberId, not the subscriber's — attributing
        // it to the subscriber would misfile it against the wrong
        // person's accumulators. When the source 837 doesn't carry the
        // dependent's own id (some payers expect demographic matching
        // instead, which nothing in this codebase does), this falls
        // through to the subscriber's id and the claim will very likely
        // fail member resolution downstream — a real, informative
        // failure, not a silent misattribution.
        var memberId = source.Patient?.MemberId ?? source.Subscriber.MemberId;

        var claimLines = source.ServiceLines.Select(MapLine).ToList();
        var (serviceDateFrom, serviceDateTo) = DeriveClaimDateRange(claimLines);

        return new AdapterClaim
        {
            TenantId = tenantId,
            ClaimNumber = source.ClaimId,
            MemberId = memberId,
            SubscriberId = source.Subscriber.MemberId,

            SubscriberFirstName = source.Subscriber.FirstName,
            SubscriberLastName = source.Subscriber.LastName,
            PatientFirstName = source.Patient?.FirstName,
            PatientLastName = source.Patient?.LastName,
            PatientRelationship = source.Patient?.RelationshipCode,

            // Not carried by the 837 itself — it's a payer/plan-level
            // classification, not part of the claim transaction. Commercial
            // is the safest default; callers who know better (e.g. a
            // tenant-level default) can override after mapping.
            LineOfBusiness = LineOfBusiness.Commercial,

            BillingProviderNPI = source.BillingProvider.Npi,
            BillingProviderName = source.BillingProvider.Name,
            RenderingProviderNPI = source.ClaimHeader.RenderingProvider?.Npi,
            RenderingProviderName = source.ClaimHeader.RenderingProvider?.Name,

            PlaceOfServiceCode = source.ClaimHeader.PlaceOfServiceCode ?? "11",
            ClaimType = MapClaimType(source.ClaimType),
            ClaimFrequencyCode = source.ClaimHeader.FrequencyCode ?? "1",
            TotalChargeAmount = source.TotalClaimedAmount,

            ServiceDateFrom = serviceDateFrom,
            ServiceDateTo = serviceDateTo,

            DiagnosisCodes = (source.ClaimHeader.DiagnosisCodes ?? []).Select(MapDiagnosis).ToList(),
            ClaimLines = claimLines,

            Status = ClaimStatus.Submitted,
            SubmittedDate = DateTime.UtcNow,

            PriorAuthorizationNumber = source.ClaimHeader.PriorAuthorizationNumber,
            EDI837ControlNumber = source.TransactionControlNumber,
        };
    }

    // Platform ClaimType is 1-based (Professional=1,...); the engine's is
    // 0-based (Professional=0,...) — same trap ClaimToX12837Mapper's
    // MapClaimType comment warns about. Switch by name, never raw-cast.
    private static ClaimType MapClaimType(EngineModels.ClaimType type) => type switch
    {
        EngineModels.ClaimType.Professional => ClaimType.Professional,
        EngineModels.ClaimType.Institutional => ClaimType.Institutional,
        EngineModels.ClaimType.Dental => ClaimType.Dental,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown engine ClaimType")
    };

    private static AdapterDiagnosisCode MapDiagnosis(EngineModels.DiagnosisCode d) => new()
    {
        Code = d.Code,
        CodeQualifier = d.Qualifier,
        PointerNumber = d.Pointer ?? 0,
    };

    private static AdapterClaimLine MapLine(EngineModels.ServiceLine line)
    {
        var from = ParseD8(line.ServiceDate) ?? default;
        var to = ParseD8(line.ServiceDateEnd) ?? from;

        return new AdapterClaimLine
        {
            LineNumber = line.LineNumber,
            ProcedureCode = line.ProcedureCode,
            ProcedureDescription = line.Description,
            Modifiers = line.Modifiers ?? [],
            DiagnosisPointers = line.DiagnosisPointers ?? [],
            Units = line.Units,
            ChargeAmount = line.ChargeAmount,
            ServiceDateFrom = from,
            ServiceDateTo = to,
            PlaceOfServiceCode = line.PlaceOfService,
            RevenueCode = line.RevenueCode,
        };
    }

    private static (DateTime From, DateTime To) DeriveClaimDateRange(List<AdapterClaimLine> lines)
    {
        if (lines.Count == 0)
        {
            return (default, default);
        }
        return (lines.Min(l => l.ServiceDateFrom), lines.Max(l => l.ServiceDateTo));
    }

    private static DateTime? ParseD8(string? d8Date) =>
        !string.IsNullOrEmpty(d8Date) &&
        DateTime.TryParseExact(d8Date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}
