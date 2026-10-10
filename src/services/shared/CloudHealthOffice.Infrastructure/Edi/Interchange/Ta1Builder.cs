using System.Globalization;

namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>The decision for one received interchange: TA104 and TA105.</summary>
public sealed record Ta1Decision(string AckCode, string NoteCode)
{
    public bool IsRejected => AckCode == Ta1AckCodes.Rejected;

    /// <summary>
    /// TA104/TA105 for an interchange's findings. The first finding is the
    /// note (X12 allows one TA105 per TA1). The interchange is rejected when
    /// any finding's code is not in <paramref name="notedCodes"/>, accepted
    /// with errors (E) when every finding is noted only, and accepted (A, 000)
    /// with no findings.
    /// </summary>
    public static Ta1Decision For(IReadOnlyList<X12EnvelopeFinding> findings, IReadOnlyCollection<string> notedCodes)
    {
        if (findings.Count == 0) return new Ta1Decision(Ta1AckCodes.Accepted, Ta1NoteCodes.NoError);

        var rejecting = findings.FirstOrDefault(f => !notedCodes.Contains(f.NoteCode));
        return rejecting is not null
            ? new Ta1Decision(Ta1AckCodes.Rejected, rejecting.NoteCode)
            : new Ta1Decision(Ta1AckCodes.AcceptedWithErrors, findings[0].NoteCode);
    }
}

/// <summary>
/// Builds the TA1 interchange returned for a received interchange: an
/// ISA/IEA from the receiver back to the sender (ISA05/06 and ISA07/08
/// swapped), one TA1 segment, and IEA01 = 0 (a TA1 is not in a functional group).
/// </summary>
public static class Ta1Builder
{
    public const char DefaultElementSeparator = '*';
    public const char DefaultComponentSeparator = ':';
    public const char DefaultSegmentTerminator = '~';
    public const char DefaultRepetitionSeparator = '^';

    public sealed class Options
    {
        /// <summary>ISA13/IEA02 of the TA1 interchange itself (1–999999999).</summary>
        public required long ControlNumber { get; init; }

        /// <summary>When the TA1 is produced (ISA09/ISA10, and TA102/TA103 when the received ones are unusable).</summary>
        public DateTime Now { get; init; } = DateTime.UtcNow;

        /// <summary>Sender used when the received ISA07/ISA08 are unusable.</summary>
        public string FallbackSenderQualifier { get; init; } = "ZZ";

        public string FallbackSenderId { get; init; } = "CHO";
    }

    /// <summary>Builds the TA1 interchange for <paramref name="received"/> (which must have a readable header).</summary>
    public static string Build(X12InterchangeEnvelope received, Ta1Decision decision, Options options)
    {
        var h = received.Header ?? throw new ArgumentException("A TA1 needs the received interchange's ISA header.", nameof(received));
        var findings = received.Findings.Select(f => f.NoteCode).ToHashSet();

        // Echo the received delimiters when they are sound, so the sender can read the TA1 with its own settings.
        var delimitersSound = !findings.Overlaps([Ta1NoteCodes.InvalidElementSeparator, Ta1NoteCodes.InvalidSegmentTerminator, Ta1NoteCodes.InvalidComponentSeparator]);
        var el = delimitersSound ? h.ElementSeparator : DefaultElementSeparator;
        var comp = delimitersSound ? h.ComponentSeparator : DefaultComponentSeparator;
        var term = delimitersSound && h.SegmentTerminator is not ('\n' or '\r') ? h.SegmentTerminator : DefaultSegmentTerminator;

        var version = h.Version is "00401" or "00501" ? h.Version : "00501";
        var isa11 = version == "00401"
            ? "U"
            : delimitersSound && !findings.Contains(Ta1NoteCodes.InvalidStandardsIdentifier) && h.E(11).Length == 1
                ? h.E(11)
                : DefaultRepetitionSeparator.ToString();
        if (isa11.Length == 1 && version != "00401" && (isa11[0] == el || isa11[0] == comp || isa11[0] == term))
            isa11 = DefaultRepetitionSeparator.ToString();

        // The TA1 goes from the received interchange's receiver back to its sender.
        var senderQual = Usable(h.E(7), 2, findings, Ta1NoteCodes.InvalidReceiverQualifier) ?? options.FallbackSenderQualifier;
        var senderId = Usable(h.E(8), 15, findings, Ta1NoteCodes.InvalidReceiverId) ?? options.FallbackSenderId;
        var receiverQual = Usable(h.E(5), 2, findings, Ta1NoteCodes.InvalidSenderQualifier) ?? "ZZ";
        var receiverId = string.IsNullOrWhiteSpace(h.E(6)) ? "UNKNOWN" : h.E(6).Trim();

        var usage = h.UsageIndicator is "P" or "T" ? h.UsageIndicator : "P";
        var control = options.ControlNumber.ToString("D9", CultureInfo.InvariantCulture);

        var ta101 = AckedControlNumber(h.ControlNumber);
        var ta102 = findings.Contains(Ta1NoteCodes.InvalidDate) ? options.Now.ToString("yyMMdd", CultureInfo.InvariantCulture) : h.Date;
        var ta103 = findings.Contains(Ta1NoteCodes.InvalidTime) ? options.Now.ToString("HHmm", CultureInfo.InvariantCulture) : h.Time;

        string[] isa =
        [
            "ISA", "00", new string(' ', 10), "00", new string(' ', 10),
            Pad(senderQual, 2), Pad(senderId, 15), Pad(receiverQual, 2), Pad(receiverId, 15),
            options.Now.ToString("yyMMdd", CultureInfo.InvariantCulture), options.Now.ToString("HHmm", CultureInfo.InvariantCulture),
            isa11, version, control, "0", usage, comp.ToString(),
        ];

        return string.Join(el, isa) + term
             + string.Join(el, "TA1", ta101, ta102, ta103, decision.AckCode, decision.NoteCode) + term
             + string.Join(el, "IEA", "0", control) + term;
    }

    /// <summary>TA101: the received ISA13, or the closest valid 9-digit form of it.</summary>
    internal static string AckedControlNumber(string isa13)
    {
        var digits = new string(isa13.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0) return "000000000";
        return digits.Length >= 9 ? digits[^9..] : digits.PadLeft(9, '0');
    }

    private static string? Usable(string raw, int width, HashSet<string> findings, string code) =>
        findings.Contains(code) || string.IsNullOrWhiteSpace(raw) ? null : raw.Trim()[..Math.Min(raw.Trim().Length, width)];

    private static string Pad(string value, int width) =>
        value.Length >= width ? value[..width] : value.PadRight(width);
}
