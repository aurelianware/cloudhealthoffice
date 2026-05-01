using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using EngineModels = CloudHealthOffice.ClaimsScrubEngine.Models;
using EngineServices = CloudHealthOffice.ClaimsScrubEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.4 — behavior coverage for <see cref="ScrubbingStage"/>:
/// engine result → stage outcome translation, structured outcome on
/// context, exception → Reject, mapper exception → Reject.
/// </summary>
public class ScrubbingStageTests
{
    private const string TenantId = "tenant-1";
    private const string ClaimVersionId = "ver-1";
    private const string Npi = "1234567890";

    private readonly EngineServices.IClaimRoutingService _engine = Substitute.For<EngineServices.IClaimRoutingService>();

    private ScrubbingStage NewStage() =>
        new(_engine, NullLogger<ScrubbingStage>.Instance);

    [Fact]
    public void Stage_metadata_matches_pipeline_contract()
    {
        var stage = NewStage();

        Assert.Equal("Scrubbing", stage.Name);
        Assert.Equal(100, stage.Order);
        // Decision 4 — scrubbing is foundational; orchestrator must
        // not allow per-tenant disablement.
        Assert.True(stage.IsRequired);
    }

    [Fact]
    public async Task CleanResponse_returns_Pass_with_Approve_outcome()
    {
        _engine.ScrubAndRouteAsync(Arg.Any<EngineModels.ClaimsScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildResponse(errors: 0, warnings: 0, status: "clean"));

        var ctx = NewContext();
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(result.Continue);
        Assert.NotNull(ctx.ScrubbingResult);
        Assert.Equal(ScrubbingDecision.Approve, ctx.ScrubbingResult!.Decision);
        Assert.Empty(ctx.ScrubbingResult.Errors);
        Assert.Empty(ctx.ScrubbingResult.Warnings);
    }

    [Fact]
    public async Task WarningsOnly_returns_Pass_with_warnings_on_context()
    {
        _engine.ScrubAndRouteAsync(Arg.Any<EngineModels.ClaimsScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildResponse(
                errors: 0,
                warnings: 1,
                status: "flagged",
                warningResults: new[]
                {
                    NewValidationResult("AL002", "Total Matches Line Sum", EngineModels.ValidationSeverity.Warning),
                }));

        var ctx = NewContext();
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        // Decision 7 — warnings continue through the pipeline; the
        // outcome travels on context.ScrubbingResult.
        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(result.Continue);
        Assert.Equal(ScrubbingDecision.Approve, ctx.ScrubbingResult!.Decision);
        Assert.Empty(ctx.ScrubbingResult.Errors);
        Assert.Single(ctx.ScrubbingResult.Warnings);
        Assert.Equal("AL002", ctx.ScrubbingResult.Warnings[0].RuleId);
    }

    [Fact]
    public async Task Errors_return_Reject_and_short_circuit()
    {
        _engine.ScrubAndRouteAsync(Arg.Any<EngineModels.ClaimsScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildResponse(
                errors: 1,
                warnings: 0,
                status: "rejected",
                errorResults: new[]
                {
                    NewValidationResult("DC003", "Billing Provider NPI Required", EngineModels.ValidationSeverity.Error,
                        editCode: "562"),
                }));

        var ctx = NewContext();
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Reject, result.Outcome);
        Assert.False(result.Continue);
        Assert.Equal(ScrubbingDecision.RejectStructural, ctx.ScrubbingResult!.Decision);
        Assert.Single(ctx.ScrubbingResult.Errors);
        Assert.Equal("DC003", ctx.ScrubbingResult.Errors[0].RuleId);
        Assert.Equal("562", ctx.ScrubbingResult.Errors[0].EditCode);
        Assert.Contains("DC003", result.Reason);
    }

    [Fact]
    public async Task ErrorsAndWarnings_return_Reject_but_warnings_persist_on_context()
    {
        _engine.ScrubAndRouteAsync(Arg.Any<EngineModels.ClaimsScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildResponse(
                errors: 1,
                warnings: 1,
                status: "rejected",
                errorResults: new[] { NewValidationResult("DC001", "Subscriber MemberId Required", EngineModels.ValidationSeverity.Error) },
                warningResults: new[] { NewValidationResult("AL002", "Total Matches Line Sum", EngineModels.ValidationSeverity.Warning) }));

        var ctx = NewContext();
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Reject, result.Outcome);
        Assert.Equal(ScrubbingDecision.RejectStructural, ctx.ScrubbingResult!.Decision);
        Assert.Single(ctx.ScrubbingResult.Errors);
        // Warnings still surface for the audit trail even when an error
        // drives the rejection.
        Assert.Single(ctx.ScrubbingResult.Warnings);
    }

    [Fact]
    public async Task Engine_throwing_returns_Reject_with_structured_error()
    {
        _engine.ScrubAndRouteAsync(Arg.Any<EngineModels.ClaimsScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns<EngineModels.ClaimsScrubResponse>(_ => throw new InvalidOperationException("engine boom"));

        var ctx = NewContext();
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        // Decision 12 — catch in stage; produce structured outcome
        // rather than letting the orchestrator's safety-net Reject
        // produce an opaque "stage threw" result.
        Assert.Equal(ClaimAdjudicationOutcome.Reject, result.Outcome);
        Assert.Equal(ScrubbingDecision.RejectStructural, ctx.ScrubbingResult!.Decision);
        Assert.Single(ctx.ScrubbingResult.Errors);
        Assert.Equal("ENGINE_EXCEPTION", ctx.ScrubbingResult.Errors[0].RuleId);
        Assert.Contains("InvalidOperationException", result.Reason);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        _engine.ScrubAndRouteAsync(Arg.Any<EngineModels.ClaimsScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns<EngineModels.ClaimsScrubResponse>(_ => throw new OperationCanceledException());

        var ctx = NewContext();
        var sut = NewStage();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.ExecuteAsync(ctx, cts.Token));
    }

    [Fact]
    public async Task Engine_response_RoutingNote_captured_on_outcome()
    {
        _engine.ScrubAndRouteAsync(Arg.Any<EngineModels.ClaimsScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildResponse(
                errors: 0,
                warnings: 0,
                status: "clean",
                routingReason: "Claim is clean — routed to adjudication."));

        var ctx = NewContext();
        var sut = NewStage();

        await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("Claim is clean — routed to adjudication.", ctx.ScrubbingResult!.RoutingNote);
    }

    private static ClaimAdjudicationContext NewContext() => new()
    {
        TenantId = TenantId,
        ClaimVersionId = ClaimVersionId,
        Claim = new AdapterClaim
        {
            TenantId = TenantId,
            Id = ClaimVersionId,
            ClaimVersionId = ClaimVersionId,
            ClaimNumber = "CLM-1",
            MemberId = "MEM-1",
            SubscriberFirstName = "Pat",
            SubscriberLastName = "Roe",
            BillingProviderNPI = Npi,
            ClaimType = ClaimType.Professional,
            PlaceOfServiceCode = "11",
            TotalChargeAmount = 100m,
            ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
            DiagnosisCodes = new List<AdapterDiagnosisCode>
            {
                new() { Code = "Z00.00", PointerNumber = 1 },
            },
            ClaimLines = new List<AdapterClaimLine>
            {
                new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, Units = 1,
                        ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc) },
            },
        },
        ResolvedMember = new ResolvedMember
        {
            MemberId = "MEM-1",
            DateOfBirth = new DateTime(1980, 6, 15, 0, 0, 0, DateTimeKind.Utc),
        },
    };

    private static EngineModels.ClaimsScrubResponse BuildResponse(
        int errors,
        int warnings,
        string status,
        EngineModels.ValidationResult[]? errorResults = null,
        EngineModels.ValidationResult[]? warningResults = null,
        string routingReason = "Routed.")
    {
        var results = new List<EngineModels.ValidationResult>();
        if (errorResults is not null) results.AddRange(errorResults);
        if (warningResults is not null) results.AddRange(warningResults);

        return new EngineModels.ClaimsScrubResponse
        {
            Result = new EngineModels.ClaimValidationResult
            {
                ClaimId = ClaimVersionId,
                ClaimType = EngineModels.ClaimType.Professional,
                PatientControlNumber = "CLM-1",
                Status = status,
                RulesExecuted = 21,
                RulesPassed = 21 - errors - warnings,
                RulesFailed = errors + warnings,
                ErrorCount = errors,
                WarningCount = warnings,
                Results = results,
                ValidatedAt = DateTime.UtcNow.ToString("o"),
                Routing = new EngineModels.ClaimRoutingDecision
                {
                    Destination = errors > 0 ? "work-queue" : "adjudication",
                    Reason = routingReason,
                },
            },
        };
    }

    private static EngineModels.ValidationResult NewValidationResult(
        string ruleId, string ruleName, EngineModels.ValidationSeverity severity,
        string? editCode = null)
        => new()
        {
            RuleId = ruleId,
            RuleName = ruleName,
            Passed = false,
            Severity = severity,
            Message = $"{ruleName} failed",
            EditCode = editCode,
        };
}
