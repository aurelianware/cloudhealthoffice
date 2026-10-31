using CloudHealthOffice.Infrastructure.Import.Cpcds;

namespace ClaimsService.Import.Cpcds;

/// <summary>
/// The CPCDS claim-file columns the importer reads, by their CPCDS data
/// element names.
///
/// <para>
/// CPCDS (the CARIN Common Payer Consumer Data Set) is the data dictionary the
/// CARIN Blue Button IG was built from, so its claim elements map one-to-one
/// onto the C4BB ExplanationOfBenefit. A CPCDS claim extract is flat: one row
/// per claim line, with the claim header repeated on each row, and diagnosis
/// and procedure codes carried on whichever rows the extract put them on. The
/// header names here are the CPCDS element names as the CARIN sample data and
/// Synthea's CPCDS exporter spell them. Headers are matched ignoring case,
/// spaces and punctuation, and a plan whose extract names a column differently
/// supplies a column map (<see cref="CpcdsImportOptions.ColumnMap"/>) rather
/// than renaming its file.
/// </para>
/// </summary>
public enum CpcdsClaimColumn
{
    // ── header: identity ─────────────────────────────────────────────────────
    ClaimUniqueIdentifier,
    MemberId,
    CoverageId,
    PatientAccountNumber,
    ClaimAdjustedFromIdentifier,
    ClaimAdjustedToIdentifier,
    ClaimType,

    // ── header: dates ────────────────────────────────────────────────────────
    ClaimServiceStartDate,
    ClaimServiceEndDate,
    ClaimPaidDate,
    ClaimReceivedDate,
    MemberAdmissionDate,
    MemberDischargeDate,

    // ── header: institutional ────────────────────────────────────────────────
    ClaimDiagnosisRelatedGroup,
    ClaimSourceInpatientAdmissionCode,
    ClaimInpatientAdmissionTypeCode,
    ClaimBillFacilityTypeCode,
    ClaimServiceClassificationTypeCode,
    ClaimFrequencyCode,
    PatientDischargeStatusCode,

    // ── header: status ───────────────────────────────────────────────────────
    ClaimProcessingStatusCode,
    ClaimPaymentStatusCode,
    ClaimPaymentDenialCode,

    // ── header: providers ────────────────────────────────────────────────────
    ClaimBillingProviderNpi,
    ClaimBillingProviderNetworkStatus,
    ClaimAttendingProviderNpi,
    ClaimSiteOfServiceNpi,
    ClaimReferringProviderNpi,
    ClaimPerformingProviderNpi,
    ClaimPerformingProviderNetworkStatus,

    // ── header: money ────────────────────────────────────────────────────────
    ClaimTotalSubmittedAmount,
    ClaimTotalAllowedAmount,
    ClaimPaymentAmount,
    ClaimAmountPaidToProvider,
    MemberPaidDeductible,
    CoinsuranceLiabilityAmount,
    CopayAmount,
    MemberLiability,
    ClaimDisallowedAmount,

    // ── line ─────────────────────────────────────────────────────────────────
    LineNumber,
    ServiceFromDate,
    ServiceToDate,
    PlaceOfServiceCode,
    RevenueCenterCode,
    NumberOfUnits,
    NationalDrugCode,
    QuantityDispensed,
    LinePaymentDenialCode,
    LineSubmittedAmount,
    LineAllowedAmount,
    LinePaymentAmount,
    LineMemberLiability,
    ToothNumber,
    ToothSurface,

    // ── diagnosis ────────────────────────────────────────────────────────────
    DiagnosisCode,
    DiagnosisDescription,
    PresentOnAdmission,
    DiagnosisCodeType,
    DiagnosisType,

    // ── procedure ────────────────────────────────────────────────────────────
    ProcedureCode,
    ProcedureDescription,
    ProcedureDate,
    ProcedureCodeType,
    ProcedureType,
    ModifierCode1,
    ModifierCode2,
    ModifierCode3,
    ModifierCode4,
}

/// <summary>Header names for <see cref="CpcdsClaimColumn"/>, and the matching rule.</summary>
public static class CpcdsClaimColumns
{
    /// <summary>
    /// The CPCDS name of each column, then any common alternative spellings.
    /// The first name is the one the importer's documentation and errors use.
    /// </summary>
    public static readonly IReadOnlyDictionary<CpcdsClaimColumn, string[]> Names =
        new Dictionary<CpcdsClaimColumn, string[]>
        {
            [CpcdsClaimColumn.ClaimUniqueIdentifier] = ["Claim unique identifier", "Claim id", "Claim number"],
            [CpcdsClaimColumn.MemberId] = ["Member id", "Member identifier", "Patient id"],
            [CpcdsClaimColumn.CoverageId] = ["Coverage id", "Coverage identifier"],
            [CpcdsClaimColumn.PatientAccountNumber] = ["Patient account number"],
            [CpcdsClaimColumn.ClaimAdjustedFromIdentifier] = ["Claim adjusted from identifier"],
            [CpcdsClaimColumn.ClaimAdjustedToIdentifier] = ["Claim adjusted to identifier"],
            [CpcdsClaimColumn.ClaimType] = ["Claim type"],

            [CpcdsClaimColumn.ClaimServiceStartDate] = ["Claim service start date"],
            [CpcdsClaimColumn.ClaimServiceEndDate] = ["Claim service end date"],
            [CpcdsClaimColumn.ClaimPaidDate] = ["Claim paid date"],
            [CpcdsClaimColumn.ClaimReceivedDate] = ["Claim received date"],
            [CpcdsClaimColumn.MemberAdmissionDate] = ["Member admission date"],
            [CpcdsClaimColumn.MemberDischargeDate] = ["Member discharge date"],

            [CpcdsClaimColumn.ClaimDiagnosisRelatedGroup] = ["Claim diagnosis related group", "DRG"],
            [CpcdsClaimColumn.ClaimSourceInpatientAdmissionCode] = ["Claim source inpatient admission code"],
            [CpcdsClaimColumn.ClaimInpatientAdmissionTypeCode] = ["Claim inpatient admission type code"],
            [CpcdsClaimColumn.ClaimBillFacilityTypeCode] = ["Claim bill facility type code"],
            [CpcdsClaimColumn.ClaimServiceClassificationTypeCode] = ["Claim service classification type code"],
            [CpcdsClaimColumn.ClaimFrequencyCode] = ["Claim frequency code"],
            [CpcdsClaimColumn.PatientDischargeStatusCode] = ["Patient discharge status code"],

            [CpcdsClaimColumn.ClaimProcessingStatusCode] = ["Claim processing status code"],
            [CpcdsClaimColumn.ClaimPaymentStatusCode] = ["Claim payment status code"],
            [CpcdsClaimColumn.ClaimPaymentDenialCode] = ["Claim payment denial code"],

            [CpcdsClaimColumn.ClaimBillingProviderNpi] = ["Claim billing provider NPI"],
            [CpcdsClaimColumn.ClaimBillingProviderNetworkStatus] = ["Claim billing provider network status"],
            [CpcdsClaimColumn.ClaimAttendingProviderNpi] = ["Claim attending provider NPI"],
            [CpcdsClaimColumn.ClaimSiteOfServiceNpi] = ["Claim site of service NPI"],
            [CpcdsClaimColumn.ClaimReferringProviderNpi] = ["Claim referring provider NPI"],
            [CpcdsClaimColumn.ClaimPerformingProviderNpi] = ["Claim performing provider NPI", "Claim rendering provider NPI"],
            [CpcdsClaimColumn.ClaimPerformingProviderNetworkStatus] = ["Claim performing provider network status"],

            [CpcdsClaimColumn.ClaimTotalSubmittedAmount] = ["Claim total submitted amount"],
            [CpcdsClaimColumn.ClaimTotalAllowedAmount] = ["Claim total allowed amount"],
            [CpcdsClaimColumn.ClaimPaymentAmount] = ["Claim payment amount"],
            [CpcdsClaimColumn.ClaimAmountPaidToProvider] = ["Claim amount paid to provider"],
            [CpcdsClaimColumn.MemberPaidDeductible] = ["Member paid deductible"],
            [CpcdsClaimColumn.CoinsuranceLiabilityAmount] = ["Co-insurance liability amount", "Coinsurance liability amount"],
            [CpcdsClaimColumn.CopayAmount] = ["Copay amount"],
            [CpcdsClaimColumn.MemberLiability] = ["Member liability"],
            [CpcdsClaimColumn.ClaimDisallowedAmount] = ["Claim disallowed amount"],

            [CpcdsClaimColumn.LineNumber] = ["Line number"],
            [CpcdsClaimColumn.ServiceFromDate] = ["Service (from) date", "Service from date"],
            [CpcdsClaimColumn.ServiceToDate] = ["Service to date"],
            [CpcdsClaimColumn.PlaceOfServiceCode] = ["Place of service code"],
            [CpcdsClaimColumn.RevenueCenterCode] = ["Revenue center code"],
            [CpcdsClaimColumn.NumberOfUnits] = ["Number of units"],
            [CpcdsClaimColumn.NationalDrugCode] = ["National drug code"],
            [CpcdsClaimColumn.QuantityDispensed] = ["Quantity dispensed"],
            [CpcdsClaimColumn.LinePaymentDenialCode] = ["Line payment denial code"],
            [CpcdsClaimColumn.LineSubmittedAmount] = ["Line submitted amount"],
            [CpcdsClaimColumn.LineAllowedAmount] = ["Line allowed amount"],
            [CpcdsClaimColumn.LinePaymentAmount] = ["Line payment amount"],
            [CpcdsClaimColumn.LineMemberLiability] = ["Line member liability"],
            [CpcdsClaimColumn.ToothNumber] = ["Tooth number"],
            [CpcdsClaimColumn.ToothSurface] = ["Tooth surface"],

            [CpcdsClaimColumn.DiagnosisCode] = ["Diagnosis code"],
            [CpcdsClaimColumn.DiagnosisDescription] = ["Diagnosis description"],
            [CpcdsClaimColumn.PresentOnAdmission] = ["Present on admission"],
            [CpcdsClaimColumn.DiagnosisCodeType] = ["Diagnosis code type"],
            [CpcdsClaimColumn.DiagnosisType] = ["Diagnosis type"],

            [CpcdsClaimColumn.ProcedureCode] = ["Procedure code"],
            [CpcdsClaimColumn.ProcedureDescription] = ["Procedure description"],
            [CpcdsClaimColumn.ProcedureDate] = ["Procedure date"],
            [CpcdsClaimColumn.ProcedureCodeType] = ["Procedure code type"],
            [CpcdsClaimColumn.ProcedureType] = ["Procedure type"],
            [CpcdsClaimColumn.ModifierCode1] = ["Modifier Code-1", "Modifier code 1", "Modifier 1"],
            [CpcdsClaimColumn.ModifierCode2] = ["Modifier Code-2", "Modifier code 2", "Modifier 2"],
            [CpcdsClaimColumn.ModifierCode3] = ["Modifier Code-3", "Modifier code 3", "Modifier 3"],
            [CpcdsClaimColumn.ModifierCode4] = ["Modifier Code-4", "Modifier code 4", "Modifier 4"],
        };

    /// <summary>Columns a claim file must have; without them no claim can be built.</summary>
    public static readonly IReadOnlyList<CpcdsClaimColumn> Required =
    [
        CpcdsClaimColumn.ClaimUniqueIdentifier,
        CpcdsClaimColumn.MemberId,
        CpcdsClaimColumn.ClaimType,
        CpcdsClaimColumn.ClaimServiceStartDate,
        CpcdsClaimColumn.ClaimBillingProviderNpi,
    ];

    /// <summary>The CPCDS name of a column.</summary>
    public static string DisplayName(CpcdsClaimColumn column) => Names[column][0];

    /// <summary>
    /// The match key for a header: lower case, letters and digits only, so
    /// "Service (from) date", "SERVICE_FROM_DATE" and "service from date" all
    /// match.
    /// </summary>
    public static string Normalize(string header) => CpcdsHeaders.Normalize(header);

    /// <summary>
    /// Resolves the file's header row to column positions. A column map entry
    /// (CPCDS name or enum name → the file's header) wins over the built-in
    /// names. Unrecognized headers are ignored, and returned so the import
    /// report can list them.
    /// </summary>
    public static (Dictionary<CpcdsClaimColumn, int> Positions, List<string> Unrecognized) Resolve(
        IReadOnlyList<string> headers,
        IReadOnlyDictionary<string, string>? columnMap)
    {
        var lookup = new Dictionary<string, CpcdsClaimColumn>(StringComparer.Ordinal);
        foreach (var (column, names) in Names)
        {
            foreach (var name in names)
                lookup.TryAdd(Normalize(name), column);
            lookup.TryAdd(Normalize(column.ToString()), column);
        }

        if (columnMap is not null)
        {
            foreach (var (target, fileHeader) in columnMap)
            {
                if (string.IsNullOrWhiteSpace(fileHeader)) continue;
                if (lookup.TryGetValue(Normalize(target), out var column))
                    lookup[Normalize(fileHeader)] = column;
            }
        }

        var positions = new Dictionary<CpcdsClaimColumn, int>();
        var unrecognized = new List<string>();
        for (var i = 0; i < headers.Count; i++)
        {
            if (lookup.TryGetValue(Normalize(headers[i]), out var column))
                positions.TryAdd(column, i);
            else if (!string.IsNullOrWhiteSpace(headers[i]))
                unrecognized.Add(headers[i]);
        }

        return (positions, unrecognized);
    }
}
