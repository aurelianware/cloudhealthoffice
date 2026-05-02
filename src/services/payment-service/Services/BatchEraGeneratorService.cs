using System.Text;
using PaymentService.Models;

namespace PaymentService.Services;

/// <summary>
/// Batched 835 Health Care Claim Payment / Advice (ERA) generator.
///
/// Specification: X12 005010X221A1 (ASC X12N 835)
///
/// Differs from the per-payment <see cref="IEraGeneratorService"/> in
/// that one ISA/IEA file is produced per trading partner — N CLP
/// loops within a single ST/SE envelope, one per claim in the batch
/// for that partner. This is the production path for operator-initiated
/// PaymentRun execution (5.10 Decision 2c).
///
/// Segment hierarchy (per envelope):
///   ISA  — Interchange control header
///   GS   — Functional group header
///   ST   — Transaction set header (835)
///   BPR  — Financial information (payment summary across the batch)
///   TRN  — Reassociation trace number (envelope control number)
///   DTM  — Production date
///   N1   — Payer identification (1000A loop)
///   N1   — Payee identification (1000B loop)
///   [CLP — Claim payment    ] 2100 loop, one per claim in the batch
///   [SVC — Service line     ] 2110 loop, one per service line
///   [CAS — Adjustments      ] within 2100 and 2110
///   PLB  — Provider-level balance adjustment (optional)
///   SE   — Transaction set trailer (segment count includes ST and SE)
///   GE   — Functional group trailer
///   IEA  — Interchange control trailer
///
/// Phase 1 simplifications:
///   - One ST/SE envelope per ISA/IEA file (no multi-envelope-per-file)
///   - Payee identity comes from the first ClaimPayment in the batch
///     when the batch is single-payee; multi-payee batches use the
///     trading partner's configured payee identity
///   - PLB segments only fire when explicit ProviderAdjustments are
///     supplied on the batch input
///   - The envelope's BPR/TRN reflects the first input Payment's check
///     number; the caller (PaymentRunService) is expected to allocate
///     a single check per trading partner so every CLP loop's
///     downstream finalize CheckNumber matches the envelope-level TRN
/// </summary>
public interface IBatchEraGeneratorService
{
    /// <summary>
    /// Group N payment-claim pairs by their TradingPartnerId and emit
    /// one ISA/IEA file per group, each containing one ST/SE envelope
    /// with one CLP loop per claim.
    ///
    /// Pairs whose TradingPartnerId is missing from
    /// <paramref name="tradingPartnersById"/> are skipped with a
    /// warning; the surviving partners still emit. This avoids a single
    /// misconfigured partner failing the whole PaymentRun.
    /// </summary>
    IReadOnlyList<EraEnvelope> GenerateBatch(
        IEnumerable<EraPaymentInput> inputs,
        IReadOnlyDictionary<string, TradingPartnerInfo> tradingPartnersById);
}

/// <summary>
/// One claim's contribution to a batched 835 envelope. Carries the
/// already-built <see cref="Payment.ClaimPayments"/> entry (with
/// service lines + adjustments applied by
/// <see cref="ICarcRarcMappingService"/>) plus the trading partner the
/// envelope routes to.
///
/// <para>5.12b — <see cref="IsReversal"/> threads the reversal-mode
/// signal through to the produced <see cref="EraEnvelope.IsReversal"/>
/// so the caller can persist the envelope with
/// <see cref="EraEnvelopeRecord.ReversalRunId"/> set in place of
/// <see cref="EraEnvelopeRecord.PaymentRunId"/>. CLP02 reversal status
/// "22" and CAS sign-flips are set upstream by
/// <c>ReversalRunService</c> when constructing the
/// <see cref="Payment"/>; the generator does not branch on this flag.</para>
/// </summary>
public class EraPaymentInput
{
    public string TradingPartnerId { get; set; } = string.Empty;
    public Payment Payment { get; set; } = new();
    public bool IsReversal { get; set; } = false;
}

/// <summary>
/// One generated 835 envelope. Persisted by the caller as an
/// <see cref="EraEnvelopeRecord"/> and exposed by
/// <c>EraEnvelopesController</c>. The <c>IsReversal</c> flag is the
/// caller's signal to set <see cref="EraEnvelopeRecord.ReversalRunId"/>
/// rather than <see cref="EraEnvelopeRecord.PaymentRunId"/>.
/// </summary>
public record EraEnvelope(
    string TradingPartnerId,
    string EdiContent,
    int ClaimCount,
    decimal TotalPaymentAmount,
    string ControlNumber,
    IReadOnlyList<string> ClaimIds,
    bool IsReversal);

public class BatchEraGeneratorService : IBatchEraGeneratorService
{
    private readonly ILogger<BatchEraGeneratorService> _logger;

    public BatchEraGeneratorService(ILogger<BatchEraGeneratorService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<EraEnvelope> GenerateBatch(
        IEnumerable<EraPaymentInput> inputs,
        IReadOnlyDictionary<string, TradingPartnerInfo> tradingPartnersById)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(tradingPartnersById);

        var byPartner = inputs
            .Where(i => i.Payment is not null)
            .GroupBy(i => i.TradingPartnerId ?? string.Empty);

        var output = new List<EraEnvelope>();

        foreach (var group in byPartner)
        {
            var tradingPartnerId = group.Key;
            if (string.IsNullOrEmpty(tradingPartnerId))
            {
                _logger.LogWarning(
                    "Batched 835 generation: skipping {Count} payments with missing TradingPartnerId",
                    group.Count());
                continue;
            }

            if (!tradingPartnersById.TryGetValue(tradingPartnerId, out var tp) || tp is null)
            {
                _logger.LogWarning(
                    "Batched 835 generation: trading partner {TradingPartnerId} not resolved; skipping {Count} payments",
                    tradingPartnerId, group.Count());
                continue;
            }

            var envelope = BuildEnvelope(tradingPartnerId, group.ToList(), tp);
            output.Add(envelope);
        }

        return output;
    }

    private EraEnvelope BuildEnvelope(
        string tradingPartnerId,
        IReadOnlyList<EraPaymentInput> inputs,
        TradingPartnerInfo tp)
    {
        var now = DateTime.UtcNow;
        var controlNumber = GenerateControlNumber(now);
        var sb = new StringBuilder();
        int segmentCount = 0;

        var totalAmount = inputs.Sum(i => i.Payment.TotalPaymentAmount);
        var claimCount = inputs.Sum(i => i.Payment.ClaimPayments.Count);
        var claimIds = inputs
            .SelectMany(i => i.Payment.ClaimPayments.Select(cp => cp.ClaimId))
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var first = inputs[0].Payment;
        var paymentMethod = first.PaymentMethod;
        var paymentDate = first.PaymentDate;
        var traceCheckNumber = first.CheckNumber;

        // ── ISA ────────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, false,
            $"ISA*00*          *00*          " +
            $"*ZZ*{tp.InterchangeSenderId.PadRight(15)} " +
            $"*ZZ*{tp.InterchangeReceiverId.PadRight(15)} " +
            $"*{now:yyMMdd}*{now:HHmm}*^*00501*{controlNumber}*0*P*:~"));

        // ── GS ─────────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, false,
            $"GS*HP*{tp.ApplicationSenderId}*{tp.ApplicationReceiverId}" +
            $"*{now:yyyyMMdd}*{now:HHmm}*1*X*005010X221A1~"));

        // ── ST ─────────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, true, "ST*835*0001*005010X221A1~"));

        // ── BPR ─ Financial Information (envelope-wide) ────────────────
        var bprCode = totalAmount > 0 ? "C" : "I";
        var payMethod = paymentMethod switch
        {
            "CHK" => "CHK",
            "ACH" => "ACH",
            _     => "NON"
        };

        string bpr;
        if (payMethod == "ACH" && tp.PayerRoutingNumber is not null)
        {
            bpr = $"BPR*{bprCode}*{totalAmount:F2}*C*ACH" +
                  $"*CCP*01*{tp.PayerRoutingNumber}*DA*{tp.PayerAccountNumber ?? string.Empty}" +
                  $"*{FormatDate(paymentDate)}" +
                  $"*01*{tp.PayeeRoutingNumber ?? string.Empty}*DA*{tp.PayeeAccountNumber ?? string.Empty}" +
                  $"*{FormatDate(paymentDate)}~";
        }
        else if (payMethod == "CHK")
        {
            bpr = $"BPR*{bprCode}*{totalAmount:F2}*C*CHK" +
                  $"****{FormatDate(paymentDate)}~";
        }
        else
        {
            bpr = $"BPR*{bprCode}*{totalAmount:F2}*C*NON" +
                  $"****{FormatDate(paymentDate)}~";
        }
        sb.Append(Seg(ref segmentCount, true, bpr));

        // ── TRN ─ Reassociation Trace Number (envelope) ────────────────
        sb.Append(Seg(ref segmentCount, true,
            $"TRN*1*{traceCheckNumber}*{first.PayerId ?? "1999999999"}~"));

        // ── DTM ─ Production Date ──────────────────────────────────────
        sb.Append(Seg(ref segmentCount, true,
            $"DTM*405*{now:yyyyMMdd}~"));

        // ── 1000A ─ Payer Identification ───────────────────────────────
        sb.Append(Seg(ref segmentCount, true,
            $"N1*PR*{Esc(first.PayerName)}*XV*{first.PayerId ?? "UNASSIGNED"}~"));

        // ── 1000B ─ Payee Identification ───────────────────────────────
        var payeeNpiQual = string.IsNullOrEmpty(first.PayeeNPI) ? "" : $"*XX*{first.PayeeNPI}";
        sb.Append(Seg(ref segmentCount, true,
            $"N1*PE*{Esc(first.PayeeName)}{payeeNpiQual}~"));

        // ── 2000 / 2100 loops — CLP per claim across all payments ──────
        foreach (var input in inputs)
        {
            foreach (var claimPay in input.Payment.ClaimPayments)
            {
                sb.Append(BuildClaimLoop(claimPay, ref segmentCount));
            }
        }

        // ── PLB ─ Provider Level Adjustments (across batch) ────────────
        var allPlbs = inputs.SelectMany(i => i.Payment.ProviderAdjustments).ToList();
        if (allPlbs.Any())
        {
            foreach (var chunk in allPlbs.Chunk(6))
            {
                var plbAdjustments = string.Concat(
                    chunk.Select(adj =>
                        $"*{adj.AdjustmentIdentifier}:{adj.ReferenceIdentification ?? string.Empty}*{adj.Amount:F2}"));

                var fiscalDate = chunk.First().FiscalPeriodEnd ?? now;
                sb.Append(Seg(ref segmentCount, true,
                    $"PLB*{first.PayeeNPI ?? "PROVIDER"}*{fiscalDate:yyyyMMdd}{plbAdjustments}~"));
            }
        }

        // ── SE ─ Transaction Set Trailer ───────────────────────────────
        sb.Append(Seg(ref segmentCount, true, $"SE*{segmentCount + 1}*0001~"));

        // ── GE / IEA ──────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, false, "GE*1*1~"));
        sb.Append(Seg(ref segmentCount, false, $"IEA*1*{controlNumber}~"));

        var edi = sb.ToString();

        _logger.LogInformation(
            "Generated batched 835 envelope for trading partner {TradingPartnerId}: {ClaimCount} claims, {PaymentCount} payments, ${Amount:F2}, control {Control}",
            tradingPartnerId, claimCount, inputs.Count, totalAmount, controlNumber);

        // All payments routed to the same partner-group share the
        // reversal-mode signal — operators don't mix reversal and non-
        // reversal payments in the same envelope. We mark the envelope
        // reversal-mode iff every payment in the group carries the flag,
        // so a malformed mixed batch fails closed (envelope persists as
        // PaymentRun, not ReversalRun). ReversalRunService always batches
        // pure reversal inputs.
        var isReversal = inputs.All(i => i.IsReversal);

        return new EraEnvelope(
            TradingPartnerId: tradingPartnerId,
            EdiContent: edi,
            ClaimCount: claimCount,
            TotalPaymentAmount: totalAmount,
            ControlNumber: controlNumber,
            ClaimIds: claimIds,
            IsReversal: isReversal);
    }

    private static string BuildClaimLoop(ClaimPayment cp, ref int segmentCount)
    {
        var sb = new StringBuilder();

        sb.Append(Seg(ref segmentCount, true,
            $"CLP*{cp.PatientControlNumber}*{cp.ClaimStatusCode}" +
            $"*{cp.ChargeAmount:F2}*{cp.PaymentAmount:F2}*{cp.PatientResponsibilityAmount:F2}" +
            $"*HM*{cp.PayerClaimControlNumber ?? cp.ClaimId}~"));

        if (!string.IsNullOrEmpty(cp.MemberId))
        {
            sb.Append(Seg(ref segmentCount, true,
                $"NM1*QC*1**{cp.MemberId}****MI*{cp.MemberId}~"));
        }

        if (!string.IsNullOrEmpty(cp.RenderingProviderNPI))
        {
            sb.Append(Seg(ref segmentCount, true,
                $"NM1*82*1*****XX*{cp.RenderingProviderNPI}~"));
        }

        if (cp.ClaimReceivedDate.HasValue)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"DTM*050*{FormatDate(cp.ClaimReceivedDate.Value)}~"));
        }

        // Header CAS (claim-level)
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

    private static string BuildServiceLineLoop(ServiceLinePayment sl, ref int segmentCount)
    {
        var sb = new StringBuilder();

        string svcCode = !string.IsNullOrEmpty(sl.RevenueCode)
            ? $"NU:{sl.RevenueCode}:{sl.ProcedureCode}"
            : $"HC:{sl.ProcedureCode}";

        sb.Append(Seg(ref segmentCount, true,
            $"SVC*{svcCode}*{sl.ChargeAmount:F2}*{sl.PaymentAmount:F2}**{sl.Units:G}~"));

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

    private static string Seg(ref int count, bool counted, string segment)
    {
        if (counted) count++;
        return segment;
    }

    private static string FormatDate(DateTime dt) => dt.ToString("yyyyMMdd");

    private static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("*", " ").Replace("~", " ").Replace(":", " ").Replace("\\", " ");
    }

    private static string GenerateControlNumber(DateTime now)
        => now.Ticks.ToString()[^9..].PadLeft(9, '0');
}
