using System.Globalization;
using ClaimsService.Models;
using ClaimsService.Repositories;
using C = ClaimsService.Import.Cpcds.CpcdsClaimColumn;

namespace ClaimsService.Import.Cpcds;

/// <summary>
/// Maps one CPCDS claim onto CHO's claim record, already adjudicated. Once
/// stored, the claim is served by the same projections as a claim CHO
/// adjudicated itself: <c>ExplanationOfBenefitProjector</c> (CARIN BB
/// financial, Patient Access) and <c>CarinNonFinancialEobProjector</c>
/// (Provider Access). That is the point of mapping to the claim record rather
/// than straight to FHIR: one shaping, one set of conformance tests, one
/// Inferno run, whoever adjudicated the claim.
///
/// <para>
/// The mapper refuses a claim rather than load part of it. A claim missing a
/// line, or with an amount that does not parse, would show a member money that
/// is wrong, and an EOB that is wrong is worse than one that is late. Gaps that
/// do not change what the member sees (an unrecognized diagnosis code system,
/// say) are warnings.
/// </para>
/// </summary>
internal static class CpcdsClaimMapper
{
    internal sealed class Mapped
    {
        public Claim? Claim { get; set; }
        public List<string> Errors { get; } = [];
        public List<string> Warnings { get; } = [];

        /// <summary>"Claim payment amount" as the file states it, for the report's totals, even when the claim is rejected.</summary>
        public decimal? PaidAmount { get; set; }

        /// <summary>"Claim total submitted amount" as the file states it.</summary>
        public decimal? SubmittedAmount { get; set; }
    }

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyyMMdd", "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-dd HH:mm:ss",
    ];

    public static Mapped Map(CpcdsClaimRecord record, string choMemberId, CpcdsImportOptions options, string? actorId)
    {
        var result = new Mapped();
        var errors = result.Errors;
        var warnings = result.Warnings;
        var rows = record.Rows;

        // ── header ───────────────────────────────────────────────────────────
        // The header repeats on every row. The first value given wins; rows
        // that disagree about who the claim is for, or what kind of claim it
        // is, make the claim unloadable.
        string? Header(C column) => rows.Select(r => r.Get(column)).FirstOrDefault(v => v is not null);

        foreach (var identity in new[] { C.MemberId, C.ClaimType })
        {
            var distinct = rows.Select(r => r.Get(identity)).Where(v => v is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count > 1)
            {
                errors.Add($"Rows disagree on \"{CpcdsClaimColumns.DisplayName(identity)}\": {string.Join(", ", distinct)}.");
            }
        }

        result.SubmittedAmount = Amount(Header(C.ClaimTotalSubmittedAmount), C.ClaimTotalSubmittedAmount, null, errors);
        result.PaidAmount = Amount(Header(C.ClaimPaymentAmount), C.ClaimPaymentAmount, null, errors);

        if (Header(C.MemberId) is null)
            errors.Add("\"Member id\" is empty.");

        var claimTypeCode = Header(C.ClaimType);
        var claimType = MapClaimType(claimTypeCode, errors);

        var serviceStart = Date(Header(C.ClaimServiceStartDate), C.ClaimServiceStartDate, null, errors);
        if (serviceStart is null && Header(C.ClaimServiceStartDate) is null)
            errors.Add("\"Claim service start date\" is empty.");
        var serviceEnd = Date(Header(C.ClaimServiceEndDate), C.ClaimServiceEndDate, null, errors) ?? serviceStart;

        var billingNpi = Header(C.ClaimBillingProviderNpi);
        if (billingNpi is null)
            errors.Add("\"Claim billing provider NPI\" is empty.");
        else if (!IsNpiShape(billingNpi))
            errors.Add($"\"Claim billing provider NPI\" '{billingNpi}' is not a 10-digit NPI.");

        var receivedDate = Date(Header(C.ClaimReceivedDate), C.ClaimReceivedDate, null, errors);
        var paidDate = Date(Header(C.ClaimPaidDate), C.ClaimPaidDate, null, errors);

        // ── diagnoses ────────────────────────────────────────────────────────
        var diagnoses = Diagnoses(rows, warnings);

        // ── lines ────────────────────────────────────────────────────────────
        var institutionalProcedures = new List<(InstitutionalProcedureCode Code, bool Principal)>();
        var lines = Lines(rows, claimType, serviceStart, serviceEnd, institutionalProcedures, errors, warnings);
        if (lines.Count == 0 && errors.Count == 0)
            errors.Add("The claim has no lines: no row carries a \"Line number\".");

        // ── money ────────────────────────────────────────────────────────────
        var lineCharges = lines.Sum(l => l.ChargeAmount);
        var totalCharge = result.SubmittedAmount ?? lineCharges;
        if (result.SubmittedAmount is { } stated && lines.Count > 0 && Math.Abs(stated - lineCharges) > 0.01m)
        {
            warnings.Add(
                $"\"Claim total submitted amount\" {stated:0.00} differs from the sum of the lines' submitted amounts {lineCharges:0.00}; the claim total is used.");
        }

        var allowed = Amount(Header(C.ClaimTotalAllowedAmount), C.ClaimTotalAllowedAmount, null, errors)
            ?? SumLines(lines, l => l.AdjudicationResult?.AllowedAmount);
        var paid = result.PaidAmount
            ?? Amount(Header(C.ClaimAmountPaidToProvider), C.ClaimAmountPaidToProvider, null, errors)
            ?? SumLines(lines, l => l.AdjudicationResult?.PaidAmount);
        var deductible = Amount(Header(C.MemberPaidDeductible), C.MemberPaidDeductible, null, errors) ?? 0m;
        var coinsurance = Amount(Header(C.CoinsuranceLiabilityAmount), C.CoinsuranceLiabilityAmount, null, errors) ?? 0m;
        var copay = Amount(Header(C.CopayAmount), C.CopayAmount, null, errors) ?? 0m;
        var memberLiability = Amount(Header(C.MemberLiability), C.MemberLiability, null, errors)
            ?? deductible + coinsurance + copay;

        var denialCode = Header(C.ClaimPaymentDenialCode);
        var pendCode = Header(C.PendReasonCode);
        var status = MapStatus(
            Header(C.ClaimProcessingStatusCode), Header(C.ClaimPaymentStatusCode), denialCode, paid ?? 0m, warnings, pendCode);

        // A claim the core has pended has paid nothing yet, and may not say so.
        var corePended = status == ClaimStatus.Pended;
        if (paid is null && Header(C.ClaimPaymentAmount) is null && !corePended)
            errors.Add("No payment amount: \"Claim payment amount\", \"Claim amount paid to provider\" and \"Line payment amount\" are all empty.");
        var pendedDate = corePended ? Date(Header(C.PendedDate), C.PendedDate, null, errors) : null;

        // The core's pend is kept on the claim's source, not as CHO's Pended
        // status: a pended core claim is not CHO's work, and must not reach
        // CHO's pend work queues or examiners. It is an open claim.
        if (corePended)
            status = ClaimStatus.Received;

        if (errors.Count > 0)
            return result;

        var adjudication = new AdjudicationResult
        {
            NetworkTier = NetworkTier(Header(C.ClaimBillingProviderNetworkStatus) ?? Header(C.ClaimPerformingProviderNetworkStatus)),
            AllowedAmount = allowed ?? 0m,
            PayerPayment = paid ?? 0m,
            DeductibleAmount = deductible,
            CoinsuranceAmount = coinsurance,
            CopayAmount = copay,
            PatientResponsibility = memberLiability,
            DenialReasonCode = denialCode,
            PaymentDate = status is ClaimStatus.Paid or ClaimStatus.PartiallyPaid ? paidDate : null,
        };

        // A denied line is part of the member's explanation. CPCDS gives the
        // CARC but not its group code, so the group is left out rather than
        // guessed.
        foreach (var row in rows)
        {
            if (row.Get(C.LinePaymentDenialCode) is not { } lineDenial) continue;
            var lineNumber = row.Get(C.LineNumber);
            if (adjudication.AdjustmentReasons.Any(a => a.ReasonCode == lineDenial && a.Description == $"Line {lineNumber}"))
                continue;
            adjudication.AdjustmentReasons.Add(new ClaimAdjustmentReason
            {
                GroupCode = string.Empty,
                ReasonCode = lineDenial,
                Description = $"Line {lineNumber}",
            });
        }

        var claim = new Claim
        {
            ClaimNumber = record.ClaimId,
            MemberId = choMemberId,
            CoverageId = Header(C.CoverageId),
            LineOfBusiness = options.LineOfBusiness,
            ClaimType = claimType!.Value,
            BillingProviderNPI = billingNpi!,
            // An NCPDP claim's service provider is the dispensing pharmacy,
            // which bills under its organization (type 2) NPI.
            BillingProviderEntityType = claimType == ClaimType.Pharmacy ? "2" : null,
            RenderingProviderNPI = Header(C.ClaimPerformingProviderNpi) ?? Header(C.ClaimAttendingProviderNpi),
            FacilityNPI = Header(C.ClaimSiteOfServiceNpi),
            // CMS place of service 01 is "Pharmacy".
            PlaceOfServiceCode = lines.Select(l => l.PlaceOfServiceCode).FirstOrDefault(p => p is not null)
                                 ?? (claimType == ClaimType.Pharmacy ? "01" : "11"),
            ClaimFrequencyCode = Header(C.ClaimFrequencyCode) ?? "1",
            OriginalClaimReference = Header(C.ClaimAdjustedFromIdentifier),
            PriorAuthorizationNumber = Header(C.PriorAuthorizationNumber),
            TotalChargeAmount = totalCharge,
            ServiceDateFrom = serviceStart!.Value,
            ServiceDateTo = serviceEnd ?? serviceStart.Value,
            DiagnosisCodes = diagnoses,
            ClaimLines = lines,
            Status = status,
            VersionState = ClaimRepository.MapStatusToVersionState(status),
            SubmittedDate = receivedDate ?? serviceEnd ?? serviceStart.Value,
            ReceivedDate = receivedDate,
            AdjudicatedDate = corePended ? null : paidDate ?? receivedDate,
            PaidDate = status is ClaimStatus.Paid or ClaimStatus.PartiallyPaid ? paidDate : null,
            AdjudicationResult = adjudication,
            CreatedBy = actorId,
            LastUpdatedBy = actorId,
            PublishedBy = actorId,
            ExternalSource = corePended
                ? new ClaimExternalSource
                {
                    CorePend = new ClaimCorePend
                    {
                        Code = pendCode,
                        Reason = Header(C.PendReason),
                        PendedDate = pendedDate,
                    },
                }
                : null,
        };

        if (claimType == ClaimType.Institutional)
            claim.Institutional = Institutional(Header, claimTypeCode, institutionalProcedures, errors, warnings);
        if (claimType == ClaimType.Pharmacy)
            claim.Pharmacy = Pharmacy(Header, errors, warnings);

        if (errors.Count > 0)
            return result;

        result.Claim = claim;
        return result;
    }

    // ── claim type and status ────────────────────────────────────────────────

    private static ClaimType? MapClaimType(string? code, List<string> errors)
    {
        switch (code?.Trim().ToLowerInvariant())
        {
            case "inpatient-facility":
            case "outpatient-facility":
            case "institutional":
                return ClaimType.Institutional;
            case "professional-nonclinician":
            case "professional":
            case "vision":
                return ClaimType.Professional;
            case "oral":
            case "dental":
                return ClaimType.Dental;
            case "pharmacy":
                return ClaimType.Pharmacy;
            case null:
                errors.Add("\"Claim type\" is empty.");
                return null;
            default:
                errors.Add(
                    $"\"Claim type\" '{code}' is not a CPCDS claim type (inpatient-facility, outpatient-facility, " +
                    "professional-nonclinician, oral, pharmacy).");
                return null;
        }
    }

    /// <summary>
    /// The claim's CHO status. A cancelled claim is voided; otherwise the
    /// payment status says paid, partially paid or denied. When the file gives
    /// no payment status, a denial code on a claim that paid nothing means it
    /// was denied: a claim that paid nothing because the money went to the
    /// deductible carries no denial code, and is paid.
    /// <see cref="ClaimStatus.Pended"/> means the core has the claim pended
    /// (payment status "pended", or no payment status, a pend reason code and
    /// nothing paid); the mapper stores such a claim as received and keeps
    /// the pend on <see cref="ClaimExternalSource.CorePend"/>.
    /// </summary>
    internal static ClaimStatus MapStatus(
        string? processingStatus, string? paymentStatus, string? denialCode, decimal paid, List<string> warnings,
        string? pendCode = null)
    {
        switch (processingStatus?.Trim().ToLowerInvariant())
        {
            case "cancelled":
            case "canceled":
            case "entered-in-error":
            case "void":
            case "voided":
                return ClaimStatus.Voided;
        }

        switch (paymentStatus?.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("-", string.Empty))
        {
            case "paid":
            case "complete":
                return ClaimStatus.Paid;
            case "partiallypaid":
            case "partial":
                return ClaimStatus.PartiallyPaid;
            case "denied":
            case "deny":
                return ClaimStatus.Denied;
            case "pended":
            case "pend":
            case "pending":
            case "suspended":
            case "suspend":
            case "held":
            case "hold":
                return ClaimStatus.Pended;
            case null:
            case "":
                break;
            default:
                warnings.Add($"\"Claim payment status code\" '{paymentStatus}' is not recognized; status taken from the amounts.");
                break;
        }

        if (!string.IsNullOrWhiteSpace(pendCode) && string.IsNullOrWhiteSpace(denialCode) && paid == 0m)
            return ClaimStatus.Pended;
        return !string.IsNullOrWhiteSpace(denialCode) && paid == 0m ? ClaimStatus.Denied : ClaimStatus.Paid;
    }

    /// <summary>CHO's network tier for a CPCDS network status ("innetwork" / "outofnetwork", and the usual spellings).</summary>
    internal static string? NetworkTier(string? status)
        => status?.Trim().ToLowerInvariant().Replace("-", string.Empty).Replace(" ", string.Empty) switch
        {
            "innetwork" or "in" or "par" or "participating" or "contracted" => "InNetwork",
            "outofnetwork" or "out" or "nonpar" or "nonparticipating" or "noncontracted" => "OutOfNetwork",
            _ => null,
        };

    // ── institutional ────────────────────────────────────────────────────────

    private static InstitutionalClaimDetails Institutional(
        Func<C, string?> header,
        string? claimTypeCode,
        List<(InstitutionalProcedureCode Code, bool Principal)> procedures,
        List<string> errors,
        List<string> warnings)
    {
        var facility = header(C.ClaimBillFacilityTypeCode);
        var classification = header(C.ClaimServiceClassificationTypeCode);
        string facilityType;
        if (facility is { Length: 1 } && classification is { Length: 1 })
        {
            facilityType = facility + classification;
        }
        else if (facility is { Length: 2 } && classification is null)
        {
            facilityType = facility;
        }
        else
        {
            // Without the bill type the EOB cannot tell inpatient from
            // outpatient except by the CPCDS claim type, so that decides.
            var inpatient = string.Equals(claimTypeCode, "inpatient-facility", StringComparison.OrdinalIgnoreCase);
            facilityType = inpatient ? "11" : "13";
            warnings.Add(
                $"Bill type not given (\"Claim bill facility type code\" + \"Claim service classification type code\"); " +
                $"taken as {facilityType} from the claim type.");
        }

        var principal = procedures.FirstOrDefault(p => p.Principal).Code ?? procedures.FirstOrDefault().Code;
        var details = new InstitutionalClaimDetails
        {
            FacilityTypeCode = facilityType,
            AdmissionDate = Date(header(C.MemberAdmissionDate), C.MemberAdmissionDate, null, errors),
            DischargeDate = Date(header(C.MemberDischargeDate), C.MemberDischargeDate, null, errors),
            AdmissionTypeCode = header(C.ClaimInpatientAdmissionTypeCode),
            AdmissionSourceCode = header(C.ClaimSourceInpatientAdmissionCode),
            PatientStatusCode = header(C.PatientDischargeStatusCode),
            DrgCode = header(C.ClaimDiagnosisRelatedGroup),
            PrincipalProcedure = principal,
            OtherProcedures = procedures.Select(p => p.Code).Where(p => !ReferenceEquals(p, principal)).ToList(),
        };
        return details;
    }

    // ── pharmacy ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The NCPDP header of a pharmacy claim. CARIN BB's Pharmacy EOB requires
    /// days supply, the dispense-as-written code, the fill number and the
    /// refills authorized. Days supply has no safe default, so a claim without
    /// it is refused. The other three have an NCPDP default (0: no product
    /// selection indicated, original fill, no refills), which is used with a
    /// warning when the file leaves them empty, as a PBM claim would have sent
    /// them.
    /// </summary>
    private static PharmacyClaimDetails Pharmacy(Func<C, string?> header, List<string> errors, List<string> warnings)
    {
        var details = new PharmacyClaimDetails();

        var daysSupply = Amount(header(C.DaysSupply), C.DaysSupply, null, errors);
        if (daysSupply is null && header(C.DaysSupply) is null)
            errors.Add("\"Days supply\" is empty; a pharmacy claim needs it.");
        else if (daysSupply is <= 0)
            errors.Add($"\"Days supply\" '{header(C.DaysSupply)}' is not a positive number.");
        details.DaysSupply = daysSupply ?? 0m;

        details.DispenseAsWrittenCode = NcpdpCode(header(C.DawProductSelectionCode), C.DawProductSelectionCode, 9, "0", errors, warnings) ?? "0";
        details.RefillNumber = Count(header(C.RefillNumber), C.RefillNumber, errors, warnings);
        details.RefillsAuthorized = Count(header(C.RefillsAuthorized), C.RefillsAuthorized, errors, warnings);
        if (details.RefillNumber > details.RefillsAuthorized && header(C.RefillsAuthorized) is not null)
        {
            warnings.Add(
                $"\"Refill number\" {details.RefillNumber} is more than \"Refills authorized\" {details.RefillsAuthorized}.");
        }

        details.PrescriptionOriginCode = NcpdpCode(header(C.PrescriptionOriginCode), C.PrescriptionOriginCode, 5, null, errors, warnings);
        details.CompoundCode = NcpdpCode(header(C.CompoundCode), C.CompoundCode, 2, null, errors, warnings);
        details.BrandGenericIndicator = header(C.PlanReportedBrandGenericCode);
        details.PrescriptionNumber = header(C.RxServiceReferenceNumber);
        details.PharmacyServiceType = header(C.PharmacyServiceTypeCode);
        details.PatientResidenceCode = header(C.PatientResidenceCode);

        var prescriber = header(C.ClaimPrescribingProviderNpi);
        if (prescriber is not null && !IsNpiShape(prescriber))
            warnings.Add($"\"Claim prescribing provider NPI\" '{prescriber}' is not a 10-digit NPI and was left off the EOB.");
        else
            details.PrescriberNpi = prescriber;

        return details;
    }

    /// <summary>
    /// A single-digit NCPDP code between 0 and <paramref name="max"/>. Empty
    /// gives <paramref name="fallback"/>, with a warning when there is one;
    /// anything else is an error, since the EOB binds the element to the NCPDP
    /// code list.
    /// </summary>
    private static string? NcpdpCode(
        string? value, C column, int max, string? fallback, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (fallback is not null)
                warnings.Add($"\"{CpcdsClaimColumns.DisplayName(column)}\" is empty; taken as {fallback}.");
            return fallback;
        }

        var code = value.Trim();
        if (code.Length == 1 && char.IsAsciiDigit(code[0]) && code[0] - '0' <= max)
            return code;

        errors.Add($"\"{CpcdsClaimColumns.DisplayName(column)}\" '{value}' is not an NCPDP code (0-{max}).");
        return fallback;
    }

    /// <summary>A whole number of fills; empty is 0, with a warning.</summary>
    private static int Count(string? value, C column, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            warnings.Add($"\"{CpcdsClaimColumns.DisplayName(column)}\" is empty; taken as 0.");
            return 0;
        }
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0)
            return count;

        errors.Add($"\"{CpcdsClaimColumns.DisplayName(column)}\" '{value}' is not a whole number.");
        return 0;
    }

    // ── diagnoses ────────────────────────────────────────────────────────────

    private static List<DiagnosisCode> Diagnoses(List<CpcdsRow> rows, List<string> warnings)
    {
        var diagnoses = new List<DiagnosisCode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (row.Get(C.DiagnosisCode) is not { } raw) continue;
            var code = raw.Replace(".", string.Empty).Trim().ToUpperInvariant();

            if (!IsIcd10Cm(row.Get(C.DiagnosisCodeType), code))
            {
                if (skipped.Add(code))
                {
                    warnings.Add(
                        $"Diagnosis '{raw}' (type '{row.Get(C.DiagnosisCodeType) ?? "none"}') is not ICD-10-CM and was left off the EOB.");
                }
                continue;
            }
            if (!seen.Add(code)) continue;

            var type = row.Get(C.DiagnosisType)?.ToLowerInvariant();
            diagnoses.Add(new DiagnosisCode
            {
                Code = code,
                Description = row.Get(C.DiagnosisDescription),
                PresentOnAdmission = row.Get(C.PresentOnAdmission),
                CodeQualifier = type switch
                {
                    "principal" => "ABK",
                    "admitting" => "ABJ",
                    _ => "ABF",
                },
            });
        }

        // One principal: the first the file named, or the first diagnosis, as
        // an 837 would have it.
        var principal = diagnoses.FirstOrDefault(d => d.CodeQualifier == "ABK") ?? diagnoses.FirstOrDefault(d => d.CodeQualifier != "ABJ");
        foreach (var diagnosis in diagnoses)
        {
            if (diagnosis.CodeQualifier == "ABK" && !ReferenceEquals(diagnosis, principal))
                diagnosis.CodeQualifier = "ABF";
        }
        if (principal is not null)
            principal.CodeQualifier = "ABK";

        // Principal first, then the order the file gave.
        diagnoses = diagnoses.OrderBy(d => ReferenceEquals(d, principal) ? 0 : 1).ToList();
        for (var i = 0; i < diagnoses.Count; i++)
            diagnoses[i].PointerNumber = i + 1;

        return diagnoses;
    }

    private static bool IsIcd10Cm(string? codeType, string code)
    {
        var type = codeType?.Trim().ToLowerInvariant().Replace("-", string.Empty).Replace(" ", string.Empty);
        if (type is "icd10" or "icd10cm" or "abk" or "abf" or "http://hl7.org/fhir/sid/icd10cm")
            return true;
        if (!string.IsNullOrEmpty(type))
            return false;

        // No type given: accept what has the ICD-10-CM shape (a letter, a
        // digit, then 1-5 letters or digits).
        return code.Length is >= 3 and <= 7
               && char.IsAsciiLetterUpper(code[0])
               && char.IsAsciiDigit(code[1])
               && code.All(char.IsAsciiLetterOrDigit);
    }

    // ── lines ────────────────────────────────────────────────────────────────

    private static List<ClaimLine> Lines(
        List<CpcdsRow> rows,
        ClaimType? claimType,
        DateTime? serviceStart,
        DateTime? serviceEnd,
        List<(InstitutionalProcedureCode Code, bool Principal)> institutionalProcedures,
        List<string> errors,
        List<string> warnings)
    {
        var lines = new SortedDictionary<int, ClaimLine>();

        foreach (var row in rows)
        {
            // Institutional ICD-10-PCS procedures are the claim's, not a line's.
            if (row.Get(C.ProcedureCode) is { } procedureCode && IsIcd10Pcs(row.Get(C.ProcedureCodeType)))
            {
                var type = row.Get(C.ProcedureType)?.ToLowerInvariant();
                if (claimType == ClaimType.Institutional)
                {
                    var code = procedureCode.Replace(".", string.Empty).ToUpperInvariant();
                    if (!institutionalProcedures.Any(p => p.Code.Code == code))
                    {
                        institutionalProcedures.Add((new InstitutionalProcedureCode
                        {
                            Code = code,
                            CodeQualifier = type is "principal" or "primary" ? "BBR" : "BBQ",
                            Date = Date(row.Get(C.ProcedureDate), C.ProcedureDate, row.LineNumber, errors),
                        }, type is "principal" or "primary"));
                    }
                }
                else
                {
                    warnings.Add($"ICD-10-PCS procedure '{procedureCode}' on a non-institutional claim was left off the EOB.");
                }
                if (row.Get(C.LineNumber) is null) continue;
            }

            if (row.Get(C.LineNumber) is not { } lineText) continue;
            if (!int.TryParse(lineText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber) || lineNumber <= 0)
            {
                errors.Add($"File line {row.LineNumber}: \"Line number\" '{lineText}' is not a positive whole number.");
                continue;
            }

            // A line may span rows (one per diagnosis, say): the first row
            // that names the line defines it.
            if (lines.ContainsKey(lineNumber)) continue;

            var line = new ClaimLine
            {
                LineNumber = lineNumber,
                ProcedureCode = IsIcd10Pcs(row.Get(C.ProcedureCodeType)) ? string.Empty : row.Get(C.ProcedureCode)?.ToUpperInvariant() ?? string.Empty,
                ProcedureDescription = IsIcd10Pcs(row.Get(C.ProcedureCodeType)) ? null : row.Get(C.ProcedureDescription),
                Modifiers = new[] { C.ModifierCode1, C.ModifierCode2, C.ModifierCode3, C.ModifierCode4 }
                    .Select(row.Get).Where(m => m is not null).Select(m => m!.ToUpperInvariant()).ToList(),
                Units = Amount(row.Get(C.NumberOfUnits), C.NumberOfUnits, row.LineNumber, errors) ?? 1m,
                ChargeAmount = Amount(row.Get(C.LineSubmittedAmount), C.LineSubmittedAmount, row.LineNumber, errors) ?? 0m,
                ServiceDateFrom = Date(row.Get(C.ServiceFromDate), C.ServiceFromDate, row.LineNumber, errors)
                                  ?? serviceStart ?? default,
                PlaceOfServiceCode = row.Get(C.PlaceOfServiceCode),
                RevenueCode = row.Get(C.RevenueCenterCode),
                PriorAuthorizationNumber = row.Get(C.LinePriorAuthorizationNumber),
                NationalDrugCode = row.Get(C.NationalDrugCode),
                DrugQuantity = Amount(row.Get(C.QuantityDispensed), C.QuantityDispensed, row.LineNumber, errors),
            };
            line.ServiceDateTo = Date(row.Get(C.ServiceToDate), C.ServiceToDate, row.LineNumber, errors)
                                 ?? (row.Get(C.ServiceFromDate) is null ? serviceEnd : null)
                                 ?? line.ServiceDateFrom;

            if (row.Get(C.LineSubmittedAmount) is null)
                warnings.Add($"Line {lineNumber}: \"Line submitted amount\" is empty; taken as 0.00.");

            var lineAllowed = Amount(row.Get(C.LineAllowedAmount), C.LineAllowedAmount, row.LineNumber, errors);
            var linePaid = Amount(row.Get(C.LinePaymentAmount), C.LinePaymentAmount, row.LineNumber, errors);
            var lineLiability = Amount(row.Get(C.LineMemberLiability), C.LineMemberLiability, row.LineNumber, errors);
            if (lineAllowed is not null || linePaid is not null || lineLiability is not null)
            {
                line.AdjudicationResult = new LineAdjudicationResult
                {
                    AllowedAmount = lineAllowed ?? 0m,
                    PaidAmount = linePaid ?? 0m,
                    PatientResponsibility = lineLiability ?? 0m,
                };
            }

            if (row.Get(C.ToothNumber) is { } tooth)
            {
                line.Teeth.Add(new ClaimLineTooth
                {
                    ToothCode = tooth,
                    Surfaces = (row.Get(C.ToothSurface) ?? string.Empty)
                        .Where(char.IsAsciiLetter).Select(ch => char.ToUpperInvariant(ch).ToString()).Distinct().ToList(),
                });
            }

            // Every C4BB profile requires item.productOrService. A pharmacy
            // line is the drug: its NDC, or the "compound" literal for a
            // compound (compound code 2). An institutional line can be
            // identified by its revenue code alone; any other line needs its
            // procedure code.
            if (claimType == ClaimType.Pharmacy)
            {
                line.DrugQuantity ??= Amount(row.Get(C.NumberOfUnits), C.NumberOfUnits, row.LineNumber, errors);
                if (row.Get(C.NumberOfUnits) is null && line.DrugQuantity is { } dispensed)
                    line.Units = dispensed;
                if (line.NationalDrugCode is { } ndc)
                    line.NationalDrugCode = NormalizeNdc(ndc);
                var compound = row.Get(C.CompoundCode)?.Trim() == "2";
                if (line.NationalDrugCode is null && !compound)
                {
                    errors.Add(
                        $"Line {lineNumber} (file line {row.LineNumber}) of a pharmacy claim has no \"National drug code\".");
                }
                else if (line.NationalDrugCode is { Length: not 11 } && !compound)
                {
                    errors.Add(
                        $"Line {lineNumber} (file line {row.LineNumber}): \"National drug code\" '{row.Get(C.NationalDrugCode)}' " +
                        "is not an 11-digit NDC.");
                }
            }
            else if (string.IsNullOrEmpty(line.ProcedureCode)
                && !(claimType == ClaimType.Institutional && line.RevenueCode is not null))
            {
                errors.Add(
                    $"Line {lineNumber} (file line {row.LineNumber}) has no \"Procedure code\"" +
                    (claimType == ClaimType.Institutional ? " or \"Revenue center code\"." : "."));
            }

            lines[lineNumber] = line;
        }

        return lines.Values.ToList();
    }

    /// <summary>
    /// The NDC as CARIN BB codes it: the 11-digit 5-4-2 form without hyphens.
    /// A hyphenated 10-digit NDC (4-4-2, 5-3-2, 5-4-1) is padded to 5-4-2 by
    /// its short segment, which is the standard conversion; a value already
    /// 11 digits passes through. Anything else is returned with its hyphens
    /// removed, for the caller to refuse.
    /// </summary>
    internal static string NormalizeNdc(string ndc)
    {
        var trimmed = ndc.Trim();
        var parts = trimmed.Split('-');
        if (parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)))
        {
            return parts[0].PadLeft(5, '0') + parts[1].PadLeft(4, '0') + parts[2].PadLeft(2, '0');
        }
        return trimmed.Replace("-", string.Empty).Replace(" ", string.Empty);
    }

    private static bool IsIcd10Pcs(string? codeType)
        => codeType?.Trim().ToLowerInvariant().Replace("-", string.Empty).Replace(" ", string.Empty)
            is "icd10pcs" or "pcs" or "http://www.cms.gov/medicare/coding/icd10";

    // ── values ───────────────────────────────────────────────────────────────

    private static decimal? SumLines(List<ClaimLine> lines, Func<ClaimLine, decimal?> amount)
    {
        var amounts = lines.Select(amount).Where(a => a is not null).ToList();
        return amounts.Count == 0 ? null : amounts.Sum();
    }

    internal static decimal? Amount(string? value, C column, int? fileLine, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim().Replace("$", string.Empty).Replace(",", string.Empty);
        var negative = cleaned.StartsWith('(') && cleaned.EndsWith(')');
        if (negative) cleaned = cleaned[1..^1];

        if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            return negative ? -amount : amount;

        errors.Add($"{Where(fileLine)}\"{CpcdsClaimColumns.DisplayName(column)}\" '{value}' is not an amount.");
        return null;
    }

    internal static DateTime? Date(string? value, C column, int? fileLine, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
        {
            return DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        }

        errors.Add($"{Where(fileLine)}\"{CpcdsClaimColumns.DisplayName(column)}\" '{value}' is not a date (use yyyy-MM-dd).");
        return null;
    }

    private static string Where(int? fileLine) => fileLine is { } line ? $"File line {line}: " : string.Empty;

    private static bool IsNpiShape(string npi) => npi.Length == 10 && npi.All(char.IsAsciiDigit);
}
