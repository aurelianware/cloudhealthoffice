using AuthorizationService.Controllers;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AuthorizationService.Tests.Controllers;

public class AuthorizationsControllerSlaTests
{
    private readonly Mock<IAuthorizationRepository> _repositoryMock;
    private readonly Mock<IWebHostEnvironment> _environmentMock;
    private readonly Mock<ILogger<AuthorizationsController>> _loggerMock;
    private readonly AuthorizationsController _controller;

    public AuthorizationsControllerSlaTests()
    {
        _repositoryMock = new Mock<IAuthorizationRepository>();
        _environmentMock = new Mock<IWebHostEnvironment>();
        _environmentMock.SetupGet(x => x.EnvironmentName).Returns("Test");
        _loggerMock = new Mock<ILogger<AuthorizationsController>>();
        _controller = new AuthorizationsController(_repositoryMock.Object, _environmentMock.Object, _loggerMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    [Fact]
    public async Task ValidateAuthorization_AllowsAnonymousLocalValidation()
    {
        var authorization = CreateApprovedAuth("AUTH-001", expiresOn: new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc));
        _repositoryMock
            .Setup(r => r.GetByAuthorizationNumberAsync("AUTH-001"))
            .ReturnsAsync(authorization);

        var result = await _controller.ValidateAuthorization(
            "AUTH-001",
            procedureCode: "99201",
            serviceDate: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<AuthorizationValidationResponse>().Subject;
        response.IsValid.Should().BeFalse();
        response.ValidationMessage.Should().Be("Authorization expired or not yet active");
    }

    [Fact]
    public async Task ValidateAuthorization_ForbidsAnonymousProductionValidation()
    {
        _environmentMock.SetupGet(x => x.EnvironmentName).Returns("Production");

        var result = await _controller.ValidateAuthorization(
            "AUTH-001",
            procedureCode: "99201",
            serviceDate: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        result.Result.Should().BeOfType<ForbidResult>();
        _repositoryMock.Verify(r => r.GetByAuthorizationNumberAsync(It.IsAny<string>()), Times.Never);
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

    private static Authorization CreateApprovedAuth(string authNumber, DateTime expiresOn) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = "tenant-1",
        AuthorizationNumber = authNumber,
        MemberId = "MBR-001",
        PatientFirstName = "Jane",
        PatientLastName = "Doe",
        PatientDateOfBirth = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        LineOfBusiness = LineOfBusiness.Medicaid,
        RequestingProviderNPI = "1234567890",
        AuthorizationType = AuthorizationType.PreAuthorization,
        ServiceTypeCode = "1",
        LevelOfService = "U",
        RequestedServiceDateFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        ApprovedServiceDateFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        ApprovedServiceDateTo = expiresOn,
        ExpirationDate = expiresOn,
        Status = AuthorizationStatus.Approved,
        SubmittedDate = DateTime.UtcNow.AddDays(-7),
        RequestedServices =
        {
            new RequestedService
            {
                ProcedureCode = "99201",
                ServiceStatus = "A1",
                RequestedUnits = 1,
                ApprovedUnits = 1
            }
        }
    };
}
