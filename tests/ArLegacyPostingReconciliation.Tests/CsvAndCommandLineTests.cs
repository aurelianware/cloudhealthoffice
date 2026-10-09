using ArLegacyPostingReconciliation;

namespace ArLegacyPostingReconciliation.Tests;

/// <summary>The CSV format finance edits, and the command line, without a database.</summary>
public sealed class CsvAndCommandLineTests
{
    // ── CSV ────────────────────────────────────────────────────────────────

    [Fact]
    public void Csv_RoundTripsQuotesCommasNewlinesAndFormulaGuard()
    {
        var values = new[] { "a,b", "say \"hi\"", "=SUM(A1)", "-5", "+1-555", "@cmd", "\t=1+1", "\r=1+1", "two\nlines", "" };
        var line = Csv.Line(values);
        var parsed = Csv.Parse("﻿" + line + "\r\n").Single();

        parsed.Should().HaveCount(values.Length);
        parsed.Select(Csv.Unguard).Should().Equal(values);
        parsed[2].Should().Be("'=SUM(A1)");
        parsed[3].Should().Be("-5", "a plain number is not a formula");
        parsed[4].Should().StartWith("'");
        parsed[5].Should().StartWith("'");
        parsed[6].Should().StartWith("'\t", "a leading tab is guarded too");
        parsed[7].Should().StartWith("'\r", "a leading carriage return is guarded too");
    }

    [Theory]
    [InlineData("a,b\"c,d\n", "quote")]          // a quote inside an unquoted field
    [InlineData("a,\"b\"c,d\n", "after a closing quote")]
    [InlineData("a,\"b,d\n", "inside a quoted field")]
    [InlineData("a,b\rc,d\n", "carriage return")]
    public void Csv_Malformed_IsRejected(string text, string message)
    {
        var act = () => Csv.Parse(text);

        act.Should().Throw<FormatException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void Csv_BlankLinesAreSkipped_AndCrLfAccepted()
    {
        Csv.Parse("a,b\r\n\r\nc,d\r\n").Should().HaveCount(2);
    }

    private static string Header(params string[] columns) => Csv.Line(columns) + "\n";

    [Fact]
    public void Csv_UnexpectedOrRepeatedOrMissingColumns_AreRejected()
    {
        string[] expected = ["a", "b"];

        FluentActions.Invoking(() => Csv.ParseWithHeader(Header("a", "b", "extra") + "1,2,3\n", expected))
            .Should().Throw<FormatException>().WithMessage("*unexpected column*'extra'*");
        FluentActions.Invoking(() => Csv.ParseWithHeader(Header("a", "a", "b") + "1,1,2\n", expected))
            .Should().Throw<FormatException>().WithMessage("*more than once*");
        FluentActions.Invoking(() => Csv.ParseWithHeader(Header("a") + "1\n", expected))
            .Should().Throw<FormatException>().WithMessage("*lacks column*b*");
        FluentActions.Invoking(() => Csv.ParseWithHeader(Header("A", "b") + "1,2\n", expected))
            .Should().Throw<FormatException>("column names are exact");
        Csv.ParseWithHeader(Header("b", "a") + "2,1\n", expected).Single().Fields["a"].Should().Be("1");
    }

    [Theory]
    [InlineData("1,2,3\n")]
    [InlineData("1\n")]
    public void Csv_RowWithMoreOrFewerFieldsThanTheHeader_IsRejected(string row)
    {
        FluentActions.Invoking(() => Csv.ParseWithHeader(Header("a", "b") + row, ["a", "b"]))
            .Should().Throw<FormatException>().WithMessage("*field(s), the header has 2*");
    }

    // ── Command line ───────────────────────────────────────────────────────

    [Fact]
    public void CommandLine_ParsesOptionsAndSwitches()
    {
        var line = CommandLine.Parse(["reconcile", "--csv", "r.csv", "--execute", "--sha256=abc", "--operator", "ops", "--confirm-database", "db", "--tenant", "t1, t2"]);

        line.Command.Should().Be("reconcile");
        line["csv"].Should().Be("r.csv");
        line["sha256"].Should().Be("abc");
        line.Has("execute").Should().BeTrue();
        line.List("tenant").Should().Equal("t1", "t2");
        line["log"].Should().BeNull();
    }

    [Theory]
    [InlineData(new[] { "reconcile", "--Execute" }, "lower-case: did you mean --execute")]
    [InlineData(new[] { "reconcile", "--CSV", "x" }, "did you mean --csv")]
    [InlineData(new[] { "reconcile", "--exec" }, "Unknown option --exec")]
    [InlineData(new[] { "list", "--execute" }, "Unknown option --execute for 'list'")]
    [InlineData(new[] { "reconcile", "--csv" }, "--csv needs a value")]
    [InlineData(new[] { "reconcile", "--csv", "--execute" }, "--csv needs a value")]
    [InlineData(new[] { "reconcile", "--csv", "a", "--csv", "b" }, "more than once")]
    [InlineData(new[] { "reconcile", "--execute", "true" }, "Unexpected argument 'true'")]
    [InlineData(new[] { "reconcile", "--execute=false" }, "takes no value")]
    [InlineData(new[] { "reconcile", "-csv", "x" }, "Unexpected argument '-csv'")]
    [InlineData(new[] { "Reconcile" }, "did you mean 'reconcile'")]
    [InlineData(new[] { "apply" }, "Unknown command 'apply'")]
    [InlineData(new[] { "list", "--MongoDb:ConnectionString", "mongodb://x" }, "come from the environment")]
    public void CommandLine_Invalid_IsAClearError(string[] args, string message)
    {
        FluentActions.Invoking(() => CommandLine.Parse(args))
            .Should().Throw<ArgumentException>().Where(e => e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Main_WithAMisCasedFlag_ExitsWithUsage_NotAnException()
    {
        var exit = await Program.Main(["reconcile", "--csv", "x.csv", "--Execute"]);

        exit.Should().Be(2);
    }
}
