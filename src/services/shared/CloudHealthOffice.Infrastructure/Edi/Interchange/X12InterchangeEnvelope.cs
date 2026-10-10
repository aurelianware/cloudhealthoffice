namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>
/// The ISA header of one interchange, as received. Values are the raw
/// elements (fixed-width, space padded) unless a property says it is trimmed.
/// </summary>
public sealed class X12InterchangeHeader
{
    /// <summary>ISA01–ISA16, raw. Always 16 entries once a header is read.</summary>
    public required IReadOnlyList<string> Elements { get; init; }

    public required char ElementSeparator { get; init; }

    /// <summary>ISA16.</summary>
    public required char ComponentSeparator { get; init; }

    /// <summary>The character after ISA16.</summary>
    public required char SegmentTerminator { get; init; }

    /// <summary>1-based ISA element (ISA06 → <c>E(6)</c>), raw.</summary>
    public string E(int n) => n >= 1 && n <= Elements.Count ? Elements[n - 1] : string.Empty;

    public string SenderQualifier => E(5).Trim();
    public string SenderId => E(6).Trim();
    public string ReceiverQualifier => E(7).Trim();
    public string ReceiverId => E(8).Trim();
    public string Date => E(9);
    public string Time => E(10);
    public string Version => E(12);
    public string ControlNumber => E(13);
    public string AckRequested => E(14);
    public string UsageIndicator => E(15);

    /// <summary>The submitter, as the duplicate-control-number check keys it: <c>ISA05:ISA06</c>, trimmed.</summary>
    public string SenderKey => $"{SenderQualifier}:{SenderId}";
}

/// <summary>One envelope-level finding, carrying the TA105 note code it maps to.</summary>
public sealed record X12EnvelopeFinding(string NoteCode, string Message);

/// <summary>
/// One ISA…IEA interchange found in a file: its header, what the trailer
/// declared, what the body contained, and the envelope findings.
/// </summary>
public sealed class X12InterchangeEnvelope
{
    /// <summary>Null when the ISA could not be split into 16 elements.</summary>
    public X12InterchangeHeader? Header { get; init; }

    /// <summary>Offset of the ISA in the original content.</summary>
    public int Start { get; init; }

    /// <summary>The interchange's text, ISA through the IEA terminator (or to where it stopped).</summary>
    public string RawText { get; set; } = string.Empty;

    public List<X12EnvelopeFinding> Findings { get; } = [];

    /// <summary>GS segments counted between ISA and IEA.</summary>
    public int GroupCount { get; set; }

    /// <summary>IEA01 as received, null if absent.</summary>
    public string? DeclaredGroupCount { get; set; }

    /// <summary>IEA02 as received, null if absent.</summary>
    public string? TrailerControlNumber { get; set; }

    public bool HasTrailer { get; set; }

    /// <summary>GS01 of each functional group (e.g. HC, BE, HS, HN).</summary>
    public List<string> FunctionalIdentifierCodes { get; } = [];

    /// <summary>ST01 of each transaction set (e.g. 837, 834, 270).</summary>
    public List<string> TransactionSetIds { get; } = [];

    /// <summary>True when the interchange carries TA1 segments (an interchange acknowledgment).</summary>
    public bool HasTa1Segments { get; set; }

    /// <summary>ISA13 as the acknowledgment reports it: the header's when readable (code 001 says so).</summary>
    public string? ControlNumber => Header?.ControlNumber;

    public void Add(string noteCode, string message) => Findings.Add(new X12EnvelopeFinding(noteCode, message));
}

/// <summary>What <see cref="X12EnvelopeReader.Read"/> found in a file.</summary>
public sealed class X12EnvelopeReadResult
{
    public required IReadOnlyList<X12InterchangeEnvelope> Interchanges { get; init; }

    /// <summary>Set when the content has no ISA at all, so no TA1 can be addressed.</summary>
    public string? UnreadableReason { get; init; }

    public bool IsReadable => UnreadableReason is null && Interchanges.Count > 0;
}
