using System.Globalization;

namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>The control numbers of one outbound interchange: ISA13/IEA02, each GS06/GE02, each ST02/SE02.</summary>
public sealed record X12InterchangeNumbers(
    string InterchangeControlNumber,
    IReadOnlyList<string> GroupControlNumbers,
    IReadOnlyList<string> TransactionSetControlNumbers);

/// <summary>Outbound content with its control numbers assigned, and the numbers per interchange.</summary>
public sealed record X12ControlNumberAssignment(string Content, IReadOnlyList<X12InterchangeNumbers> Interchanges);

/// <summary>
/// Assigns the control numbers of every interchange CHO sends
/// (docs/architecture/edi-operations.md §5). Builders write whatever
/// placeholder numbers they like; the assigned numbers replace them:
/// <list type="bullet">
/// <item>ISA13 / IEA02: the next value of the sequence for this tenant and
/// sender → receiver pair (the same sequence TA1s use), nine digits, wrapping
/// from 999999999 to 1, so it is never reused within the duplicate window;</item>
/// <item>GS06 / GE02: the next value of the sequence for this tenant and
/// application sender → receiver (GS02 → GS03);</item>
/// <item>ST02 / SE02: 0001, 0002, … within the interchange.</item>
/// </list>
/// </summary>
public interface IX12ControlNumberAllocator
{
    /// <summary>
    /// <paramref name="content"/> with fresh control numbers. Content with no
    /// readable ISA is returned unchanged with no interchanges. A failure of
    /// the sequence store is thrown: an interchange must not go out with a
    /// number that may repeat.
    /// </summary>
    Task<X12ControlNumberAssignment> AssignAsync(string tenantId, string content, CancellationToken ct = default);
}

public sealed class X12ControlNumberAllocator : IX12ControlNumberAllocator
{
    private readonly IX12InterchangeStore _store;

    public X12ControlNumberAllocator(IX12InterchangeStore store) => _store = store;

    /// <summary>The ISA13 sequence for an interchange from <paramref name="senderKey"/> to <paramref name="receiverKey"/> (qualifier:id).</summary>
    public static string InterchangeSequenceKey(string senderKey, string receiverKey) => $"{senderKey}>{receiverKey}";

    /// <summary>The GS06 sequence for a functional group from GS02 to GS03.</summary>
    public static string GroupSequenceKey(string applicationSender, string applicationReceiver) =>
        $"GS:{applicationSender}>{applicationReceiver}";

    public async Task<X12ControlNumberAssignment> AssignAsync(string tenantId, string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var shapes = X12ControlNumberStamper.Shape(content);
        // Content with no interchange, or whose envelope segments are too short
        // to carry their numbers, has nothing to assign: it goes out as built
        // rather than taking numbers from the sequence that it cannot carry.
        if (shapes.Count == 0 || X12ControlNumberStamper.Apply(content, Placeholders(shapes)) is null)
            return new X12ControlNumberAssignment(content, []);

        var numbers = new List<X12InterchangeNumbers>(shapes.Count);
        foreach (var shape in shapes)
        {
            var isa13 = await _store.NextControlNumberAsync(tenantId, InterchangeSequenceKey(shape.SenderKey, shape.ReceiverKey), ct);
            var groups = new List<string>(shape.Groups.Count);
            foreach (var (sender, receiver) in shape.Groups)
                groups.Add((await _store.NextControlNumberAsync(tenantId, GroupSequenceKey(sender, receiver), ct))
                    .ToString(CultureInfo.InvariantCulture));
            var sets = Enumerable.Range(1, shape.TransactionSetCount)
                .Select(n => n.ToString("D4", CultureInfo.InvariantCulture))
                .ToList();
            numbers.Add(new X12InterchangeNumbers(isa13.ToString("D9", CultureInfo.InvariantCulture), groups, sets));
        }

        var stamped = X12ControlNumberStamper.Apply(content, numbers)
                      ?? throw new InvalidOperationException("The interchange's structure changed while its control numbers were assigned.");
        return new X12ControlNumberAssignment(stamped, numbers);
    }

    private static List<X12InterchangeNumbers> Placeholders(IReadOnlyList<X12InterchangeShape> shapes) =>
        shapes.Select(s => new X12InterchangeNumbers(
                "000000001",
                s.Groups.Select(_ => "1").ToList(),
                Enumerable.Range(1, s.TransactionSetCount).Select(n => n.ToString("D4", CultureInfo.InvariantCulture)).ToList()))
            .ToList();
}

/// <summary>What the control numbers of one interchange are assigned to.</summary>
public sealed record X12InterchangeShape(
    string SenderKey,
    string ReceiverKey,
    IReadOnlyList<(string Sender, string Receiver)> Groups,
    int TransactionSetCount);

/// <summary>Reads and rewrites the control numbers of outbound interchanges.</summary>
public static class X12ControlNumberStamper
{
    /// <summary>Each interchange's parties, functional groups (GS02, GS03) and transaction set count.</summary>
    public static IReadOnlyList<X12InterchangeShape> Shape(string? content)
    {
        var read = X12EnvelopeReader.Read(content);
        if (!read.IsReadable) return [];
        var shapes = new List<X12InterchangeShape>();
        foreach (var env in read.Interchanges)
        {
            if (env.Header is not { } h) continue;
            var groups = new List<(string, string)>();
            var sets = 0;
            foreach (var e in Segments(env.RawText, h).Select(s => s.Elements))
            {
                switch (e[0])
                {
                    case "GS":
                        groups.Add((At(e, 2), At(e, 3)));
                        break;
                    case "ST":
                        sets++;
                        break;
                }
            }
            shapes.Add(new X12InterchangeShape(h.SenderKey, h.ReceiverKey, groups, sets));
        }
        return shapes;
    }

    /// <summary>
    /// <paramref name="content"/> with <paramref name="numbers"/> written into
    /// ISA13/IEA02, GS06/GE02 and ST02/SE02, or null when the content does not
    /// have exactly that many interchanges, groups and transaction sets.
    /// </summary>
    public static string? Apply(string content, IReadOnlyList<X12InterchangeNumbers> numbers)
    {
        var read = X12EnvelopeReader.Read(content);
        var interchanges = read.Interchanges.Where(i => i.Header is not null).ToList();
        if (!read.IsReadable || interchanges.Count != numbers.Count) return null;

        // Rewrite from the last interchange back, so earlier offsets stay valid.
        var result = content;
        for (var i = interchanges.Count - 1; i >= 0; i--)
        {
            var env = interchanges[i];
            var stamped = ApplyOne(env.RawText, env.Header!, numbers[i]);
            if (stamped is null) return null;
            result = result[..env.Start] + stamped + result[(env.Start + env.RawText.Length)..];
        }
        return result;
    }

    private static string? ApplyOne(string raw, X12InterchangeHeader h, X12InterchangeNumbers n)
    {
        if (n.InterchangeControlNumber.Length != 9) return null;
        var segments = raw.Split(h.SegmentTerminator);
        var group = -1;
        var set = -1;
        var isa = false;
        for (var i = 0; i < segments.Length; i++)
        {
            var trimmed = segments[i].TrimStart();
            if (trimmed.Length == 0) continue;
            var lead = segments[i][..(segments[i].Length - trimmed.Length)];
            var e = trimmed.Split(h.ElementSeparator);
            switch (e[0])
            {
                case "ISA" when e.Length > 13:
                    e[13] = n.InterchangeControlNumber;
                    isa = true;
                    break;
                case "IEA" when e.Length > 2:
                    e[2] = n.InterchangeControlNumber;
                    break;
                case "GS" when e.Length > 6:
                    if (++group >= n.GroupControlNumbers.Count) return null;
                    e[6] = n.GroupControlNumbers[group];
                    break;
                case "GE" when e.Length > 2:
                    if (group < 0) return null;
                    e[2] = n.GroupControlNumbers[group];
                    break;
                case "ST" when e.Length > 2:
                    if (++set >= n.TransactionSetControlNumbers.Count) return null;
                    e[2] = n.TransactionSetControlNumbers[set];
                    break;
                case "SE" when e.Length > 2:
                    if (set < 0) return null;
                    e[2] = n.TransactionSetControlNumbers[set];
                    break;
                default:
                    continue;
            }
            segments[i] = lead + string.Join(h.ElementSeparator, e);
        }
        return isa && group + 1 == n.GroupControlNumbers.Count && set + 1 == n.TransactionSetControlNumbers.Count
            ? string.Join(h.SegmentTerminator, segments)
            : null;
    }

    /// <summary>The control numbers <paramref name="content"/> carries now (for a record of what was sent).</summary>
    public static IReadOnlyList<X12InterchangeNumbers> Read(string? content)
    {
        var read = X12EnvelopeReader.Read(content);
        if (!read.IsReadable) return [];
        var list = new List<X12InterchangeNumbers>();
        foreach (var env in read.Interchanges)
        {
            if (env.Header is not { } h) continue;
            var groups = new List<string>();
            var sets = new List<string>();
            foreach (var e in Segments(env.RawText, h).Select(s => s.Elements))
            {
                if (e[0] == "GS") groups.Add(At(e, 6));
                else if (e[0] == "ST") sets.Add(At(e, 2));
            }
            list.Add(new X12InterchangeNumbers(h.ControlNumber.Trim(), groups, sets));
        }
        return list;
    }

    private static IEnumerable<(string Raw, string[] Elements)> Segments(string raw, X12InterchangeHeader h) =>
        raw.Split(h.SegmentTerminator)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => (s, s.Split(h.ElementSeparator)));

    private static string At(string[] e, int i) => i < e.Length ? e[i].Trim() : string.Empty;
}
