using EnrollmentImportService.Services.Edi;

namespace EnrollmentImportService.Tests.Support;

/// <summary>The real streaming parser, except that it throws <see cref="X12FormatException"/> while <see cref="Fail"/> is set.</summary>
public sealed class FlakyStreamingParser : IStreaming834Parser
{
    private readonly Streaming834Parser _inner = new();
    public string? Fail { get; set; }

    public IAsyncEnumerable<ParsedEnrollmentUnit> ParseAsync(Stream content, string fileName, IReadOnlyCollection<int>? acceptedInterchanges = null, CancellationToken ct = default) =>
        Fail is { } message ? Throw(message) : _inner.ParseAsync(content, fileName, acceptedInterchanges, ct);

#pragma warning disable CS1998 // an iterator that only throws
    private static async IAsyncEnumerable<ParsedEnrollmentUnit> Throw(string message)
    {
        throw new X12FormatException(message);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
#pragma warning restore CS1998
}
