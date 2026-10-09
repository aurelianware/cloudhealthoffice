using PremiumBillingService.Edi;
using PremiumBillingService.Models;

namespace PremiumBillingService.Tests.Remittance;

public class Edi820ParserTests
{
    [Fact]
    public void Parse_ReadsBprTrnPayerAndOrganizationRemittance()
    {
        var edi = Synthetic820.Single(new Synthetic820.Transaction { Amount = 1500.00m, Trace = "TRC-ORG-1" }
            .Organization()
            .Rmr("INV-GRP001-2026-03", 1000.00m, 1000.00m)
            .Coverage("20260301", "20260331")
            .Rmr("INV-GRP001-2026-02", 500.00m, 600.00m)
            .Adx(-100.00m, "52"));

        var advice = Edi820Parser.Parse(edi, "synthetic.820").Should().ContainSingle().Subject;

        advice.Source.Should().Be(RemittanceSource.X12820);
        advice.SourceFileName.Should().Be("synthetic.820");
        advice.TransactionHandlingCode.Should().Be("C");
        advice.PaymentAmount.Should().Be(1500.00m);
        advice.PaymentMethod.Should().Be("ACH");
        advice.PaymentDate.Should().Be(new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc));
        advice.TraceNumber.Should().Be("TRC-ORG-1");
        advice.PayerId.Should().Be("1512345678");
        advice.PayerName.Should().Be("SYNTHETIC EMPLOYER INC");
        advice.Warnings.Should().BeEmpty();

        advice.Items.Should().HaveCount(2);
        var first = advice.Items[0];
        first.LineNumber.Should().Be(1);
        first.Level.Should().Be(RemittanceLevel.Organization);
        first.ReferenceQualifier.Should().Be("IK");
        first.Reference.Should().Be("INV-GRP001-2026-03");
        first.Amount.Should().Be(1000.00m);
        first.BilledAmount.Should().Be(1000.00m);
        first.CoveragePeriodStart.Should().Be(new DateTime(2026, 3, 1));
        first.CoveragePeriodEnd.Should().Be(new DateTime(2026, 3, 31));
        var second = advice.Items[1];
        second.Adjustments.Should().ContainSingle().Which.Should().BeEquivalentTo(new RemittanceAdjustmentDetail { Amount = -100.00m, ReasonCode = "52" });
    }

    [Fact]
    public void Parse_ReadsIndividualRemittance()
    {
        var edi = Synthetic820.Single(new Synthetic820.Transaction { Amount = 750m }
            .Individual("1", "EMP-0001", "MBR-0001", "SAMPLE", "ALEX")
            .Rmr("INV-GRP001-2026-03", 450m, qualifier: "AZ")
            .Individual("2", "EMP-0002", "MBR-0002", "TEST", "JORDAN")
            .Rmr("INV-GRP001-2026-03", 300m, qualifier: "AZ"));

        var items = Edi820Parser.Parse(edi).Single().Items;

        items.Select(i => (i.Level, i.EntityNumber, i.EntityId, i.MemberId, i.MemberName, i.Amount)).Should().Equal(
            (RemittanceLevel.Individual, "1", "EMP-0001", "MBR-0001", "ALEX SAMPLE", 450m),
            (RemittanceLevel.Individual, "2", "EMP-0002", "MBR-0002", "JORDAN TEST", 300m));
    }

    [Fact]
    public void Parse_ReadsEveryTransactionSet()
    {
        var file = new Synthetic820()
            .Add(new Synthetic820.Transaction { Amount = 10m, Trace = "T1" }.Organization().Rmr("A", 10m))
            .Add(new Synthetic820.Transaction { Amount = 20m, Trace = "T2" }.Organization().Rmr("B", 20m))
            .ToString();

        Edi820Parser.Parse(file).Select(a => a.TraceNumber).Should().Equal("T1", "T2");
    }

    [Fact]
    public void Parse_UsesTheDelimitersDeclaredInIsa()
    {
        var edi = Synthetic820.Single(new Synthetic820.Transaction { Amount = 5m }.Organization().Rmr("INV-X", 5m))
            .Replace('*', '|').Replace('~', '\'');

        Edi820Parser.Parse(edi).Single().Items.Single().Reference.Should().Be("INV-X");
    }

    [Fact]
    public void Parse_WarnsWhenSeCountIsWrong()
    {
        var edi = Synthetic820.Single(new Synthetic820.Transaction { Amount = 5m }.Organization().Rmr("INV-X", 5m))
            .Replace("SE*10*0001", "SE*99*0001");

        Edi820Parser.Parse(edi).Single().Warnings.Should().ContainSingle().Which.Should().Contain("SE01 declares 99");
    }

    [Fact]
    public void Parse_ReadsBpr03Ref38AndCheckNumber_AndRoundsAmountsToCents()
    {
        var edi = Synthetic820.Single(new Synthetic820.Transaction { Amount = 10m, Trace = "77001", Method = "CHK", CreditDebit = "D", Group = "GRP009" }
                .Organization().Rmr("INV-X", 10m))
            .Replace("RMR*IK*INV-X**10.00", "RMR*IK*INV-X**10.005");

        var advice = Edi820Parser.Parse(edi).Single();

        advice.CreditDebitFlag.Should().Be("D");
        advice.GroupReference.Should().Be("GRP009");
        advice.CheckNumber.Should().Be("77001");
        advice.Items.Single().Amount.Should().Be(10.01m);
    }

    [Fact]
    public void Parse_RejectsMissingTrn()
    {
        var edi = Synthetic820.Single(new Synthetic820.Transaction { Amount = 5m, Trace = "GONE" }.Organization().Rmr("INV-X", 5m))
            .Replace("TRN*1*GONE*1512345678~\n", string.Empty);

        FluentActions.Invoking(() => Edi820Parser.Parse(edi)).Should().Throw<FormatException>().WithMessage("*no TRN*");
    }

    [Fact]
    public void Parse_RejectsAnUnreadableAmount()
    {
        var edi = Synthetic820.Single(new Synthetic820.Transaction { Amount = 5m }.Organization().Rmr("INV-X", 5m))
            .Replace("RMR*IK*INV-X**5.00", "RMR*IK*INV-X**FIVE");

        FluentActions.Invoking(() => Edi820Parser.Parse(edi)).Should().Throw<FormatException>().WithMessage("*RMR04 'FIVE'*");
    }

    [Fact]
    public void Parse_RejectsANon820()
    {
        var edi = Synthetic820.Single(new Synthetic820.Transaction { Amount = 5m }).Replace("ST*820*", "ST*835*");

        FluentActions.Invoking(() => Edi820Parser.Parse(edi)).Should().Throw<FormatException>().WithMessage("*not an 820*");
    }
}

public class LockboxCsvParserTests
{
    [Fact]
    public void Parse_GroupsRowsIntoChecks()
    {
        const string csv = """
            batch,item,deposit_date,check_number,payer_id,payer_name,check_amount,invoice_number,amount
            001,1,2026-03-06,10001,EMPLOYER-A,"Synthetic Employer, Inc.","1,500.00",INV-GRP001-2026-03,1000.00
            001,1,2026-03-06,10001,EMPLOYER-A,"Synthetic Employer, Inc.","1,500.00",INV-GRP001-2026-02,500.00
            001,2,03/06/2026,10002,EMPLOYER-B,,250.00,INV-GRP002-2026-03,$250.00
            """;

        var checks = LockboxCsvParser.Parse(csv, "lockbox.csv");

        checks.Should().HaveCount(2);
        var first = checks[0];
        first.Source.Should().Be(RemittanceSource.Lockbox);
        first.TraceNumber.Should().Be("LBX-0-20260306-001-1-10001");
        first.PayerId.Should().Be("EMPLOYER-A");
        first.PayerName.Should().Be("Synthetic Employer, Inc.");
        first.CheckNumber.Should().Be("10001");
        first.PaymentAmount.Should().Be(1500.00m);
        first.PaymentDate.Should().Be(new DateTime(2026, 3, 6));
        first.Items.Select(i => (i.LineNumber, i.Reference, i.Amount)).Should().Equal(
            (1, "INV-GRP001-2026-03", 1000.00m), (2, "INV-GRP001-2026-02", 500.00m));
        checks[1].PayerName.Should().BeNull();
        checks[1].Items.Single().Amount.Should().Be(250.00m);
    }

    [Fact]
    public void Parse_TraceIncludesLockboxDateAndCheck_SoNextDaysBatchNumbersDoNotCollide()
    {
        const string csv = """
            lockbox,batch,item,deposit_date,check_number,payer_id,check_amount,invoice_number,amount,group_number
            LB7,001,1,2026-03-05,70001,EMPLOYER-A,100.004,INV-1,100.004,GRP001
            LB7,001,1,2026-03-06,70002,EMPLOYER-A,50.00,INV-2,50.00,GRP001
            """;

        var checks = LockboxCsvParser.Parse(csv);

        checks.Select(c => c.TraceNumber).Should().Equal("LBX-LB7-20260305-001-1-70001", "LBX-LB7-20260306-001-1-70002");
        checks[0].PaymentAmount.Should().Be(100.00m);
        checks[0].GroupReference.Should().Be("GRP001");
    }

    [Fact]
    public void Parse_RejectsAnInconsistentCheckAmount()
    {
        const string csv = """
            batch,item,deposit_date,check_number,payer_id,check_amount,invoice_number,amount
            001,1,2026-03-06,10001,EMPLOYER-A,100.00,INV-1,50.00
            001,1,2026-03-06,10001,EMPLOYER-A,200.00,INV-2,50.00
            """;

        FluentActions.Invoking(() => LockboxCsvParser.Parse(csv)).Should().Throw<FormatException>().WithMessage("Line 3:*");
    }

    [Fact]
    public void Parse_RejectsAMissingColumn()
    {
        FluentActions.Invoking(() => LockboxCsvParser.Parse("batch,item\n1,1\n")).Should().Throw<FormatException>().WithMessage("*missing*");
    }
}
