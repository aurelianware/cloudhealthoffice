using System.Text;
using ClaimsService.EDI.Florida.Models;
using ClaimsService.Models;

namespace ClaimsService.EDI.Florida;

/// <summary>
/// Service contract for retrieving a provider's Florida Medicaid Provider Number
/// from the Provider Service. The FL Medicaid Provider Number is distinct from NPI
/// and is required in the 2010AA REF*1D segment for FMMIS encounter submissions.
/// </summary>
public interface IProviderService
{
    /// <summary>
    /// Look up the Florida Medicaid Provider Number for the given NPI.
    /// Returns null if the provider is not enrolled in FL Medicaid.
    /// </summary>
    Task<string?> GetFloridaMedicaidProviderIdAsync(string npi, string tenantId);
}

/// <summary>
/// Service contract for retrieving the tenant's compliance configuration,
/// including FMMIS submitter credentials and state compliance parameters.
/// Backed by the reference-data-service <c>/api/compliance-config/{tenantId}</c> API.
/// </summary>
public interface ITenantComplianceConfigService
{
    /// <summary>
    /// Get the compliance configuration for a tenant.
    /// </summary>
    Task<FmmisComplianceConfigDto?> GetConfigAsync(string tenantId);
}

/// <summary>
/// Local DTO mirroring the subset of fields from the reference-data-service
/// <c>TenantComplianceConfig</c> that the FMMIS transformer needs.
/// Populated by the <see cref="ITenantComplianceConfigService"/> HTTP client.
/// </summary>
public class FmmisComplianceConfigDto
{
    /// <summary>ISA06 Submitter ID for FMMIS batch submissions.</summary>
    public string FmmisSubmitterId { get; set; } = string.Empty;

    /// <summary>ISA08 Interchange Sender ID for FMMIS transmissions.</summary>
    public string FmmisInterchangeSenderId { get; set; } = string.Empty;

    /// <summary>Whether the tenant participates in SMMC 3.0 MPIP.</summary>
    public bool MpipEnabled { get; set; }

    /// <summary>Encounter submission window in days (default 60).</summary>
    public int EncounterSubmissionDays { get; set; } = FmmisCompanionGuide.EncounterSubmissionWindowDays;
}

/// <summary>
/// Transforms an adjudicated <see cref="Claim"/> into an FMMIS-compliant X12
/// 005010 837P or 837I encounter transaction, applying all FL Companion Guide
/// deviations documented in <see cref="FmmisCompanionGuide"/>.
///
/// <para><b>Transformation rules applied:</b></para>
/// <list type="number">
///   <item>Subscriber is always primary — member is the subscriber in 2000B,
///         NM109 = Medicaid ID, no 2000C dependent loop.</item>
///   <item>FL Medicaid Provider Number — REF*1D added to 2010AA billing
///         provider loop via <see cref="IProviderService"/> lookup.</item>
///   <item>FMMIS receiver — ISA08 = 'FMMIS' (padded to 15 chars).</item>
///   <item>Encounter indicator — BHT02 = '18' (reissue) to indicate
///         encounter submission rather than original claim.</item>
/// </list>
/// </summary>
public class FmmisClaimTransformer
{
    private readonly IProviderService _providerService;
    private readonly ITenantComplianceConfigService _complianceConfigService;
    private readonly ILogger<FmmisClaimTransformer> _logger;

    public FmmisClaimTransformer(
        IProviderService providerService,
        ITenantComplianceConfigService complianceConfigService,
        ILogger<FmmisClaimTransformer> logger)
    {
        _providerService = providerService;
        _complianceConfigService = complianceConfigService;
        _logger = logger;
    }

    /// <summary>
    /// Transform an adjudicated claim into an FMMIS-compliant 837 encounter transaction.
    /// </summary>
    /// <param name="claim">An adjudicated claim (must pass <see cref="FmmisCompanionGuide.ValidateFmmisCompliance"/>).</param>
    /// <param name="tenantId">The tenant owning this claim (used to fetch FMMIS credentials).</param>
    /// <returns>An <see cref="FmmisTransaction"/> containing the complete X12 EDI string.</returns>
    /// <exception cref="FmmisValidationException">Thrown when the claim fails FMMIS compliance validation.</exception>
    public async Task<FmmisTransaction> TransformAsync(Claim claim, string tenantId)
    {
        // ── Step 1: Validate claim compliance ────────────────────────
        var validationErrors = FmmisCompanionGuide.ValidateFmmisCompliance(claim);
        if (validationErrors.Count > 0)
        {
            throw new FmmisValidationException(validationErrors);
        }

        // ── Step 2: Fetch provider FL Medicaid ID ────────────────────
        var flMedicaidProviderId = await _providerService
            .GetFloridaMedicaidProviderIdAsync(claim.BillingProviderNPI, tenantId);

        if (string.IsNullOrWhiteSpace(flMedicaidProviderId))
        {
            throw new FmmisValidationException(new[]
            {
                $"Billing provider NPI '{claim.BillingProviderNPI}' does not have a " +
                "Florida Medicaid Provider Number on file. REF*1D cannot be populated."
            });
        }

        // ── Step 3: Fetch tenant FMMIS credentials ───────────────────
        var complianceConfig = await _complianceConfigService.GetConfigAsync(tenantId);

        if (complianceConfig is null || string.IsNullOrWhiteSpace(complianceConfig.FmmisSubmitterId))
        {
            throw new FmmisValidationException(new[]
            {
                $"Tenant '{tenantId}' does not have FMMIS submitter credentials configured."
            });
        }

        // ── Step 4: Build the FMMIS-compliant 837 ────────────────────
        var now = DateTime.UtcNow;
        var controlNumber = GenerateControlNumber(now);
        var versionCode = claim.ClaimType == ClaimType.Institutional
            ? FmmisCompanionGuide.VersionCode837I
            : FmmisCompanionGuide.VersionCode837P;

        var edi = BuildEdi(claim, complianceConfig, flMedicaidProviderId, controlNumber, versionCode, now);

        var transactionType = claim.ClaimType == ClaimType.Institutional ? "837I" : "837P";

        _logger.LogInformation(
            "Transformed claim {ClaimNumber} to FMMIS {TransactionType} encounter (control={ControlNumber})",
            claim.ClaimNumber, transactionType, controlNumber);

        return new FmmisTransaction
        {
            ClaimNumber = claim.ClaimNumber,
            InterchangeControlNumber = controlNumber,
            TenantId = tenantId,
            SubmitterId = complianceConfig.FmmisSubmitterId,
            TransactionType = transactionType,
            RawEdi = edi,
            TransformedAtUtc = now,
            MedicaidId = claim.MemberId,
            FloridaMedicaidProviderId = flMedicaidProviderId
        };
    }

    // ── EDI Builder (pure function) ──────────────────────────────────

    /// <summary>
    /// Builds the complete FMMIS 837 EDI string. This is a pure function
    /// (no I/O) to facilitate unit testing.
    /// </summary>
    public static string BuildEdi(
        Claim claim,
        FmmisComplianceConfigDto config,
        string flMedicaidProviderId,
        string controlNumber,
        string versionCode,
        DateTime now)
    {
        var sb = new StringBuilder(4096);
        int segCount = 0;
        int hlSeq = 0;

        // ── ISA — Rule 3: ISA08 = 'FMMIS' ───────────────────────────
        sb.Append(Seg(ref segCount, false,
            $"ISA*00*          *00*          " +
            $"*{FmmisCompanionGuide.IsaQualifier}*{config.FmmisSubmitterId.PadRight(15)}" +
            $"*{FmmisCompanionGuide.IsaQualifier}*{FmmisCompanionGuide.FmmisReceiverId.PadRight(15)}" +
            $"*{now:yyMMdd}*{now:HHmm}" +
            $"*{FmmisCompanionGuide.RepetitionSeparator}" +
            $"*{FmmisCompanionGuide.InterchangeVersion}" +
            $"*{controlNumber}*0" +
            $"*{FmmisCompanionGuide.ProductionIndicator}*:~"));

        // ── GS ───────────────────────────────────────────────────────
        sb.Append(Seg(ref segCount, false,
            $"GS*{FmmisCompanionGuide.FunctionalIdCode837}" +
            $"*{Esc(config.FmmisSubmitterId)}" +
            $"*{FmmisCompanionGuide.FmmisReceiverId}" +
            $"*{now:yyyyMMdd}*{now:HHmm}*1*X*{versionCode}~"));

        // ── ST ───────────────────────────────────────────────────────
        sb.Append(Seg(ref segCount, true, $"ST*837*0001*{versionCode}~"));

        // ── BHT — Rule 4: encounter purpose code ─────────────────────
        sb.Append(Seg(ref segCount, true,
            $"BHT*{FmmisCompanionGuide.HierarchicalStructureCode}" +
            $"*{FmmisCompanionGuide.EncounterPurposeCode}" +
            $"*{claim.ClaimNumber}" +
            $"*{now:yyyyMMdd}*{now:HHmm}" +
            $"*{FmmisCompanionGuide.TransactionTypeChargeable}~"));

        // ── 1000A Submitter ──────────────────────────────────────────
        var submitterName = Esc(claim.BillingProviderName ?? config.FmmisSubmitterId);
        sb.Append(Seg(ref segCount, true,
            $"NM1*{FmmisCompanionGuide.EntitySubmitter}*2*{submitterName}*****46*{Esc(config.FmmisSubmitterId)}~"));
        sb.Append(Seg(ref segCount, true, "PER*IC*FMMIS ENCOUNTER SUBMISSION*TE*0000000000~"));

        // ── 1000B Receiver (FMMIS) ───────────────────────────────────
        sb.Append(Seg(ref segCount, true,
            $"NM1*{FmmisCompanionGuide.EntityReceiver}*2*FLORIDA MEDICAID FMMIS*****46*{FmmisCompanionGuide.FmmisReceiverId}~"));

        // ── 2000A Billing/Pay-To Provider HL ─────────────────────────
        int hlBillingProvider = ++hlSeq;
        sb.Append(Seg(ref segCount, true, $"HL*{hlBillingProvider}**20*1~"));

        // ── 2010AA Billing Provider ──────────────────────────────────
        var providerName = Esc(claim.BillingProviderName ?? "UNKNOWN PROVIDER");
        sb.Append(Seg(ref segCount, true,
            $"NM1*{FmmisCompanionGuide.EntityBillingProvider}*2*{providerName}*****XX*{claim.BillingProviderNPI}~"));
        sb.Append(Seg(ref segCount, true, "N3*ADDRESS ON FILE~"));
        sb.Append(Seg(ref segCount, true, "N4*CITY*FL*00000~"));

        // ── Rule 2: REF*1D = FL Medicaid Provider Number ─────────────
        sb.Append(Seg(ref segCount, true,
            $"REF*{FmmisCompanionGuide.FlMedicaidProviderRefQualifier}*{Esc(flMedicaidProviderId)}~"));

        // ── 2000B Subscriber HL — Rule 1: member = subscriber ────────
        int hlSubscriber = ++hlSeq;
        // HL04 = 0 — no children (no 2000C dependent loop for FMMIS)
        sb.Append(Seg(ref segCount, true, $"HL*{hlSubscriber}*{hlBillingProvider}*22*0~"));

        // SBR — subscriber is always primary for FL Medicaid
        sb.Append(Seg(ref segCount, true,
            $"SBR*{FmmisCompanionGuide.PayerResponsibilityPrimary}" +
            $"*18*****{FmmisCompanionGuide.ClaimFilingIndicatorMedicaid}~"));

        // ── 2010BA Subscriber Name — NM109 = Medicaid ID ─────────────
        var subLastName = Esc(claim.PatientLastName ?? claim.SubscriberLastName ?? string.Empty);
        var subFirstName = Esc(claim.PatientFirstName ?? claim.SubscriberFirstName ?? string.Empty);
        sb.Append(Seg(ref segCount, true,
            $"NM1*{FmmisCompanionGuide.EntitySubscriber}*1*{subLastName}*{subFirstName}****MI*{Esc(claim.MemberId)}~"));

        // ── 2010BB Payer ─────────────────────────────────────────────
        sb.Append(Seg(ref segCount, true,
            $"NM1*{FmmisCompanionGuide.EntityPayer}*2*FLORIDA MEDICAID*****PI*FMMIS~"));

        // ── 2300 Claim Information ───────────────────────────────────
        var facilityCode = claim.PlaceOfServiceCode;
        var frequencyCode = claim.ClaimFrequencyCode;

        sb.Append(Seg(ref segCount, true,
            $"CLM*{Esc(claim.ClaimNumber)}*{claim.TotalChargeAmount:F2}***{facilityCode}:B:{frequencyCode}*Y*A*Y*Y~"));

        // DTP*434 Statement Dates (institutional) or DTP*472 Service Date (professional)
        if (claim.ClaimType == ClaimType.Institutional)
        {
            sb.Append(Seg(ref segCount, true,
                $"DTP*434*RD8*{claim.ServiceDateFrom:yyyyMMdd}-{claim.ServiceDateTo:yyyyMMdd}~"));
        }
        else
        {
            sb.Append(Seg(ref segCount, true,
                $"DTP*472*RD8*{claim.ServiceDateFrom:yyyyMMdd}-{claim.ServiceDateTo:yyyyMMdd}~"));
        }

        // Prior auth number (if present)
        if (!string.IsNullOrEmpty(claim.PriorAuthorizationNumber))
        {
            sb.Append(Seg(ref segCount, true,
                $"REF*G1*{Esc(claim.PriorAuthorizationNumber)}~"));
        }

        // HI — Diagnosis codes
        if (claim.DiagnosisCodes.Count > 0)
        {
            var hiParts = claim.DiagnosisCodes
                .Select((dx, i) => $"{(i == 0 ? "ABK" : "ABF")}:{Esc(dx.Code)}")
                .ToList();
            sb.Append(Seg(ref segCount, true, $"HI*{string.Join("*", hiParts)}~"));
        }

        // ── 2400 Service Lines ───────────────────────────────────────
        foreach (var line in claim.ClaimLines)
        {
            // LX — Line counter
            sb.Append(Seg(ref segCount, true, $"LX*{line.LineNumber}~"));

            if (claim.ClaimType == ClaimType.Institutional)
            {
                // SV2 — Institutional service line
                var revCode = line.RevenueCode ?? "0001";
                sb.Append(Seg(ref segCount, true,
                    $"SV2*{revCode}*HC:{Esc(line.ProcedureCode)}{FormatModifiers(line.Modifiers)}" +
                    $"*{line.ChargeAmount:F2}*UN*{line.Units:F0}~"));
            }
            else
            {
                // SV1 — Professional service line
                var diagPointers = line.DiagnosisPointers.Count > 0
                    ? string.Join(":", line.DiagnosisPointers)
                    : "1";
                sb.Append(Seg(ref segCount, true,
                    $"SV1*HC:{Esc(line.ProcedureCode)}{FormatModifiers(line.Modifiers)}" +
                    $"*{line.ChargeAmount:F2}*UN*{line.Units:F0}*{line.PlaceOfServiceCode ?? facilityCode}" +
                    $"**{diagPointers}~"));
            }

            // DTP*472 — Service date
            sb.Append(Seg(ref segCount, true,
                $"DTP*472*RD8*{line.ServiceDateFrom:yyyyMMdd}-{line.ServiceDateTo:yyyyMMdd}~"));

            // Line-level adjudication (SVD) for encounter data
            if (line.AdjudicationResult is not null)
            {
                sb.Append(Seg(ref segCount, true,
                    $"SVD*FMMIS*{line.AdjudicationResult.PaidAmount:F2}" +
                    $"*HC:{Esc(line.ProcedureCode)}**{line.Units:F0}~"));
                // DTP*573 — Adjudication date
                if (claim.AdjudicatedDate.HasValue)
                {
                    sb.Append(Seg(ref segCount, true,
                        $"DTP*573*D8*{claim.AdjudicatedDate.Value:yyyyMMdd}~"));
                }
            }
        }

        // ── SE ───────────────────────────────────────────────────────
        // SE01 = total segments from ST to SE inclusive; segCount already
        // includes ST through the last service-line segment, so +1 for SE itself.
        sb.Append(Seg(ref segCount, false, $"SE*{segCount + 1}*0001~"));

        // ── GE / IEA ─────────────────────────────────────────────────
        sb.Append(Seg(ref segCount, false, "GE*1*1~"));
        sb.Append(Seg(ref segCount, false, $"IEA*1*{controlNumber}~"));

        return sb.ToString();
    }

    // ── Helpers (pure, static for unit testing) ──────────────────────

    /// <summary>Format modifier list as :mod1:mod2:... for SV1/SV2 composite.</summary>
    internal static string FormatModifiers(List<string> modifiers)
    {
        if (modifiers.Count == 0) return string.Empty;
        return ":" + string.Join(":", modifiers.Select(Esc));
    }

    /// <summary>Escape X12 delimiter characters (* ~ : \).</summary>
    internal static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("*", " ").Replace("~", " ").Replace(":", " ").Replace("\\", " ");
    }

    /// <summary>Append a segment and optionally increment the counted-segment tally.</summary>
    private static string Seg(ref int count, bool counted, string segment)
    {
        if (counted) count++;
        return segment;
    }

    /// <summary>Generate a 9-digit control number from the current timestamp ticks.</summary>
    internal static string GenerateControlNumber(DateTime now)
        => now.Ticks.ToString()[^9..].PadLeft(9, '0');
}
