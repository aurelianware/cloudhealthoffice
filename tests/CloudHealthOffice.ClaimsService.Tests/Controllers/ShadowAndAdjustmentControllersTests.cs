using ClaimsService.Controllers;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication.Shadow;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>
/// Shadow adjudication's settings and run endpoints, and the claim correction
/// (adjustment) endpoints: request validation, the tenant always being the
/// caller's, and every service outcome mapping to its documented status code.
/// </summary>
public class ShadowAndAdjustmentControllersTests
{
    private const string Tenant = "tenant-1";
    private const string User = "examiner-7";

    private readonly ICurrentActor _actor = Substitute.For<ICurrentActor>();

    public ShadowAndAdjustmentControllersTests() => _actor.UserId.Returns(User);

    private static ControllerContext Context(string? tenant = Tenant)
    {
        var http = new DefaultHttpContext();
        if (tenant is not null) http.Items["TenantId"] = tenant;
        return new ControllerContext { HttpContext = http };
    }

    // ── Shadow adjudication ────────────────────────────────────────────

    private readonly IShadowAdjudicationService _shadow = Substitute.For<IShadowAdjudicationService>();
    private readonly IShadowAdjudicationRepository _shadowStore = Substitute.For<IShadowAdjudicationRepository>();
    private readonly InMemoryShadowAdjudicationTenantSettingsRepository _settings = new();

    private ShadowAdjudicationController Shadow(bool deploymentOn = true, params string[] tenants) =>
        new(_shadow, _shadowStore,
            new ShadowAdjudicationTenants(Options.Create(new ShadowAdjudicationOptions { Enabled = deploymentOn, Tenants = tenants.ToList() }), _settings),
            _actor, new FixedTime(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero)))
        { ControllerContext = Context() };

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Settings_describe_the_deployment_switch_and_the_tenants_choice()
    {
        var view = Assert.IsType<ShadowAdjudicationSettingsView>(
            Assert.IsType<OkObjectResult>((await Shadow(deploymentOn: false).GetSettings()).Result).Value);

        Assert.Equal(Tenant, view.TenantId);
        Assert.False(view.EnabledForDeployment);
        Assert.False(view.Running);
    }

    [Fact]
    public async Task Turning_shadow_on_saves_who_and_when_for_the_callers_tenant_only()
    {
        var result = await Shadow(true, "someone-else").PutSettings(new ShadowAdjudicationSettingsRequest { Enabled = true });

        var view = Assert.IsType<ShadowAdjudicationSettingsView>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(view.Enabled);
        Assert.True(view.Running);
        Assert.Equal(User, view.UpdatedBy);
        Assert.Equal(new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc), view.UpdatedAt);
        Assert.Null(await _settings.GetAsync("someone-else"));
    }

    [Fact]
    public async Task Turning_shadow_on_does_not_run_while_the_deployment_switch_is_off()
    {
        var result = await Shadow(deploymentOn: false).PutSettings(new ShadowAdjudicationSettingsRequest { Enabled = true });

        var view = Assert.IsType<ShadowAdjudicationSettingsView>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(view.Enabled);
        Assert.False(view.Running);
    }

    [Fact]
    public async Task Settings_without_a_body_are_400_and_nothing_is_saved()
    {
        Assert.IsType<BadRequestObjectResult>((await Shadow().PutSettings(null)).Result);
        Assert.Null(await _settings.GetAsync(Tenant));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_must_name_claims_or_an_import_batch(bool emptyList)
    {
        var request = emptyList ? new ShadowAdjudicationRunRequest { ClaimIds = [] } : null;

        Assert.IsType<BadRequestObjectResult>((await Shadow().Run(request, default)).Result);
        await _shadow.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task A_run_limit_below_one_is_400()
    {
        var result = await Shadow().Run(new ShadowAdjudicationRunRequest { ImportBatchId = "b1", Limit = 0 }, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task A_run_returns_its_totals_for_the_callers_tenant()
    {
        var request = new ShadowAdjudicationRunRequest { ImportBatchId = "batch-1" };
        _shadow.RunAsync(Tenant, request, User, Arg.Any<CancellationToken>())
            .Returns(new ShadowAdjudicationRunResponse { Run = new ShadowAdjudicationRun { Id = "run-1" }, Results = [] });

        var result = await Shadow().Run(request, default);

        Assert.Equal("run-1", Assert.IsType<ShadowAdjudicationRun>(Assert.IsType<OkObjectResult>(result.Result).Value).Id);
    }

    [Fact]
    public async Task A_run_while_shadow_is_off_is_409()
    {
        _shadow.RunAsync(Tenant, Arg.Any<ShadowAdjudicationRunRequest>(), User, Arg.Any<CancellationToken>())
            .Throws(new ShadowAdjudicationDisabledException("Shadow adjudication is off for this tenant."));

        var result = await Shadow().Run(new ShadowAdjudicationRunRequest { ClaimIds = ["C1"] }, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Shadow_runs_are_listed_and_read_per_tenant()
    {
        _shadowStore.ListRunsAsync(Tenant, 1000, Arg.Any<CancellationToken>()).Returns([new ShadowAdjudicationRun { Id = "run-1" }]);
        _shadowStore.GetRunAsync(Tenant, "run-1", Arg.Any<CancellationToken>()).Returns(new ShadowAdjudicationRun { Id = "run-1" });

        Assert.IsType<OkObjectResult>((await Shadow().ListRuns(1_000_000)).Result);
        Assert.IsType<OkObjectResult>((await Shadow().GetRun("run-1", default)).Result);
        Assert.IsType<NotFoundResult>((await Shadow().GetRun("not-mine", default)).Result);
    }

    [Fact]
    public async Task Shadow_results_reject_an_unknown_kind_and_404_an_unknown_run()
    {
        Assert.IsType<BadRequestObjectResult>((await Shadow().ListResults("run-1", kind: "Wrong")).Result);
        Assert.IsType<NotFoundResult>((await Shadow().ListResults("not-mine")).Result);
        Assert.IsType<NotFoundResult>((await Shadow().Report("not-mine")).Result);
        Assert.IsType<NotFoundResult>(await Shadow().DiscrepanciesCsv("not-mine"));
    }

    [Fact]
    public async Task Shadow_results_filter_by_kind()
    {
        _shadowStore.GetRunAsync(Tenant, "run-1", Arg.Any<CancellationToken>()).Returns(new ShadowAdjudicationRun { Id = "run-1" });
        _shadowStore.ListResultsAsync(Tenant, "run-1", ShadowDiscrepancyKind.PaidVariance, 200, Arg.Any<CancellationToken>())
            .Returns([new ShadowAdjudicationResult()]);

        var result = await Shadow().ListResults("run-1", ShadowDiscrepancyKind.PaidVariance);

        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ShadowAdjudicationResult>>(Assert.IsType<OkObjectResult>(result.Result).Value));
    }

    // ── Claim adjustments (correct a claim) ────────────────────────────

    private readonly IClaimAdjustmentService _adjustments = Substitute.For<IClaimAdjustmentService>();
    private readonly IClaimAdjustmentRepository _adjustmentStore = Substitute.For<IClaimAdjustmentRepository>();
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();

    private ClaimAdjustmentsController Adjustments(string? tenant = Tenant) =>
        new(_adjustments, _adjustmentStore, _claims, _actor) { ControllerContext = Context(tenant) };

    private static ClaimAdjustment Adjustment() => new()
    {
        Id = "adj-1", TenantId = Tenant, PredecessorClaimId = "C1", NewClaimId = "C1-A", AdjustmentReason = "Rate correction",
    };

    private static ClaimAdjustmentRequest Request() => new() { AdjustmentReason = "Rate correction" };

    private void Returns(ClaimAdjustmentResult result) =>
        _adjustments.CreateAdjustmentAsync("C1", Arg.Any<ClaimAdjustmentRequest>(), "key-1", Tenant, User, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(result);

    [Fact]
    public async Task The_correction_draft_is_404_for_another_tenants_claim()
    {
        _claims.GetByIdAsync("C1").Returns(new Claim { Id = "C1", TenantId = "other-tenant" });

        Assert.IsType<NotFoundObjectResult>(await Adjustments().GetCorrectionDraft("C1"));
    }

    [Fact]
    public async Task The_correction_draft_is_404_for_a_missing_claim()
    {
        _claims.GetByIdAsync("C1").Returns((Claim?)null);

        Assert.IsType<NotFoundObjectResult>(await Adjustments().GetCorrectionDraft("C1"));
    }

    [Fact]
    public async Task An_adjustment_needs_a_body_and_an_idempotency_key()
    {
        Assert.IsType<BadRequestObjectResult>(await Adjustments().CreateAdjustment("C1", null!, "key-1"));
        Assert.IsType<BadRequestObjectResult>(await Adjustments().CreateAdjustment("C1", Request(), null));
        Assert.IsType<BadRequestObjectResult>(await Adjustments().CreateAdjustment("C1", Request(), " "));
        await _adjustments.DidNotReceiveWithAnyArgs().CreateAdjustmentAsync(default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task A_new_adjustment_is_201_pointing_at_it()
    {
        Returns(new ClaimAdjustmentResult { Outcome = ClaimAdjustmentOutcome.Created, Adjustment = Adjustment() });

        var created = Assert.IsType<CreatedAtActionResult>(await Adjustments().CreateAdjustment("C1", Request(), "key-1"));

        Assert.Equal(nameof(ClaimAdjustmentsController.GetAdjustment), created.ActionName);
        Assert.Equal("adj-1", created.RouteValues!["id"]);
        Assert.Equal("C1-A", Assert.IsType<ClaimAdjustmentResponse>(created.Value).NewClaimId);
    }

    [Fact]
    public async Task A_replayed_request_returns_the_existing_adjustment_with_200()
    {
        Returns(ClaimAdjustmentResult.AlreadyExists(Adjustment()));

        var ok = Assert.IsType<OkObjectResult>(await Adjustments().CreateAdjustment("C1", Request(), "key-1"));

        Assert.Equal("adj-1", Assert.IsType<ClaimAdjustmentResponse>(ok.Value).Id);
    }

    [Fact]
    public async Task Idempotency_and_in_flight_conflicts_are_409_naming_the_existing_adjustment()
    {
        Returns(ClaimAdjustmentResult.IdempotencyConflict(Adjustment(), "Key reused with a different body."));
        var first = Assert.IsType<ConflictObjectResult>(await Adjustments().CreateAdjustment("C1", Request(), "key-1"));
        Assert.Contains("adj-1", first.Value!.ToString());

        Returns(ClaimAdjustmentResult.ConflictingAdjustment(Adjustment(), "An adjustment is already in flight."));
        var second = Assert.IsType<ConflictObjectResult>(await Adjustments().CreateAdjustment("C1", Request(), "key-1"));
        Assert.Contains("adj-1", second.Value!.ToString());
    }

    [Fact]
    public async Task A_missing_predecessor_is_404()
    {
        Returns(ClaimAdjustmentResult.PredecessorNotFound("Claim C1 not found."));

        Assert.IsType<NotFoundObjectResult>(await Adjustments().CreateAdjustment("C1", Request(), "key-1"));
    }

    [Fact]
    public async Task Adjusting_a_claim_in_the_wrong_state_or_an_adjustment_of_an_adjustment_is_422()
    {
        var predecessor = new Claim { Id = "C1", TenantId = Tenant, Status = ClaimStatus.Submitted };

        Returns(ClaimAdjustmentResult.InvalidSourceState(predecessor, "Only paid or denied claims can be adjusted."));
        Assert.IsType<UnprocessableEntityObjectResult>(await Adjustments().CreateAdjustment("C1", Request(), "key-1"));

        Returns(ClaimAdjustmentResult.DepthLimitExceeded(predecessor, "Adjust the latest version."));
        Assert.IsType<UnprocessableEntityObjectResult>(await Adjustments().CreateAdjustment("C1", Request(), "key-1"));
    }

    [Fact]
    public async Task A_rejected_corrected_claim_is_400_and_an_unsupported_adapter_is_501()
    {
        var errors = new[] { new ValidationError { Field = "ClaimLines[0].ProcedureCode", Code = "Required", Message = "Required" } };

        Returns(ClaimAdjustmentResult.SubmissionFailed("Invalid claim.", errors, ClaimSubmissionFailureKind.Validation));
        Assert.IsType<BadRequestObjectResult>(await Adjustments().CreateAdjustment("C1", Request(), "key-1"));

        Returns(ClaimAdjustmentResult.SubmissionFailed("Not supported.", errors, ClaimSubmissionFailureKind.NotImplemented));
        Assert.Equal(StatusCodes.Status501NotImplemented,
            Assert.IsType<ObjectResult>(await Adjustments().CreateAdjustment("C1", Request(), "key-1")).StatusCode);
    }

    [Fact]
    public async Task Adjustments_are_read_for_the_callers_tenant()
    {
        _adjustmentStore.GetByIdAsync(Tenant, "adj-1", Arg.Any<CancellationToken>()).Returns(Adjustment());
        _adjustmentStore.ListAsync(Tenant, Arg.Is<ClaimAdjustmentListFilter>(f => f.PredecessorClaimId == "C1"), Arg.Any<CancellationToken>())
            .Returns((new List<ClaimAdjustment> { Adjustment() }, 1));

        Assert.IsType<OkObjectResult>(await Adjustments().GetAdjustment("adj-1"));
        Assert.IsType<NotFoundObjectResult>(await Adjustments().GetAdjustment("adj-other"));
        var list = Assert.IsType<ClaimAdjustmentListResponse>(
            Assert.IsType<OkObjectResult>(await Adjustments().ListAdjustmentsForClaim("C1")).Value);
        Assert.Equal(1, list.Total);
    }

    [Fact]
    public async Task Without_a_tenant_the_adjustment_endpoints_refuse_rather_than_read_across_tenants()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Adjustments(tenant: null).GetAdjustment("adj-1"));
    }
}
