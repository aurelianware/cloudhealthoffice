using System.Text;
using ClaimsService.EDI.Florida.Models;

namespace ClaimsService.EDI.Florida;

/// <summary>
/// Assembles one or more <see cref="FmmisTransaction"/> instances into
/// FMMIS-compliant submission files with proper ISA/IEA interchange envelopes
/// and GS/GE functional groups (837P and 837I in separate groups).
///
/// <para>Each file follows the FMMIS naming convention
/// <c>FMMIS.{SubmitterId}.{yyyyMMdd_HHmmss}.dat</c> and contains at most
/// <see cref="DefaultMaxTransactionsPerFile"/> ST/SE transaction sets.</para>
/// </summary>
public class FmmisFileBuilder
{
    /// <summary>
    /// Default maximum number of ST/SE transaction sets per file.
    /// FMMIS recommends batches no larger than 5 000 encounters.
    /// </summary>
    public const int DefaultMaxTransactionsPerFile = 5000;

    private readonly ILogger<FmmisFileBuilder> _logger;

    public FmmisFileBuilder(ILogger<FmmisFileBuilder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Build one or more FMMIS submission files from the supplied transactions.
    /// Transactions are grouped by type (837P / 837I) into separate GS/GE
    /// functional groups within each file. If the total transaction count
    /// exceeds <paramref name="maxTransactionsPerFile"/>, multiple files are produced.
    /// </summary>
    /// <param name="transactions">Transformed FMMIS transactions (from <see cref="FmmisClaimTransformer"/>).</param>
    /// <param name="config">Tenant's FMMIS compliance configuration (submitter ID, etc.).</param>
    /// <param name="maxTransactionsPerFile">Maximum ST/SE envelopes per file (default 5 000).</param>
    /// <returns>One or more submission files ready for SFTP transmission.</returns>
    public IReadOnlyList<FmmisSubmissionFile> Build(
        IEnumerable<FmmisTransaction> transactions,
        FmmisComplianceConfigDto config,
        int maxTransactionsPerFile = DefaultMaxTransactionsPerFile)
    {
        var txList = transactions.ToList();
        if (txList.Count == 0)
        {
            return Array.Empty<FmmisSubmissionFile>();
        }

        var files = new List<FmmisSubmissionFile>();
        var chunks = Chunk(txList, maxTransactionsPerFile);

        foreach (var chunk in chunks)
        {
            var now = DateTime.UtcNow;
            var controlNumber = GenerateControlNumber(now);
            var fileName = GenerateFileName(config.FmmisSubmitterId, now);

            var ediContent = BuildInterchange(chunk, config, controlNumber, now);
            var contentBytes = Encoding.UTF8.GetBytes(ediContent);

            var file = new FmmisSubmissionFile
            {
                FileName = fileName,
                Content = contentBytes,
                TransactionCount = chunk.Count,
                ClaimIds = chunk.Select(t => t.ClaimNumber).ToList()
            };

            files.Add(file);

            _logger.LogInformation(
                "Built FMMIS file {FileName}: {TransactionCount} transactions, {Bytes} bytes",
                fileName, chunk.Count, contentBytes.Length);
        }

        return files.AsReadOnly();
    }

    // ── Interchange Builder ──────────────────────────────────────────

    /// <summary>
    /// Build a single ISA/IEA interchange containing GS/GE groups for each
    /// transaction type (837P, 837I). Pure function for testability.
    /// </summary>
    internal static string BuildInterchange(
        List<FmmisTransaction> transactions,
        FmmisComplianceConfigDto config,
        string controlNumber,
        DateTime now)
    {
        var sb = new StringBuilder(transactions.Count * 2048);

        // ── ISA ──────────────────────────────────────────────────────
        sb.Append(
            $"ISA*00*          *00*          " +
            $"*{FmmisCompanionGuide.IsaQualifier}*{config.FmmisSubmitterId.PadRight(15)}" +
            $"*{FmmisCompanionGuide.IsaQualifier}*{FmmisCompanionGuide.FmmisReceiverId.PadRight(15)}" +
            $"*{now:yyMMdd}*{now:HHmm}" +
            $"*{FmmisCompanionGuide.RepetitionSeparator}" +
            $"*{FmmisCompanionGuide.InterchangeVersion}" +
            $"*{controlNumber}*0" +
            $"*{FmmisCompanionGuide.ProductionIndicator}*:~");

        // ── Group by transaction type (separate GS/GE per type) ──────
        var groups = transactions
            .GroupBy(t => t.TransactionType)
            .OrderBy(g => g.Key) // 837I before 837P for deterministic output
            .ToList();

        int gsControlSeq = 0;

        foreach (var group in groups)
        {
            gsControlSeq++;
            var versionCode = group.Key == "837I"
                ? FmmisCompanionGuide.VersionCode837I
                : FmmisCompanionGuide.VersionCode837P;

            var txsInGroup = group.ToList();

            // ── GS ──────────────────────────────────────────────────
            sb.Append(
                $"GS*{FmmisCompanionGuide.FunctionalIdCode837}" +
                $"*{Esc(config.FmmisSubmitterId)}" +
                $"*{FmmisCompanionGuide.FmmisReceiverId}" +
                $"*{now:yyyyMMdd}*{now:HHmm}" +
                $"*{gsControlSeq}*X*{versionCode}~");

            // ── ST/SE transaction sets ───────────────────────────────
            int stSeq = 0;
            foreach (var tx in txsInGroup)
            {
                stSeq++;
                var stControlNumber = stSeq.ToString().PadLeft(4, '0');
                var innerSegments = ExtractInnerSegments(tx.RawEdi);
                // Segment count = inner + ST + SE
                var segCount = innerSegments.Count + 2;

                sb.Append($"ST*837*{stControlNumber}*{versionCode}~");

                foreach (var seg in innerSegments)
                {
                    sb.Append(seg);
                    sb.Append('~');
                }

                sb.Append($"SE*{segCount}*{stControlNumber}~");
            }

            // ── GE ──────────────────────────────────────────────────
            sb.Append($"GE*{txsInGroup.Count}*{gsControlSeq}~");
        }

        // ── IEA ──────────────────────────────────────────────────────
        sb.Append($"IEA*{groups.Count}*{controlNumber}~");

        return sb.ToString();
    }

    // ── Segment Extraction ───────────────────────────────────────────

    /// <summary>
    /// Extract the inner segments from a single-transaction EDI string,
    /// stripping the ISA, GS, ST, SE, GE, and IEA envelope segments.
    /// Returns only the content segments (BHT through the last service line).
    /// </summary>
    internal static List<string> ExtractInnerSegments(string rawEdi)
    {
        var segments = rawEdi.Split('~', StringSplitOptions.RemoveEmptyEntries);
        var inner = new List<string>();

        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0) continue;

            // Skip envelope segments — keep only content between ST and SE
            if (trimmed.StartsWith("ISA*") ||
                trimmed.StartsWith("GS*") ||
                trimmed.StartsWith("ST*") ||
                trimmed.StartsWith("SE*") ||
                trimmed.StartsWith("GE*") ||
                trimmed.StartsWith("IEA*"))
            {
                continue;
            }

            inner.Add(trimmed);
        }

        return inner;
    }

    // ── File Naming ──────────────────────────────────────────────────

    /// <summary>
    /// Generate the FMMIS-required file name:
    /// <c>FMMIS.{SubmitterId}.{yyyyMMdd_HHmmss}.dat</c>
    /// </summary>
    internal static string GenerateFileName(string submitterId, DateTime timestamp)
        => $"FMMIS.{submitterId}.{timestamp:yyyyMMdd_HHmmss}.dat";

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>Split a list into chunks of the specified size.</summary>
    private static List<List<T>> Chunk<T>(List<T> source, int chunkSize)
    {
        var chunks = new List<List<T>>();
        for (int i = 0; i < source.Count; i += chunkSize)
        {
            chunks.Add(source.GetRange(i, Math.Min(chunkSize, source.Count - i)));
        }
        return chunks;
    }

    /// <summary>Escape X12 delimiter characters.</summary>
    private static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("*", " ").Replace("~", " ").Replace(":", " ").Replace("\\", " ");
    }

    /// <summary>Generate a 9-digit control number from timestamp ticks.</summary>
    internal static string GenerateControlNumber(DateTime now)
        => now.Ticks.ToString()[^9..].PadLeft(9, '0');
}
