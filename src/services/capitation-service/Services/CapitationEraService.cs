using System.Text;
using CapitationService.Models;

namespace CapitationService.Services;

/// <summary>
/// 835 Health Care Claim Payment / Advice (ERA) generator for capitation payments.
///
/// Specification: X12 005010X221A1 (ASC X12N 835)
///
/// Capitation 835s differ from fee-for-service 835s:
///   - No individual claim references (claims are tracking-only under capitation)
///   - CLP02 = "22" (capitation payment, not claim status)
///   - CLP06 = "CP" (capitation/HMO claim filing indicator)
///   - No SVC service lines (capitation is not per-service)
///   - CAS CO-45 for contractual withhold adjustments
///   - PLB segments for provider-level adjustments: withholds (WO), retro (72), etc.
///   - One CLP per member-month on the statement
///
/// Segment hierarchy:
///   ISA  — Interchange control header
///   GS   — Functional group header
///   ST   — Transaction set header (835)
///   BPR  — Financial information (payment method, total amount)
///   TRN  — Reassociation trace number (statement number)
///   DTM  — Production date
///   N1   — Payer identification (1000A loop — health plan)
///   N1   — Payee identification (1000B loop — provider)
///   [CLP — Member-month capitation] 2100 loop, one per member
///   [CAS — Withhold adjustment   ] within 2100
///   PLB  — Provider-level adjustments (withholds, retro, incentives)
///   SE   — Transaction set trailer
///   GE   — Functional group trailer
///   IEA  — Interchange control trailer
///
/// Usage notes:
///   - Segment terminator: ~
///   - Element separator: *
///   - Sub-element separator: :
///   - Segment count in SE01 includes ST and SE themselves
/// </summary>
public interface ICapitationEraService
{
    /// <summary>
    /// Generate an X12 005010X221A1 835 ERA for a capitation statement.
    /// Returns the raw EDI string ready for transmission or file storage.
    /// </summary>
    string Generate835ForStatement(
        CapitationStatement statement,
        CapitationContract contract,
        CapitationEraTradingPartnerInfo tradingPartner);
}

/// <summary>
/// Trading partner info for capitation 835 generation.
/// </summary>
public class CapitationEraTradingPartnerInfo
{
    public string InterchangeSenderId { get; set; } = "SENDER";
    public string InterchangeReceiverId { get; set; } = "RECEIVER";
    public string ApplicationSenderId { get; set; } = "SENDER";
    public string ApplicationReceiverId { get; set; } = "RECEIVER";
    /// <summary>Health plan name (N1*PR payer loop)</summary>
    public string PayerName { get; set; } = string.Empty;
    /// <summary>Health plan payer ID (N1*PR, NM109)</summary>
    public string PayerId { get; set; } = string.Empty;
    /// <summary>Payer's bank routing number (BPR07)</summary>
    public string? PayerRoutingNumber { get; set; }
    /// <summary>Payer's bank account number (BPR09)</summary>
    public string? PayerAccountNumber { get; set; }
    /// <summary>Provider's bank routing number (BPR12)</summary>
    public string? PayeeRoutingNumber { get; set; }
    /// <summary>Provider's bank account number (BPR14)</summary>
    public string? PayeeAccountNumber { get; set; }
}

public class CapitationEraService : ICapitationEraService
{
    private readonly ILogger<CapitationEraService> _logger;

    public CapitationEraService(ILogger<CapitationEraService> logger)
    {
        _logger = logger;
    }

    public string Generate835ForStatement(
        CapitationStatement statement,
        CapitationContract contract,
        CapitationEraTradingPartnerInfo tp)
    {
        var now = DateTime.UtcNow;
        var controlNumber = GenerateControlNumber(now);
        var sb = new StringBuilder();
        int segmentCount = 0;

        // ── ISA — Interchange Control Header ─────────────────────────────
        sb.Append(Seg(ref segmentCount, false,
            $"ISA*00*          *00*          " +
            $"*ZZ*{tp.InterchangeSenderId.PadRight(15)}" +
            $"*ZZ*{tp.InterchangeReceiverId.PadRight(15)}" +
            $"*{now:yyMMdd}*{now:HHmm}*^*00501*{controlNumber}*0*P*:~"));

        // ── GS — Functional Group Header ─────────────────────────────────
        sb.Append(Seg(ref segmentCount, false,
            $"GS*HP*{tp.ApplicationSenderId}*{tp.ApplicationReceiverId}" +
            $"*{now:yyyyMMdd}*{now:HHmm}*1*X*005010X221A1~"));

        // ── ST — Transaction Set Header ──────────────────────────────────
        sb.Append(Seg(ref segmentCount, true, "ST*835*0001*005010X221A1~"));

        // ── BPR — Financial Information ──────────────────────────────────
        var bprCode = statement.NetPayable > 0 ? "C" : "I";
        string bpr;
        if (tp.PayerRoutingNumber is not null && tp.PayeeRoutingNumber is not null)
        {
            // Full ACH EFT detail
            bpr = $"BPR*{bprCode}*{statement.NetPayable:F2}*C*ACH" +
                  $"*CCP*01*{tp.PayerRoutingNumber}*DA*{tp.PayerAccountNumber ?? string.Empty}" +
                  $"*{FormatDate(statement.PaymentDate ?? now)}" +
                  $"*01*{tp.PayeeRoutingNumber}*DA*{tp.PayeeAccountNumber ?? string.Empty}" +
                  $"*{FormatDate(statement.PaymentDate ?? now)}~";
        }
        else if (!string.IsNullOrEmpty(statement.CheckNumber))
        {
            bpr = $"BPR*{bprCode}*{statement.NetPayable:F2}*C*CHK" +
                  $"****{FormatDate(statement.PaymentDate ?? now)}~";
        }
        else
        {
            bpr = $"BPR*{bprCode}*{statement.NetPayable:F2}*C*NON" +
                  $"****{FormatDate(statement.PaymentDate ?? now)}~";
        }
        sb.Append(Seg(ref segmentCount, true, bpr));

        // ── TRN — Reassociation Trace Number ─────────────────────────────
        // TRN02 = statement number as the check/EFT trace
        sb.Append(Seg(ref segmentCount, true,
            $"TRN*1*{statement.StatementNumber}*{tp.PayerId}~"));

        // ── DTM — Production Date ────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, true,
            $"DTM*405*{now:yyyyMMdd}~"));

        // ── 1000A — Payer Identification (Health Plan) ───────────────────
        sb.Append(Seg(ref segmentCount, true,
            $"N1*PR*{Esc(tp.PayerName)}*XV*{tp.PayerId}~"));

        // ── 1000B — Payee Identification (Provider) ──────────────────────
        sb.Append(Seg(ref segmentCount, true,
            $"N1*PE*{Esc(statement.ProviderName)}*XX*{statement.ProviderNPI}~"));

        // ── 2100 loops — one CLP per member-month ────────────────────────
        foreach (var lineItem in statement.LineItems)
        {
            sb.Append(BuildMemberCapitationLoop(lineItem, contract, ref segmentCount));
        }

        // ── PLB — Provider Level Adjustments ─────────────────────────────
        // Emit PLB for withhold, retro adjustments, incentive payments, etc.
        var plbItems = new List<(string Code, string RefId, decimal Amount)>();

        // Withhold amount (held back from gross)
        if (statement.WithholdAmount > 0)
        {
            plbItems.Add(("WO", "WITHHOLD", -statement.WithholdAmount));
        }

        // Statement-level adjustments
        foreach (var adj in statement.Adjustments)
        {
            var plbCode = adj.Type switch
            {
                CapitationAdjustmentType.RetroEnrollment => "72",      // Capitation payment
                CapitationAdjustmentType.RetroDisenrollment => "72",   // Capitation payment
                CapitationAdjustmentType.RiskScoreUpdate => "72",
                CapitationAdjustmentType.RateCorrection => "72",
                CapitationAdjustmentType.WithholdRelease => "WO",      // Withholding
                CapitationAdjustmentType.IncentivePayment => "L6",     // Interest/incentive
                CapitationAdjustmentType.StopLossCredit => "FB",       // Forward balance
                _ => "72"
            };
            plbItems.Add((plbCode, Esc(adj.Description)?[..Math.Min(adj.Description.Length, 30)] ?? "", adj.Amount));
        }

        // PLB can carry up to 6 adjustment reason/amount pairs per segment
        if (plbItems.Count > 0)
        {
            foreach (var chunk in plbItems.Chunk(6))
            {
                var plbAdjustments = string.Concat(
                    chunk.Select(item =>
                        $"*{item.Code}:{Esc(item.RefId)}*{item.Amount:F2}"));

                sb.Append(Seg(ref segmentCount, true,
                    $"PLB*{statement.ProviderNPI}*{FormatDate(statement.CapitationPeriodEnd)}{plbAdjustments}~"));
            }
        }

        // ── SE — Transaction Set Trailer ─────────────────────────────────
        // SE01 = segment count including ST and SE
        sb.Append(Seg(ref segmentCount, true, $"SE*{segmentCount + 1}*0001~"));

        // ── GE / IEA ─────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, false, "GE*1*1~"));
        sb.Append(Seg(ref segmentCount, false, $"IEA*1*{controlNumber}~"));

        var era = sb.ToString();

        _logger.LogInformation(
            "Generated capitation 835 ERA for statement {StatementNumber}: {MemberCount} members, " +
            "{SegmentCount} segments, ${NetPayable:F2}",
            statement.StatementNumber, statement.LineItems.Count, segmentCount, statement.NetPayable);

        return era;
    }

    /// <summary>
    /// Build the 2100 CLP loop for a single member-month capitation line item.
    /// CLP02 = "22" (capitation payment status)
    /// CLP06 = "CP" (capitation claim filing indicator)
    /// No SVC service lines — capitation is not per-service.
    /// </summary>
    private static string BuildMemberCapitationLoop(
        CapitationLineItem li, CapitationContract contract, ref int segmentCount)
    {
        var sb = new StringBuilder();

        // CLP — Claim Payment Information (member-month capitation)
        // CLP01: Member ID (patient control number)
        // CLP02: "22" = capitation payment
        // CLP03: Gross amount (charge equivalent)
        // CLP04: Net amount (paid amount)
        // CLP05: 0 (no patient responsibility in capitation)
        // CLP06: "CP" = capitation claim filing indicator
        // CLP07: contract number as payer claim control number
        sb.Append(Seg(ref segmentCount, true,
            $"CLP*{li.MemberId}*22*{li.GrossAmount:F2}*{li.NetAmount:F2}*0*CP*{contract.ContractNumber}~"));

        // NM1 — Patient/Member Name
        if (!string.IsNullOrEmpty(li.MemberName))
        {
            // Parse member name (assumes "First Last" format)
            var nameParts = li.MemberName.Split(' ', 2);
            var firstName = nameParts.Length > 0 ? Esc(nameParts[0]) : "";
            var lastName = nameParts.Length > 1 ? Esc(nameParts[1]) : Esc(nameParts[0]);
            sb.Append(Seg(ref segmentCount, true,
                $"NM1*QC*1*{lastName}*{firstName}****MI*{li.MemberId}~"));
        }

        // DTM — Capitation period dates
        sb.Append(Seg(ref segmentCount, true,
            $"DTM*150*{FormatDate(li.AssignmentEffectiveDate)}~"));

        // CAS — Contractual adjustment for withhold (if any)
        // CO-45 = Charge exceeds fee schedule/maximum allowable (contractual obligation)
        if (li.WithholdAmount > 0)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"CAS*CO*45*{li.WithholdAmount:F2}~"));
        }

        // AMT — Supplemental amount: base PMPM before risk adjustment
        sb.Append(Seg(ref segmentCount, true,
            $"AMT*B6*{li.BasePMPM:F2}~"));

        // QTY — Risk score as supplemental quantity
        if (li.RiskScore != 1.0m)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"QTY*CA*{li.RiskScore:F4}~"));
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

    private static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("*", " ").Replace("~", " ").Replace(":", " ").Replace("\\", " ");
    }

    private static string GenerateControlNumber(DateTime now)
        => now.Ticks.ToString()[^9..].PadLeft(9, '0');
}
