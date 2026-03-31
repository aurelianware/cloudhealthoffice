using AuthorizationService.Controllers;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AuthorizationService.Tests.Controllers;

public class AuthorizationsControllerSlaTests
{
    private readonly Mock<IAuthorizationRepository> _repositoryMock;
    private readonly Mock<ILogger<AuthorizationsController>> _loggerMock;
    private readonly AuthorizationsController _controller;

    public AuthorizationsControllerSlaTests()
    {
        _repositoryMock = new Mock<IAuthorizationRepository>();
        _loggerMock = new Mock<ILogger<AuthorizationsController>>();
        _controller = new AuthorizationsController(_repositoryMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task GetAtRisk_ReturnsOnlyWarningAndAbove_ByDefault()
    {
        // Arrange: 3 auths — one at None level (recent), one at Warning, one at Critical
        var auths = new[]
        {
            CreateAuth("AUTH-001", "U", hoursAgo: 10),   // None — too early for warning
            CreateAuth("AUTH-002", "U", hoursAgo: 50),   // Warning (48h+)
            CreateAuth("AUTH-003", "U", hoursAgo: 65),   // Critical (64h+)
        };

        _repositoryMock
            .Setup(r => r.GetOpenAuthorizationsAsync(null))
            .ReturnsAsync(auths);

        // Act
        var result = await _controller.GetAtRiskAuthorizations();

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var statuses = okResult.Value.Should().BeAssignableTo<IEnumerable<AuthorizationSlaStatus>>().Subject.ToList();
        statuses.Should().HaveCount(2);
        statuses.Should().OnlyContain(s =>
            s.EscalationLevel >= SlaEscalationLevel.Warning);
    }

    [Fact]
    public async Task GetAtRisk_SortedByHoursRemainingAscending()
    {
        // Arrange: most urgent first
        var auths = new[]
        {
            CreateAuth("AUTH-001", "U", hoursAgo: 50),   // ~22h remaining
            CreateAuth("AUTH-002", "U", hoursAgo: 65),   // ~7h remaining
            CreateAuth("AUTH-003", "U", hoursAgo: 48),   // ~24h remaining
        };

        _repositoryMock
            .Setup(r => r.GetOpenAuthorizationsAsync(null))
            .ReturnsAsync(auths);

        // Act
        var result = await _controller.GetAtRiskAuthorizations();

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var statuses = okResult.Value.Should().BeAssignableTo<IEnumerable<AuthorizationSlaStatus>>().Subject.ToList();
        statuses.Should().BeInAscendingOrder(s => s.HoursRemaining);
    }

    [Fact]
    public async Task GetAtRisk_FiltersByTenant()
    {
        // Arrange
        var auths = new[]
        {
            CreateAuth("AUTH-001", "U", hoursAgo: 50, tenantId: "tenant-a"),
        };

        _repositoryMock
            .Setup(r => r.GetOpenAuthorizationsAsync("tenant-a"))
            .ReturnsAsync(auths);

        // Act
        var result = await _controller.GetAtRiskAuthorizations(
            minLevel: SlaEscalationLevel.Warning, tenantId: "tenant-a");

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var statuses = okResult.Value.Should().BeAssignableTo<IEnumerable<AuthorizationSlaStatus>>().Subject.ToList();
        statuses.Should().HaveCount(1);
        statuses[0].TenantId.Should().Be("tenant-a");
        _repositoryMock.Verify(r => r.GetOpenAuthorizationsAsync("tenant-a"), Times.Once);
    }

    private static Authorization CreateAuth(
        string authNumber, string levelOfService, double hoursAgo, string tenantId = "tenant-1") => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = tenantId,
        AuthorizationNumber = authNumber,
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
