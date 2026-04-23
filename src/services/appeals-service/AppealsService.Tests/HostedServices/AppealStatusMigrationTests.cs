using AppealsService.HostedServices;
using AppealsService.Models;

namespace AppealsService.Tests.HostedServices;

/// <summary>
/// Unit tests for the legacy-status → (Closed + ClosureReasonCode)
/// mapping. Full end-to-end migration (Mongo scan + rewrite + audit-event
/// emission) is covered via in-memory fakes in the integration-style
/// smoke tests — this suite pins the mapping table itself.
/// </summary>
public class AppealStatusMigrationTests
{
    [Theory]
    [InlineData("Approved", AppealClosureReasonCode.Approved)]
    [InlineData("Denied", AppealClosureReasonCode.Denied)]
    [InlineData("PartialApproval", AppealClosureReasonCode.PartialApproval)]
    [InlineData("Withdrawn", AppealClosureReasonCode.Withdrawn)]
    public void MapLegacyStatus_ProducesExpectedReason(string legacy, AppealClosureReasonCode expected)
    {
        AppealStatusMigrationHostedService.MapLegacyStatus(legacy).Should().Be(expected);
    }

    [Theory]
    [InlineData("UnknownStatus")]
    [InlineData("Draft")]       // non-terminal — should not reach the migration filter
    [InlineData("Submitted")]   // same
    public void MapLegacyStatus_UnknownFallsBackToOther(string legacy)
    {
        AppealStatusMigrationHostedService.MapLegacyStatus(legacy).Should().Be(AppealClosureReasonCode.Other);
    }
}
