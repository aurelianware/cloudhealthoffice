namespace EnrollmentImportService.Services.Edi;

/// <summary>
/// One X12 segment: the segment id (e.g. "ISA", "INS", "NM1") plus its
/// elements in order. Element 0 is always the segment id itself is
/// excluded here — <see cref="Element"/> indexes start at the first
/// element after the id, matching the EDI convention of NM101, NM102, etc.
/// minus one (0-based).
/// </summary>
public sealed class X12Segment
{
    public required string Id { get; init; }
    public required IReadOnlyList<string> Elements { get; init; }

    /// <summary>0-based element accessor. Returns null when the element is absent or empty.</summary>
    public string? Element(int index) =>
        index >= 0 && index < Elements.Count && Elements[index].Length > 0
            ? Elements[index]
            : null;
}

/// <summary>
/// Thrown when the input cannot be tokenized as X12 (e.g. missing/malformed ISA envelope).
/// </summary>
public sealed class X12FormatException(string message) : Exception(message);

/// <summary>A tokenized X12 file: its segments plus the delimiters detected from the ISA envelope.</summary>
public sealed class X12Document
{
    public required IReadOnlyList<X12Segment> Segments { get; init; }
    public required char ElementSeparator { get; init; }
    public required char ComponentSeparator { get; init; }
}

/// <summary>
/// Splits raw X12 EDI text into segments. Delimiters (element separator,
/// segment terminator, component separator) are auto-detected from the
/// ISA envelope per the X12 standard rather than assumed — real files
/// vary (e.g. "~" vs newline-terminated), and hardcoding a delimiter is
/// how a parser silently mis-splits a file that uses a different one.
/// Mirrors the delimiter-detection approach already proven in
/// containers/x12-parser/parse_x12.py for this repo's other X12 transactions.
/// </summary>
public static class X12Tokenizer
{
    public static X12Document Tokenize(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new X12FormatException("EDI content is empty.");
        }

        var trimmed = content.TrimStart();
        if (!trimmed.StartsWith("ISA", StringComparison.Ordinal))
        {
            throw new X12FormatException("EDI content does not start with an ISA segment.");
        }

        // The ISA segment is a fixed-width 106 characters (including the
        // trailing segment terminator): element separator is the char
        // immediately after "ISA", the component separator is ISA16
        // (element index 15), and the segment terminator is the very
        // next character after that.
        if (trimmed.Length < 106)
        {
            throw new X12FormatException("ISA segment is shorter than the required 106 characters.");
        }

        var elementSeparator = trimmed[3];
        var componentSeparator = trimmed[104];
        var segmentTerminator = trimmed[105];

        var rawSegments = trimmed.Split(segmentTerminator);
        var segments = new List<X12Segment>(rawSegments.Length);

        foreach (var raw in rawSegments)
        {
            // Segment terminators are sometimes followed by a newline for
            // readability — strip incidental whitespace, not meaningful content.
            var candidate = raw.Trim('\r', '\n', ' ', '\t');
            if (candidate.Length == 0)
            {
                continue;
            }

            var parts = candidate.Split(elementSeparator);
            var id = parts[0];
            var elements = parts.Length > 1 ? parts[1..] : [];
            segments.Add(new X12Segment { Id = id, Elements = elements });
        }

        return new X12Document
        {
            Segments = segments,
            ElementSeparator = elementSeparator,
            ComponentSeparator = componentSeparator
        };
    }

    /// <summary>Splits a composite element (e.g. "HC:99213:25") on the component separator.</summary>
    public static string[] SplitComponents(string element, char componentSeparator) =>
        element.Split(componentSeparator);
}
