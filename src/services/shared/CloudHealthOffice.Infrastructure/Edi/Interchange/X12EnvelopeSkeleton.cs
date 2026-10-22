using System.Security.Cryptography;
using System.Text;

namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>A file reduced to what its envelope check reads, with the size and hash of the bytes it came from.</summary>
public sealed class X12EnvelopeSkeletonResult
{
    /// <summary>
    /// Text that <see cref="X12EnvelopeReader.Read"/> reads to the same
    /// findings, counts, headers and GS06/ST02 control numbers as the full
    /// file. Pass it to <see cref="IX12InterchangeIntake.ReceiveAsync"/> as the
    /// content. It is small (a few segments per functional group), never the
    /// file itself, so <see cref="X12InterchangeEnvelope.RawText"/> of what it
    /// reads is not the file's text.
    /// </summary>
    public required string Skeleton { get; init; }

    /// <summary>Bytes read from the stream.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>SHA-256 of those bytes, lower-case hex.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>
/// Reads an X12 file as a stream and keeps only what the envelope check
/// (<see cref="X12EnvelopeReader"/>) looks at, so a file of any size gets the
/// same ISA/IEA validation, duplicate ISA13 check and TA1 without being held
/// in memory (docs/architecture/edi-operations.md §11).
///
/// <para>
/// The envelope check reads every ISA in full; GS, GE, TA1, IEA and ST
/// segments (their elements matter: GS01–GS08, GS06, ST01, ST02, IEA01–02);
/// and of every other segment only its id and whether a functional group was
/// open. Between two of the segments it keeps, the open-group state cannot
/// change, so a run of other segments is kept as its first segment's id. The
/// skeleton therefore grows with the number of groups and transaction sets,
/// not with the number of members or claims.
/// </para>
/// </summary>
public static class X12EnvelopeSkeleton
{
    /// <summary>Characters looked at for an ISA: the reader searches 300 past its tag.</summary>
    private const int IsaLookahead = 400;

    public static async Task<X12EnvelopeSkeletonResult> BuildAsync(Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var hashing = new HashingReadStream(content);
        using var reader = new StreamReader(hashing, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        var source = new X12TextWindow(reader);
        var skeleton = await BuildSkeletonAsync(source, ct);

        // The hash covers the whole file, including anything the envelope
        // check did not need to read (content after a malformed ISA).
        await source.DrainAsync(ct);
        return new X12EnvelopeSkeletonResult
        {
            Skeleton = skeleton,
            SizeBytes = hashing.BytesRead,
            Sha256 = hashing.HashHex(),
        };
    }

    private static async Task<string> BuildSkeletonAsync(X12TextWindow source, CancellationToken ct)
    {
        var skeleton = new StringBuilder();
        await source.SkipWhileAsync(c => char.IsWhiteSpace(c) || c == '\uFEFF', ct);
        var start = await source.PeekAsync(IsaLookahead, ct);
        if (!(start.Length > 3 && start.StartsWith("ISA", StringComparison.Ordinal)))
        {
            // Not an interchange: the reader decides from the first characters.
            skeleton.Append(start);
            return skeleton.ToString();
        }

        while (true)
        {
            var look = await source.PeekAsync(IsaLookahead, ct);
            var elementSeparator = look.Length > 3 ? look[3] : '\0';
            var seps = new List<int>(16);
            var limit = Math.Min(look.Length, 300);
            for (var i = 3; i < limit && seps.Count < 16; i++)
                if (look[i] == elementSeparator) seps.Add(i);

            if (elementSeparator == '\0' || seps.Count < 16 || seps[15] + 2 >= look.Length)
            {
                // The reader reports this ISA and stops reading the file.
                skeleton.Append(look);
                return skeleton.ToString();
            }

            var isaLength = seps[15] + 3;
            var terminator = look[seps[15] + 2];
            skeleton.Append(look, 0, isaLength);
            await source.SkipAsync(isaLength, ct);

            var next = await ReadBodyAsync(source, skeleton, elementSeparator, terminator, ct);
            if (next == BodyEnd.EndOfFile) return skeleton.ToString();
            if (next == BodyEnd.NewIsa) continue;

            // After an IEA: whitespace, then another ISA, the end, or content the reader reports.
            await source.SkipWhileAsync(c => char.IsWhiteSpace(c) || c == '\uFEFF', ct);
            var after = await source.PeekAsync(8, ct);
            if (after.Length == 0) return skeleton.ToString();
            if (IsIsaAt(after, 0)) continue;
            skeleton.Append(after);
            return skeleton.ToString();
        }
    }

    private enum BodyEnd { Trailer, NewIsa, EndOfFile }

    private static async Task<BodyEnd> ReadBodyAsync(X12TextWindow source, StringBuilder skeleton, char elementSeparator, char terminator, CancellationToken ct)
    {
        var inRun = false;
        while (true)
        {
            var (raw, terminated) = await source.ReadUntilAsync(terminator, ct);
            if (raw is null) return BodyEnd.EndOfFile;
            var seg = raw.Trim();
            if (seg.Length == 0)
            {
                if (!terminated) return BodyEnd.EndOfFile;
                continue;
            }

            if (IsIsaAt(seg, 0))
            {
                // A new interchange before this one's IEA: the reader reports it
                // and starts the next interchange at that ISA, with its own delimiters.
                var lead = raw.Length - raw.TrimStart().Length;
                // The outer loop copies the ISA into the skeleton.
                source.PushBack(raw[lead..] + (terminated ? terminator.ToString() : string.Empty));
                return BodyEnd.NewIsa;
            }

            var id = seg.Split(elementSeparator, 2)[0];
            switch (id)
            {
                case "GS" or "GE" or "TA1" or "ST" or "IEA":
                    skeleton.Append(seg).Append(terminator);
                    inRun = false;
                    if (id == "IEA") return BodyEnd.Trailer;
                    break;
                default:
                    if (!inRun)
                    {
                        // Only the id and the open-group state matter, and the
                        // state is the same until the next kept segment.
                        skeleton.Append(id).Append(elementSeparator).Append(terminator);
                        inRun = true;
                    }
                    break;
            }

            if (!terminated) return BodyEnd.EndOfFile;
        }
    }

    private static bool IsIsaAt(string s, int i) =>
        i + 3 < s.Length && s[i] == 'I' && s[i + 1] == 'S' && s[i + 2] == 'A' && !char.IsLetterOrDigit(s[i + 3]) && !char.IsWhiteSpace(s[i + 3]);

    /// <summary>Counts and hashes the bytes read through it.</summary>
    private sealed class HashingReadStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long BytesRead { get; private set; }

        public string HashHex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

        public override int Read(byte[] buffer, int offset, int count) => Record(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken);
            return Record(buffer.Span[..n]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Record(ReadOnlySpan<byte> read)
        {
            _hash.AppendData(read);
            BytesRead += read.Length;
            return read.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
