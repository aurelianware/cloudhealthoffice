using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>
/// The real <see cref="ClaimFinalizationService"/> and
/// <see cref="AccumulatorOutboxProcessor"/> behind the claims endpoints (only
/// the repository and benefit engine are substitutes, the repository keeping
/// the claim in memory):
/// <list type="bullet">
///   <item>A $0 remittance for an Approved claim (allowed amount applied to the
///     deductible) finalizes it as Paid, and its pending accumulator commit is
///     kept and lands; under the old path it became Denied and the outbox
///     dropped the commit, losing the member's deductible credit.</item>
///   <item>A $0 remittance for a Pended claim leaves it Pended (422).</item>
///   <item>DELETE {id} and PUT {id}/status (Voided) save and drive the void's
///     accumulator reversal, like POST {id}/void.</item>
/// </list>
/// </summary>
public class ZeroPayRemittanceAndVoidAccumulatorTests : IClassFixture<ClaimsApiFactory>, IDisposable
{
    private const string Tenant = "test-tenant";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid PlanId = Guid.NewGuid();

    private readonly ClaimsApiFactory _factory;
    private readonly WebApplicationFactory<Program> _host;
    private readonly HttpClient _client;
    private readonly IBenefitCalculationEngine _engine;
    private readonly Dictionary<string, Claim> _store = new();

    public ZeroPayRemittanceAndVoidAccumulatorTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _engine = factory.BenefitEngine;
        _engine.ClearReceivedCalls();
        _engine.CommitAccumulatorsAsync(Arg.Any<AccumulatorCommit>(), Arg.Any<CancellationToken>())
            .Returns(AccumulatorCommitResult.Of(AccumulatorCommitOutcome.Committed));

        var repo = factory.ClaimRepository;
        var outbox = new AccumulatorOutboxProcessor(
            repo, _engine, new AdjudicationTenantContext(), Options.Create(new AccumulatorOutboxOptions()),
            NullLogger<AccumulatorOutboxProcessor>.Instance);
        var finalization = new ClaimFinalizationService(
            repo, factory.VersionEventPublisher, Substitute.For<IClaimEventPublisher>(), factory.AdjustmentService,
            NullLogger<ClaimFinalizationService>.Instance, _engine, outbox);

        _host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(IClaimFinalizationService)).ToList())
                services.Remove(d);
            services.AddSingleton<IClaimFinalizationService>(finalization);
        }));
        _client = _host.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
    }

    public void Dispose() => _host.Dispose();

    /// <summary>A claim held in memory behind the substitute repository's reads and writes.</summary>
    private Claim Seed(ClaimStatus status, ClaimVersionState state, bool withPendingCommit = false)
    {
        var id = "zp-" + Guid.NewGuid().ToString("N");
        var claim = new Claim
        {
            Id = id,
            ClaimVersionId = id,
            TenantId = Tenant,
            ClaimNumber = "CLM-ZP",
            Status = status,
            VersionState = state,
            MemberId = "m1",
            SubscriberId = "s1",
            BenefitPlanId = PlanId.ToString(),
            BillingProviderNPI = "1234567893",
            LineOfBusiness = LineOfBusiness.Commercial,
            TotalChargeAmount = 100m,
            ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
            // Allowed $100, all of it the member's deductible: the plan pays $0.
            AdjudicationResult = new AdjudicationResult { AllowedAmount = 100m, DeductibleAmount = 100m, PayerPayment = 0m },
        };
        if (withPendingCommit)
        {
            claim.PendingAccumulatorCommit = AccumulatorOutboxItem.ForCommit(new AccumulatorCommit
            {
                CommitId = "commit-" + id,
                ClaimId = id,
                MemberId = "m1",
                SubscriberId = "s1",
                BenefitPlanId = PlanId,
                PlanYear = "2026",
            }, DateTime.UtcNow);
        }
        _store[id] = claim;

        var repo = _factory.ClaimRepository;
        repo.GetByIdAsync(id).Returns(_ => _store[id]);
        repo.GetForAccumulatorOutboxAsync(Tenant, id, Arg.Any<CancellationToken>()).Returns(_ => _store[id]);
        repo.UpdateAsync(Arg.Is<Claim>(c => c.Id == id)).Returns(ci => _store[id] = ci.Arg<Claim>());
        repo.CompleteAccumulatorOutboxAsync(Tenant, id, Arg.Any<AccumulatorOutboxKind>(), Arg.Any<string>(),
                Arg.Any<AccumulatorClampReview?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (ci.ArgAt<AccumulatorOutboxKind>(2) == AccumulatorOutboxKind.Commit)
                    _store[id].PendingAccumulatorCommit = null;
                else
                    _store[id].PendingAccumulatorReversal = null;
                return true;
            });
        repo.MarkVoidedProjectionAsync(Tenant, id, Arg.Any<DateTime>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<AccumulatorOutboxItem?>())
            .Returns(ci =>
            {
                var c = _store[id];
                c.Status = ClaimStatus.Voided;
                c.VersionState = ClaimVersionState.Voided;
                c.PendingAccumulatorReversal = ci.ArgAt<AccumulatorOutboxItem?>(5);
                return true;
            });
        return claim;
    }

    private static object Remittance(decimal amount, string? checkNumber = "0000100001") => new
    {
        controlNumber = "PR-0001",
        checkNumber,
        paymentDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
        paymentAmount = amount,
        paymentRunId = "run-1",
        eraEnvelopeId = "env-1",
    };

    [Fact]
    public async Task ZeroPayRemittance_ApprovedClaim_IsPaid_AndItsAccumulatorCommitIsKept()
    {
        var claim = Seed(ClaimStatus.Approved, ClaimVersionState.Adjudicated, withPendingCommit: true);
        var commitId = claim.PendingAccumulatorCommit!.Id;

        var response = await _client.PostAsJsonAsync($"/api/claims/{claim.Id}/remittance", Remittance(0m), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = _store[claim.Id];
        Assert.Equal(ClaimStatus.Paid, stored.Status);
        Assert.Equal(ClaimVersionState.Paid, stored.VersionState);
        Assert.Equal("0000100001", stored.AdjudicationResult!.CheckNumber);
        Assert.Equal(0m, stored.AdjudicationResult.PayerPayment);
        Assert.Equal("PR-0001", stored.EDI835ControlNumber);
        Assert.Equal(commitId, stored.PendingAccumulatorCommit?.Id); // survived the finalize write
        await _factory.VersionEventPublisher.Received(1).PublishVersionPaidAsync(
            Arg.Is<Claim>(c => c.Id == claim.Id), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        // The outbox commits it for the Paid claim (a Denied one would be dropped).
        var outcome = await new AccumulatorOutboxProcessor(
                _factory.ClaimRepository, _engine, new AdjudicationTenantContext(),
                Options.Create(new AccumulatorOutboxOptions()), NullLogger<AccumulatorOutboxProcessor>.Instance)
            .ProcessAsync(Tenant, claim.Id, AccumulatorOutboxKind.Commit, null, CancellationToken.None);

        Assert.Equal(AccumulatorOutboxResult.Committed, outcome);
        await _engine.Received(1).CommitAccumulatorsAsync(
            Arg.Is<AccumulatorCommit>(c => c.CommitId == commitId), Arg.Any<CancellationToken>());
        Assert.Null(_store[claim.Id].PendingAccumulatorCommit);

        // A repeat of the same remittance is an idempotent no-op.
        var repeat = await _client.PostAsJsonAsync($"/api/claims/{claim.Id}/remittance", Remittance(0m), Json);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        await _factory.VersionEventPublisher.Received(1).PublishVersionPaidAsync(
            Arg.Is<Claim>(c => c.Id == claim.Id), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ZeroPayRemittance_NoCheckNumber_IsKeyedOnTheControlNumber_AndRepeatsIdempotently()
    {
        var claim = Seed(ClaimStatus.Approved, ClaimVersionState.Adjudicated);

        var first = await _client.PostAsJsonAsync($"/api/claims/{claim.Id}/remittance", Remittance(0m, checkNumber: null), Json);
        var repeat = await _client.PostAsJsonAsync($"/api/claims/{claim.Id}/remittance", Remittance(0m, checkNumber: null), Json);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(ClaimStatus.Paid, _store[claim.Id].Status);
        Assert.Equal("PR-0001", _store[claim.Id].AdjudicationResult!.CheckNumber);
    }

    [Fact]
    public async Task ZeroPayRemittance_PendedClaim_StaysPended_422()
    {
        var claim = Seed(ClaimStatus.Pended, ClaimVersionState.Submitted);

        var response = await _client.PostAsJsonAsync($"/api/claims/{claim.Id}/remittance", Remittance(0m), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ClaimStatus.Pended, _store[claim.Id].Status);
        Assert.Null(_store[claim.Id].AdjudicationResult!.CheckNumber);
        await _factory.ClaimRepository.DidNotReceive().UpdateAsync(Arg.Is<Claim>(c => c.Id == claim.Id));
    }

    /// <summary>
    /// The payment cycle finalizes an adjudicated denial: a $0 remittance on
    /// a Deny claim (non-final) makes it Denied (terminal), records the 835
    /// trace, and emits ClaimVersionDenied; a repeat is an idempotent 200.
    /// </summary>
    [Fact]
    public async Task ZeroPayRemittance_DenyClaim_IsFinalizedToDenied_AndRepeatsIdempotently()
    {
        var claim = Seed(ClaimStatus.Deny, ClaimVersionState.Adjudicated);
        claim.AdjudicationResult = new AdjudicationResult { PayerPayment = 0m, DenialReasonCode = "27", DenialReason = "Coverage terminated" };

        var response = await _client.PostAsJsonAsync($"/api/claims/{claim.Id}/remittance", Remittance(0m, checkNumber: "PR-0001-D1"), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = _store[claim.Id];
        Assert.Equal(ClaimStatus.Denied, stored.Status);
        Assert.Equal(ClaimVersionState.Denied, stored.VersionState);
        Assert.Equal("PR-0001-D1", stored.AdjudicationResult!.CheckNumber);
        Assert.Equal("27", stored.AdjudicationResult.DenialReasonCode);
        Assert.Equal("PR-0001", stored.EDI835ControlNumber);
        await _factory.VersionEventPublisher.Received(1).PublishVersionDeniedAsync(
            Arg.Is<Claim>(c => c.Id == claim.Id), "Coverage terminated", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        var repeat = await _client.PostAsJsonAsync($"/api/claims/{claim.Id}/remittance", Remittance(0m, checkNumber: "PR-0001-D1"), Json);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        await _factory.VersionEventPublisher.Received(1).PublishVersionDeniedAsync(
            Arg.Is<Claim>(c => c.Id == claim.Id), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A paid remittance never finalizes a Deny claim: 422, unchanged.</summary>
    [Fact]
    public async Task PaidRemittance_DenyClaim_Is422_Unchanged()
    {
        var claim = Seed(ClaimStatus.Deny, ClaimVersionState.Adjudicated);

        var response = await _client.PostAsJsonAsync($"/api/claims/{claim.Id}/remittance", Remittance(80m), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ClaimStatus.Deny, _store[claim.Id].Status);
        await _factory.ClaimRepository.DidNotReceive().UpdateAsync(Arg.Is<Claim>(c => c.Id == claim.Id));
    }

    /// <summary>
    /// Denials write no accumulators in either phase: a commit left on a claim
    /// that is now Deny (an examiner denied it after Pay) is dropped, never
    /// written.
    /// </summary>
    [Theory]
    [InlineData(ClaimStatus.Deny, ClaimVersionState.Adjudicated)]
    [InlineData(ClaimStatus.Denied, ClaimVersionState.Denied)]
    public async Task AccumulatorOutbox_DropsTheCommit_OfADeniedClaim_InEitherPhase(ClaimStatus status, ClaimVersionState state)
    {
        var claim = Seed(status, state, withPendingCommit: true);

        var outcome = await new AccumulatorOutboxProcessor(
                _factory.ClaimRepository, _engine, new AdjudicationTenantContext(),
                Options.Create(new AccumulatorOutboxOptions()), NullLogger<AccumulatorOutboxProcessor>.Instance)
            .ProcessAsync(Tenant, claim.Id, AccumulatorOutboxKind.Commit, null, CancellationToken.None);

        Assert.Equal(AccumulatorOutboxResult.Dropped, outcome);
        await _engine.DidNotReceiveWithAnyArgs().CommitAccumulatorsAsync(default!, default);
        Assert.Null(_store[claim.Id].PendingAccumulatorCommit);
    }

    [Fact]
    public async Task ZeroPayRemittance_DeniedClaim_StaysDenied()
    {
        var claim = Seed(ClaimStatus.Denied, ClaimVersionState.Denied);

        var response = await _client.PostAsJsonAsync($"/api/claims/{claim.Id}/remittance", Remittance(0m), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ClaimStatus.Denied, _store[claim.Id].Status);
        await _factory.ClaimRepository.DidNotReceive().UpdateAsync(Arg.Is<Claim>(c => c.Id == claim.Id));
    }

    [Fact]
    public async Task Delete_PaidClaim_SavesAndDrivesTheAccumulatorReversal()
    {
        var claim = Seed(ClaimStatus.Paid, ClaimVersionState.Paid);

        var response = await _client.DeleteAsync($"/api/claims/{claim.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ClaimStatus.Voided, _store[claim.Id].Status);
        await _factory.ClaimRepository.Received(1).MarkVoidedProjectionAsync(
            Tenant, claim.Id, Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(),
            Arg.Is<AccumulatorOutboxItem?>(r => r != null && r.Reversal!.ClaimId == claim.Id));
        await _engine.Received(1).ReverseClaimAsync(
            "m1", "s1", PlanId, new DateOnly(2026, 4, 15), claim.Id, Arg.Any<CancellationToken>());
        Assert.Null(_store[claim.Id].PendingAccumulatorReversal); // driven and cleared
        await _factory.ClaimRepository.DidNotReceive().UpdateAsync(Arg.Is<Claim>(c => c.Id == claim.Id));
    }

    [Fact]
    public async Task PutStatusVoided_PaidClaim_SavesAndDrivesTheAccumulatorReversal()
    {
        var claim = Seed(ClaimStatus.Paid, ClaimVersionState.Paid);

        var response = await _client.PutAsJsonAsync(
            $"/api/claims/{claim.Id}/status", new { status = (int)ClaimStatus.Voided, notes = "duplicate" }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ClaimStatus.Voided, _store[claim.Id].Status);
        await _factory.ClaimRepository.Received(1).MarkVoidedProjectionAsync(
            Tenant, claim.Id, Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(),
            Arg.Is<AccumulatorOutboxItem?>(r => r != null));
        await _engine.Received(1).ReverseClaimAsync(
            "m1", "s1", PlanId, Arg.Any<DateOnly>(), claim.Id, Arg.Any<CancellationToken>());
        await _factory.VersionEventPublisher.Received(1).PublishVersionVoidedAsync(
            Arg.Is<Claim>(c => c.Id == claim.Id), "duplicate", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_ApprovedUnpaidClaim_IsRefused_Unchanged()
    {
        var claim = Seed(ClaimStatus.Approved, ClaimVersionState.Adjudicated, withPendingCommit: true);

        var response = await _client.DeleteAsync($"/api/claims/{claim.Id}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ClaimStatus.Approved, _store[claim.Id].Status);
        await _factory.ClaimRepository.DidNotReceive().MarkVoidedProjectionAsync(
            Tenant, claim.Id, Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<AccumulatorOutboxItem?>());
    }
}
