using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PremiumBillingService.Models;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Services;

public class NachaFileServiceTests
{
    private readonly NachaFileService _service;
    private readonly NachaFileOptions _defaultOptions;

    public NachaFileServiceTests()
    {
        var configuration = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<NachaFileService>>();
        _service = new NachaFileService(configuration, logger.Object);

        _defaultOptions = new NachaFileOptions
        {
            ImmediateDestination = "091000019",
            ImmediateOrigin = "1234567890",
            ImmediateDestinationName = "TEST BANK",
            ImmediateOriginName = "HEALTH PLAN CO",
            CompanyName = "HEALTH PLAN",
            CompanyId = "1234567890",
            OriginatingDfi = 9100001,
            CompanyEntryDescription = "PREMIUM"
        };
    }

    private static NachaEntryDetail CreateEntry(
        string routing = "091000019",
        string account = "123456789",
        decimal amount = 1500.00m,
        string groupNumber = "GRP001") => new()
    {
        RoutingNumber = routing,
        AccountNumber = account,
        AccountType = BankAccountType.Checking,
        Amount = amount,
        GroupNumber = groupNumber,
        IndividualName = "ACME CORP",
        IndividualId = groupNumber
    };

    [Fact]
    public void GenerateNachaFile_WithSingleEntry_ReturnsValidResult()
    {
        var entries = new List<NachaEntryDetail> { CreateEntry() };

        var result = _service.GenerateNachaFile(entries, _defaultOptions);

        result.EntryCount.Should().Be(1);
        result.TotalAmount.Should().Be(1500.00m);
        result.FileName.Should().Contain("ACH-");
        result.FileName.Should().EndWith(".ach");
        result.FileReference.Should().StartWith("NACHA-");
        result.FileContent.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GenerateNachaFile_WithMultipleEntries_SumsTotalAmount()
    {
        var entries = new List<NachaEntryDetail>
        {
            CreateEntry(amount: 1000.00m),
            CreateEntry(amount: 2500.50m),
            CreateEntry(amount: 750.25m)
        };

        var result = _service.GenerateNachaFile(entries, _defaultOptions);

        result.EntryCount.Should().Be(3);
        result.TotalAmount.Should().Be(4250.75m);
    }

    [Fact]
    public void GenerateNachaFile_AssignsTraceNumbers()
    {
        var entries = new List<NachaEntryDetail>
        {
            CreateEntry(groupNumber: "GRP001"),
            CreateEntry(groupNumber: "GRP002")
        };

        _service.GenerateNachaFile(entries, _defaultOptions);

        entries[0].TraceNumber.Should().NotBeNullOrEmpty();
        entries[1].TraceNumber.Should().NotBeNullOrEmpty();
        entries[0].TraceNumber.Should().NotBe(entries[1].TraceNumber);
    }

    [Fact]
    public void GenerateNachaFile_TraceNumberContainsOriginatingDfi()
    {
        var entries = new List<NachaEntryDetail> { CreateEntry() };

        _service.GenerateNachaFile(entries, _defaultOptions);

        entries[0].TraceNumber.Should().StartWith("09100001");
    }

    [Fact]
    public void GenerateNachaFile_WithEmptyEntries_Throws()
    {
        var entries = new List<NachaEntryDetail>();

        var act = () => _service.GenerateNachaFile(entries, _defaultOptions);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("No entries to include in NACHA file");
    }

    [Fact]
    public void GenerateNachaFile_FileStartsWithFileHeaderRecord()
    {
        var entries = new List<NachaEntryDetail> { CreateEntry() };

        var result = _service.GenerateNachaFile(entries, _defaultOptions);

        var firstLine = result.FileContent.Split('\n')[0].TrimEnd('\r');
        firstLine.Should().StartWith("101"); // Record Type 1 + Priority Code 01
    }

    [Fact]
    public void GenerateNachaFile_ContainsBatchHeaderRecord()
    {
        var entries = new List<NachaEntryDetail> { CreateEntry() };

        var result = _service.GenerateNachaFile(entries, _defaultOptions);

        var lines = result.FileContent.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        lines.Should().Contain(l => l.StartsWith("5")); // Batch Header
    }

    [Fact]
    public void GenerateNachaFile_ContainsEntryDetailRecords()
    {
        var entries = new List<NachaEntryDetail>
        {
            CreateEntry(),
            CreateEntry()
        };

        var result = _service.GenerateNachaFile(entries, _defaultOptions);

        var lines = result.FileContent.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        lines.Count(l => l.StartsWith("6")).Should().Be(2); // Entry Details
    }

    [Theory]
    [InlineData(BankAccountType.Checking, "627")] // 6 = record type, 27 = checking debit
    [InlineData(BankAccountType.Savings, "637")]   // 6 = record type, 37 = savings debit
    public void GenerateNachaFile_UsesCorrectTransactionCode(BankAccountType accountType, string expectedPrefix)
    {
        var entry = CreateEntry();
        entry.AccountType = accountType;
        var entries = new List<NachaEntryDetail> { entry };

        var result = _service.GenerateNachaFile(entries, _defaultOptions);

        var lines = result.FileContent.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var entryLine = lines.First(l => l.StartsWith("6"));
        entryLine.Should().StartWith(expectedPrefix);
    }

    [Fact]
    public void GenerateNachaFile_ContainsBatchControlRecord()
    {
        var entries = new List<NachaEntryDetail> { CreateEntry() };

        var result = _service.GenerateNachaFile(entries, _defaultOptions);

        var lines = result.FileContent.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        lines.Should().Contain(l => l.StartsWith("8")); // Batch Control
    }

    [Fact]
    public void GenerateNachaFile_ContainsFileControlRecord()
    {
        var entries = new List<NachaEntryDetail> { CreateEntry() };

        var result = _service.GenerateNachaFile(entries, _defaultOptions);

        var lines = result.FileContent.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        lines.Should().Contain(l => l.StartsWith("9")); // File Control
    }

    [Fact]
    public void GenerateNachaFile_PadsToBlockBoundary()
    {
        var entries = new List<NachaEntryDetail> { CreateEntry() };

        var result = _service.GenerateNachaFile(entries, _defaultOptions);

        var lines = result.FileContent.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrEmpty(l))
            .ToArray();

        // Total lines should be a multiple of 10 (NACHA block size)
        (lines.Length % 10).Should().Be(0);
    }

    [Fact]
    public void GenerateNachaFile_FileNameContainsCompanyId()
    {
        var entries = new List<NachaEntryDetail> { CreateEntry() };

        var result = _service.GenerateNachaFile(entries, _defaultOptions);

        result.FileName.Should().Contain(_defaultOptions.CompanyId);
    }
}
