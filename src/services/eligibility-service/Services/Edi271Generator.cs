using System.Globalization;
using System.Text;
using EligibilityService.Models;
using CloudHealthOffice.Infrastructure.Logging;

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
        // BHT03 echoes the 270's reference when it sent one.
        var bhtRef = !string.IsNullOrEmpty(inquiry.ReferenceId)
            ? Esc(inquiry.ReferenceId)
            : response.ControlNumber.Length > 0
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

        // TRN*2 — the 270's TRN*1 trace numbers, echoed so the submitter
        // can match the response (CORE requires the echo).
        if (inquiry.TraceNumbers.Count > 0)
        {
            foreach (var trace in inquiry.TraceNumbers)
            {
                sb.Append(Seg(ref segmentCount, true,
                    Join("TRN", "2", Esc(trace.Number), Esc(trace.Originator), Esc(trace.Supplemental)) + "~"));
            }
        }
        else if (response.ControlNumber.Length > 0)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"TRN*2*{Esc(response.ControlNumber)}*{payerId}~"));
        }

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
            var coverageLevel = string.IsNullOrEmpty(response.CoverageLevel)
                ? "IND" : response.CoverageLevel;
            var planDesc = Esc(response.InsurancePlanName);
            planDesc = planDesc[..Math.Min(50, planDesc.Length)];
            var planAuthRequired = response.Benefits.Any(b =>
                b.AuthorizationRequired == "Y" && X12ServiceTypes.FromCategory(b.ServiceTypeCode) == X12ServiceTypes.HealthBenefitPlanCoverage);
            sb.Append(Seg(ref segmentCount, true,
                Eb(("01", "1"), ("02", coverageLevel), ("03", X12ServiceTypes.HealthBenefitPlanCoverage), ("05", planDesc),
                   ("11", planAuthRequired ? "Y" : "")) + "~"));

            if (!string.IsNullOrEmpty(response.GroupNumber))
            {
                sb.Append(Seg(ref segmentCount, true,
                    $"REF*1L*{Esc(response.GroupNumber)}~"));
            }

            // Plan-level deductible and out-of-pocket (CORE: base and remaining deductible).
            if (response.Deductible != null)
            {
                AppendDeductibleSegments(sb, ref segmentCount, response.Deductible);
            }
            if (response.OutOfPocket != null)
            {
                AppendOutOfPocketSegments(sb, ref segmentCount, response.OutOfPocket);
            }

            // Service-type answers: for a generic (30) inquiry, the CORE
            // service types the plan covers; otherwise the types asked for.
            var requested = inquiry.ServiceTypeCodes.Count > 0
                ? inquiry.ServiceTypeCodes
                : new List<string> { string.IsNullOrEmpty(inquiry.ServiceTypeCode) ? X12ServiceTypes.HealthBenefitPlanCoverage : inquiry.ServiceTypeCode };
            var serviceTypes = requested.Contains(X12ServiceTypes.HealthBenefitPlanCoverage)
                ? X12ServiceTypes.CoreGeneric.Concat(requested.Where(c => c != X12ServiceTypes.HealthBenefitPlanCoverage)).Distinct().ToList()
                : requested.Distinct().ToList();

            var byServiceType = response.Benefits
                .Select(b => (Code: X12ServiceTypes.FromCategory(b.ServiceTypeCode), Benefit: b))
                .Where(x => x.Code is not null)
                .GroupBy(x => x.Code!)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Benefit).ToList());

            // Plan-wide cost sharing (category 30) goes under 30, after the
            // plan's own EB*1, so it is not repeated as a service type.
            if (byServiceType.TryGetValue(X12ServiceTypes.HealthBenefitPlanCoverage, out var planBenefits))
                AppendServiceTypeSegments(sb, ref segmentCount, coverageLevel, X12ServiceTypes.HealthBenefitPlanCoverage, planBenefits, activeCoverage: false);

            foreach (var serviceType in serviceTypes)
            {
                if (serviceType == X12ServiceTypes.HealthBenefitPlanCoverage || !byServiceType.TryGetValue(serviceType, out var benefits))
                    continue;
                AppendServiceTypeSegments(sb, ref segmentCount, coverageLevel, serviceType, benefits, activeCoverage: true);
            }
        }
        else
        {
            // Inactive coverage: EB*6 alone. (AAA in 2100C is for a request
            // CHO could not process, such as a subscriber it cannot find; 42
            // would tell the submitter to try again later.)
            sb.Append(Seg(ref segmentCount, true, $"EB*6**30~"));

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
            LogSanitizer.MaskId(inquiry.SubscriberId), response.IsCovered, response.Benefits.Count);

        return sb.ToString();
    }

    // ── Deductible / out-of-pocket EB segments ────────────────────────
    //
    // EB06 time period: 23 = calendar year (22 = service year) for the plan
    // amount, 24 = year to date for the amount met, 29 = remaining. The
    // amount is EB07. Remaining is sent even when it is zero (deductible met).

    private static string PlanPeriod(string? timePeriod) =>
        timePeriod is not null && timePeriod.Contains("Service", StringComparison.OrdinalIgnoreCase) ? "22" : "23";

    private static void AppendDeductibleSegments(StringBuilder sb, ref int segmentCount, DeductibleInfo ded)
    {
        var period = PlanPeriod(ded.TimePeriod);
        AppendAccumulator(sb, ref segmentCount, "C", "IND", period, ded.IndividualDeductible, ded.IndividualDeductibleMet, ded.IndividualDeductibleRemaining);
        AppendAccumulator(sb, ref segmentCount, "C", "FAM", period, ded.FamilyDeductible, ded.FamilyDeductibleMet, ded.FamilyDeductibleRemaining);
    }

    private static void AppendOutOfPocketSegments(StringBuilder sb, ref int segmentCount, OutOfPocketInfo oop)
    {
        AppendAccumulator(sb, ref segmentCount, "G", "IND", "23", oop.IndividualOOPMax, oop.IndividualOOPMet, oop.IndividualOOPRemaining);
        AppendAccumulator(sb, ref segmentCount, "G", "FAM", "23", oop.FamilyOOPMax, oop.FamilyOOPMet, oop.FamilyOOPRemaining);
    }

    private static void AppendAccumulator(StringBuilder sb, ref int segmentCount, string eb01, string level, string period,
        decimal amount, decimal met, decimal remaining)
    {
        if (amount <= 0)
            return;
        sb.Append(Seg(ref segmentCount, true, Eb(("01", eb01), ("02", level), ("03", "30"), ("06", period), ("07", Money(amount)), ("12", "Y")) + "~"));
        if (met > 0)
            sb.Append(Seg(ref segmentCount, true, Eb(("01", eb01), ("02", level), ("03", "30"), ("06", "24"), ("07", Money(met)), ("12", "Y")) + "~"));
        sb.Append(Seg(ref segmentCount, true, Eb(("01", eb01), ("02", level), ("03", "30"), ("06", "29"), ("07", Money(Math.Max(0, remaining))), ("12", "Y")) + "~"));
    }

    // ── Per-service-type EB segments ──────────────────────────────────

    /// <summary>
    /// Active coverage for one service type (EB*1, EB11 = Y when prior
    /// authorization is required), then the patient's share: co-payment
    /// (EB*B, EB07, per visit), co-insurance (EB*A, EB08 as a decimal) and
    /// visit limits (EB*F, EB09 VS / EB10). EB12 says in or out of network.
    /// </summary>
    private static void AppendServiceTypeSegments(StringBuilder sb, ref int segmentCount, string level, string serviceType,
        List<EligibilityBenefit> benefits, bool activeCoverage)
    {
        if (activeCoverage)
        {
            var authRequired = benefits.Any(b => b.AuthorizationRequired == "Y");
            sb.Append(Seg(ref segmentCount, true,
                Eb(("01", "1"), ("02", level), ("03", serviceType), ("11", authRequired ? "Y" : "")) + "~"));
        }

        foreach (var benefit in benefits)
        {
            var benefitLevel = string.IsNullOrEmpty(benefit.CoverageLevel) ? level : benefit.CoverageLevel;
            var network = benefit.NetworkIndicator == "N" ? "N" : "Y";
            if (benefit.MonetaryAmount is { } copay)
            {
                sb.Append(Seg(ref segmentCount, true,
                    Eb(("01", "B"), ("02", benefitLevel), ("03", serviceType), ("06", "27"), ("07", Money(copay)), ("12", network)) + "~"));
            }
            if (benefit.Percentage is { } percent)
            {
                // Plans store 20 for 20%; X12 wants 0.2.
                var ratio = percent / 100m;
                sb.Append(Seg(ref segmentCount, true,
                    Eb(("01", "A"), ("02", benefitLevel), ("03", serviceType), ("08", ratio.ToString("0.###", CultureInfo.InvariantCulture)), ("12", network)) + "~"));
            }
            if (benefit.Quantity is { } visits && visits > 0)
            {
                sb.Append(Seg(ref segmentCount, true,
                    Eb(("01", "F"), ("02", benefitLevel), ("03", serviceType), ("06", "23"), ("09", "VS"), ("10", visits.ToString("0", CultureInfo.InvariantCulture)), ("12", network)) + "~"));
            }
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
    }

    /// <summary>An EB segment from (position, value) pairs, positions "01".."13"; trailing empties dropped.</summary>
    private static string Eb(params (string Position, string Value)[] elements)
    {
        var e = new string[13];
        foreach (var (position, value) in elements)
            e[int.Parse(position, CultureInfo.InvariantCulture) - 1] = value;
        return Join(new[] { "EB" }.Concat(e.Select(v => v ?? string.Empty)).ToArray());
    }

    private static string Join(params string?[] elements)
    {
        var last = elements.Length - 1;
        while (last > 0 && string.IsNullOrEmpty(elements[last])) last--;
        return string.Join("*", elements.Take(last + 1).Select(v => v ?? string.Empty));
    }

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    // ── Helpers ───────────────────────────────────────────────────────

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
