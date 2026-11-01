using EncounterSubmissionService.KafkaConsumers;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.Services;
using EncounterSubmissionService.StateRoutes;
using Microsoft.Extensions.Logging.Abstractions;
using static CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes.StateRouteTestData;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes;

/// <summary>
/// The consumer admits a state only when its route is configured for the tenant,
/// and sends Texas / California records to the state route, never to FMMIS.
/// </summary>
public class StateRouteConsumerTests
{
    private static AdjudicationCompletedMessage Message(string state, string lob = "Medicaid") => new()
    {
        TenantId = "tenant-1",
        ClaimId = "claim-1",
        ClaimNumber = "CLM-1",
        AdjudicatedAt = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc),
        LineOfBusiness = lob,
        StateCode = state,
        Status = "Paid"
    };

    private static (AdjudicationCompletedConsumer, Mock<IEncounterSubmissionService>, Mock<IStateEncounterBatchService>)
        Create(IStateEncounterRouteRegistry registry)
    {
        var fl = new Mock<IEncounterSubmissionService>();
        fl.Setup(s => s.CreateSubmissionRecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new EncounterSubmission());
        var state = new Mock<IStateEncounterBatchService>();
        state.Setup(s => s.CreateSubmissionRecordAsync(It.IsAny<StateEncounterRoute>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync((StateEncounterRoute r, string c, string t, DateTime at) =>
                new EncounterSubmission { StateCode = r.StateCode, ClaimId = c, TenantId = t });
        var consumer = new AdjudicationCompletedConsumer(fl.Object, NullLogger<AdjudicationCompletedConsumer>.Instance,
            registry, state.Object);
        return (consumer, fl, state);
    }

    [Theory]
    [InlineData("TX")]
    [InlineData("CA")]
    public async Task UnconfiguredState_CreatesNoRecord(string stateCode)
    {
        var (consumer, fl, state) = Create(StateEncounterRouteRegistry.FloridaOnly());

        await consumer.ProcessMessageAsync(Message(stateCode));

        fl.VerifyNoOtherCalls();
        state.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfiguredTexas_GoesToTheTmhpRoute_NotFmmis()
    {
        var (consumer, fl, state) = Create(Registry(tx: TexasOptions()));

        await consumer.ProcessMessageAsync(Message("TX"));

        state.Verify(s => s.CreateSubmissionRecordAsync(StateEncounterRoute.Texas, "claim-1", "tenant-1",
            It.IsAny<DateTime>()), Times.Once);
        fl.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfiguredCalifornia_GoesToTheDhcsRoute()
    {
        var (consumer, fl, state) = Create(Registry(ca: CaliforniaOptions()));

        await consumer.ProcessMessageAsync(Message("CA"));

        state.Verify(s => s.CreateSubmissionRecordAsync(StateEncounterRoute.California, "claim-1", "tenant-1",
            It.IsAny<DateTime>()), Times.Once);
        fl.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfiguredTexas_StillRefusesNonMedicaid()
    {
        var (consumer, fl, state) = Create(Registry(tx: TexasOptions()));

        await consumer.ProcessMessageAsync(Message("TX", "Commercial"));

        fl.VerifyNoOtherCalls();
        state.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Florida_IsUnchanged()
    {
        var (consumer, fl, state) = Create(Registry(tx: TexasOptions(), ca: CaliforniaOptions()));

        await consumer.ProcessMessageAsync(Message("FL"));

        fl.Verify(s => s.CreateSubmissionRecordAsync("claim-1", "tenant-1", It.IsAny<DateTime>()), Times.Once);
        state.VerifyNoOtherCalls();
    }
}
