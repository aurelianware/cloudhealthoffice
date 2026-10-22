using System.Runtime.CompilerServices;
using System.Text;
using CloudHealthOffice.Infrastructure.Edi.Interchange;

namespace EnrollmentImportService.Services.Edi;

/// <summary>
/// <see cref="X12Tokenizer"/> for a stream: the same delimiter detection and
/// segment rules, one segment at a time, so a file of any size is read in a
/// fixed amount of memory (edi-operations.md §11).
/// </summary>
public static class X12SegmentStreamReader
{
    private const int IsaLength = 106;

    public static async IAsyncEnumerable<X12Segment> ReadAsync(Stream content, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        var window = new X12TextWindow(reader);

        // Leading whitespace, as X12Tokenizer's TrimStart.
        await window.SkipWhileAsync(char.IsWhiteSpace, ct);
        if (window.Available == 0)
            throw new X12FormatException("EDI content is empty.");

        var head = await window.PeekAsync(IsaLength, ct);
        if (!head.StartsWith("ISA", StringComparison.Ordinal))
            throw new X12FormatException("EDI content does not start with an ISA segment.");
        if (head.Length < IsaLength)
            throw new X12FormatException("ISA segment is shorter than the required 106 characters.");

        var elementSeparator = head[3];
        var segmentTerminator = head[105];

        while (true)
        {
            var (raw, _) = await window.ReadUntilAsync(segmentTerminator, ct);
            if (raw is null) yield break;
            var candidate = raw.Trim('\r', '\n', ' ', '\t');
            if (candidate.Length == 0) continue;
            var parts = candidate.Split(elementSeparator);
            yield return new X12Segment { Id = parts[0], Elements = parts.Length > 1 ? parts[1..] : [] };
        }
    }
}
