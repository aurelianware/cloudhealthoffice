using System.Runtime.CompilerServices;
using EnrollmentImportService.Models;

namespace EnrollmentImportService.Services.Edi;

/// <summary>What one unit of an 834 is: a subscriber with its dependents, or a dependent sent without its subscriber.</summary>
public enum EnrollmentUnitKind
{
    Subscriber,
    StandaloneDependent,
}

/// <summary>
/// One unit of work parsed from an 834 (edi-operations.md §11): a subscriber
/// loop with the dependents that follow it, or one standalone dependent.
/// <see cref="Batch"/> is an <see cref="Enrollment834"/> holding just that
/// unit, numbered from its place in the file.
/// </summary>
public sealed class ParsedEnrollmentUnit
{
    /// <summary>0-based, in the order units are applied: every subscriber unit in file order, then every standalone dependent.</summary>
    public required int Sequence { get; init; }

    public required EnrollmentUnitKind Kind { get; init; }

    /// <summary>"INS 3" or "dependent 2 without its subscriber", as the import records it.</summary>
    public required string FilePosition { get; init; }

    /// <summary>The subscriber id (REF*0F): units with the same key apply in file order.</summary>
    public required string OrderingKey { get; init; }

    public required Enrollment834 Batch { get; init; }
}

public interface IStreaming834Parser
{
    /// <summary>
    /// Reads an 834 as a stream and yields it unit by unit. Interchanges whose
    /// index (0-based, in file order) is not in <paramref name="acceptedInterchanges"/>
    /// are skipped (null: all). Throws <see cref="X12FormatException"/> when
    /// the content is not X12.
    /// </summary>
    IAsyncEnumerable<ParsedEnrollmentUnit> ParseAsync(Stream content, string fileName, IReadOnlyCollection<int>? acceptedInterchanges = null, CancellationToken ct = default);
}

/// <summary>
/// The 834 parser for files of any size. It reads segment by segment and hands
/// <see cref="Enrollment834EdiParser"/> one family at a time (the transaction
/// set's header, then a subscriber's INS loop and the dependent loops that
/// follow it), so the loop rules are the same as for a whole file.
///
/// <para>
/// Dependents are attached to a subscriber whose loop they follow, by REF*0F.
/// A dependent that is not (sent alone, before its subscriber, or after
/// another subscriber's loop) is held and yielded after every subscriber, as
/// the whole-file import processes standalone dependents last. The one
/// difference from <see cref="Enrollment834EdiParser.Parse"/>: a dependent
/// separated from its subscriber by another subscriber's loop is a standalone
/// dependent here, applied once its subscriber is on file, instead of being
/// attached to it.
/// </para>
/// </summary>
public sealed class Streaming834Parser : IStreaming834Parser
{
    public async IAsyncEnumerable<ParsedEnrollmentUnit> ParseAsync(
        Stream content, string fileName, IReadOnlyCollection<int>? acceptedInterchanges = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var header = new List<X12Segment>();
        List<X12Segment>? family = null;
        var held = new List<(Dependent Dependent, List<string> Warnings)>();
        var interchange = -1;
        var skipping = false;
        var sequence = 0;
        var subscriberIndex = 0;

        await foreach (var segment in X12SegmentStreamReader.ReadAsync(content, ct))
        {
            if (segment.Id == "ISA")
            {
                interchange++;
                skipping = acceptedInterchanges is not null && !acceptedInterchanges.Contains(interchange);
                continue;
            }
            if (skipping) continue;

            switch (segment.Id)
            {
                case "ST":
                    foreach (var unit in Flush()) yield return unit;
                    header = [segment];
                    break;
                case "SE":
                    foreach (var unit in Flush()) yield return unit;
                    break;
                case "INS":
                    if (family is not null && segment.Element(0) == "N")
                    {
                        family.Add(segment);
                    }
                    else
                    {
                        foreach (var unit in Flush()) yield return unit;
                        family = [segment];
                    }
                    break;
                default:
                    (family ?? header).Add(segment);
                    break;
            }
        }
        foreach (var unit in Flush()) yield return unit;

        for (var k = 0; k < held.Count; k++)
        {
            var (dependent, warnings) = held[k];
            yield return new ParsedEnrollmentUnit
            {
                Sequence = sequence++,
                Kind = EnrollmentUnitKind.StandaloneDependent,
                FilePosition = $"dependent {k + 1} without its subscriber",
                OrderingKey = dependent.SubscriberId ?? string.Empty,
                Batch = new Enrollment834
                {
                    FileName = fileName,
                    ParsedAt = DateTime.UtcNow,
                    TransactionCount = 1,
                    DependentEnrollments = [dependent],
                    DependentIndexOffset = k,
                    ParseWarnings = warnings,
                },
            };
        }

        IEnumerable<ParsedEnrollmentUnit> Flush()
        {
            if (family is null) yield break;
            var parsed = Enrollment834EdiParser.ParseSegments(header.Concat(family), fileName);
            family = null;

            var warnings = parsed.ParseWarnings;
            foreach (var subscriber in parsed.Enrollments)
            {
                yield return new ParsedEnrollmentUnit
                {
                    Sequence = sequence++,
                    Kind = EnrollmentUnitKind.Subscriber,
                    FilePosition = $"INS {subscriberIndex + 1}",
                    OrderingKey = subscriber.SubscriberId ?? string.Empty,
                    Batch = new Enrollment834
                    {
                        FileName = fileName,
                        ParsedAt = parsed.ParsedAt,
                        TransactionCount = 1 + subscriber.Dependents.Count,
                        Enrollments = [subscriber],
                        SubscriberIndexOffset = subscriberIndex,
                        ParseWarnings = warnings,
                    },
                };
                subscriberIndex++;
                warnings = [];
            }
            foreach (var dependent in parsed.DependentEnrollments)
            {
                held.Add((dependent, warnings));
                warnings = [];
            }
        }
    }
}
