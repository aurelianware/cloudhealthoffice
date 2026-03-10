using System.Text;
using ClaimsService.Models;

namespace ClaimsService.Services;

/// <summary>
/// 277CA Health Care Claim Acknowledgment generator.
///
/// Specification: X12 005010X214 (ASC X12N 277)
///
/// Segment hierarchy:
///   ISA   — Interchange control header
///   GS    — Functional group header (GS01=HN)
///   ST    — Transaction set header (277)
///   BHT   — Beginning of hierarchical transaction
///   2000A — Information Source (Payer)     HL*1**20*1
///   2000B — Information Receiver           HL*2*1*21*1
///   2000C — Service Provider               HL*3*2*19*1  (with TRN)
///   2000D — Subscriber                     HL*4*3*22*0  (with STC status)
///   SE    — Transaction set trailer
///   GE    — Functional group trailer
///   IEA   — Interchange control trailer
///
/// STC01 composition: {CategoryCode}:{StatusCode}:{EntityCode}
///   Category codes: A=Acknowledged, F=Finalized, P=Pending, R=Rejected
///   Status codes (X12 Health Care Claim Status):
///     2   = Paid
///     4   = Denied
///     15  = In adjudication
///     16  = Pended (waiting for additional information)
///     19  = Not in system (voided/reversed)
///     20  = Accepted for adjudication
///     97  = Finalized - adjudicated
///   Entity code: 85 = Billing Provider
///   Action codes: WQ = Pending, U = Adjudication complete
/// </summary>
public interface IClaimAcknowledgmentService
{
    /// <summary>
    /// Generate an X12 005010X214 277CA for the given claim.
    /// Returns the raw EDI string ready for transmission.
    /// </summary>
    string Generate277CA(Claim claim, ClaimAcknowledgmentConfig config);
}

/// <summary>
/// Trading-partner / payer config needed to populate ISA, GS, and N1 segments.
/// Sourced from claims-service configuration (Ack:* keys) or DI.
/// </summary>
public class ClaimAcknowledgmentConfig
{
    public string InterchangeSenderId   { get; set; } = "SENDER";
    public string InterchangeReceiverId { get; set; } = "RECEIVER";
    public string ApplicationSenderId   { get; set; } = "SENDER";
    public string ApplicationReceiverId { get; set; } = "RECEIVER";
    public string PayerName             { get; set; } = "Cloud Health Office";
    public string PayerId               { get; set; } = "CHO";
    /// <summary>Payer originator application ID for TRN03 (typically payer's NPI or ID)</summary>
    public string PayerOriginatorId     { get; set; } = "CHO";
}

public class ClaimAcknowledgmentService : IClaimAcknowledgmentService
{
    private readonly ILogger<ClaimAcknowledgmentService> _logger;

    public ClaimAcknowledgmentService(ILogger<ClaimAcknowledgmentService> logger)
    {
        _logger = logger;
    }

    public string Generate277CA(Claim claim, ClaimAcknowledgmentConfig cfg)
    {
        var now = DateTime.UtcNow;
        var controlNumber = GenerateControlNumber(now);
        var sb = new StringBuilder();
        int segmentCount = 0;
        int hlCount = 0;

        // ── ISA ────────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, false,
            $"ISA*00*          *00*          " +
            $"*ZZ*{cfg.InterchangeSenderId.PadRight(15)}" +
            $"*ZZ*{cfg.InterchangeReceiverId.PadRight(15)}" +
            $"*{now:yyMMdd}*{now:HHmm}*^*00501*{controlNumber}*0*P*:~"));

        // ── GS — HN = Health Care Claim Status Notification ──────────
        sb.Append(Seg(ref segmentCount, false,
            $"GS*HN*{cfg.ApplicationSenderId}*{cfg.ApplicationReceiverId}" +
            $"*{now:yyyyMMdd}*{now:HHmm}*1*X*005010X214~"));

        // ── ST ─────────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, true, "ST*277*0001*005010X214~"));

        // ── BHT — Beginning of Hierarchical Transaction ───────────────
        // BHT01=0010 (claim status), BHT02=08 (response), BHT03=reference ID,
        // BHT04=date, BHT05=time
        sb.Append(Seg(ref segmentCount, true,
            $"BHT*0010*08*{claim.Id[..Math.Min(10, claim.Id.Length)]}*{now:yyyyMMdd}*{now:HHmm}~"));

        // ── 2000A — Information Source (Payer) ────────────────────────
        // HL03=20 (Information Source), HL04=1 (has children)
        int hlA = ++hlCount;
        sb.Append(Seg(ref segmentCount, true, $"HL*{hlA}**20*1~"));
        sb.Append(Seg(ref segmentCount, true,
            $"NM1*PR*2*{Esc(cfg.PayerName)}*****PI*{cfg.PayerId}~"));

        // ── 2000B — Information Receiver (Submitter / Billing Org) ───
        // HL03=21 (Information Receiver), HL04=1 (has children)
        int hlB = ++hlCount;
        sb.Append(Seg(ref segmentCount, true, $"HL*{hlB}*{hlA}*21*1~"));
        // NM1*41 = Submitter; use billing provider name as submitter org
        var submitterName = Esc(claim.BillingProviderName ?? claim.BillingProviderNPI);
        sb.Append(Seg(ref segmentCount, true,
            $"NM1*41*2*{submitterName}*****46*{claim.BillingProviderNPI}~"));

        // ── 2000C — Service Provider (Billing Provider) ──────────────
        // HL03=19 (Provider), HL04=1 (has children)
        int hlC = ++hlCount;
        sb.Append(Seg(ref segmentCount, true, $"HL*{hlC}*{hlB}*19*1~"));
        // NM1*1P = Provider; qualifier XX = NPI
        sb.Append(Seg(ref segmentCount, true,
            $"NM1*1P*2*{submitterName}*****XX*{claim.BillingProviderNPI}~"));
        // TRN — Reference trace number (claim ID)
        sb.Append(Seg(ref segmentCount, true,
            $"TRN*1*{claim.ClaimNumber}*{cfg.PayerOriginatorId}~"));

        // ── 2000D — Subscriber (Member / Patient) ────────────────────
        // HL03=22 (Subscriber), HL04=0 (no children — leaf)
        int hlD = ++hlCount;
        sb.Append(Seg(ref segmentCount, true, $"HL*{hlD}*{hlC}*22*0~"));

        // NM1*IL = Insured (subscriber/member)
        var lastName  = Esc(claim.PatientLastName  ?? claim.SubscriberLastName  ?? string.Empty);
        var firstName = Esc(claim.PatientFirstName ?? claim.SubscriberFirstName ?? string.Empty);
        sb.Append(Seg(ref segmentCount, true,
            $"NM1*IL*1*{lastName}*{firstName}****MI*{claim.MemberId}~"));

        // TRN — Submitter trace (claim number)
        sb.Append(Seg(ref segmentCount, true,
            $"TRN*2*{claim.ClaimNumber}*{cfg.PayerOriginatorId}~"));

        // ── STC — Claim Status ────────────────────────────────────────
        var (stc01, actionCode) = MapStatus(claim.Status);
        var stcDate = (claim.AdjudicatedDate ?? claim.ReceivedDate ?? claim.SubmittedDate).ToString("yyyyMMdd");
        sb.Append(Seg(ref segmentCount, true,
            $"STC*{stc01}*{stcDate}*{actionCode}*{claim.TotalChargeAmount:F2}~"));

        // REF*1K — Payer claim control number (if set via adjudication)
        if (!string.IsNullOrEmpty(claim.EDI835ControlNumber))
        {
            sb.Append(Seg(ref segmentCount, true,
                $"REF*1K*{claim.EDI835ControlNumber}~"));
        }

        // DTP*472 — Service date
        sb.Append(Seg(ref segmentCount, true,
            $"DTP*472*D8*{claim.ServiceDateFrom:yyyyMMdd}~"));

        // ── SE ─────────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, true, $"SE*{segmentCount}*0001~"));

        // ── GE / IEA ───────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, false, "GE*1*1~"));
        sb.Append(Seg(ref segmentCount, false, $"IEA*1*{controlNumber}~"));

        _logger.LogInformation(
            "Generated 277CA for claim {ClaimNumber} (status={Status}): {SegmentCount} segments",
            claim.ClaimNumber, claim.Status, segmentCount);

        return sb.ToString();
    }

    // ── Status mapping ────────────────────────────────────────────────

    /// <summary>
    /// Maps ClaimStatus to (STC01 composite, action code).
    ///
    /// STC01 = {CategoryCode}:{StatusCode}:{EntityCode}
    ///   85 = Billing Provider
    ///
    /// Status codes (X12 Health Care Claim Status Code List):
    ///   2  = Paid
    ///   4  = Denied
    ///   15 = In adjudication
    ///   16 = Pended
    ///   19 = Not in system
    ///   20 = Accepted for adjudication
    ///   97 = Adjudicated - finalized
    /// </summary>
    private static (string stc01, string actionCode) MapStatus(ClaimStatus status) =>
        status switch
        {
            ClaimStatus.Submitted      => ("A:20:85", "WQ"),  // Acknowledged, accepted for adjudication
            ClaimStatus.Received       => ("A:20:85", "WQ"),  // Acknowledged, accepted
            ClaimStatus.InAdjudication => ("P:15:85", "WQ"),  // Pending, in adjudication
            ClaimStatus.Pended         => ("P:16:85", "WQ"),  // Pending, additional info needed
            ClaimStatus.Approved       => ("F:97:85", "U"),   // Finalized, adjudicated
            ClaimStatus.PartiallyPaid  => ("F:97:85", "U"),   // Finalized, adjudicated (partial)
            ClaimStatus.Denied         => ("F:4:85",  "U"),   // Finalized, denied
            ClaimStatus.Paid           => ("F:2:85",  "U"),   // Finalized, paid
            ClaimStatus.Voided         => ("R:19:85", "U"),   // Rejected, not in system
            _                          => ("A:20:85", "WQ"),
        };

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
