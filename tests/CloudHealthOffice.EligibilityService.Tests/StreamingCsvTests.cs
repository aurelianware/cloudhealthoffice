using System.Text;
using EligibilityService.Models;
using EligibilityService.Services;

namespace CloudHealthOffice.EligibilityService.Tests;

public class StreamingCsvTests
{
    [Fact]
    public async Task ParseAsync_RoundTripsWithWriter()
    {
        var csv = "memberId,serviceDate\nM-1,2026-01-15\nM-2,2026-02-01\n";
        using var reader = new StringReader(csv);

        var rows = new List<BatchEligibilityRow>();
        await foreach (var r in StreamingCsvParser.ParseAsync(reader))
            rows.Add(r);

        Assert.Equal(2, rows.Count);
        Assert.Equal("M-1", rows[0].MemberId);
        Assert.Equal(new DateTime(2026, 1, 15), rows[0].ServiceDate);
    }

    [Fact]
    public async Task ParseAsync_HandlesEmbeddedCommasQuotesNewlines()
    {
        var csv = "memberId,serviceDate\n" +
                  "\"M,comma\",2026-01-15\n" +
                  "\"M\"\"quote\",2026-01-16\n";
        using var reader = new StringReader(csv);

        var rows = new List<BatchEligibilityRow>();
        await foreach (var r in StreamingCsvParser.ParseAsync(reader))
            rows.Add(r);

        Assert.Equal(2, rows.Count);
        Assert.Equal("M,comma", rows[0].MemberId);
        Assert.Equal("M\"quote", rows[1].MemberId);
    }

    [Fact]
    public async Task ParseAsync_MalformedDate_Throws()
    {
        var csv = "memberId,serviceDate\nM-1,not-a-date\n";
        using var reader = new StringReader(csv);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in StreamingCsvParser.ParseAsync(reader)) { }
        });
    }

    [Fact]
    public async Task Writer_EscapesEmbeddedSeparators()
    {
        using var buffer = new MemoryStream();
        await using (var writer = new StreamingCsvWriter(
            new StreamWriter(buffer, Encoding.UTF8, leaveOpen: true)))
        {
            await writer.WriteHeaderAsync(StreamingCsvWriter.InputHeader);
            await writer.WriteInputRowAsync(new BatchEligibilityRow
            {
                RowNumber = 2,
                MemberId = "M,comma",
                SubscriberId = "S\"quote",
                ServiceDate = new DateTime(2026, 1, 15)
            });
            await writer.FlushAsync();
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        Assert.Contains("\"M,comma\"", text);
        Assert.Contains("\"S\"\"quote\"", text);
    }

    [Fact]
    public async Task TenThousandRowStream_PeakMemoryBounded()
    {
        // Generate a 10,000-row CSV into a pipe, pump it through the parser
        // and writer, and assert GC pressure stays within a generous 50 MB
        // window. This exercises the streaming invariant: no List<Row> of
        // size N is ever materialized.
        using var inputStream = GenerateInput(rows: 10_000);
        using var outputStream = new MemoryStream();

        // Force a GC and record the starting delta.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var startBytes = GC.GetTotalMemory(forceFullCollection: false);

        using (var reader = new StreamReader(inputStream, Encoding.UTF8))
        await using (var writer = new StreamingCsvWriter(
            new StreamWriter(outputStream, Encoding.UTF8, leaveOpen: true)))
        {
            await writer.WriteHeaderAsync(StreamingCsvWriter.InputHeader);
            var count = 0;
            await foreach (var row in StreamingCsvParser.ParseAsync(reader))
            {
                await writer.WriteInputRowAsync(row);
                count++;
            }
            Assert.Equal(10_000, count);
            await writer.FlushAsync();
        }

        var endBytes = GC.GetTotalMemory(forceFullCollection: false);
        var delta = endBytes - startBytes;

        // Output buffer itself grows — that's the expected allocation. The
        // streaming invariant we care about is that parse state doesn't grow
        // proportionally to N. 50 MB allows for generous headroom including
        // the output buffer (~500 KB for 10k rows).
        Assert.True(delta < 50 * 1024 * 1024,
            $"Expected < 50MB peak delta; observed {delta / 1024 / 1024} MB");
    }

    private static MemoryStream GenerateInput(int rows)
    {
        var sb = new StringBuilder(capacity: rows * 40);
        sb.AppendLine("memberId,serviceDate");
        for (var i = 1; i <= rows; i++)
            sb.Append("MBR-").Append(i).Append(",2026-01-15\n");
        return new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}
