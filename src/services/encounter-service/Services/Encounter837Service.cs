using System.Text;
using EncounterService.Models;

namespace EncounterService.Services;

/// <summary>
/// Generates X12 005010X222A1 (837P) / 005010X223A3 (837I) encounter transactions
/// for submission to trading partners / payers.
///
/// Segment hierarchy (837P Professional):
///   ISA   — Interchange control header
///   GS    — Functional group header (GS01=HC)
///   ST    — Transaction set header (837)
///   BHT   — Beginning of hierarchical transaction
///   1000A — Submitter name (NM1*41)
///   1000B — Receiver name (NM1*40)
///   2000A — Billing Provider HL
///   2010AA — Billing Provider name/NPI
///   2000B — Subscriber HL
///   2010BA — Subscriber name
///   2010BB — Payer name
///   2300  — Claim information (CLM)
///   2400  — Service line detail (SV1/SV2 + DTP)
///   SE    — Transaction set trailer
///   GE    — Functional group trailer
///   IEA   — Interchange control trailer
/// </summary>
public interface IEncounter837Service
{
    /// <summary>
    /// Generate an X12 837 transaction for the given encounter.
    /// Returns the raw EDI string ready for transmission.
    /// </summary>
    string Generate837(Encounter encounter, Encounter837Config config);
}

/// <summary>
/// Configuration for 837 generation — trading-partner / submitter identifiers.
/// </summary>
public class Encounter837Config
{
    public string InterchangeSenderId   { get; set; } = "SENDER";
    public string InterchangeReceiverId { get; set; } = "RECEIVER";
    public string ApplicationSenderId   { get; set; } = "SENDER";
    public string ApplicationReceiverId { get; set; } = "RECEIVER";
    public string SubmitterName         { get; set; } = "Cloud Health Office";
    public string SubmitterContactName  { get; set; } = "EDI Department";
    public string SubmitterContactPhone { get; set; } = "5555555555";
}

public class Encounter837Service : IEncounter837Service
{
    private readonly ILogger<Encounter837Service> _logger;

    public Encounter837Service(ILogger<Encounter837Service> logger)
    {
        _logger = logger;
    }

    public string Generate837(Encounter encounter, Encounter837Config cfg)
    {
        var now = DateTime.UtcNow;
        var controlNumber = GenerateControlNumber(now);
        var sb = new StringBuilder();
        int segmentCount = 0;
        int hlCount = 0;

        // ── ISA ────────────────────────────────────────────────────────
        // ISA06/ISA08 must be exactly 15 characters; truncate if longer, pad if shorter
        var senderId   = FormatIsaId(cfg.InterchangeSenderId);
        var receiverId = FormatIsaId(cfg.InterchangeReceiverId);
        sb.Append(Seg(ref segmentCount, false,
            $"ISA*00*          *00*          " +
            $"*ZZ*{senderId}" +
            $"*ZZ*{receiverId}" +
            $"*{now:yyMMdd}*{now:HHmm}*^*00501*{controlNumber}*0*P*:~"));

        // ── GS — HC = Health Care Claim ────────────────────────────────
        var gsVersionId = encounter.EncounterType == EncounterType.Institutional
            ? "005010X223A3" : "005010X222A1";
        sb.Append(Seg(ref segmentCount, false,
            $"GS*HC*{cfg.ApplicationSenderId}*{cfg.ApplicationReceiverId}" +
            $"*{now:yyyyMMdd}*{now:HHmm}*1*X*{gsVersionId}~"));

        // ── ST ─────────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, true, $"ST*837*0001*{gsVersionId}~"));

        // ── BHT ────────────────────────────────────────────────────────
        // BHT01=0019 (original/corrected claim), BHT02=00 (original) or CH (chargeable)
        sb.Append(Seg(ref segmentCount, true,
            $"BHT*0019*00*{encounter.EncounterControlNumber}*{now:yyyyMMdd}*{now:HHmm}*CH~"));

        // ── 1000A — Submitter ──────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, true,
            $"NM1*41*2*{Esc(cfg.SubmitterName)}*****46*{cfg.ApplicationSenderId}~"));
        sb.Append(Seg(ref segmentCount, true,
            $"PER*IC*{Esc(cfg.SubmitterContactName)}*TE*{cfg.SubmitterContactPhone}~"));

        // ── 1000B — Receiver ───────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, true,
            $"NM1*40*2*{Esc(encounter.PayerName ?? encounter.PayerId)}*****46*{encounter.PayerId}~"));

        // ── 2000A — Billing Provider HL ────────────────────────────────
        int hlBillingProvider = ++hlCount;
        sb.Append(Seg(ref segmentCount, true, $"HL*{hlBillingProvider}**20*1~"));
        sb.Append(Seg(ref segmentCount, true,
            $"NM1*85*2*{Esc(encounter.BillingProviderName ?? encounter.BillingProviderNPI)}*****XX*{encounter.BillingProviderNPI}~"));

        // ── 2000B — Subscriber HL ──────────────────────────────────────
        int hlSubscriber = ++hlCount;
        sb.Append(Seg(ref segmentCount, true, $"HL*{hlSubscriber}*{hlBillingProvider}*22*0~"));
        sb.Append(Seg(ref segmentCount, true, "SBR*P*18*****MC~"));

        // 2010BA — Subscriber Name
        var subLastName  = Esc(encounter.SubscriberLastName  ?? encounter.PatientLastName  ?? string.Empty);
        var subFirstName = Esc(encounter.SubscriberFirstName ?? encounter.PatientFirstName ?? string.Empty);
        sb.Append(Seg(ref segmentCount, true,
            $"NM1*IL*1*{subLastName}*{subFirstName}****MI*{encounter.SubscriberId ?? encounter.MemberId}~"));

        // 2010BB — Payer Name
        sb.Append(Seg(ref segmentCount, true,
            $"NM1*PR*2*{Esc(encounter.PayerName ?? encounter.PayerId)}*****PI*{encounter.PayerId}~"));

        // ── 2300 — Claim Information ───────────────────────────────────
        // CLM01=EncounterControlNumber, CLM02=TotalCharge, CLM05=POS:B:FrequencyCode
        sb.Append(Seg(ref segmentCount, true,
            $"CLM*{encounter.EncounterControlNumber}*{encounter.TotalChargeAmount:F2}***" +
            $"{encounter.PlaceOfServiceCode}:B:{encounter.ClaimFrequencyCode}*Y*A*Y*Y~"));

        // REF*D9 — Claim Identifier (for corrections, reference original)
        if (encounter.SubmissionType == SubmissionType.Correction ||
            encounter.SubmissionType == SubmissionType.Resubmission)
        {
            if (!string.IsNullOrEmpty(encounter.OriginalEncounterControlNumber))
            {
                sb.Append(Seg(ref segmentCount, true,
                    $"REF*F8*{encounter.OriginalEncounterControlNumber}~"));
            }
        }

        // DTP*434 — Service date range
        if (encounter.ServiceDateFrom == encounter.ServiceDateTo)
        {
            sb.Append(Seg(ref segmentCount, true,
                $"DTP*434*D8*{encounter.ServiceDateFrom:yyyyMMdd}~"));
        }
        else
        {
            sb.Append(Seg(ref segmentCount, true,
                $"DTP*434*RD8*{encounter.ServiceDateFrom:yyyyMMdd}-{encounter.ServiceDateTo:yyyyMMdd}~"));
        }

        // HI — Diagnosis codes
        if (encounter.DiagnosisCodes.Count > 0)
        {
            var hiSegments = new List<string>();
            foreach (var dx in encounter.DiagnosisCodes)
            {
                hiSegments.Add($"{dx.CodeQualifier}:{dx.Code}");
            }
            sb.Append(Seg(ref segmentCount, true,
                $"HI*{string.Join("*", hiSegments)}~"));
        }

        // ── 2400 — Service Lines ───────────────────────────────────────
        foreach (var line in encounter.ServiceLines)
        {
            sb.Append(Seg(ref segmentCount, true, $"LX*{line.LineNumber}~"));

            if (encounter.EncounterType == EncounterType.Institutional)
            {
                // SV2 — Institutional service line; units formatted per X12 R data type (max 3 decimal places)
                var revCode = line.RevenueCode ?? "0001";
                sb.Append(Seg(ref segmentCount, true,
                    $"SV2*{revCode}*HC:{line.ProcedureCode}" +
                    $"{FormatModifiers(line.Modifiers)}" +
                    $"*{line.ChargeAmount:F2}*UN*{line.Units:0.###}~"));
            }
            else
            {
                // SV1 — Professional service line; units formatted per X12 R data type (max 3 decimal places)
                var dxPointers = line.DiagnosisPointers.Count > 0
                    ? string.Join(":", line.DiagnosisPointers)
                    : "1";
                sb.Append(Seg(ref segmentCount, true,
                    $"SV1*HC:{line.ProcedureCode}" +
                    $"{FormatModifiers(line.Modifiers)}" +
                    $"*{line.ChargeAmount:F2}*UN*{line.Units:0.###}***{dxPointers}~"));
            }

            // DTP*472 — Service date
            if (line.ServiceDateFrom == line.ServiceDateTo)
            {
                sb.Append(Seg(ref segmentCount, true,
                    $"DTP*472*D8*{line.ServiceDateFrom:yyyyMMdd}~"));
            }
            else
            {
                sb.Append(Seg(ref segmentCount, true,
                    $"DTP*472*RD8*{line.ServiceDateFrom:yyyyMMdd}-{line.ServiceDateTo:yyyyMMdd}~"));
            }
        }

        // ── SE ─────────────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, true, $"SE*{segmentCount}*0001~"));

        // ── GE / IEA ───────────────────────────────────────────────────
        sb.Append(Seg(ref segmentCount, false, "GE*1*1~"));
        sb.Append(Seg(ref segmentCount, false, $"IEA*1*{controlNumber}~"));

        _logger.LogInformation(
            "Generated 837 for encounter {ControlNumber} (type={Type}, submission={SubmissionType}): {SegmentCount} segments",
            encounter.EncounterControlNumber, encounter.EncounterType, encounter.SubmissionType, segmentCount);

        return sb.ToString();
    }

    private static string FormatModifiers(List<string> modifiers)
    {
        if (modifiers.Count == 0) return string.Empty;
        return ":" + string.Join(":", modifiers.Take(4));
    }

    /// <summary>
    /// Formats an ISA sender/receiver ID to exactly 15 characters as required by X12.
    /// Truncates values that exceed 15 characters; right-pads shorter values with spaces.
    /// </summary>
    private static string FormatIsaId(string value)
        => value.Length > 15 ? value[..15] : value.PadRight(15);

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
