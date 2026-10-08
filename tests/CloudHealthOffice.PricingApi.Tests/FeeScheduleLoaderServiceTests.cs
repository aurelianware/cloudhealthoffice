using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.PricingApi.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudHealthOffice.PricingApi.Tests;

public class FeeScheduleLoaderServiceTests : IDisposable
{
    private readonly Mock<IFeeScheduleRepository> _repo = new();
    private readonly List<FeeScheduleEntry> _upserted = new();
    private readonly string _csvPath = Path.Combine(Path.GetTempPath(), $"pprrvu-{Guid.NewGuid():N}.csv");
    private readonly FeeScheduleLoaderService _sut;

    public FeeScheduleLoaderServiceTests()
    {
        _repo.Setup(r => r.BulkUpsertEntriesAsync(It.IsAny<IEnumerable<FeeScheduleEntry>>()))
            .Callback<IEnumerable<FeeScheduleEntry>>(entries => _upserted.AddRange(entries))
            .Returns(Task.CompletedTask);
        _sut = new FeeScheduleLoaderService(_repo.Object, NullLogger<FeeScheduleLoaderService>.Instance);
    }

    public void Dispose()
    {
        if (File.Exists(_csvPath))
            File.Delete(_csvPath);
    }

    [Fact]
    public async Task SeedMedicareRbrvs_ParsesMultProcColumnByHeaderName()
    {
        // Column order differs from the class map; MULT PROC is matched by its header name.
        await File.WriteAllLinesAsync(_csvPath, new[]
        {
            "HCPCS,MOD,DESCRIPTION,MULT PROC,WORK_RVU,NON_FAC_PE_RVU,FAC_PE_RVU,MP_RVU,CONV_FACTOR",
            "99213,,Office visit,0,0.97,1.10,0.73,0.08,32.7442",
            "27447,,Total knee arthroplasty,2,20.71,12.73,12.73,4.49,32.7442",
            "45380,,Colonoscopy with biopsy,3,3.22,4.44,1.56,0.33,32.7442",
            "73721,,MRI knee,4,1.09,5.26,0.80,0.14,32.7442",
            "97110,,Therapeutic exercises,5,0.44,0.58,0.25,0.03,32.7442",
            "36415,,Venipuncture,9,0.00,0.15,0.10,0.01,32.7442",
            "10060,,I&D abscess,,1.22,1.79,0.78,0.19,32.7442",
            "10061,,I&D abscess complex,X,2.45,2.60,1.40,0.30,32.7442",
        });

        var count = await _sut.SeedMedicareRbrvs(_csvPath, 2025);

        count.Should().Be(8);
        var indicators = _upserted.ToDictionary(e => e.ProcedureCode, e => e.MultipleProcedureIndicator);
        indicators["99213"].Should().Be(0);
        indicators["27447"].Should().Be(2);
        indicators["45380"].Should().Be(3);
        indicators["73721"].Should().Be(4);
        indicators["97110"].Should().Be(5);
        indicators["36415"].Should().Be(9);
        indicators["10060"].Should().BeNull(); // blank → unknown
        indicators["10061"].Should().BeNull(); // unparseable → unknown
    }

    [Theory]
    [InlineData("MULT_PROC")]
    [InlineData("MULTIPLE_PROCEDURE_INDICATOR")]
    public async Task SeedMedicareRbrvs_AcceptsAlternateMultProcHeaders(string header)
    {
        await File.WriteAllLinesAsync(_csvPath, new[]
        {
            $"HCPCS,WORK_RVU,NON_FAC_PE_RVU,FAC_PE_RVU,MP_RVU,CONV_FACTOR,{header}",
            "27447,20.71,12.73,12.73,4.49,32.7442,2",
        });

        await _sut.SeedMedicareRbrvs(_csvPath, 2025);

        _upserted.Single().MultipleProcedureIndicator.Should().Be(2);
    }

    [Fact]
    public async Task SeedMedicareRbrvs_NoMultProcColumn_LeavesIndicatorUnknown()
    {
        await File.WriteAllLinesAsync(_csvPath, new[]
        {
            "HCPCS,WORK_RVU,NON_FAC_PE_RVU,FAC_PE_RVU,MP_RVU,CONV_FACTOR",
            "27447,20.71,12.73,12.73,4.49,32.7442",
        });

        await _sut.SeedMedicareRbrvs(_csvPath, 2025);

        var entry = _upserted.Single();
        entry.MultipleProcedureIndicator.Should().BeNull();
        entry.NonFacilityRate.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SeedDemoData_RbrvsEntriesCarryIndicator_EmIsZero()
    {
        await _sut.SeedDemoDataAsync();

        var rbrvs = _upserted.Where(e => e.FeeScheduleId == "MEDICARE_RBRVS_2025").ToList();
        rbrvs.Should().NotBeEmpty();
        rbrvs.Should().OnlyContain(e => e.MultipleProcedureIndicator != null);
        rbrvs.Where(e => e.ProcedureCode.StartsWith("99")).Should().OnlyContain(e => e.MultipleProcedureIndicator == 0);
        rbrvs.Single(e => e.ProcedureCode == "27447").MultipleProcedureIndicator.Should().Be(2);
    }
}
