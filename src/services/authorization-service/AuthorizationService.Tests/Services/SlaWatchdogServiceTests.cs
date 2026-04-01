using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services;
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
        _repositoryMock.Verify(r => r.UpdateAsync(auth), Times.Once);
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
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Authorization>()), Times.Never);
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
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Authorization>()), Times.Never);
    }

    [Fact]
    public async Task DeniedAuth_IsSkipped()
    {
        var auth = CreateAuth(levelOfService: "U", hoursAgo: 100);
        auth.Status = AuthorizationStatus.Denied;
        SetupRepository(auth);

        await RunWatchdog();

        auth.SlaEscalation.Should().Be(SlaEscalationLevel.None);
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Authorization>()), Times.Never);
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
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Authorization>()), Times.Never);
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

    private void SetupRepository(Authorization auth)
    {
        _repositoryMock
            .Setup(r => r.GetOpenAuthorizationsAsync(null))
            .ReturnsAsync(new[] { auth });
        _repositoryMock
            .Setup(r => r.UpdateAsync(It.IsAny<Authorization>()))
            .ReturnsAsync((Authorization a) => a);
    }

    private async Task RunWatchdog()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_repositoryMock.Object);
        var sp = services.BuildServiceProvider();

        var service = new SlaWatchdogService(sp, _loggerMock.Object);
        await service.EvaluateAllAuthorizationsAsync();
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
