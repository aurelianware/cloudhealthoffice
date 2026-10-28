using AuthorizationService.Backends;
using AuthorizationService.Controllers;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthorizationService.Tests.Controllers;

public class AuthorizationsControllerSlaTests
{
    private readonly Mock<IAuthorizationRepository> _repositoryMock;
    private readonly Mock<IWebHostEnvironment> _environmentMock;
    private readonly Mock<ILogger<AuthorizationsController>> _loggerMock;
    private readonly Mock<ICurrentActor> _actorMock;
    private readonly AuthorizationsController _controller;

    public AuthorizationsControllerSlaTests()
    {
        _repositoryMock = new Mock<IAuthorizationRepository>();
        _environmentMock = new Mock<IWebHostEnvironment>();
        _environmentMock.SetupGet(x => x.EnvironmentName).Returns("Test");
        _loggerMock = new Mock<ILogger<AuthorizationsController>>();
        _actorMock = new Mock<ICurrentActor>();
        _actorMock.SetupGet(a => a.TenantId).Returns("tenant-1");
        _actorMock.SetupGet(a => a.UserId).Returns("reviewer-1");

        // Default Replace mode: create routes through the CHO-native backend,
        // which delegates to the mocked repository.
        var selector = new AuthorizationBackendSelector(
            new IAuthorizationBackend[] { new ChoAuthorizationBackend(_repositoryMock.Object) },
            Options.Create(new AuthorizationBackendOptions()));

        _controller = new AuthorizationsController(
            _repositoryMock.Object, selector, _environmentMock.Object, _loggerMock.Object, _actorMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    [Fact]
    public async Task ValidateAuthorization_ExpiredAuthorization_IsInvalid()
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
            .Setup(r => r.GetOpenAuthorizationsAsync("tenant-1"))
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
            .Setup(r => r.GetOpenAuthorizationsAsync("tenant-1"))
            .ReturnsAsync(auths);

        // Act
        var result = await _controller.GetAtRiskAuthorizations();

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var statuses = okResult.Value.Should().BeAssignableTo<IEnumerable<AuthorizationSlaStatus>>().Subject.ToList();
        statuses.Should().BeInAscendingOrder(s => s.HoursRemaining);
    }

    [Fact]
    public async Task GetAtRisk_UsesTheTokenTenant()
    {
        // The tenant comes from the token (ICurrentActor), never from a
        // ?tenantId= query parameter (removed: it allowed cross-tenant reads).
        _actorMock.SetupGet(a => a.TenantId).Returns("tenant-a");

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
            minLevel: SlaEscalationLevel.Warning);

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var statuses = okResult.Value.Should().BeAssignableTo<IEnumerable<AuthorizationSlaStatus>>().Subject.ToList();
        statuses.Should().HaveCount(1);
        statuses[0].TenantId.Should().Be("tenant-a");
        _repositoryMock.Verify(r => r.GetOpenAuthorizationsAsync("tenant-a"), Times.Once);
    }

    [Fact]
    public async Task ExtendDecisionDeadline_PersistsTheLawfulExtension()
    {
        var auth = CreateAuth("AUTH-EXT", "E", hoursAgo: 24);
        auth.LineOfBusiness = LineOfBusiness.Medicare;
        AuthorizationService.Services.AuthorizationDeadlinePolicy.Initialize(auth);
        _repositoryMock.Setup(r => r.GetByIdAsync(auth.Id)).ReturnsAsync(auth);
        AuthorizationDecisionExtension? written = null;
        _repositoryMock
            .Setup(r => r.ExtendDecisionDeadlineAsync(auth, It.IsAny<AuthorizationDecisionExtension>(), It.IsAny<PaReportingFact?>(), It.IsAny<CancellationToken>()))
            .Callback<Authorization, AuthorizationDecisionExtension, PaReportingFact?, CancellationToken>((_, e, _, _) => written = e)
            .Returns(Task.CompletedTask);

        var result = await _controller.ExtendDecisionDeadline(auth.Id, new ExtendDecisionDeadline
        {
            Basis = DecisionExtensionBasis.EnrolleeRequested, Reason = "Member asked for time to send records",
        });

        result.Result.Should().BeOfType<OkObjectResult>();
        written!.GrantedBy.Should().Be("reviewer-1", "the actor comes from the token");
        written.ExtendedDueAt.Should().Be(written.OriginalDueAt.AddDays(14));
    }

    [Fact]
    public async Task ExtendDecisionDeadline_OnACommercialRequest_ConflictsAndWritesNothing()
    {
        var auth = CreateAuth("AUTH-COM", "E", hoursAgo: 24);
        _repositoryMock.Setup(r => r.GetByIdAsync(auth.Id)).ReturnsAsync(auth);

        var result = await _controller.ExtendDecisionDeadline(auth.Id, new ExtendDecisionDeadline
        {
            Basis = DecisionExtensionBasis.EnrolleeRequested, Reason = "Member asked",
        });

        result.Result.Should().BeOfType<ConflictObjectResult>();
        _repositoryMock.Verify(r => r.ExtendDecisionDeadlineAsync(It.IsAny<Authorization>(),
            It.IsAny<AuthorizationDecisionExtension>(), It.IsAny<PaReportingFact?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordExtensionNotice_RecordsTheLetterOnceAndIsIdempotent()
    {
        var auth = CreateAuth("AUTH-NOTICE", "E", hoursAgo: 24);
        auth.DecisionExtension = new AuthorizationDecisionExtension
        {
            GrantedBy = "reviewer-1", Basis = DecisionExtensionBasis.PlanNeedsAdditionalInformation, Reason = "Records requested",
        };
        _repositoryMock.Setup(r => r.GetByIdAsync(auth.Id)).ReturnsAsync(auth);
        _repositoryMock.Setup(r => r.RecordExtensionNoticeAsync(auth, It.IsAny<ExtensionMemberNotice>(), It.IsAny<CancellationToken>()))
            .Callback<Authorization, ExtensionMemberNotice, CancellationToken>((a, n, _) => a.DecisionExtension!.MemberNotice = n)
            .Returns(Task.CompletedTask);

        var first = await _controller.RecordExtensionNotice(auth.Id, new RecordExtensionNotice { LetterId = "LTR-1" });
        first.Result.Should().BeOfType<OkObjectResult>();
        auth.DecisionExtension.MemberNotice!.RecordedBy.Should().Be("reviewer-1");

        (await _controller.RecordExtensionNotice(auth.Id, new RecordExtensionNotice { LetterId = "LTR-1" }))
            .Result.Should().BeOfType<OkObjectResult>();
        (await _controller.RecordExtensionNotice(auth.Id, new RecordExtensionNotice { LetterId = "LTR-2" }))
            .Result.Should().BeOfType<ConflictObjectResult>();
        _repositoryMock.Verify(r => r.RecordExtensionNoticeAsync(It.IsAny<Authorization>(), It.IsAny<ExtensionMemberNotice>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordExtensionNotice_WithoutAnExtension_Conflicts()
    {
        var auth = CreateAuth("AUTH-NOEXT", "E", hoursAgo: 24);
        _repositoryMock.Setup(r => r.GetByIdAsync(auth.Id)).ReturnsAsync(auth);
        (await _controller.RecordExtensionNotice(auth.Id, new RecordExtensionNotice { LetterId = "LTR-1" }))
            .Result.Should().BeOfType<ConflictObjectResult>();
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
