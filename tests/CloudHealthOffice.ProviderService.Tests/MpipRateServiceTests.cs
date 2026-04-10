using EphemeralMongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using ProviderService.Models;
using ProviderService.Services;
using Xunit;

namespace CloudHealthOffice.ProviderService.Tests;

public class MpipRateServiceTests : IAsyncLifetime
{
    private const string TenantId = "test-tenant";
    private const string ProviderId = "provider-001";
    private const string Period = "2025-2026";

    private IMongoRunner _runner = null!;
    private IMongoDatabase _database = null!;
    private MpipRateService _service = null!;

    public Task InitializeAsync()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions
        {
            ConnectionTimeout = TimeSpan.FromSeconds(30)
        });
        var client = new MongoClient(_runner.ConnectionString);
        _database = client.GetDatabase("mpip_test");
        _service = new MpipRateService(_database, NullLogger<MpipRateService>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            _runner.Dispose();
        }
        catch (TypeLoadException)
        {
            // EphemeralMongo.Core 2.0.0 references MongoClientBase which was
            // removed in MongoDB.Driver 3.x.  The TypeLoadException only occurs
            // during disposal; the MongoDB process is cleaned up by the OS.
        }
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // SPECIALIST AUTO-QUALIFY
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Specialist_Under21_Returns_EnhancedMultiplier()
    {
        // Arrange — specialist auto-qualifies regardless of IsQualified flag
        await SeedQualification(MpipProviderType.Specialist, isQualified: true,
            method: MpipQualificationMethod.AutoQualified_Specialist);

        // Act — member age 19 (under 21)
        var multiplier = await _service.GetEnhancedRateMultiplierAsync(
            ProviderId, TenantId,
            serviceDate: new DateTime(2026, 1, 15),
            memberAgeAtServiceDate: 19);

        // Assert
        Assert.Equal(MpipRateService.EnhancedMultiplier, multiplier);
    }

    [Fact]
    public async Task Specialist_Age21_Returns_StandardMultiplier()
    {
        // Arrange
        await SeedQualification(MpipProviderType.Specialist, isQualified: true,
            method: MpipQualificationMethod.AutoQualified_Specialist);

        // Act — member age 21 (at the boundary)
        var multiplier = await _service.GetEnhancedRateMultiplierAsync(
            ProviderId, TenantId,
            serviceDate: new DateTime(2026, 1, 15),
            memberAgeAtServiceDate: 21);

        // Assert — age >= 21 always returns 1.0x
        Assert.Equal(MpipRateService.StandardMultiplier, multiplier);
    }

    [Fact]
    public async Task Specialist_Age22_Returns_StandardMultiplier()
    {
        // Arrange
        await SeedQualification(MpipProviderType.Specialist, isQualified: true,
            method: MpipQualificationMethod.AutoQualified_Specialist);

        // Act
        var multiplier = await _service.GetEnhancedRateMultiplierAsync(
            ProviderId, TenantId,
            serviceDate: new DateTime(2026, 1, 15),
            memberAgeAtServiceDate: 22);

        // Assert
        Assert.Equal(MpipRateService.StandardMultiplier, multiplier);
    }

    // ═══════════════════════════════════════════════════════════════════
    // PCP QUALIFIED
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PCP_Qualified_Under21_Returns_EnhancedMultiplier()
    {
        // Arrange — PCP who met AHCA benchmarks
        await SeedQualification(MpipProviderType.PrimaryCare, isQualified: true,
            method: MpipQualificationMethod.PerformanceBenchmark);

        // Act
        var multiplier = await _service.GetEnhancedRateMultiplierAsync(
            ProviderId, TenantId,
            serviceDate: new DateTime(2026, 3, 1),
            memberAgeAtServiceDate: 19);

        // Assert
        Assert.Equal(MpipRateService.EnhancedMultiplier, multiplier);
    }

    [Fact]
    public async Task PCP_NotQualified_Under21_Returns_StandardMultiplier()
    {
        // Arrange — PCP who did NOT meet benchmarks
        await SeedQualification(MpipProviderType.PrimaryCare, isQualified: false,
            method: MpipQualificationMethod.NotQualified);

        // Act
        var multiplier = await _service.GetEnhancedRateMultiplierAsync(
            ProviderId, TenantId,
            serviceDate: new DateTime(2026, 3, 1),
            memberAgeAtServiceDate: 19);

        // Assert
        Assert.Equal(MpipRateService.StandardMultiplier, multiplier);
    }

    // ═══════════════════════════════════════════════════════════════════
    // OB/GYN
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ObGyn_Qualified_Under21_Returns_EnhancedMultiplier()
    {
        await SeedQualification(MpipProviderType.ObGyn, isQualified: true,
            method: MpipQualificationMethod.PerformanceBenchmark);

        var multiplier = await _service.GetEnhancedRateMultiplierAsync(
            ProviderId, TenantId,
            serviceDate: new DateTime(2026, 2, 15),
            memberAgeAtServiceDate: 17);

        Assert.Equal(MpipRateService.EnhancedMultiplier, multiplier);
    }

    // ═══════════════════════════════════════════════════════════════════
    // OTHER PROVIDER TYPE
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OtherType_Under21_Returns_StandardMultiplier()
    {
        await SeedQualification(MpipProviderType.Other, isQualified: false,
            method: MpipQualificationMethod.NotQualified);

        var multiplier = await _service.GetEnhancedRateMultiplierAsync(
            ProviderId, TenantId,
            serviceDate: new DateTime(2026, 3, 1),
            memberAgeAtServiceDate: 15);

        Assert.Equal(MpipRateService.StandardMultiplier, multiplier);
    }

    // ═══════════════════════════════════════════════════════════════════
    // NO QUALIFICATION RECORD
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoQualificationRecord_Returns_StandardMultiplier()
    {
        // Arrange — no seed data

        var multiplier = await _service.GetEnhancedRateMultiplierAsync(
            "unknown-provider", TenantId,
            serviceDate: new DateTime(2026, 3, 1),
            memberAgeAtServiceDate: 10);

        Assert.Equal(MpipRateService.StandardMultiplier, multiplier);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FISCAL YEAR PERIOD RESOLUTION
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(2025, 10, 1, "2025-2026")]  // Oct 1 = start of new fiscal year
    [InlineData(2025, 12, 31, "2025-2026")] // Dec 31 = still in 2025-2026
    [InlineData(2026, 1, 1, "2025-2026")]   // Jan 1 = still in 2025-2026
    [InlineData(2026, 9, 30, "2025-2026")]  // Sep 30 = last day of 2025-2026
    [InlineData(2026, 10, 1, "2026-2027")]  // Oct 1 = start of new period
    [InlineData(2025, 9, 30, "2024-2025")]  // Sep 30 2025 = end of 2024-2025
    public void GetFiscalYearPeriod_Returns_CorrectPeriod(
        int year, int month, int day, string expectedPeriod)
    {
        var serviceDate = new DateTime(year, month, day);
        var period = MpipRateService.GetFiscalYearPeriod(serviceDate);
        Assert.Equal(expectedPeriod, period);
    }

    // ═══════════════════════════════════════════════════════════════════
    // UPSERT + QUERY ROUND-TRIP
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpsertQualification_SetsMultiplier_And_Persists()
    {
        // Arrange
        var qualification = CreateQualification(MpipProviderType.PrimaryCare, true,
            MpipQualificationMethod.PerformanceBenchmark);

        // Act
        await _service.UpsertQualificationAsync(qualification);
        var retrieved = await _service.GetQualificationAsync(ProviderId, TenantId, Period);

        // Assert
        Assert.NotNull(retrieved);
        Assert.True(retrieved.IsQualified);
        Assert.Equal(MpipRateService.EnhancedMultiplier, retrieved.EnhancedRateMultiplier);
    }

    [Fact]
    public async Task GetQualifiedProviders_Returns_OnlyQualified()
    {
        // Arrange — seed one qualified, one not
        var q1 = CreateQualification(MpipProviderType.Specialist, true,
            MpipQualificationMethod.AutoQualified_Specialist);
        q1.ProviderId = "qualified-1";

        var q2 = CreateQualification(MpipProviderType.PrimaryCare, false,
            MpipQualificationMethod.NotQualified);
        q2.ProviderId = "not-qualified-1";

        await _service.UpsertQualificationAsync(q1);
        await _service.UpsertQualificationAsync(q2);

        // Act
        var qualified = (await _service.GetQualifiedProvidersAsync(TenantId, Period)).ToList();

        // Assert
        Assert.Single(qualified);
        Assert.Equal("qualified-1", qualified[0].ProviderId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FIXTURES
    // ═══════════════════════════════════════════════════════════════════

    private async Task SeedQualification(
        MpipProviderType type,
        bool isQualified,
        MpipQualificationMethod method)
    {
        await _service.UpsertQualificationAsync(
            CreateQualification(type, isQualified, method));
    }

    private static MpipProviderQualification CreateQualification(
        MpipProviderType type,
        bool isQualified,
        MpipQualificationMethod method) => new()
    {
        TenantId = TenantId,
        ProviderId = ProviderId,
        Npi = "1234567890",
        ProviderType = type,
        QualificationPeriod = Period,
        IsQualified = isQualified,
        QualificationMethod = method,
        EffectiveDate = new DateTime(2025, 10, 1),
        ExpirationDate = new DateTime(2026, 9, 30)
    };
}
