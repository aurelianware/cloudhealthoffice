using CloudHealthOffice.EncounterEngine.Domain;

namespace CloudHealthOffice.EncounterEngine.Services;

/// <summary>
/// Wraps one or more ST…SE encounter transactions inside an X12 ISA/GS/GE/IEA
/// interchange envelope.
///
/// Envelope format (005010X222A2 / 005010X223A3):
///   ISA — Interchange Control Header (fixed-width, 106 chars)
///   GS  — Functional Group Header
///     ST … SE  (repeated per encounter)
///   GE  — Functional Group Trailer
///   IEA — Interchange Control Trailer
/// </summary>
public class EncounterBatchBuilder : IEncounterBatchBuilder
{
    private static readonly string Eol = "\n";

    public EncounterBatch Build(IReadOnlyList<EncounterRecord> encounters, BatchEnvelope envelope)
    {
        if (encounters.Count == 0)
            throw new ArgumentException("At least one encounter is required to build a batch.", nameof(encounters));

        var now = DateTime.UtcNow;
        var date6  = now.ToString("yyMMdd");
        var date8  = now.ToString("yyyyMMdd");
        var time   = now.ToString("HHmm");
        var icn    = PadControlNumber(envelope.InterchangeControlNumber, 9);
        var gcn    = envelope.GroupControlNumber;

        var lines = new List<string>();

        // ── ISA — Interchange Control Header (fixed-width fields) ────────────
        // ISA is always exactly 106 characters + segment terminator
        lines.Add(
            $"ISA*00*          *00*          " +
            $"*ZZ*{Pad(envelope.SenderId, 15)}*ZZ*{Pad(envelope.ReceiverId, 15)}" +
            $"*{date6}*{time}*^*00501*{icn}*0*P*:");

        // ── GS — Functional Group Header ─────────────────────────────────────
        // GS01=HC for healthcare claims
        lines.Add($"GS*HC*{envelope.ApplicationSenderId}*{envelope.ApplicationReceiverId}*{date8}*{time}*{gcn}*X*005010X222A2");

        // ── ST…SE blocks ─────────────────────────────────────────────────────
        foreach (var encounter in encounters)
        {
            // Append each transaction set (already includes ST and SE)
            lines.Add(encounter.RawX12.TrimEnd());
        }

        // ── GE — Functional Group Trailer ────────────────────────────────────
        lines.Add($"GE*{encounters.Count}*{gcn}");

        // ── IEA — Interchange Control Trailer ────────────────────────────────
        lines.Add($"IEA*1*{icn}");

        var rawX12 = string.Join(Eol, lines) + Eol;

        return new EncounterBatch
        {
            BatchId      = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            TenantId     = envelope.TenantId,
            CreatedAt    = now,
            TransactionCount = encounters.Count,
            RawX12       = rawX12,
            EncounterControlNumbers = encounters.Select(e => e.EncounterControlNumber).ToList()
        };
    }

    private static string Pad(string value, int length) =>
        value.Length >= length ? value[..length] : value.PadRight(length);

    private static string PadControlNumber(string value, int length) =>
        value.Length >= length ? value[..length] : value.PadLeft(length, '0');
}
