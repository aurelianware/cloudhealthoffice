using System.Text;
using EligibilityService.Models;

namespace EligibilityService.Services;

/// <summary>
/// Generates X12 005010X279A1 (271 Eligibility/Benefit Response) EDI.
///
/// Segment hierarchy:
///   ISA   — Interchange control header (sender/receiver swapped from 270)
///   GS    — Functional group header (GS01=HB)
///   ST    — Transaction set header (271)
///   BHT   — Beginning of hierarchical transaction (BHT02=11=response)
///   2000A — Information Source (Payer)     HL*1**20*1
///   2000B — Information Receiver (Provider) HL*2*1*21*1
///   2000C — Subscriber                     HL*3*2*22*0 (or *1 if dependent)
///     NM1*IL   — Subscriber name
///     REF*0F   — Subscriber ID
///     TRN      — Trace number (echo from 270)
///     DTP      — Coverage dates
///     EB       — Eligibility/Benefit segments (one per benefit type)
///     AAA      — Rejection code (if not covered)
///   2000D — Dependent (optional)
///   SE    — Transaction set trailer
///   GE    — Functional group trailer
///   IEA   — Interchange control trailer
///
/// EB segment: EB01*EB02*EB03*EB04*EB05*EB06*EB07*EB08*...
///   EB01 — Eligibility/benefit information code
///     1  = Active Coverage
///     6  = Inactive Coverage
///     A  = Co-Insurance
///     B  = Co-Payment
///     C  = Deductible
///     F  = Limitations (visit limit)
///     G  = Out-of-Pocket (Stop Loss)
///   EB02 — Coverage Level Code (EMP, ESP, ECH, FAM, IND, CHD, DEP)
///   EB03 — Service Type Code (30=Health Benefit Plan, 33=Chiro, etc.)
///   EB06 — Time Period Qualifier
///     23 = Contract (annual plan benefit)
///     27 = Visit
///   EB07 — Monetary Amount
///   EB08 — Percent (as decimal: 0.20 for 20%)
///   EB12 — In-Network Indicator (Y/W)
///   EB13 — Procedure Code (optional)
/// </summary>
public interface IEdi271Generator
{
    /// <summary>
    /// Generate a 271 response EDI string.
    /// </summary>
    /// <param name="inquiry">The original 270 inquiry (used for subscriber/provider identity).</param>
    /// <param name="response">The eligibility response from ProcessInquiryAsync.</param>
    /// <param name="isaSenderId">ISA06 for the 271 (= 270's ISA08 — the payer's interchange ID).</param>
    /// <param name="isaReceiverId">ISA08 for the 271 (= 270's ISA06 — the provider's interchange ID).</param>
    string Generate(
        EligibilityInquiry inquiry,
        EligibilityResponse response,
        string isaSenderId,
        string isaReceiverId);
}

public class Edi271Generator : IEdi271Generator
{
    private readonly ILogger<Edi271Generator> _logger;

    public Edi271Generator(ILogger<Edi271Generator> logger)
    {
        _logger = logger;
    }

    public string Generate(
        EligibilityInquiry inquiry,
        EligibilityResponse response,
        string isaSenderId,
        string isaReceiverId)
    {
        var now = DateTime.UtcNow;
        var controlNumber = GenerateControlNumber(now);
        var sb = new StringBuilder();
        int segmentCount = 0;
        int hlCount = 0;

        // ── ISA — sender/receiver are REVERSED from the 270 ───────────
        var senderId   = (isaSenderId.Length  > 0 ? isaSenderId  : inquiry.PayerId).PadRight(15);
        var receiverId = (isaReceiverId.Length > 0 ? isaReceiverId : inquiry.ProviderId).PadRight(15);

        sb.Append(Seg(ref segmentCount, false,
            $"ISA*00*          *00*          " +
            $"*ZZ*{senderId}" +
            $"*ZZ*{receiverId}" +
            $"*{now:yyMMdd}*{now:HHmm}*^*00501*{controlNumber}*0*P*:~"));

        // ── GS — HB = Health Care Eligibility/Benefit Info ───────────
        var appSender   = string.IsNullOrEmpty(inquiry.PayerId)    ? "PAYER"    : inquiry.PayerId;
        var appReceiver = string.IsNullOrEmpty(inquiry.ProviderId)  ? "PROVIDER" : inquiry.ProviderId;
        sb.Append(Seg(ref segmentCount, false,
            $"GS*HB*{appSender}*{appReceiver}" +
            $"*{now:yyyyMMdd}*{now:HHmm}*1*X*005010X279A1~"));

        // ── ST ─────────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, true, "ST*271*0001*005010X279A1~"));

        // ── BHT — BHT02=11 (response), BHT06=11 (response) ───────────
        var bhtRef = response.ControlNumber.Length > 0
            ? response.ControlNumber[..Math.Min(10, response.ControlNumber.Length)]
            : inquiry.Id[..Math.Min(10, inquiry.Id.Length)];
        sb.Append(Seg(ref segmentCount, true,
            $"BHT*0022*11*{bhtRef}*{now:yyyyMMdd}*{now:HHmm}~"));

        // ── 2000A — Information Source (Payer) ─────────────────────────
        int hlA = ++hlCount;
        sb.Append(Seg(ref segmentCount, true, $"HL*{hlA}**20*1~"));
        var payerName = Esc(string.IsNullOrEmpty(inquiry.PayerName) ? "HEALTH PLAN" : inquiry.PayerName);
        var payerId   = Esc(inquiry.PayerId.Length > 0 ? inquiry.PayerId : "UNASSIGNED");
        sb.Append(Seg(ref segmentCount, true,
            $"NM1*PR*2*{payerName}*****PI*{payerId}~"));

        // ── 2000B — Information Receiver (Provider) ────────────────────
        int hlB = ++hlCount;
        sb.Append(Seg(ref segmentCount, true, $"HL*{hlB}*{hlA}*21*1~"));
        var providerNpi  = Esc(inquiry.ProviderNPI.Length > 0 ? inquiry.ProviderNPI : inquiry.ProviderId);
        var providerName = Esc(providerNpi);
        sb.Append(Seg(ref segmentCount, true,
            $"NM1*1P*2*{providerName}*****XX*{providerNpi}~"));

        // ── 2000C — Subscriber ─────────────────────────────────────────
        // HL04: 1 if dependent loop follows, 0 if not
        bool hasDependent = !string.IsNullOrEmpty(inquiry.DependentLastName);
        int hlC = ++hlCount;
        sb.Append(Seg(ref segmentCount, true,
            $"HL*{hlC}*{hlB}*22*{(hasDependent ? "1" : "0")}~"));

        // NM1*IL — Subscriber Name
        var subLast  = Esc(inquiry.SubscriberLastName);
        var subFirst = Esc(inquiry.SubscriberFirstName);
        var subId    = Esc(inquiry.SubscriberId);
        sb.Append(Seg(ref segmentCount, true,
            $"NM1*IL*1*{subLast}*{subFirst}****MI*{subId}~"));

        // REF*0F — Subscriber ID (echo from 270)
        if (!string.IsNullOrEmpty(inquiry.SubscriberId))
        {
            sb.Append(Seg(ref segmentCount, true, $"REF*0F*{subId}~"));
        }

        // DMG — Subscriber demographics (if known)
        if (inquiry.SubscriberDOB != default)
        {
            var gender = string.IsNullOrEmpty(inquiry.SubscriberGender) ? "U" : inquiry.SubscriberGender;
            sb.Append(Seg(ref segmentCount, true,
                $"DMG*D8*{inquiry.SubscriberDOB:yyyyMMdd}*{gender}~"));
        }

        // TRN — Trace number (echo back subscriber trace from 270)
        sb.Append(Seg(ref segmentCount, true,
            $"TRN*2*{response.ControlNumber}*{payerId}~"));

        // ── Coverage dates ─────────────────────────────────────────────
        if (response.CoverageBeginDate.HasValue)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"DTP*346*D8*{response.CoverageBeginDate.Value:yyyyMMdd}~"));
        }
        if (response.CoverageEndDate.HasValue)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"DTP*347*D8*{response.CoverageEndDate.Value:yyyyMMdd}~"));
        }

        // ── EB segments — Eligibility/Benefit information ──────────────
        if (response.IsCovered)
        {
            // Active coverage — general
            var coverageLevel = string.IsNullOrEmpty(response.CoverageLevel)
                ? "IND" : response.CoverageLevel;
            var planDesc = Esc(response.InsurancePlanName).Replace(" ", "_")[..Math.Min(50, Esc(response.InsurancePlanName).Length)];
            sb.Append(Seg(ref segmentCount, true,
                $"EB*1*{coverageLevel}*30**{planDesc}~"));

            // Group number
            if (!string.IsNullOrEmpty(response.GroupNumber))
            {
                sb.Append(Seg(ref segmentCount, true,
                    $"REF*1L*{Esc(response.GroupNumber)}~"));
            }

            // Deductible
            if (response.Deductible != null)
            {
                AppendDeductibleSegments(sb, ref segmentCount, response.Deductible);
            }

            // Out-of-pocket maximum
            if (response.OutOfPocket != null)
            {
                AppendOutOfPocketSegments(sb, ref segmentCount, response.OutOfPocket);
            }

            // Per-benefit EB segments (copay, coinsurance, service-specific coverage)
            foreach (var benefit in response.Benefits)
            {
                AppendBenefitSegments(sb, ref segmentCount, benefit);
            }
        }
        else
        {
            // Inactive coverage
            sb.Append(Seg(ref segmentCount, true, $"EB*6**30~"));

            // AAA — Rejection code
            // AAA01=Y (subscriber is covered by another plan), AAA03=42 (No active coverage)
            // AAA04=Y (dependent flag)
            sb.Append(Seg(ref segmentCount, true, "AAA*N**42*Y~"));

            // MSG — Free-text rejection reason
            if (!string.IsNullOrEmpty(response.RejectionReason))
            {
                var msg = Esc(response.RejectionReason)[..Math.Min(264, Esc(response.RejectionReason).Length)];
                sb.Append(Seg(ref segmentCount, true, $"MSG*{msg}~"));
            }
        }

        // ── 2000D — Dependent (optional) ──────────────────────────────
        if (hasDependent)
        {
            int hlD = ++hlCount;
            sb.Append(Seg(ref segmentCount, true, $"HL*{hlD}*{hlC}*23*0~"));

            var depLast  = Esc(inquiry.DependentLastName);
            var depFirst = Esc(inquiry.DependentFirstName ?? string.Empty);
            sb.Append(Seg(ref segmentCount, true,
                $"NM1*QC*1*{depLast}*{depFirst}~"));

            if (inquiry.DependentDOB.HasValue)
            {
                var depGender = string.IsNullOrEmpty(inquiry.DependentGender) ? "U" : inquiry.DependentGender;
                sb.Append(Seg(ref segmentCount, true,
                    $"DMG*D8*{inquiry.DependentDOB.Value:yyyyMMdd}*{depGender}~"));
            }
        }

        // ── SE ─────────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, true, $"SE*{segmentCount + 1}*0001~"));

        // ── GE / IEA ───────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, false, "GE*1*1~"));
        sb.Append(Seg(ref segmentCount, false, $"IEA*1*{controlNumber}~"));

        _logger.LogInformation(
            "Generated 271 for subscriber {SubscriberId}: covered={IsCovered}, {BenefitCount} EB segments",
            SanitizeForLog(inquiry.SubscriberId), response.IsCovered, response.Benefits.Count);

        return sb.ToString();
    }

    // ── Deductible EB segments ────────────────────────────────────────

    private static void AppendDeductibleSegments(StringBuilder sb, ref int segmentCount, DeductibleInfo ded)
    {
        // EB*C = Deductible, EB06=23 = Contract/Plan Year
        if (ded.IndividualDeductible > 0)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"EB*C*IND*30***23**{ded.IndividualDeductible:F2}~"));
        }
        if (ded.IndividualDeductibleMet > 0)
        {
            // Qualifier 29 = Remaining is not met; use 26 = Exceeded for amount consumed
            sb.Append(Seg(ref segmentCount, true,
                $"EB*C*IND*30***26**{ded.IndividualDeductibleMet:F2}~"));
        }
        if (ded.IndividualDeductibleRemaining > 0)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"EB*C*IND*30***27**{ded.IndividualDeductibleRemaining:F2}~"));
        }
        if (ded.FamilyDeductible > 0)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"EB*C*FAM*30***23**{ded.FamilyDeductible:F2}~"));
        }
        if (ded.FamilyDeductibleMet > 0)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"EB*C*FAM*30***26**{ded.FamilyDeductibleMet:F2}~"));
        }
        if (ded.FamilyDeductibleRemaining > 0)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"EB*C*FAM*30***27**{ded.FamilyDeductibleRemaining:F2}~"));
        }
    }

    // ── Out-of-pocket EB segments ─────────────────────────────────────

    private static void AppendOutOfPocketSegments(StringBuilder sb, ref int segmentCount, OutOfPocketInfo oop)
    {
        // EB*G = Out-of-Pocket (Stop Loss)
        if (oop.IndividualOOPMax > 0)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"EB*G*IND*30***23**{oop.IndividualOOPMax:F2}~"));
        }
        if (oop.IndividualOOPMet > 0)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"EB*G*IND*30***26**{oop.IndividualOOPMet:F2}~"));
        }
        if (oop.IndividualOOPRemaining > 0)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"EB*G*IND*30***27**{oop.IndividualOOPRemaining:F2}~"));
        }
        if (oop.FamilyOOPMax > 0)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"EB*G*FAM*30***23**{oop.FamilyOOPMax:F2}~"));
        }
        if (oop.FamilyOOPMet > 0)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"EB*G*FAM*30***26**{oop.FamilyOOPMet:F2}~"));
        }
        if (oop.FamilyOOPRemaining > 0)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"EB*G*FAM*30***27**{oop.FamilyOOPRemaining:F2}~"));
        }
    }

    // ── Per-benefit EB segments ───────────────────────────────────────

    private static void AppendBenefitSegments(StringBuilder sb, ref int segmentCount, EligibilityBenefit benefit)
    {
        // Determine EB01 from benefit type + insurance type hint
        // Most benefits from the service already have ServiceTypeCode and amounts
        // We emit one EB segment per benefit entry using the data as-is.
        var eb01 = benefit.InsuranceType switch
        {
            "B" => "B", // Co-payment
            "A" => "A", // Co-insurance
            "F" => "F", // Limitation (visit limits)
            _   => "1", // Default: Active coverage for this service type
        };

        // If monetary amount and percent both absent, treat as general coverage flag
        var coverageLevel = string.IsNullOrEmpty(benefit.CoverageLevel) ? "IND" : benefit.CoverageLevel;
        var svcType       = string.IsNullOrEmpty(benefit.ServiceTypeCode) ? "30" : benefit.ServiceTypeCode;
        var timePeriod    = string.IsNullOrEmpty(benefit.TimePeriodQualifier) ? string.Empty : benefit.TimePeriodQualifier;
        var network       = benefit.NetworkIndicator == "N" ? "W" : "Y"; // W=out-of-network, Y=in-network

        string eb;
        if (benefit.MonetaryAmount.HasValue)
        {
            eb = $"EB*{eb01}*{coverageLevel}*{svcType}***{timePeriod}**{benefit.MonetaryAmount.Value:F2}****{network}~";
        }
        else if (benefit.Percentage.HasValue)
        {
            var pct = benefit.Percentage.Value / 100m; // convert to decimal (20% → 0.20)
            eb = $"EB*{eb01}*{coverageLevel}*{svcType}***{timePeriod}***{pct:F2}***{network}~";
        }
        else
        {
            eb = $"EB*{eb01}*{coverageLevel}*{svcType}~";
        }

        sb.Append(Seg(ref segmentCount, true, eb));

        // Auth requirement message
        if (benefit.AuthorizationRequired == "Y")
        {
            sb.Append(Seg(ref segmentCount, true,
                $"MSG*Prior authorization required for {Esc(benefit.ServiceTypeName)}~"));
        }

        // Benefit dates
        if (benefit.BenefitBeginDate.HasValue)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"DTP*348*D8*{benefit.BenefitBeginDate.Value:yyyyMMdd}~"));
        }
        if (benefit.BenefitEndDate.HasValue)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"DTP*349*D8*{benefit.BenefitEndDate.Value:yyyyMMdd}~"));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    private static string Seg(ref int count, bool counted, string segment)
    {
        if (counted) count++;
        return segment;
    }

    private static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("*", " ").Replace("~", " ").Replace(":", " ").Replace("\\", " ");
    }

    private static string GenerateControlNumber(DateTime now)
        => now.Ticks.ToString()[^9..].PadLeft(9, '0');
}
