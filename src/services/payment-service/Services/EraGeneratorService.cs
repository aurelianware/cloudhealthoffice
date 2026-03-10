using System.Text;
using PaymentService.Models;

namespace PaymentService.Services;

/// <summary>
/// 835 Health Care Claim Payment / Advice (ERA) generator.
///
/// Specification: X12 005010X221A1 (ASC X12N 835)
///
/// Segment hierarchy:
///   ISA  — Interchange control header
///   GS   — Functional group header
///   ST   — Transaction set header (835)
///   BPR  — Financial information (payment method, amount, EFT/check detail)
///   TRN  — Reassociation trace number (check/EFT number)
///   DTM  — Production date
///   N1   — Payer identification (1000A loop)
///   N1   — Payee identification (1000B loop)
///   [CLP — Claim payment  ] 2100 loop, one per claim
///   [SVC — Service line   ] 2110 loop, one per service line
///   [CAS — Adjustments    ] within 2100 and 2110
///   PLB  — Provider-level balance adjustment (optional)
///   SE   — Transaction set trailer
///   GE   — Functional group trailer
///   IEA  — Interchange control trailer
///
/// CARC / RARC reference:
///   CO-45  Contractual obligation (allowed < billed)
///   PR-1   Patient deductible
///   PR-2   Patient coinsurance
///   PR-3   Patient copay
///   OA-23  Benefit maximum reached
///
/// Usage notes:
///   - Segment terminator: ~
///   - Element separator: *
///   - Sub-element separator: :
///   - Line wrapping: none (full string, single line per segment)
///   - Segment count in SE01 includes ST and SE themselves
/// </summary>
public interface IEraGeneratorService
{
    /// <summary>
    /// Generate an X12 005010X221A1 835 ERA for the given payment record.
    /// Returns the raw EDI string ready for transmission or file storage.
    /// </summary>
    string Generate835(Payment payment, TradingPartnerInfo tradingPartner);
}

/// <summary>
/// Minimal trading partner info needed for ISA/GS/N1 segments.
/// Sourced from the payment-service's TradingPartner config or injected
/// by the caller (PaymentRunService / PaymentsController).
/// </summary>
public class TradingPartnerInfo
{
    public string InterchangeSenderId { get; set; } = "SENDER";
    public string InterchangeReceiverId { get; set; } = "RECEIVER";
    public string ApplicationSenderId { get; set; } = "SENDER";
    public string ApplicationReceiverId { get; set; } = "RECEIVER";
    /// <summary>ABA routing number for payer's bank (BPR07)</summary>
    public string? PayerRoutingNumber { get; set; }
    /// <summary>Payer bank account number (BPR08)</summary>
    public string? PayerAccountNumber { get; set; }
    /// <summary>Payee's bank routing number (BPR12)</summary>
    public string? PayeeRoutingNumber { get; set; }
    /// <summary>Payee's bank account number (BPR13)</summary>
    public string? PayeeAccountNumber { get; set; }
}

public class EraGeneratorService : IEraGeneratorService
{
    private readonly ILogger<EraGeneratorService> _logger;

    public EraGeneratorService(ILogger<EraGeneratorService> logger)
    {
        _logger = logger;
    }

    public string Generate835(Payment payment, TradingPartnerInfo tp)
    {
        var now = DateTime.UtcNow;
        var controlNumber = GenerateControlNumber(now);
        var sb = new StringBuilder();
        int segmentCount = 0;

        // ── ISA ────────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, false,   // ISA is not counted in SE01
            $"ISA*00*          *00*          " +
            $"*ZZ*{tp.InterchangeSenderId.PadRight(15)} " +
            $"*ZZ*{tp.InterchangeReceiverId.PadRight(15)} " +
            $"*{now:yyMMdd}*{now:HHmm}*^*00501*{controlNumber}*0*P*:~"));

        // ── GS ─────────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, false,
            $"GS*HP*{tp.ApplicationSenderId}*{tp.ApplicationReceiverId}" +
            $"*{now:yyyyMMdd}*{now:HHmm}*1*X*005010X221A1~"));

        // ── ST ─────────────────────────────────────────────────────────
        // ST is counted in SE01
        sb.Append(Seg(ref segmentCount, true, "ST*835*0001*005010X221A1~"));

        // ── BPR — Financial Information ─────────────────────────────────
        // BPR01: Transaction handling code
        //   C = Payment accompanies remittance
        //   D = Payment/remittance info sent separately
        //   I = Remittance information only (zero-pay ERA)
        var bprCode = payment.TotalPaymentAmount > 0 ? "C" : "I";
        // BPR06: Payment method code  CHK=check, ACH=EFT, NON=non-payment
        var payMethod = payment.PaymentMethod switch
        {
            "CHK" => "CHK",
            "ACH" => "ACH",
            _     => "NON"
        };

        string bpr;
        if (payMethod == "ACH" && tp.PayerRoutingNumber is not null)
        {
            // Full ACH EFT detail (BPR04-BPR16)
            bpr = $"BPR*{bprCode}*{payment.TotalPaymentAmount:F2}*C*ACH" +
                  $"*CCP*01*{tp.PayerRoutingNumber}*DA*{tp.PayerAccountNumber ?? string.Empty}" +
                  $"*{FormatDate(payment.PaymentDate)}" +
                  $"*01*{tp.PayeeRoutingNumber ?? string.Empty}*DA*{tp.PayeeAccountNumber ?? string.Empty}" +
                  $"*{FormatDate(payment.PaymentDate)}~";
        }
        else if (payMethod == "CHK")
        {
            bpr = $"BPR*{bprCode}*{payment.TotalPaymentAmount:F2}*C*CHK" +
                  $"****{FormatDate(payment.PaymentDate)}~";
        }
        else
        {
            // NON — remittance only
            bpr = $"BPR*{bprCode}*{payment.TotalPaymentAmount:F2}*C*NON" +
                  $"****{FormatDate(payment.PaymentDate)}~";
        }
        sb.Append(Seg(ref segmentCount, true, bpr));

        // ── TRN — Reassociation Trace Number ────────────────────────────
        // TRN01=1 (check/eft), TRN02=check/EFT number, TRN03=payer ID
        sb.Append(Seg(ref segmentCount, true,
            $"TRN*1*{payment.CheckNumber}*{payment.PayerId ?? "1999999999"}~"));

        // ── DTM — Production Date ────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, true,
            $"DTM*405*{now:yyyyMMdd}~"));

        // ── 1000A — Payer Identification ────────────────────────────────
        sb.Append(Seg(ref segmentCount, true,
            $"N1*PR*{Esc(payment.PayerName)}*XV*{payment.PayerId ?? "UNASSIGNED"}~"));

        // ── 1000B — Payee Identification ────────────────────────────────
        // NM109 qualifier: XX=NPI
        var payeeNpiQual = string.IsNullOrEmpty(payment.PayeeNPI) ? "" : $"*XX*{payment.PayeeNPI}";
        sb.Append(Seg(ref segmentCount, true,
            $"N1*PE*{Esc(payment.PayeeName)}{payeeNpiQual}~"));

        // ── 2000 / 2100 loops — one CLP per claim ───────────────────────
        foreach (var claimPay in payment.ClaimPayments)
        {
            sb.Append(BuildClaimLoop(claimPay, ref segmentCount));
        }

        // ── PLB — Provider Level Adjustment (if any) ─────────────────────
        if (payment.ProviderAdjustments.Any())
        {
            // PLB can carry up to 6 adjustment reason/amount pairs per segment
            foreach (var chunk in payment.ProviderAdjustments.Chunk(6))
            {
                var plbAdjustments = string.Concat(
                    chunk.Select(adj =>
                        $"*{adj.AdjustmentIdentifier}:{adj.ReferenceIdentification ?? string.Empty}*{adj.Amount:F2}"));

                var fiscalDate = chunk.First().FiscalPeriodEnd ?? now;
                sb.Append(Seg(ref segmentCount, true,
                    $"PLB*{payment.PayeeNPI ?? "PROVIDER"}*{fiscalDate:yyyyMMdd}{plbAdjustments}~"));
            }
        }

        // ── SE — Transaction Set Trailer ─────────────────────────────────
        sb.Append(Seg(ref segmentCount, true, $"SE*{segmentCount + 1}*0001~"));

        // ── GE / IEA ─────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, false, "GE*1*1~"));
        sb.Append(Seg(ref segmentCount, false, $"IEA*1*{controlNumber}~"));

        var era = sb.ToString();

        _logger.LogInformation(
            "Generated 835 ERA for payment {CheckNumber}: {ClaimCount} claims, {SegmentCount} segments, ${Amount:F2}",
            payment.CheckNumber, payment.ClaimPayments.Count, segmentCount, payment.TotalPaymentAmount);

        return era;
    }

    // ── 2100 claim payment loop ────────────────────────────────────────

    private static string BuildClaimLoop(ClaimPayment cp, ref int segmentCount)
    {
        var sb = new StringBuilder();

        // CLP — Claim Level Data
        // CLP01: patient control number (original claim #)
        // CLP02: claim status code  1=processed, 2=suspended, 3=denied, 4=pended
        // CLP03: total charge amount
        // CLP04: payment amount
        // CLP05: patient responsibility
        // CLP06: claim filing indicator (MB=Medicare B, MC=Medicaid, HM=HMO, 11=Other)
        // CLP07: payer claim control number (ICN)
        sb.Append(Seg(ref segmentCount, true,
            $"CLP*{cp.PatientControlNumber}*{cp.ClaimStatusCode}" +
            $"*{cp.ChargeAmount:F2}*{cp.PaymentAmount:F2}*{cp.PatientResponsibilityAmount:F2}" +
            $"*HM*{cp.PayerClaimControlNumber ?? cp.ClaimId}~"));

        // NM1 — Patient Name (if member ID available)
        if (!string.IsNullOrEmpty(cp.MemberId))
        {
            sb.Append(Seg(ref segmentCount, true,
                $"NM1*QC*1**{cp.MemberId}****MI*{cp.MemberId}~"));
        }

        // NM1 — Rendering Provider (if available)
        if (!string.IsNullOrEmpty(cp.RenderingProviderNPI))
        {
            sb.Append(Seg(ref segmentCount, true,
                $"NM1*82*1*****XX*{cp.RenderingProviderNPI}~"));
        }

        // DTP — Date Claim Received
        if (cp.ClaimReceivedDate.HasValue)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"DTM*050*{FormatDate(cp.ClaimReceivedDate.Value)}~"));
        }

        // CAS — Claim-level adjustments (CO, PR, OA, PI)
        // Up to 6 CARC/amount pairs per CAS segment
        foreach (var casGroup in cp.ClaimAdjustments.GroupBy(a => a.GroupCode))
        {
            foreach (var chunk in casGroup.Chunk(6))
            {
                var pairs = string.Concat(
                    chunk.Select(adj => $"*{adj.ReasonCode}*{adj.Amount:F2}"));
                sb.Append(Seg(ref segmentCount, true,
                    $"CAS*{casGroup.Key}{pairs}~"));
            }
        }

        // ── 2110 service line loops ────────────────────────────────────
        foreach (var sl in cp.ServiceLines)
        {
            sb.Append(BuildServiceLineLoop(sl, ref segmentCount));
        }

        return sb.ToString();
    }

    // ── 2110 service line loop ─────────────────────────────────────────

    private static string BuildServiceLineLoop(ServiceLinePayment sl, ref int segmentCount)
    {
        var sb = new StringBuilder();

        // SVC — Service Payment Information
        // SVC01: composite procedure code  HC:CPTCODE or NU:REVCODE
        // SVC02: line charge amount
        // SVC03: line payment amount
        // SVC05: units
        string svcCode = !string.IsNullOrEmpty(sl.RevenueCode)
            ? $"NU:{sl.RevenueCode}:{sl.ProcedureCode}"
            : $"HC:{sl.ProcedureCode}";

        sb.Append(Seg(ref segmentCount, true,
            $"SVC*{svcCode}*{sl.ChargeAmount:F2}*{sl.PaymentAmount:F2}**{sl.Units:G}~"));

        // DTM — Service dates
        if (sl.ServiceDateFrom.HasValue)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"DTM*472*{FormatDate(sl.ServiceDateFrom.Value)}~"));
        }
        if (sl.ServiceDateTo.HasValue && sl.ServiceDateTo != sl.ServiceDateFrom)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"DTM*473*{FormatDate(sl.ServiceDateTo.Value)}~"));
        }

        // CAS — Line-level adjustments
        foreach (var casGroup in sl.Adjustments.GroupBy(a => a.GroupCode))
        {
            foreach (var chunk in casGroup.Chunk(6))
            {
                var pairs = string.Concat(chunk.Select(adj =>
                {
                    var rarc = string.IsNullOrEmpty(adj.RemarkCode) ? string.Empty : $"*{adj.RemarkCode}";
                    return $"*{adj.ReasonCode}*{adj.Amount:F2}{rarc}";
                }));
                sb.Append(Seg(ref segmentCount, true,
                    $"CAS*{casGroup.Key}{pairs}~"));
            }
        }

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static string Seg(ref int count, bool counted, string segment)
    {
        if (counted) count++;
        return segment;
    }

    private static string FormatDate(DateTime dt) => dt.ToString("yyyyMMdd");

    /// <summary>
    /// Strip X12 delimiters from free-text fields to prevent segment corruption.
    /// </summary>
    private static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("*", " ").Replace("~", " ").Replace(":", " ").Replace("\\", " ");
    }

    private static string GenerateControlNumber(DateTime now)
        => now.Ticks.ToString()[^9..].PadLeft(9, '0');
}
