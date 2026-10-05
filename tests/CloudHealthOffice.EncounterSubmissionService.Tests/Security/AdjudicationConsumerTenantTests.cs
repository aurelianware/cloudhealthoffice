using EncounterSubmissionService.KafkaConsumers;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.Security;

/// <summary>
/// The adjudication-completed consumer takes the tenant from the message. A
/// message without one is an error: no record is created in an empty tenant.
/// </summary>
public class AdjudicationConsumerTenantTests
{
    private static AdjudicationCompletedMessage FlMedicaid(string tenantId) => new()
    {
        TenantId = tenantId,
        ClaimId = "claim-1",
        ClaimNumber = "CLM-1",
        AdjudicatedAt = DateTime.UtcNow,
        LineOfBusiness = "Medicaid",
        StateCode = "FL",
        Status = "Paid"
    };

    private static (AdjudicationCompletedConsumer Consumer, Mock<IEncounterSubmissionService> Service) Create()
    {
        var service = new Mock<IEncounterSubmissionService>();
        service.Setup(s => s.CreateSubmissionRecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync((string claim, string tenant, DateTime at) =>
                new EncounterSubmission { ClaimId = claim, TenantId = tenant, ClaimAdjudicatedAt = at });
        return (new AdjudicationCompletedConsumer(service.Object, NullLogger<AdjudicationCompletedConsumer>.Instance), service);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MessageWithoutTenant_CreatesNoRecord(string tenant)
    {
        var (consumer, service) = Create();

        await consumer.ProcessMessageAsync(FlMedicaid(tenant));

        service.Verify(s => s.CreateSubmissionRecordAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task MessageWithTenant_CreatesRecordInThatTenant()
    {
        var (consumer, service) = Create();

        await consumer.ProcessMessageAsync(FlMedicaid("tenant-1"));

        service.Verify(s => s.CreateSubmissionRecordAsync("claim-1", "tenant-1", It.IsAny<DateTime>()), Times.Once);
    }
}
