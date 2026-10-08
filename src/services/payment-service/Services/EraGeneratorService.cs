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
    /// <summary>Payer bank account number (BPR09)</summary>
    public string? PayerAccountNumber { get; set; }
    /// <summary>
    /// Originating company identifier (BPR10, and TRN03): exactly 10
    /// characters, typically "1" followed by the payer's TIN. Required for
    /// every 835 (TRN03); generation fails without it. Sourced from Era:OriginatingCompanyId.
    /// </summary>
    public string? OriginatingCompanyId { get; set; }
    /// <summary>Originating company supplemental code (BPR11, situational, 9 characters).</summary>
    public string? OriginatingCompanySupplementalCode { get; set; }
    /// <summary>Payee's bank routing number (BPR13)</summary>
    public string? PayeeRoutingNumber { get; set; }
    /// <summary>Payee's bank account number (BPR15)</summary>
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

        // BPR02 is never negative: a reversal payment's 835 is BPR02 = 0 with
        // the recoupment carried forward in a PLB FB adjustment.
        var (bprAmount, plbs, _) = Era835FinancialSegments.WithForwardBalance(
            payment.TotalPaymentAmount, payment.ProviderAdjustments, payment.CheckNumber, now);

        // BPR02 = sum(CLP04) - sum(PLB); sum(SVC03) = CLP04 per claim.
        Era835FinancialSegments.EnsureBalanced(bprAmount, payment.ClaimPayments, plbs);

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
        // BPR01: C = payment accompanies remittance, I = remittance only.
        // BPR04: ACH / CHK / NON. Element positions: Era835FinancialSegments.
        // Throws when an ACH BPR cannot be filled (e.g. no BPR10 originating
        // company id) rather than emitting a misaligned segment.
        sb.Append(Seg(ref segmentCount, true,
            Era835FinancialSegments.BuildBpr(bprAmount, payment.PaymentMethod, payment.PaymentDate, tp)));

        // ── TRN — Reassociation Trace Number ────────────────────────────
        // TRN01=1 (check/eft), TRN02=check/EFT number, TRN03=originating
        // company id (same as BPR10; required configuration)
        sb.Append(Seg(ref segmentCount, true,
            Era835FinancialSegments.BuildTrn(payment.CheckNumber, tp)));

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
            sb.Append(Era835ClaimLoops.BuildClaimLoop(claimPay, ref segmentCount));
        }

        // ── PLB — Provider Level Adjustment (if any) ─────────────────────
        if (plbs.Any())
        {
            // PLB can carry up to 6 adjustment reason/amount pairs per segment
            foreach (var chunk in plbs.Chunk(6))
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
