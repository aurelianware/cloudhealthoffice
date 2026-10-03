using CapitationService.Repositories;
using CapitationService.Services;
using Microsoft.Extensions.Logging;

namespace CapitationService.Tests.Support;

/// <summary>The real maker-checker rule with the tenant setting stubbed.</summary>
public static class TestSeparationOfDuties
{
    public static PaymentSeparationOfDuties Create(
        bool enforced = true,
        ICapitationRunRepository? runs = null,
        ILogger<PaymentSeparationOfDuties>? logger = null,
        string tenantId = "tenant-1")
    {
        var controls = new Mock<ITenantPaymentControls>();
        controls.Setup(c => c.IsSeparationOfDutiesEnforcedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(enforced);
        return new PaymentSeparationOfDuties(
            runs ?? Mock.Of<ICapitationRunRepository>(),
            controls.Object,
            new TestActor(tenantId: tenantId),
            logger ?? Mock.Of<ILogger<PaymentSeparationOfDuties>>());
    }
}
