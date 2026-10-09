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

        var institutional = source.ClaimType == EngineModels.ClaimType.Institutional
            ? MapInstitutional(source.ClaimHeader)
            : null;

        // 837I service-line dates (DTP*472) are situational; a line without
        // one inherits the claim's statement covers period (DTP*434).
        var claimLines = source.ServiceLines
            .Select(l => MapLine(l, institutional?.StatementFromDate, institutional?.StatementToDate))
            .ToList();
        var (serviceDateFrom, serviceDateTo) = DeriveClaimDateRange(claimLines);
        if (institutional?.StatementFromDate is { } statementFrom && institutional.StatementToDate is { } statementTo
            && (serviceDateFrom == default || serviceDateTo == default))
        {
            (serviceDateFrom, serviceDateTo) = (statementFrom, statementTo);
        }

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
            Institutional = institutional,

            PayerResponsibilityCode = string.IsNullOrWhiteSpace(source.PayerResponsibilityCode)
                ? null
                : source.PayerResponsibilityCode.Trim(),
            OtherPayers = MapOtherPayers(source),

            Status = ClaimStatus.Submitted,
            SubmittedDate = DateTime.UtcNow,

            PriorAuthorizationNumber = source.ClaimHeader.PriorAuthorizationNumber,
            EDI837ControlNumber = source.TransactionControlNumber,
        };
    }

    /// <summary>
    /// 2320/2330B other payers with their 2430 line adjudication. A 2430
    /// SVD01 names its payer by the 2330B NM109 identifier; an SVD whose
    /// identifier matches no 2330B payer is attributed to the only other
    /// payer when there is exactly one, and otherwise dropped (it cannot be
    /// placed in the payer order).
    /// </summary>
    private static List<ClaimOtherPayer> MapOtherPayers(EngineModels.X12837Claim source)
    {
        var payers = (source.OtherPayers ?? [])
            .Select(p => new ClaimOtherPayer
            {
                PayerResponsibilityCode = p.PayerResponsibilityCode.Trim(),
                PayerName = p.PayerName,
                PayerId = p.PayerId,
                PaidAmount = p.PaidAmount,
                ClaimAdjustments = p.ClaimAdjustments.Select(MapAdjustment).ToList(),
            })
            .ToList();
        if (payers.Count == 0)
            return payers;

        foreach (var line in source.ServiceLines)
        {
            foreach (var svd in line.OtherPayerAdjudications ?? [])
            {
                var payer = payers.FirstOrDefault(p =>
                                !string.IsNullOrEmpty(p.PayerId)
                                && string.Equals(p.PayerId, svd.PayerId?.Trim(), StringComparison.OrdinalIgnoreCase))
                            ?? (payers.Count == 1 ? payers[0] : null);
                payer?.LineAdjudications.Add(new ClaimOtherPayerLine
                {
                    LineNumber = line.LineNumber,
                    PaidAmount = svd.PaidAmount,
                    Adjustments = svd.Adjustments.Select(MapAdjustment).ToList(),
                });
            }
        }
        return payers;
    }

    private static ClaimAdjustmentReason MapAdjustment(EngineModels.ClaimAdjustmentEntry a) => new()
    {
        GroupCode = a.GroupCode,
        ReasonCode = a.ReasonCode,
        Amount = a.Amount,
    };

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
        PresentOnAdmission = string.IsNullOrEmpty(d.PresentOnAdmission) ? null : d.PresentOnAdmission,
    };

    private static InstitutionalClaimDetails MapInstitutional(EngineModels.ClaimHeader header) => new()
    {
        FacilityTypeCode = header.FacilityTypeCode,
        AdmissionDate = ParseD8(header.AdmissionDate),
        AdmissionHour = header.AdmissionHour,
        DischargeDate = ParseD8(header.DischargeDate),
        DischargeHour = header.DischargeHour,
        StatementFromDate = ParseD8(header.StatementFromDate),
        StatementToDate = ParseD8(header.StatementToDate),
        AdmissionTypeCode = header.AdmissionTypeCode,
        AdmissionSourceCode = header.AdmissionSourceCode,
        PatientStatusCode = header.PatientStatusCode,
        DrgCode = header.DrgCode,
        PrincipalProcedure = header.PrincipalProcedure is { } principal ? MapProcedure(principal) : null,
        OtherProcedures = (header.OtherProcedures ?? []).Select(MapProcedure).ToList(),
        OccurrenceCodes = (header.OccurrenceCodes ?? [])
            .Select(c => new OccurrenceCode { Code = c.Code, Date = ParseD8(c.Date) })
            .ToList(),
        OccurrenceSpanCodes = (header.OccurrenceSpanCodes ?? [])
            .Select(c => new OccurrenceSpanCode { Code = c.Code, FromDate = ParseD8(c.Date), ToDate = ParseD8(c.DateEnd) })
            .ToList(),
        ValueCodes = (header.ValueCodes ?? [])
            .Select(c => new ValueCode { Code = c.Code, Amount = c.Amount })
            .ToList(),
        ConditionCodes = (header.ConditionCodes ?? []).Select(c => c.Code).ToList(),
    };

    private static InstitutionalProcedureCode MapProcedure(EngineModels.InstitutionalCode code) => new()
    {
        Code = code.Code,
        CodeQualifier = code.Qualifier,
        Date = ParseD8(code.Date),
    };

    private static AdapterClaimLine MapLine(EngineModels.ServiceLine line, DateTime? defaultFrom, DateTime? defaultTo)
    {
        var lineFrom = ParseD8(line.ServiceDate);
        var from = lineFrom ?? defaultFrom ?? default;
        var to = ParseD8(line.ServiceDateEnd) ?? (lineFrom.HasValue ? from : defaultTo ?? from);

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
            RevenueCode = string.IsNullOrEmpty(line.RevenueCode) ? null : line.RevenueCode,
            NationalDrugCode = line.NationalDrugCode,
            DrugQuantity = line.DrugQuantity,
            DrugUnitOfMeasure = line.DrugUnitOfMeasure,
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
