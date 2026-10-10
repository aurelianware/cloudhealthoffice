namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>One TA1 segment read from a trading partner's interchange.</summary>
public sealed record ParsedTa1(
    string AcknowledgedControlNumber,
    string InterchangeDate,
    string InterchangeTime,
    string AckCode,
    string NoteCode)
{
    public string? NoteDescription => Ta1NoteCodes.Describe(NoteCode);
}

/// <summary>A TA1-carrying interchange from a trading partner.</summary>
public sealed class ParsedTa1Interchange
{
    /// <summary>The partner's ISA (ISA06 = partner, ISA08 = us).</summary>
    public required X12InterchangeHeader Header { get; init; }

    public required IReadOnlyList<ParsedTa1> Acknowledgments { get; init; }

    /// <summary>The interchange text as received.</summary>
    public required string RawText { get; init; }
}

/// <summary>Reads TA1 segments out of an inbound X12 file.</summary>
public static class Ta1Parser
{
    /// <exception cref="FormatException">The content is not X12, or holds no well-formed TA1.</exception>
    public static IReadOnlyList<ParsedTa1Interchange> Parse(string content)
    {
        var read = X12EnvelopeReader.Read(content);
        if (!read.IsReadable)
            throw new FormatException(read.UnreadableReason ?? "The file is not an X12 interchange.");

        var result = new List<ParsedTa1Interchange>();
        foreach (var env in read.Interchanges)
        {
            if (env.Header is null || !env.HasTa1Segments) continue;
            var h = env.Header;
            var acks = new List<ParsedTa1>();
            foreach (var raw in env.RawText.Split(h.SegmentTerminator))
            {
                var seg = raw.Trim();
                if (!seg.StartsWith("TA1" + h.ElementSeparator, StringComparison.Ordinal)) continue;
                var e = seg.Split(h.ElementSeparator);
                if (e.Length < 6)
                    throw new FormatException("A TA1 segment must have 5 elements.");
                if (e[4] is not (Ta1AckCodes.Accepted or Ta1AckCodes.AcceptedWithErrors or Ta1AckCodes.Rejected))
                    throw new FormatException($"TA104 '{Truncate(e[4])}' is not A, E or R.");
                if (e[5].Length != 3 || !e[5].All(char.IsAsciiDigit))
                    throw new FormatException($"TA105 '{Truncate(e[5])}' is not a 3-digit note code.");
                acks.Add(new ParsedTa1(e[1], e[2], e[3], e[4], e[5]));
            }
            if (acks.Count > 0)
                result.Add(new ParsedTa1Interchange { Header = h, Acknowledgments = acks, RawText = env.RawText });
        }

        if (result.Count == 0)
            throw new FormatException("The file contains no TA1 segment.");
        return result;
    }

    private static string Truncate(string s) => s.Length <= 8 ? s : s[..8];
}
