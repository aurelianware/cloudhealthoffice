using System.Globalization;
using ClaimsService.Fhir;
using ClaimsService.Import.Cpcds;
using ClaimsService.Models;
using C = ClaimsService.Import.Cpcds.CpcdsClaimColumn;

namespace ClaimsService.Extracts.Cpcds;

/// <summary>
/// A claim as CPCDS claim rows: one row per claim line with the claim header
/// repeated on each, the layout the CARIN sample data and CHO's own CPCDS
/// importer use. The header names are the importer's
/// (<see cref="CpcdsClaimColumns.DisplayName"/>), so a CPCDS export from one
/// CHO tenant loads into another, or into any tool that reads CPCDS, without
/// a column map.
///
/// <para>
/// Only adjudicated claims are CPCDS claims: paid, partially paid, denied
/// (and approved or denied but not yet remitted), and voided. A claim still
/// in adjudication has no answer to report. And only the current version of a
/// claim is written: a CPCDS consumer keys on the claim identifier, so a
/// changed claim is the same identifier again with its new content, which is
/// how CHO's importer reads a reload.
/// </para>
/// </summary>
public static class CpcdsClaimExportRows
{
    public const string FileName = "CPCDS_Claims.csv";

    /// <summary>The columns, in order.</summary>
    public static readonly IReadOnlyList<C> Columns =
    [
        C.ClaimUniqueIdentifier, C.MemberId, C.CoverageId, C.ClaimType,
        C.ClaimServiceStartDate, C.ClaimServiceEndDate, C.ClaimReceivedDate, C.ClaimPaidDate,
        C.MemberAdmissionDate, C.MemberDischargeDate,
        C.ClaimBillFacilityTypeCode, C.ClaimServiceClassificationTypeCode, C.ClaimFrequencyCode,
        C.ClaimDiagnosisRelatedGroup, C.ClaimInpatientAdmissionTypeCode, C.ClaimSourceInpatientAdmissionCode,
        C.PatientDischargeStatusCode,
        C.ClaimProcessingStatusCode, C.ClaimPaymentStatusCode, C.ClaimPaymentDenialCode,
        C.ClaimAdjustedFromIdentifier,
        C.ClaimBillingProviderNpi, C.ClaimBillingProviderNetworkStatus, C.ClaimPerformingProviderNpi,
        C.ClaimSiteOfServiceNpi, C.ClaimPrescribingProviderNpi,
        C.ClaimTotalSubmittedAmount, C.ClaimTotalAllowedAmount, C.ClaimPaymentAmount,
        C.MemberPaidDeductible, C.CoinsuranceLiabilityAmount, C.CopayAmount, C.MemberLiability,
        C.DaysSupply, C.RxServiceReferenceNumber, C.DawProductSelectionCode, C.RefillNumber, C.RefillsAuthorized,
        C.PrescriptionOriginCode, C.PlanReportedBrandGenericCode, C.CompoundCode, C.PharmacyServiceTypeCode,
        C.PatientResidenceCode,
        C.LineNumber, C.ServiceFromDate, C.ServiceToDate, C.PlaceOfServiceCode, C.RevenueCenterCode,
        C.NumberOfUnits, C.NationalDrugCode, C.QuantityDispensed,
        C.LineSubmittedAmount, C.LineAllowedAmount, C.LinePaymentAmount, C.LineMemberLiability, C.LinePaymentDenialCode,
        C.ToothNumber, C.ToothSurface,
        C.DiagnosisCode, C.DiagnosisDescription, C.PresentOnAdmission, C.DiagnosisCodeType, C.DiagnosisType,
        C.ProcedureCode, C.ProcedureDescription, C.ProcedureDate, C.ProcedureCodeType, C.ProcedureType,
        C.ModifierCode1, C.ModifierCode2, C.ModifierCode3, C.ModifierCode4,
    ];

    public static IReadOnlyList<string> Headers { get; } = Columns.Select(CpcdsClaimColumns.DisplayName).ToList();

    /// <summary>Whether the claim version belongs in a CPCDS export: current, and adjudicated.</summary>
    public static bool Exports(Claim claim)
        => claim.SupersededAt is null
           && claim.VersionState is not (ClaimVersionState.Draft or ClaimVersionState.Adjusted)
           && PaymentStatus(claim.Status) is not null;

    /// <summary>The claim's rows, in <see cref="Columns"/> order. Empty when <see cref="Exports"/> is false.</summary>
    public static IEnumerable<IReadOnlyList<string>> Rows(Claim claim)
    {
        if (!Exports(claim)) yield break;

        var header = Header(claim);
        var lines = claim.ClaimLines.OrderBy(l => l.LineNumber).ToList();
        var diagnoses = claim.DiagnosisCodes.OrderBy(d => d.PointerNumber).ToList();

        // A diagnosis rides on a line row; one more than there are lines
        // repeats the first line's number, which a CPCDS reader takes as the
        // same line carrying another diagnosis.
        var rowCount = Math.Max(lines.Count, diagnoses.Count);
        for (var i = 0; i < rowCount; i++)
        {
            var row = new Dictionary<C, string>(header);
            var line = i < lines.Count ? lines[i] : null;
            if (line is not null)
                AddLine(row, claim, line);
            else if (lines.Count > 0)
                row[C.LineNumber] = Number(lines[0].LineNumber);
            if (i < diagnoses.Count)
                AddDiagnosis(row, diagnoses[i]);
            yield return Ordered(row);
        }

        // ICD-10-PCS procedures are the claim's, on rows without a line.
        if (claim.ClaimType == ClaimType.Institutional && claim.Institutional is { } inst)
        {
            var procedures = new List<(InstitutionalProcedureCode Code, bool Principal)>();
            if (inst.PrincipalProcedure is { } principal) procedures.Add((principal, true));
            procedures.AddRange(inst.OtherProcedures.Select(p => (p, false)));
            foreach (var (procedure, isPrincipal) in procedures)
            {
                var row = new Dictionary<C, string>(header)
                {
                    [C.ProcedureCode] = procedure.Code,
                    [C.ProcedureCodeType] = "icd10pcs",
                    [C.ProcedureType] = isPrincipal ? "principal" : "other",
                    [C.ProcedureDate] = Date(procedure.Date),
                };
                yield return Ordered(row);
            }
        }
    }

    // ── header ───────────────────────────────────────────────────────────────

    private static Dictionary<C, string> Header(Claim claim)
    {
        var a = claim.AdjudicationResult;
        var row = new Dictionary<C, string>
        {
            [C.ClaimUniqueIdentifier] = string.IsNullOrWhiteSpace(claim.ClaimNumber) ? claim.Id : claim.ClaimNumber,
            [C.MemberId] = claim.MemberId,
            [C.CoverageId] = claim.CoverageId ?? "",
            [C.ClaimType] = ClaimTypeCode(claim),
            [C.ClaimServiceStartDate] = Date(claim.ServiceDateFrom),
            [C.ClaimServiceEndDate] = Date(claim.ServiceDateTo),
            [C.ClaimReceivedDate] = Date(claim.ReceivedDate ?? claim.SubmittedDate),
            [C.ClaimPaidDate] = Date(claim.PaidDate ?? a?.PaymentDate),
            [C.ClaimFrequencyCode] = claim.ClaimFrequencyCode,
            [C.ClaimProcessingStatusCode] = claim.Status == ClaimStatus.Voided ? "cancelled" : "active",
            [C.ClaimPaymentStatusCode] = PaymentStatus(claim.Status) ?? "",
            [C.ClaimPaymentDenialCode] = a?.DenialReasonCode ?? "",
            [C.ClaimAdjustedFromIdentifier] = claim.OriginalClaimReference ?? "",
            [C.ClaimBillingProviderNpi] = claim.BillingProviderNPI,
            [C.ClaimBillingProviderNetworkStatus] = CarinEobBasis.NetworkStatus(claim) ?? "",
            [C.ClaimPerformingProviderNpi] = claim.RenderingProviderNPI ?? "",
            [C.ClaimSiteOfServiceNpi] = claim.FacilityNPI ?? "",
            [C.ClaimTotalSubmittedAmount] = Money(claim.TotalChargeAmount),
            [C.ClaimTotalAllowedAmount] = Money(a?.AllowedAmount),
            [C.ClaimPaymentAmount] = Money(a?.PayerPayment ?? 0m),
            [C.MemberPaidDeductible] = Money(a?.DeductibleAmount),
            [C.CoinsuranceLiabilityAmount] = Money(a?.CoinsuranceAmount),
            [C.CopayAmount] = Money(a?.CopayAmount),
            [C.MemberLiability] = Money(a?.PatientResponsibility),
        };

        if (claim.Institutional is { } inst)
        {
            var facility = inst.FacilityTypeCode ?? "";
            row[C.ClaimBillFacilityTypeCode] = facility.Length > 0 ? facility[..1] : "";
            row[C.ClaimServiceClassificationTypeCode] = facility.Length > 1 ? facility[1..2] : "";
            row[C.MemberAdmissionDate] = Date(inst.AdmissionDate);
            row[C.MemberDischargeDate] = Date(inst.DischargeDate);
            row[C.ClaimDiagnosisRelatedGroup] = inst.DrgCode ?? "";
            row[C.ClaimInpatientAdmissionTypeCode] = inst.AdmissionTypeCode ?? "";
            row[C.ClaimSourceInpatientAdmissionCode] = inst.AdmissionSourceCode ?? "";
            row[C.PatientDischargeStatusCode] = inst.PatientStatusCode ?? "";
        }

        if (claim.Pharmacy is { } rx)
        {
            row[C.ClaimPrescribingProviderNpi] = rx.PrescriberNpi ?? "";
            row[C.DaysSupply] = Money(rx.DaysSupply);
            row[C.RxServiceReferenceNumber] = rx.PrescriptionNumber ?? "";
            row[C.DawProductSelectionCode] = rx.DispenseAsWrittenCode;
            row[C.RefillNumber] = Number(rx.RefillNumber);
            row[C.RefillsAuthorized] = Number(rx.RefillsAuthorized);
            row[C.PrescriptionOriginCode] = rx.PrescriptionOriginCode ?? "";
            row[C.PlanReportedBrandGenericCode] = rx.BrandGenericIndicator ?? "";
            row[C.CompoundCode] = rx.CompoundCode ?? "";
            row[C.PharmacyServiceTypeCode] = rx.PharmacyServiceType ?? "";
            row[C.PatientResidenceCode] = rx.PatientResidenceCode ?? "";
        }

        return row;
    }

    /// <summary>The CPCDS claim type: CARIN BB's claim-type and institutional subtype in one code.</summary>
    internal static string ClaimTypeCode(Claim claim) => claim.ClaimType switch
    {
        ClaimType.Institutional => CarinEobBasis.IsInpatient(claim) ? "inpatient-facility" : "outpatient-facility",
        ClaimType.Dental => "oral",
        ClaimType.Pharmacy => "pharmacy",
        _ => "professional-nonclinician",
    };

    /// <summary>The CPCDS payment status, or null for a claim not yet adjudicated.</summary>
    internal static string? PaymentStatus(ClaimStatus status) => status switch
    {
        ClaimStatus.Paid or ClaimStatus.Approved => "paid",
        ClaimStatus.PartiallyPaid => "partiallypaid",
        ClaimStatus.Denied or ClaimStatus.Deny => "denied",
        // A void keeps the payment status it had; the processing status says cancelled.
        ClaimStatus.Voided => "paid",
        _ => null,
    };

    // ── line ─────────────────────────────────────────────────────────────────

    private static void AddLine(Dictionary<C, string> row, Claim claim, ClaimLine line)
    {
        row[C.LineNumber] = Number(line.LineNumber);
        row[C.ServiceFromDate] = Date(line.ServiceDateFrom);
        row[C.ServiceToDate] = Date(line.ServiceDateTo);
        row[C.PlaceOfServiceCode] = line.PlaceOfServiceCode
            ?? (claim.ClaimType is ClaimType.Institutional or ClaimType.Pharmacy ? "" : claim.PlaceOfServiceCode);
        row[C.RevenueCenterCode] = line.RevenueCode ?? "";
        row[C.NumberOfUnits] = Money(line.Units);
        row[C.NationalDrugCode] = line.NationalDrugCode ?? "";
        row[C.QuantityDispensed] = Money(line.DrugQuantity);
        row[C.LineSubmittedAmount] = Money(line.ChargeAmount);
        row[C.LineAllowedAmount] = Money(line.AdjudicationResult?.AllowedAmount);
        row[C.LinePaymentAmount] = Money(line.AdjudicationResult?.PaidAmount);
        row[C.LineMemberLiability] = Money(line.AdjudicationResult?.PatientResponsibility);
        row[C.LinePaymentDenialCode] = LineDenial(claim, line) ?? "";

        if (line.Teeth.FirstOrDefault() is { } tooth)
        {
            row[C.ToothNumber] = tooth.ToothCode;
            row[C.ToothSurface] = string.Concat(tooth.Surfaces);
        }

        if (!string.IsNullOrEmpty(line.ProcedureCode))
        {
            row[C.ProcedureCode] = line.ProcedureCode;
            row[C.ProcedureDescription] = line.ProcedureDescription ?? "";
            var modifiers = line.Modifiers.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
            C[] slots = [C.ModifierCode1, C.ModifierCode2, C.ModifierCode3, C.ModifierCode4];
            for (var i = 0; i < Math.Min(modifiers.Count, slots.Length); i++)
                row[slots[i]] = modifiers[i];
        }
    }

    /// <summary>
    /// A line's own denial code: an adjustment reason the claim carries for
    /// that line (as the importer records them), else the first CARC an
    /// engine put on a denied line.
    /// </summary>
    private static string? LineDenial(Claim claim, ClaimLine line)
        => claim.AdjudicationResult?.AdjustmentReasons
            .FirstOrDefault(r => r.Description == $"Line {line.LineNumber}" && !string.IsNullOrWhiteSpace(r.ReasonCode))
            ?.ReasonCode;

    // ── diagnosis ────────────────────────────────────────────────────────────

    private static void AddDiagnosis(Dictionary<C, string> row, DiagnosisCode diagnosis)
    {
        row[C.DiagnosisCode] = diagnosis.Code;
        row[C.DiagnosisDescription] = diagnosis.Description ?? "";
        row[C.PresentOnAdmission] = diagnosis.PresentOnAdmission ?? "";
        row[C.DiagnosisCodeType] = "icd10";
        row[C.DiagnosisType] = diagnosis.CodeQualifier?.ToUpperInvariant() switch
        {
            "ABK" => "principal",
            "ABJ" => "admitting",
            _ => "secondary",
        };
    }

    // ── values ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> Ordered(Dictionary<C, string> row)
        => Columns.Select(c => row.TryGetValue(c, out var v) ? v : "").ToList();

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Money(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Date(DateTime? value)
        => value is { } v && v != default ? v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
}
