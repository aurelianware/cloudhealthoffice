using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CapitationService.Models;
using CapitationService.Services;

namespace CapitationService.Tests.Services;

public class NachaCreditFileServiceTests
{
    private readonly NachaCreditFileService _service;
    private readonly NachaCreditFileOptions _defaultOptions;

    public NachaCreditFileServiceTests()
    {
        var configuration = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<NachaCreditFileService>>();
        _service = new NachaCreditFileService(configuration, logger.Object);

        _defaultOptions = new NachaCreditFileOptions
        {
            ImmediateDestination = "091000019",
            ImmediateOrigin = "1234567890",
            ImmediateDestinationName = "TEST BANK",
            ImmediateOriginName = "HEALTH PLAN CO",
            CompanyName = "HEALTH PLAN",
            CompanyId = "1234567890",
            OriginatingDfi = 9100001,
            CompanyEntryDescription = "CAPITATION"
        };
    }

    private static NachaCreditEntryDetail CreateEntry(
        string routing = "091000019",
        string account = "987654321",
        decimal amount = 5000.00m,
        string npi = "1234567890",
        BankAccountType accountType = BankAccountType.Checking) => new()
    {
        RoutingNumber = routing,
        AccountNumber = account,
        AccountType = accountType,
        Amount = amount,
        ProviderNpi = npi,
        IndividualName = "DR SMITH MEDICAL",
        IndividualId = npi
    };

    [Fact]
    public void GenerateNachaCreditFile_ProducesValidFormat()
    {
        var entries = new List<NachaCreditEntryDetail> { CreateEntry() };

        var result = _service.GenerateNachaCreditFile(entries, _defaultOptions);

        result.EntryCount.Should().Be(1);
        result.TotalAmount.Should().Be(5000.00m);
        result.FileName.Should().Contain("ACH-CREDIT-");
        result.FileName.Should().EndWith(".ach");
        result.FileReference.Should().StartWith("NACHA-CR-");
        result.FileContent.Should().NotBeNullOrEmpty();

        var lines = result.FileContent.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // File header starts with 101
        lines[0].Should().StartWith("101");

        // Batch header: record type 5, service class 220 (credits only), SEC code CCD
        var batchHeader = lines[1];
        batchHeader.Should().StartWith("5");
        batchHeader.Substring(1, 3).Should().Be("220"); // Service class = credits only
        batchHeader.Should().Contain("CCD"); // SEC code for corporate credit
        batchHeader.Should().Contain("CAPITATION"); // Company entry description

        // Entry detail: record type 6, transaction code 22 (checking credit)
        var entryLine = lines.First(l => l.StartsWith("6"));
        entryLine.Substring(1, 2).Should().Be("22"); // Checking CREDIT (not 27 debit!)

        // Batch control: record type 8, service class 220
        var batchControl = lines.First(l => l.StartsWith("8"));
        batchControl.Substring(1, 3).Should().Be("220");

        // File control: record type 9
        lines.Should().Contain(l => l.StartsWith("9"));
    }

    [Theory]
    [InlineData(BankAccountType.Checking, "622")] // 6 = record type, 22 = checking credit
    [InlineData(BankAccountType.Savings, "632")]   // 6 = record type, 32 = savings credit
    public void GenerateNachaCreditFile_UsesCorrectCreditTransactionCodes(
        BankAccountType accountType, string expectedPrefix)
    {
        var entry = CreateEntry(accountType: accountType);
        var entries = new List<NachaCreditEntryDetail> { entry };

        var result = _service.GenerateNachaCreditFile(entries, _defaultOptions);

        var lines = result.FileContent.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var entryLine = lines.First(l => l.StartsWith("6"));
        entryLine.Should().StartWith(expectedPrefix);
    }

    [Fact]
    public void GenerateNachaCreditFile_MultipleEntries_CalculatesCorrectTotals()
    {
        var entries = new List<NachaCreditEntryDetail>
        {
            CreateEntry(amount: 3000.00m, npi: "1111111111"),
            CreateEntry(amount: 7500.50m, npi: "2222222222"),
            CreateEntry(amount: 1250.25m, npi: "3333333333")
        };

        var result = _service.GenerateNachaCreditFile(entries, _defaultOptions);

        result.EntryCount.Should().Be(3);
        result.TotalAmount.Should().Be(11750.75m);
    }

    [Fact]
    public void GenerateNachaCreditFile_AssignsUniqueTraceNumbers()
    {
        var entries = new List<NachaCreditEntryDetail>
        {
            CreateEntry(npi: "1111111111"),
            CreateEntry(npi: "2222222222")
        };

        _service.GenerateNachaCreditFile(entries, _defaultOptions);

        entries[0].TraceNumber.Should().NotBeNullOrEmpty();
        entries[1].TraceNumber.Should().NotBeNullOrEmpty();
        entries[0].TraceNumber.Should().NotBe(entries[1].TraceNumber);
        entries[0].TraceNumber.Should().StartWith("09100001"); // Originating DFI
    }

    [Fact]
    public void GenerateNachaCreditFile_NoEntries_Throws()
    {
        var entries = new List<NachaCreditEntryDetail>();

        var act = () => _service.GenerateNachaCreditFile(entries, _defaultOptions);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("No entries to include in NACHA credit file");
    }

    [Fact]
    public void GenerateNachaCreditFile_PadsToBlockBoundary()
    {
        var entries = new List<NachaCreditEntryDetail> { CreateEntry() };

        var result = _service.GenerateNachaCreditFile(entries, _defaultOptions);

        var lines = result.FileContent.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrEmpty(l))
            .ToArray();

        (lines.Length % 10).Should().Be(0);
    }

    [Fact]
    public void GenerateNachaCreditFile_FileNameContainsCompanyId()
    {
        var entries = new List<NachaCreditEntryDetail> { CreateEntry() };

        var result = _service.GenerateNachaCreditFile(entries, _defaultOptions);

        result.FileName.Should().Contain(_defaultOptions.CompanyId);
    }
}
