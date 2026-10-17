using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuthorizationService.Tests.Services;

public class SlaWatchdogServiceTests
{
    private readonly Mock<IAuthorizationRepository> _repositoryMock;
    private readonly Mock<ILogger<SlaWatchdogService>> _loggerMock;

    public SlaWatchdogServiceTests()
    {
        _repositoryMock = new Mock<IAuthorizationRepository>();
        _loggerMock = new Mock<ILogger<SlaWatchdogService>>();
    }

    [Fact]
    public async Task UrgentAuth_At48Hours_SetsWarning()
    {
        // Arrange
        var auth = CreateAuth(levelOfService: "U", hoursAgo: 48);
        SetupRepository(auth);

        // Act
        await RunWatchdog();

        // Assert
        auth.SlaEscalation.Should().Be(SlaEscalationLevel.Warning);
        auth.SlaEscalatedAt.Should().NotBeNull();
        _repositoryMock.Verify(r => r.RaiseSlaEscalationAsync(
            "tenant-1", auth.Id, SlaEscalationLevel.Warning, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UrgentAuth_At64Hours_SetsCritical()
    {
        var auth = CreateAuth(levelOfService: "U", hoursAgo: 64);
        SetupRepository(auth);

        await RunWatchdog();

        auth.SlaEscalation.Should().Be(SlaEscalationLevel.Critical);
        auth.SlaEscalatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UrgentAuth_At72Hours_SetsBreach()
    {
        var auth = CreateAuth(levelOfService: "U", hoursAgo: 72);
        SetupRepository(auth);

        await RunWatchdog();

        auth.SlaEscalation.Should().Be(SlaEscalationLevel.Breach);
        auth.SlaEscalatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task StandardAuth_At120Hours_SetsWarning()
    {
        var auth = CreateAuth(levelOfService: "E", hoursAgo: 120);
        SetupRepository(auth);

        await RunWatchdog();

        auth.SlaEscalation.Should().Be(SlaEscalationLevel.Warning);
        auth.SlaEscalatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task StandardAuth_At144Hours_SetsCritical()
    {
        var auth = CreateAuth(levelOfService: "E", hoursAgo: 144);
        SetupRepository(auth);

        await RunWatchdog();

        auth.SlaEscalation.Should().Be(SlaEscalationLevel.Critical);
        auth.SlaEscalatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task StandardAuth_At168Hours_SetsBreach()
    {
        var auth = CreateAuth(levelOfService: "E", hoursAgo: 168);
        SetupRepository(auth);

        await RunWatchdog();

        auth.SlaEscalation.Should().Be(SlaEscalationLevel.Breach);
        auth.SlaEscalatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task PendedAuth_WithSlaResumedAt_UsesResumedDate()
    {
        // Auth submitted 200 hours ago but SLA resumed 40 hours ago (urgent)
        // 40h < 48h warning threshold → should remain None
        var auth = CreateAuth(levelOfService: "U", hoursAgo: 200);
        auth.Status = AuthorizationStatus.Pended;
        auth.SlaResumedAt = DateTime.UtcNow.AddHours(-40);
        SetupRepository(auth);

        await RunWatchdog();

        auth.SlaEscalation.Should().Be(SlaEscalationLevel.None);
        VerifyNoEscalationWritten();
    }

    [Fact]
    public async Task PendedAuth_WithoutSlaResumedAt_UsesSubmittedDate()
    {
        // Auth submitted 50 hours ago with no SlaResumedAt (urgent)
        // 50h >= 48h warning threshold → should be Warning
        var auth = CreateAuth(levelOfService: "U", hoursAgo: 50);
        auth.Status = AuthorizationStatus.Pended;
        auth.SlaResumedAt = null;
        SetupRepository(auth);

        await RunWatchdog();

        auth.SlaEscalation.Should().Be(SlaEscalationLevel.Warning);
    }

    [Fact]
    public async Task ApprovedAuth_IsSkipped()
    {
        var auth = CreateAuth(levelOfService: "U", hoursAgo: 100);
        auth.Status = AuthorizationStatus.Approved;
        SetupRepository(auth);

        await RunWatchdog();

        auth.SlaEscalation.Should().Be(SlaEscalationLevel.None);
        VerifyNoEscalationWritten();
    }

    [Fact]
    public async Task DeniedAuth_IsSkipped()
    {
        var auth = CreateAuth(levelOfService: "U", hoursAgo: 100);
        auth.Status = AuthorizationStatus.Denied;
        SetupRepository(auth);

        await RunWatchdog();

        auth.SlaEscalation.Should().Be(SlaEscalationLevel.None);
        VerifyNoEscalationWritten();
    }

    [Fact]
    public async Task DoesNotDowngradeEscalation_FromCriticalToWarning()
    {
        // Auth already at Critical, but elapsed time only qualifies for Warning
        // (e.g., SLA was resumed and clock reset, but existing escalation is Critical)
        var auth = CreateAuth(levelOfService: "U", hoursAgo: 49); // only Warning level
        auth.SlaEscalation = SlaEscalationLevel.Critical;
        SetupRepository(auth);

        await RunWatchdog();

        auth.SlaEscalation.Should().Be(SlaEscalationLevel.Critical);
        VerifyNoEscalationWritten();
    }

    [Fact]
    public async Task NullLevelOfService_TreatedAsStandard()
    {
        var auth = CreateAuth(levelOfService: null, hoursAgo: 120);
        SetupRepository(auth);

        await RunWatchdog();

        auth.SlaEscalation.Should().Be(SlaEscalationLevel.Warning);
        auth.SlaEscalatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task EveryTenant_IsEvaluatedWithItsOwnTenant_InItsOwnScope()
    {
        var a = CreateAuth(levelOfService: "U", hoursAgo: 50);
        var b = CreateAuth(levelOfService: "U", hoursAgo: 65);
        b.TenantId = "tenant-2";
        _repositoryMock.Setup(r => r.ListTenantIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "tenant-1", "tenant-2" });
        _repositoryMock.Setup(r => r.GetOpenAuthorizationsAsync("tenant-1")).ReturnsAsync(new[] { a });
        _repositoryMock.Setup(r => r.GetOpenAuthorizationsAsync("tenant-2")).ReturnsAsync(new[] { b });
        SetupRaise(a, b);

        var summary = await RunWatchdog();

        a.SlaEscalation.Should().Be(SlaEscalationLevel.Warning);
        b.SlaEscalation.Should().Be(SlaEscalationLevel.Critical);
        _repositoryMock.Verify(r => r.GetOpenAuthorizationsAsync(null), Times.Never);
        _repositoryMock.Verify(r => r.RaiseSlaEscalationAsync(
            "tenant-2", b.Id, SlaEscalationLevel.Critical, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        summary.Tenants.Should().Be(2);
        summary.Escalated.Should().Be(2);
        _scopesCreated.Should().Be(3, "one scope lists the tenants, then one per tenant");
    }

    [Fact]
    public async Task RecordListedForAnotherTenant_IsNeverEscalated()
    {
        var foreign = CreateAuth(levelOfService: "U", hoursAgo: 100);
        foreign.TenantId = "tenant-2";
        SetupRepository(foreign);

        await RunWatchdog();

        foreign.SlaEscalation.Should().Be(SlaEscalationLevel.None);
        VerifyNoEscalationWritten();
    }

    [Fact]
    public async Task OneTenantFailing_DoesNotStopTheOthers()
    {
        var b = CreateAuth(levelOfService: "U", hoursAgo: 50);
        b.TenantId = "tenant-2";
        _repositoryMock.Setup(r => r.ListTenantIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "tenant-1", "tenant-2" });
        _repositoryMock.Setup(r => r.GetOpenAuthorizationsAsync("tenant-1"))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));
        _repositoryMock.Setup(r => r.GetOpenAuthorizationsAsync("tenant-2")).ReturnsAsync(new[] { b });
        SetupRaise(b);

        var summary = await RunWatchdog();

        b.SlaEscalation.Should().Be(SlaEscalationLevel.Warning);
        summary.FailedTenants.Should().Be(1);
        summary.Escalated.Should().Be(1);
    }

    [Fact]
    public async Task EscalationRefusedByTheStore_IsNotCounted()
    {
        var auth = CreateAuth(levelOfService: "U", hoursAgo: 50);
        _repositoryMock.Setup(r => r.ListTenantIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "tenant-1" });
        _repositoryMock.Setup(r => r.GetOpenAuthorizationsAsync("tenant-1")).ReturnsAsync(new[] { auth });
        // Changed under us (closed, or already escalated): the conditional write declines.
        _repositoryMock.Setup(r => r.RaiseSlaEscalationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SlaEscalationLevel>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var summary = await RunWatchdog();

        summary.Escalated.Should().Be(0);
    }

    private int _scopesCreated;

    private void SetupRepository(Authorization auth)
    {
        _repositoryMock
            .Setup(r => r.ListTenantIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "tenant-1" });
        _repositoryMock
            .Setup(r => r.GetOpenAuthorizationsAsync("tenant-1"))
            .ReturnsAsync(new[] { auth });
        SetupRaise(auth);
    }

    /// <summary>The store applies the escalation, as the real conditional write does.</summary>
    private void SetupRaise(params Authorization[] auths)
    {
        _repositoryMock
            .Setup(r => r.RaiseSlaEscalationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SlaEscalationLevel>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string tenant, string id, SlaEscalationLevel level, DateTime at, CancellationToken _) =>
            {
                var auth = auths.SingleOrDefault(a => a.Id == id && a.TenantId == tenant);
                if (auth is null || auth.SlaEscalation >= level) return false;
                auth.SlaEscalation = level;
                auth.SlaEscalatedAt = at;
                return true;
            });
    }

    private void VerifyNoEscalationWritten()
    {
        _repositoryMock.Verify(r => r.RaiseSlaEscalationAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SlaEscalationLevel>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Authorization>()), Times.Never);
    }

    [Fact]
    public async Task TenantList_IsCached_AndRefreshedAfterTheInterval()
    {
        var auth = CreateAuth(levelOfService: "U", hoursAgo: 10);
        SetupRepository(auth);

        await RunWatchdog();
        await RunWatchdog();
        _repositoryMock.Verify(r => r.ListTenantIdsAsync(It.IsAny<CancellationToken>()), Times.Once);

        _clock.Advance(SlaWatchdogService.TenantListRefreshInterval);
        await RunWatchdog();
        _repositoryMock.Verify(r => r.ListTenantIdsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task OneRecordFailing_DoesNotStopTheRestOfItsTenant()
    {
        var first = CreateAuth(levelOfService: "U", hoursAgo: 50);
        var second = CreateAuth(levelOfService: "U", hoursAgo: 50);
        _repositoryMock.Setup(r => r.ListTenantIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "tenant-1" });
        _repositoryMock.Setup(r => r.GetOpenAuthorizationsAsync("tenant-1")).ReturnsAsync(new[] { first, second });
        SetupRaise(second);
        _repositoryMock.Setup(r => r.RaiseSlaEscalationAsync(
                "tenant-1", first.Id, It.IsAny<SlaEscalationLevel>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient"));

        var summary = await RunWatchdog();

        second.SlaEscalation.Should().Be(SlaEscalationLevel.Warning);
        summary.Escalated.Should().Be(1);
        summary.FailedRecords.Should().Be(1);
        summary.FailedTenants.Should().Be(0);
    }

    [Fact]
    public async Task MongoTenantScoping_IsNotSupported_AndSaysSo()
    {
        _settings["MongoDb:UseTenantScoping"] = "true";
        SetupRepository(CreateAuth(levelOfService: "U", hoursAgo: 100));

        var summary = await RunWatchdog();

        summary.SkippedReason.Should().Contain("UseTenantScoping");
        _repositoryMock.Verify(r => r.ListTenantIdsAsync(It.IsAny<CancellationToken>()), Times.Never);
        _loggerMock.Verify(l => l.Log(
            LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    private readonly Dictionary<string, string?> _settings = new();
    private readonly ManualClock _clock = new();
    private SlaWatchdogService? _service;

    private async Task<SlaWatchdogRunSummary> RunWatchdog()
    {
        if (_service is null)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ =>
            {
                _scopesCreated++;
                return _repositoryMock.Object;
            });
            var sp = services.BuildServiceProvider();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(_settings).Build();

            _service = new SlaWatchdogService(
                sp.GetRequiredService<IServiceScopeFactory>(), configuration, _loggerMock.Object, _clock);
        }

        return await _service.EvaluateAllAuthorizationsAsync();
    }

    /// <summary>Real time plus whatever the test has advanced it by.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private TimeSpan _offset;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;

        public void Advance(TimeSpan by) => _offset += by;
    }

    private static Authorization CreateAuth(string? levelOfService, double hoursAgo) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = "tenant-1",
        AuthorizationNumber = $"AUTH-{Guid.NewGuid():N}",
        MemberId = "MBR-001",
        PatientFirstName = "Jane",
        PatientLastName = "Doe",
        PatientDateOfBirth = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        LineOfBusiness = LineOfBusiness.Commercial,
        RequestingProviderNPI = "1234567890",
        AuthorizationType = AuthorizationType.PreAuthorization,
        ServiceTypeCode = "1",
        LevelOfService = levelOfService,
        RequestedServiceDateFrom = DateTime.UtcNow.AddDays(-10),
        Status = AuthorizationStatus.Submitted,
        SubmittedDate = DateTime.UtcNow.AddHours(-hoursAgo),
    };
}
