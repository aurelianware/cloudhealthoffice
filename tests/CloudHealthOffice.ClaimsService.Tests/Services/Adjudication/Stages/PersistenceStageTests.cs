using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

public class PersistenceStageTests
{
    private readonly IClaimRepository _repository = Substitute.For<IClaimRepository>();
    private readonly PersistenceStage _sut;

    public PersistenceStageTests()
    {
        _sut = new PersistenceStage(_repository, NullLogger<PersistenceStage>.Instance);
    }

    [Fact]
    public void OrderingAndRequirementContract_MatchSpec()
    {
        Assert.Equal("Persistence", _sut.Name);
        Assert.Equal(999, _sut.Order);
        Assert.True(_sut.IsRequired);
    }

    [Fact]
    public async Task Execute_CallsBypassMethod_NotRegularUpdate()
    {
        var ctx = BuildContext();
        ctx.AdjudicationResult = new AdjudicationResult { AllowedAmount = 75m };
        ctx.LineAdjudicationResults = new List<LineAdjudicationResult>
        {
            new() { AllowedAmount = 75m, PaidAmount = 50m, PatientResponsibility = 25m },
        };
        StubRepositoryReturns(true);

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _repository.Received(1).UpdateAdjudicationProjectionAsync(
            "tenant-1",
            "ver-1",
            Arg.Is<AdjudicationResult>(a => a.AllowedAmount == 75m),
            Arg.Is<IReadOnlyList<LineAdjudicationResult>>(l => l.Count == 1),
            Arg.Any<CancellationToken>(),
            Arg.Any<PendDetails?>(),
            Arg.Is<bool>(isPend => !isPend),
            Arg.Is<ClaimStatus?>(status => status == ClaimStatus.Approved));
    }

    [Fact]
    public async Task Execute_BypassReturnsFalse_StageReturnsReject()
    {
        var ctx = BuildContext();
        StubRepositoryReturns(false);

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Reject, result.Outcome);
    }

    [Fact]
    public async Task Execute_RepositoryThrows_BubblesException()
    {
        var ctx = BuildContext();
        _repository.UpdateAdjudicationProjectionAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<AdjudicationResult>(),
            Arg.Any<IReadOnlyList<LineAdjudicationResult>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<PendDetails?>(),
            Arg.Any<bool>(),
            Arg.Any<ClaimStatus?>())
            .Throws(new InvalidOperationException("cosmos down"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ExecuteAsync(ctx, CancellationToken.None));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Defect A fix — isPend resolution (Reject > Deny > Pend > Pass over
    // every stage that ran before Persistence, since Persistence is
    // always Order=999/last).
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_WhenPriorStageResultIsPend_PassesIsPendTrue()
    {
        var ctx = BuildContext();
        ctx.PendDetails = new PendDetails { PendCode = "NCCI", PendReason = "bundled pair" };
        ctx.StageResults.Add(ClaimAdjudicationStageResult.Pend("NcciEdits", "pended for NCCI/MUE review"));
        StubRepositoryReturns(true);

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        await _repository.Received(1).UpdateAdjudicationProjectionAsync(
            "tenant-1", "ver-1",
            Arg.Any<AdjudicationResult>(), Arg.Any<IReadOnlyList<LineAdjudicationResult>>(),
            Arg.Any<CancellationToken>(),
            Arg.Is<PendDetails?>(p => p!.PendCode == "NCCI"),
            Arg.Is<bool>(isPend => isPend),
            Arg.Is<ClaimStatus?>(status => status == ClaimStatus.Pended));
    }

    [Fact]
    public async Task Execute_WhenNoPriorStageResults_PassesIsPendFalse()
    {
        var ctx = BuildContext();
        StubRepositoryReturns(true);

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        await _repository.Received(1).UpdateAdjudicationProjectionAsync(
            "tenant-1", "ver-1",
            Arg.Any<AdjudicationResult>(), Arg.Any<IReadOnlyList<LineAdjudicationResult>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<PendDetails?>(),
            Arg.Is<bool>(isPend => !isPend),
            Arg.Is<ClaimStatus?>(status => status == ClaimStatus.Approved));
    }

    [Fact]
    public async Task Execute_WhenPriorStageResultIsDeny_PassesIsPendFalse_EvenWithPendDetailsPresent()
    {
        // NcciEditsStage records the deterministic edit-failure snapshot on
        // PendDetails unconditionally, regardless of NcciMode — so a Deny-mode
        // claim can carry non-null PendDetails while the resolved outcome is
        // Deny, not Pend. isPend must reflect the resolved outcome (Deny wins
        // over Pend), not merely "PendDetails is non-null".
        var ctx = BuildContext();
        ctx.PendDetails = new PendDetails { PendCode = "NCCI", PendReason = "bundled pair (Deny mode)" };
        ctx.StageResults.Add(ClaimAdjudicationStageResult.Deny("NcciEdits", "denied for NCCI/MUE failure"));
        StubRepositoryReturns(true);

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        await _repository.Received(1).UpdateAdjudicationProjectionAsync(
            "tenant-1", "ver-1",
            Arg.Any<AdjudicationResult>(), Arg.Any<IReadOnlyList<LineAdjudicationResult>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<PendDetails?>(),
            Arg.Is<bool>(isPend => !isPend),
            Arg.Is<ClaimStatus?>(status => status == ClaimStatus.Denied));
    }

    [Fact]
    public async Task Execute_WhenPriorStageResultIsReject_PassesIsPendFalse()
    {
        var ctx = BuildContext();
        ctx.StageResults.Add(ClaimAdjudicationStageResult.Reject("Scrubbing", "missing NPI"));
        StubRepositoryReturns(true);

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        await _repository.Received(1).UpdateAdjudicationProjectionAsync(
            "tenant-1", "ver-1",
            Arg.Any<AdjudicationResult>(), Arg.Any<IReadOnlyList<LineAdjudicationResult>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<PendDetails?>(),
            Arg.Is<bool>(isPend => !isPend),
            Arg.Is<ClaimStatus?>(status => status == ClaimStatus.Denied));
    }

    private void StubRepositoryReturns(bool value) =>
        _repository.UpdateAdjudicationProjectionAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<AdjudicationResult>(),
            Arg.Any<IReadOnlyList<LineAdjudicationResult>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<PendDetails?>(),
            Arg.Any<bool>(),
            Arg.Any<ClaimStatus?>())
            .Returns(value);

    private static ClaimAdjudicationContext BuildContext() => new()
    {
        TenantId = "tenant-1",
        ClaimVersionId = "ver-1",
        Claim = new AdapterClaim
        {
            TenantId = "tenant-1",
            Id = "ver-1",
            ClaimVersionId = "ver-1",
            MemberId = "MEM-1",
            BillingProviderNPI = "1234567890",
        },
    };
}
