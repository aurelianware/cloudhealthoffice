using AuthorizationService.Models;
using AuthorizationService.Services.Rfai;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuthorizationService.Tests.Services;

/// <summary>
/// The additional-information request names the actor who recorded the A4
/// decision (LastUpdatedBy, from the token), not the body-supplied 278
/// reviewer contact name.
/// </summary>
public class RfaiRequestedByTests
{
    [Fact]
    public async Task RequestedBy_IsTheTokenActor_NotTheBodyReviewerName()
    {
        RfaiRequestCommand? sent = null;
        var gateway = new Mock<IRfaiRequestGateway>();
        gateway.Setup(g => g.EnsureRequestAsync(It.IsAny<RfaiRequestCommand>(), It.IsAny<CancellationToken>()))
            .Callback<RfaiRequestCommand, CancellationToken>((c, _) => sent = c)
            .ReturnsAsync(new RfaiRequestHandle { Id = "rfai-1", TrackingId = "RFAI-1", Created = true });

        var coordinator = new PendedAuthorizationRfaiCoordinator(
            gateway.Object, NullLogger<PendedAuthorizationRfaiCoordinator>.Instance);

        var authorization = new Authorization
        {
            Id = "auth-1",
            TenantId = "tenant-a",
            AuthorizationNumber = "AUTH-1",
            Status = AuthorizationStatus.Pended,
            ReviewDecision = "A4",
            ReviewerName = "attacker-supplied",
            LastUpdatedBy = "um-reviewer-7",
        };

        await coordinator.EnsureRequestForDecisionAsync(
            authorization,
            new[] { new RequestedInformationItem { Description = "Operative notes" } },
            dueDate: null,
            decisionControlNumber: "CTL-1");

        sent.Should().NotBeNull();
        sent!.RequestedBy.Should().Be("um-reviewer-7");
    }
}
