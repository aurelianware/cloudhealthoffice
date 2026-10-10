using CloudHealthOffice.BankReconciliation.Bai2;
using CloudHealthOffice.BankReconciliation.Testing;
using static CloudHealthOffice.BankReconciliation.Testing.Bai2Builder;

namespace CloudHealthOffice.BankReconciliation.Tests;

public class Bai2ParserTests
{
    // A hand-written file in the layout banks send (control totals worked out by hand).
    private const string Sample =
        "01,SYNBANK01,CHOTENANT,261010,0600,17,80,10,2/\n" +
        "02,CHOTENANT,SYNBANK01,1,261009,2400,USD,2/\n" +
        "03,0000123456789,USD,010,100000,,,015,250000,,/\n" +
        "16,165,1250075,0,BNK0001,INV-2026-0042,ACME SYNTHETIC CO PREMIUM\n" +
        "88,FOR MARCH 2026, BATCH 7/\n" +
        "16,115,500000,S,300000,150000,50000,LBX0002,LB7788/\n" +
        "16,475,12345,V,261010,1200,CHK0003,1001/\n" +
        "49,2112420,6/\n" +
        "98,2112420,1,8/\n" +
        "99,2112420,1,10/\n";

    [Fact]
    public void ParsesRecords_ControlTotals_AndContinuations()
    {
        var file = Bai2Parser.Parse(Sample);

        file.SenderId.Should().Be("SYNBANK01");
        file.FileId.Should().Be("17");
        file.CreationDate.Should().Be(new DateOnly(2026, 10, 10));
        file.BankFileKey.Should().Be("SYNBANK01|CHOTENANT|20261010|17");
        var group = file.Groups.Single();
        group.AsOfDate.Should().Be(new DateOnly(2026, 10, 9));
        var account = group.Accounts.Single();
        account.MaskedAccountNumber.Should().Be("****6789");
        account.Summaries.Select(s => (s.TypeCode, s.Amount)).Should().Equal(("010", 100000L), ("015", 250000L));

        var ach = account.Transactions[0];
        ach.TypeCode.Should().Be("165");
        ach.Direction.Should().Be(Bai2Direction.Credit);
        ach.Amount.Should().Be(1250075);
        Bai2Parser.ToDecimal(ach.Amount, "USD").Should().Be(12500.75m);
        ach.BankReference.Should().Be("BNK0001");
        ach.CustomerReference.Should().Be("INV-2026-0042");
        ach.Text.Should().Be("ACME SYNTHETIC CO PREMIUM FOR MARCH 2026, BATCH 7");

        var lockbox = account.Transactions[1];
        lockbox.Availability.FundsType.Should().Be("S");
        (lockbox.Availability.ImmediateAmount, lockbox.Availability.OneDayAmount, lockbox.Availability.TwoOrMoreDayAmount)
            .Should().Be((300000L, 150000L, 50000L));
        lockbox.Text.Should().BeNull();

        var check = account.Transactions[2];
        check.Direction.Should().Be(Bai2Direction.Debit);
        check.Availability.ValueDate.Should().Be(new DateOnly(2026, 10, 10));
        check.CustomerReference.Should().Be("1001");
    }

    [Fact]
    public void TextMayHoldCommasAndSlashes()
    {
        var file = Bai2Parser.Parse(Single(new Tx("169", 100, CustomerReference: "REF1", Text: "PAYER A, B/C DIV")).Build());
        file.AllTransactions.Single().Text.Should().Be("PAYER A, B/C DIV");
    }

    [Fact]
    public void SeveralTextContinuations_AreJoined()
    {
        var content = Single(new Tx("165", 100, CustomerReference: "R", Text: "LINE ONE", TextContinuations: ["LINE TWO", "LINE THREE/"])).Build();
        Bai2Parser.Parse(content).AllTransactions.Single().Text.Should().Be("LINE ONE LINE TWO LINE THREE");
    }

    [Fact]
    public void ContinuationOfAnAccountRecord_ContinuesItsFields()
    {
        const string content =
            "01,B,R,261010,0600,1,,,2/\n" +
            "02,R,B,1,261009,,USD,/\n" +
            "03,99887766,USD,010,1000,,\n" +
            "88,015,2000,,/\n" +
            "49,3000,3/\n" +
            "98,3000,1,5/\n" +
            "99,3000,1,7/\n";

        var account = Bai2Parser.Parse(content).Groups.Single().Accounts.Single();
        account.Summaries.Select(s => s.TypeCode).Should().Equal("010", "015");
        account.RecordCount.Should().Be(3);
    }

    [Fact]
    public void DistributedAvailability_IsRead()
    {
        var content = Single(new Tx("301", 900, FundsType: "D", AvailabilityFields: "2,0,600,1,300", BankReference: "B", CustomerReference: "C")).Build();
        var t = Bai2Parser.Parse(content).AllTransactions.Single();
        t.Availability.Distributions.Should().Equal(new Bai2Distribution(0, 600), new Bai2Distribution(1, 300));
        t.BankReference.Should().Be("B");
    }

    [Theory]
    [InlineData("account")]
    [InlineData("group")]
    [InlineData("file")]
    public void ControlTotalMismatch_RejectsTheWholeFile(string level)
    {
        var builder = new Bai2Builder
        {
            Groups = [new Group { Accounts = [new Account { Transactions = [new Tx("165", 5000, CustomerReference: "X")] }] }],
            AccountTotalDelta = level == "account" ? 1 : 0,
            GroupTotalDelta = level == "group" ? 1 : 0,
            FileTotalDelta = level == "file" ? 1 : 0,
        };

        var act = () => Bai2Parser.Parse(builder.Build());

        var expected = level switch { "account" => "49 account control total", "group" => "98 group control total", _ => "99 file control total" };
        var ex = act.Should().Throw<Bai2FormatException>().Which;
        ex.Errors.Should().Contain(e => e.Contains(expected));
    }

    [Fact]
    public void AccountMismatchMessage_MasksTheAccountNumber()
    {
        var builder = new Bai2Builder
        {
            Groups = [new Group { Accounts = [new Account { Number = "5555444433332222" }] }],
            AccountTotalDelta = 7,
        };
        var ex = Assert.Throws<Bai2FormatException>(() => Bai2Parser.Parse(builder.Build()));
        ex.Message.Should().Contain("****2222").And.NotContain("5555444433332222");
    }

    [Fact]
    public void RecordCountMismatch_Rejects()
    {
        Assert.Throws<Bai2FormatException>(() => Bai2Parser.Parse(new Bai2Builder { Groups = [new Group { Accounts = [new Account()] }], AccountRecordDelta = 1 }.Build()))
            .Message.Should().Contain("49 number of records");
        Assert.Throws<Bai2FormatException>(() => Bai2Parser.Parse(new Bai2Builder { Groups = [new Group { Accounts = [new Account()] }], FileRecordDelta = -1 }.Build()))
            .Message.Should().Contain("99 number of records");
    }

    [Fact]
    public void SignedStatusAmounts_CountTowardsControlTotals()
    {
        var builder = new Bai2Builder
        {
            Groups = [new Group { Accounts = [new Account { Summaries = [("010", -50000), ("015", 20000)] }] }],
        };
        var file = Bai2Parser.Parse(builder.Build());
        file.ControlTotal.Should().Be(-30000);
    }

    [Theory]
    [InlineData("01,B,R,261010,0600,1,,,3/\n02,R,B,1,261009,,USD,/\n98,0,0,2/\n99,0,1,4/\n", "version number must be 2")]
    [InlineData("02,R,B,1,261009,,USD,/\n", "must start with an 01")]
    [InlineData("01,B,R,261010,0600,1,,,2/\n02,R,B,1,261009,,USD,/\n03,1234,USD/\n49,0,2/\n", "expected a 98")]
    [InlineData("01,B,R,261010,0600,1,,,2/\n02,R,B,1,261009,,USD,/\n03,1234,USD/\n16,165,12A,0,,/\n49,0,3/\n98,0,1,5/\n99,0,1,7/\n", "16 amount")]
    [InlineData("01,B,R,261310,0600,1,,,2/\n99,0,0,2/\n", "creation date")]
    [InlineData("01,B,R,261010,0600,1,,,2/\n77,X/\n99,0,0,2/\n", "unknown record code")]
    [InlineData("88,ORPHAN/\n", "has no record to continue")]
    public void MalformedFiles_AreRejected(string content, string expected)
    {
        Assert.Throws<Bai2FormatException>(() => Bai2Parser.Parse(content)).Message.Should().Contain(expected);
    }

    [Fact]
    public void FixedLengthRecordsWithoutLineBreaks_AreSplitByRecordLength()
    {
        var lines = Sample.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.PadRight(80)).ToList();
        var file = Bai2Parser.Parse(string.Concat(lines));
        file.AllTransactions.Should().HaveCount(3);
    }

    [Fact]
    public void MinorUnits_FollowTheCurrency()
    {
        Bai2Parser.ToDecimal(12345, "USD").Should().Be(123.45m);
        Bai2Parser.ToDecimal(12345, "JPY").Should().Be(12345m);
    }

    [Theory]
    [InlineData("123456789", "****6789")]
    [InlineData("12", "**")]
    [InlineData(null, "")]
    public void AccountMask_ShowsLastFourOnly(string? number, string masked) => AccountMask.Mask(number).Should().Be(masked);
}
