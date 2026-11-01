using CloudHealthOffice.Infrastructure.Reporting;
using Xunit;

namespace CloudHealthOffice.Infrastructure.Tests.Reporting;

public class StandardReportsTests
{
    private sealed record Row(string Id, string Text, decimal Amount, DateTime At, bool? Flag);

    private static readonly ReportDefinition Definition =
        new("r", "Report", "d", "reports:x", [], ["Id", "Text", "Amount", "At", "Flag"]);

    [Fact]
    public void Csv_writes_the_columns_in_order_quotes_delimiters_and_neutralizes_formulas()
    {
        var csv = StandardReports.Csv(Definition.Columns,
            [new Row("1", "=SUM(A1)", 12.5m, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), null),
             new Row("2", "a, \"b\"", 0m, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), true)]);

        var lines = csv.TrimEnd().Split(Environment.NewLine);
        Assert.Equal("Id,Text,Amount,At,Flag", lines[0]);
        Assert.Equal("1,'=SUM(A1),12.5,2026-10-01T08:00:00Z,", lines[1]);
        Assert.Equal("2,\"a, \"\"b\"\"\",0,2026-10-02T00:00:00Z,true", lines[2]);
    }

    [Fact]
    public void A_column_the_row_does_not_have_is_an_error()
    {
        Assert.Throws<InvalidOperationException>(() => StandardReports.Csv<Row>(["Missing"], []));
    }

    [Fact]
    public void A_run_keeps_at_most_max_rows_and_says_it_was_cut()
    {
        var rows = Enumerable.Range(0, 5).Select(i => new Row(i.ToString(), "", 0, DateTime.UtcNow, null)).ToList();

        var run = ReportRun<Row>.Of(Definition, "u1", DateTime.UtcNow, rows, 3, new Dictionary<string, string?>());

        Assert.Equal((5, true, 3), (run.TotalCount, run.Truncated, run.Items.Count));
        Assert.Equal(Definition.Columns, run.Columns);
    }

    [Fact]
    public void Period_defaults_and_bounds()
    {
        var asOf = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        var (from, to, error) = StandardReports.Period(null, null, asOf, TimeSpan.FromDays(30), TimeSpan.FromDays(366));
        Assert.Equal((asOf.AddDays(-30), asOf, (string?)null), (from, to, error));
        Assert.NotNull(StandardReports.Period(asOf, asOf, asOf, TimeSpan.Zero, TimeSpan.FromDays(1)).Error);
        Assert.NotNull(StandardReports.Period(asOf.AddDays(-400), asOf, asOf, TimeSpan.Zero, TimeSpan.FromDays(366)).Error);
    }

    [Fact]
    public void Period_reads_a_date_without_an_offset_as_UTC_whatever_the_server_zone()
    {
        var asOf = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        var (from, to, _) = StandardReports.Period(
            new DateTime(2026, 3, 1), new DateTime(2026, 5, 1), asOf, TimeSpan.Zero, TimeSpan.FromDays(366));

        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), to);
        Assert.Equal(DateTimeKind.Utc, from.Kind);
    }
}
