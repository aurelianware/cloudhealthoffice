using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Aggregator;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.ProviderEnrollmentService.Tests;

public class MultiStateEnrollmentAggregatorTests
{
    private const string TestNpi = "1234567890";

    private static MultiStateEnrollmentAggregator CreateSut(
        IEnumerable<IStateEnrollmentSource> sources,
        ProviderEnrollmentOptions? opts = null)
    {
        opts ??= new ProviderEnrollmentOptions();
        return new MultiStateEnrollmentAggregator(
            sources,
            Options.Create(opts),
            Substitute.For<ILogger<MultiStateEnrollmentAggregator>>());
    }

    private static IStateEnrollmentSource MakeSource(
        string stateCode,
        string sourceSystem,
        StateEnrollmentRecord? singleResult = null,
        IReadOnlyList<StateEnrollmentRecord>? panelResult = null)
    {
        var source = Substitute.For<IStateEnrollmentSource>();
        source.StateCode.Returns(stateCode);
        source.SourceSystemName.Returns(sourceSystem);
        source.SupportedLobs.Returns(LineOfBusiness.All);

        if (singleResult is not null)
            source.GetEnrollmentAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
                .Returns(singleResult);

        if (panelResult is not null)
            source.GetPanelAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
                .Returns(panelResult);

        return source;
    }

    private static StateEnrollmentRecord MakeRecord(
        string npi,
        string stateCode,
        EnrollmentStatus status = EnrollmentStatus.Active,
        DateOnly? revalidationDue = null) => new()
    {
        Npi                 = npi,
        StateCode           = stateCode,
        SourceSystem        = stateCode == "TX" ? "PEMS" : "PAVE",
        Status              = status,
        EffectiveDate       = new DateOnly(2023, 1, 1),
        ProviderType        = ProviderTypeClassification.PhysicianMD,
        SupportedLobs       = LineOfBusiness.Medicaid,
        RevalidationDueDate = revalidationDue
    };

    // ── Test 1 ────────────────────────────────────────────────────

    [Fact]
    public async Task GetCrossStateProfileAsync_AllSourcesQueried_ReturnsAggregatedSummary()
    {
        // Arrange
        var txRecord = MakeRecord(TestNpi, "TX");
        var caRecord = MakeRecord(TestNpi, "CA");

        var txSource = MakeSource("TX", "PEMS", txRecord);
        var caSource = MakeSource("CA", "PAVE", caRecord);

        var sut = CreateSut([txSource, caSource]);

        // Act
        var summary = await sut.GetCrossStateProfileAsync(TestNpi);

        // Assert
        summary.Npi.Should().Be(TestNpi);
        summary.AllRecords.Should().HaveCount(2);
        summary.ActiveStates.Should().Contain("TX").And.Contain("CA");

        await txSource.Received(1).GetEnrollmentAsync(TestNpi, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await caSource.Received(1).GetEnrollmentAsync(TestNpi, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    // ── Test 2 ────────────────────────────────────────────────────

    [Fact]
    public async Task GetCrossStateProfileAsync_SourceThrows_LogsWarning_ContinuesWithOtherSources()
    {
        // Arrange
        var caRecord = MakeRecord(TestNpi, "CA");
        var txSource = MakeSource("TX", "PEMS");
        txSource.GetEnrollmentAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("TMHP is down"));

        var caSource = MakeSource("CA", "PAVE", caRecord);
        var sut = CreateSut([txSource, caSource]);

        // Act
        var summary = await sut.GetCrossStateProfileAsync(TestNpi);

        // Assert — should get CA result only, no throw
        summary.AllRecords.Should().HaveCount(1);
        summary.ActiveStates.Should().ContainSingle().Which.Should().Be("CA");
    }

    // ── Test 3 ────────────────────────────────────────────────────

    [Fact]
    public async Task GetCrossStateProfileAsync_ActiveAndPendingRecords_ClassifiedCorrectly()
    {
        // Arrange
        var activeRecord  = MakeRecord(TestNpi, "TX", EnrollmentStatus.Active);
        var pendingRecord = MakeRecord(TestNpi, "CA", EnrollmentStatus.Pending);

        var txSource = MakeSource("TX", "PEMS", activeRecord);
        var caSource = MakeSource("CA", "PAVE", pendingRecord);
        var sut = CreateSut([txSource, caSource]);

        // Act
        var summary = await sut.GetCrossStateProfileAsync(TestNpi);

        // Assert
        summary.ActiveStates.Should().ContainSingle().Which.Should().Be("TX");
        summary.PendingStates.Should().ContainSingle().Which.Should().Be("CA");
    }

    // ── Test 4 ────────────────────────────────────────────────────

    [Fact]
    public async Task GetEnrollmentForStateAsync_StateNotRegistered_ReturnsNull()
    {
        var txSource = MakeSource("TX", "PEMS");
        var sut = CreateSut([txSource]);

        // Act — ask for FL which is not registered
        var result = await sut.GetEnrollmentForStateAsync(TestNpi, "FL");

        // Assert
        result.Should().BeNull();
    }

    // ── Test 5 ────────────────────────────────────────────────────

    [Fact]
    public async Task GetEnrollmentForStateAsync_EnabledStateCodesFiltersOut_ReturnsNull()
    {
        // Arrange — both TX and CA are registered, but EnabledStateCodes only allows CA
        var txSource = MakeSource("TX", "PEMS", MakeRecord(TestNpi, "TX"));
        var caSource = MakeSource("CA", "PAVE", MakeRecord(TestNpi, "CA"));

        var opts = new ProviderEnrollmentOptions
        {
            EnabledStateCodes = ["CA"]
        };
        var sut = CreateSut([txSource, caSource], opts);

        // Act — TX is registered but filtered out by EnabledStateCodes
        var result = await sut.GetEnrollmentForStateAsync(TestNpi, "TX");

        // Assert
        result.Should().BeNull();
    }

    // ── Test 6 ────────────────────────────────────────────────────

    [Fact]
    public async Task DetectRevalidationRisks_DueSoon_ReturnsRisk()
    {
        // Arrange — revalidation due in 30 days (within the 90-day warning window)
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        var record  = MakeRecord(TestNpi, "TX", EnrollmentStatus.Active, dueDate);
        var txSource = MakeSource("TX", "PEMS", record);

        var sut = CreateSut([txSource], new ProviderEnrollmentOptions { RevalidationWarningDays = 90 });

        // Act
        var summary = await sut.GetCrossStateProfileAsync(TestNpi);

        // Assert
        summary.RevalidationRisks.Should().ContainSingle();
        summary.RevalidationRisks[0].StateCode.Should().Be("TX");
        summary.RevalidationRisks[0].DaysRemaining.Should().BeInRange(29, 31);
    }

    // ── Test 7 ────────────────────────────────────────────────────

    [Fact]
    public async Task DetectRevalidationRisks_NotDueSoon_ReturnsEmpty()
    {
        // Arrange — revalidation due in 365 days (well outside the 90-day window)
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(365);
        var record  = MakeRecord(TestNpi, "TX", EnrollmentStatus.Active, dueDate);
        var txSource = MakeSource("TX", "PEMS", record);

        var sut = CreateSut([txSource], new ProviderEnrollmentOptions { RevalidationWarningDays = 90 });

        // Act
        var summary = await sut.GetCrossStateProfileAsync(TestNpi);

        // Assert
        summary.RevalidationRisks.Should().BeEmpty();
    }

    // ── Test 8 ────────────────────────────────────────────────────

    [Fact]
    public async Task ReconcilePanelAsync_MultipleNpis_FansOutToAllSources()
    {
        // Arrange
        var npis     = new[] { "1111111111", "2222222222" };
        var txPanel  = npis.Select(n => MakeRecord(n, "TX")).ToList();
        var caPanel  = npis.Select(n => MakeRecord(n, "CA")).ToList();

        var txSource = MakeSource("TX", "PEMS", panelResult: txPanel);
        var caSource = MakeSource("CA", "PAVE", panelResult: caPanel);

        var sut = CreateSut([txSource, caSource]);

        // Act
        var summaries = await sut.ReconcilePanelAsync(npis);

        // Assert
        summaries.Should().HaveCount(2);
        summaries.Should().Contain(s => s.Npi == "1111111111");
        summaries.Should().Contain(s => s.Npi == "2222222222");

        // Each NPI should have records from both states
        foreach (var s in summaries)
        {
            s.AllRecords.Should().HaveCount(2);
            s.ActiveStates.Should().Contain("TX").And.Contain("CA");
        }

        await txSource.Received(1).GetPanelAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
        await caSource.Received(1).GetPanelAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }
}
