using AuthorizationService.Consumers;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using Microsoft.Extensions.Logging;

namespace AuthorizationService.Tests.Consumers;

public class RfaiDocsReceivedConsumerTests
{
    private readonly Mock<IAuthorizationRepository> _repositoryMock;
    private readonly Mock<ILogger<RfaiDocsReceivedConsumer>> _loggerMock;

    public RfaiDocsReceivedConsumerTests()
    {
        _repositoryMock = new Mock<IAuthorizationRepository>();
        _loggerMock = new Mock<ILogger<RfaiDocsReceivedConsumer>>();
    }

    [Fact]
    public async Task WhenAllDocsReceived_SetsAuthStatusToInReview()
    {
        // Arrange
        var auth = CreatePendedAuthorization();
        _repositoryMock
            .Setup(r => r.GetByAuthorizationNumberAsync(auth.AuthorizationNumber))
            .ReturnsAsync(auth);
        _repositoryMock
            .Setup(r => r.UpdateAsync(It.IsAny<Authorization>()))
            .ReturnsAsync((Authorization a) => a);

        var message = CreateMessage(auth.AuthorizationNumber, allReceived: true);

        var consumer = new RfaiDocsReceivedConsumer(
            _repositoryMock.Object, _loggerMock.Object);

        // Act
        await consumer.ProcessMessageAsync(message);

        // Assert
        auth.Status.Should().Be(AuthorizationStatus.InReview);
        _repositoryMock.Verify(r => r.UpdateAsync(auth), Times.Once);
    }

    [Fact]
    public async Task WhenAllDocsReceived_SetsSlaResumedAt()
    {
        // Arrange
        var auth = CreatePendedAuthorization();
        var receivedAt = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);

        _repositoryMock
            .Setup(r => r.GetByAuthorizationNumberAsync(auth.AuthorizationNumber))
            .ReturnsAsync(auth);
        _repositoryMock
            .Setup(r => r.UpdateAsync(It.IsAny<Authorization>()))
            .ReturnsAsync((Authorization a) => a);

        var message = CreateMessage(auth.AuthorizationNumber, allReceived: true, receivedAt);

        var consumer = new RfaiDocsReceivedConsumer(
            _repositoryMock.Object, _loggerMock.Object);

        // Act
        await consumer.ProcessMessageAsync(message);

        // Assert
        auth.SlaResumedAt.Should().Be(receivedAt);
    }

    [Fact]
    public async Task WhenPartialDocsReceived_KeepsStatusAsPended()
    {
        // Arrange
        var auth = CreatePendedAuthorization();
        _repositoryMock
            .Setup(r => r.GetByAuthorizationNumberAsync(auth.AuthorizationNumber))
            .ReturnsAsync(auth);

        var message = CreateMessage(auth.AuthorizationNumber, allReceived: false);

        var consumer = new RfaiDocsReceivedConsumer(
            _repositoryMock.Object, _loggerMock.Object);

        // Act
        await consumer.ProcessMessageAsync(message);

        // Assert
        auth.Status.Should().Be(AuthorizationStatus.Pended);
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Authorization>()), Times.Never);
    }

    [Fact]
    public async Task WhenPartialDocsReceived_DoesNotSetSlaResumedAt()
    {
        // Arrange
        var auth = CreatePendedAuthorization();
        _repositoryMock
            .Setup(r => r.GetByAuthorizationNumberAsync(auth.AuthorizationNumber))
            .ReturnsAsync(auth);

        var message = CreateMessage(auth.AuthorizationNumber, allReceived: false);

        var consumer = new RfaiDocsReceivedConsumer(
            _repositoryMock.Object, _loggerMock.Object);

        // Act
        await consumer.ProcessMessageAsync(message);

        // Assert
        auth.SlaResumedAt.Should().BeNull();
    }

    [Fact]
    public async Task WhenAuthNotFound_LogsWarningAndContinues()
    {
        // Arrange
        _repositoryMock
            .Setup(r => r.GetByAuthorizationNumberAsync(It.IsAny<string>()))
            .ReturnsAsync((Authorization?)null);

        var message = CreateMessage("UNKNOWN-AUTH-123", allReceived: true);

        var consumer = new RfaiDocsReceivedConsumer(
            _repositoryMock.Object, _loggerMock.Object);

        // Act — should not throw
        await consumer.ProcessMessageAsync(message);

        // Assert
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Authorization>()), Times.Never);
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("UNKNOWN-AUTH-123")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task WhenAuthAlreadyApproved_SkipsUpdate()
    {
        // Arrange
        var auth = CreatePendedAuthorization();
        auth.Status = AuthorizationStatus.Approved;

        _repositoryMock
            .Setup(r => r.GetByAuthorizationNumberAsync(auth.AuthorizationNumber))
            .ReturnsAsync(auth);

        var message = CreateMessage(auth.AuthorizationNumber, allReceived: true);

        var consumer = new RfaiDocsReceivedConsumer(
            _repositoryMock.Object, _loggerMock.Object);

        // Act
        await consumer.ProcessMessageAsync(message);

        // Assert
        auth.Status.Should().Be(AuthorizationStatus.Approved);
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Authorization>()), Times.Never);
    }

    private static Authorization CreatePendedAuthorization() => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = "tenant-1",
        AuthorizationNumber = "AUTH-2026-001",
        MemberId = "MBR-001",
        PatientFirstName = "Jane",
        PatientLastName = "Doe",
        PatientDateOfBirth = new DateTime(1990, 1, 1),
        LineOfBusiness = LineOfBusiness.Commercial,
        RequestingProviderNPI = "1234567890",
        AuthorizationType = AuthorizationType.PreAuthorization,
        ServiceTypeCode = "1",
        RequestedServiceDateFrom = DateTime.UtcNow.AddDays(-10),
        Status = AuthorizationStatus.Pended,
        RFAIIssued = true,
        SubmittedDate = DateTime.UtcNow.AddDays(-14),
    };

    private static RfaiDocsReceivedMessage CreateMessage(
        string authNumber,
        bool allReceived,
        DateTime? receivedAt = null) => new()
    {
        TenantId = "tenant-1",
        RfaiCaseId = Guid.NewGuid().ToString(),
        AuthNumber = authNumber,
        ReceivedAt = receivedAt ?? DateTime.UtcNow,
        AttachmentIds = new List<string> { "att-1" },
        AllRequestedItemsReceived = allReceived
    };
}
