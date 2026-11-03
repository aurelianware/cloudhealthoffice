using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Shadow;
using ClaimsService.Services.Adjudication.Stages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Shadow;

public class ShadowAdjudicationServiceTests
{
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly InMemoryClaimImportTransactionRepository _transactions = new();
    private readonly IShadowAdjudicationPipeline _pipeline = Substitute.For<IShadowAdjudicationPipeline>();
    private readonly IShadowAdjudicationRepository _store = Substitute.For<IShadowAdjudicationRepository>();
    private readonly ShadowAdjudicationOptions _options = new() { Enabled = true };
    private readonly InMemoryShadowAdjudicationTenantSettingsRepository _tenantSettings = new();

    public ShadowAdjudicationServiceTests()
    {
        _pipeline.EvaluateShadowAsync(Arg.Any<string>(), Arg.Any<AdapterClaim>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Passed(ci.ArgAt<AdapterClaim>(1), allowed: 80m, paid: 60m, liability: 20m));
    }

    [Fact]
    public async Task ARunIsRefused_UnlessShadowAdjudicationIsEnabledForTheTenant()
    {
        _options.Enabled = false;
        await Assert.ThrowsAsync<ShadowAdjudicationDisabledException>(() => Run(new() { ClaimIds = ["cpcds-1"] }));

        _options.Enabled = true;
        _options.Tenants.Add("another-tenant");
        await Assert.ThrowsAsync<ShadowAdjudicationDisabledException>(() => Run(new() { ClaimIds = ["cpcds-1"] }));
    }

    [Fact]
    public async Task ATenantsSavedChoice_ReplacesConfiguration_ButNotTheDeploymentSwitch()
    {
        Store(Imported("cpcds-1"));
        var tenants = Tenants();

        // Not on the configured list, but the tenant turned it on from the portal.
        _options.Tenants.Add("another-tenant");
        await tenants.SaveAsync("tenant-1", true, "admin", DateTime.UtcNow);
        var view = await tenants.DescribeAsync("tenant-1");
        Assert.True(view.Running);
        Assert.Equal(ShadowAdjudicationSettingsSource.Stored, view.Source);
        Assert.Single((await Run(new() { ClaimIds = ["cpcds-1"] })).Results);

        // Turned off by the tenant, though configuration (every tenant) would allow it.
        _options.Tenants.Clear();
        await tenants.SaveAsync("tenant-1", false, "admin", DateTime.UtcNow);
        var off = await Assert.ThrowsAsync<ShadowAdjudicationDisabledException>(() => Run(new() { ClaimIds = ["cpcds-1"] }));
        Assert.Contains("off for this tenant", off.Message);

        // On for the tenant, but the deployment's switch is off: nothing runs.
        await tenants.SaveAsync("tenant-1", true, "admin", DateTime.UtcNow);
        _options.Enabled = false;
        view = await tenants.DescribeAsync("tenant-1");
        Assert.True(view.Enabled);
        Assert.False(view.Running);
        var deployment = await Assert.ThrowsAsync<ShadowAdjudicationDisabledException>(() => Run(new() { ClaimIds = ["cpcds-1"] }));
        Assert.Contains("off in this deployment", deployment.Message);
    }

    [Fact]
    public async Task WithNoSavedChoice_ConfigurationDecides()
    {
        var tenants = Tenants();
        Assert.True((await tenants.DescribeAsync("tenant-1")).Running);
        Assert.Equal(ShadowAdjudicationSettingsSource.Configuration, (await tenants.DescribeAsync("tenant-1")).Source);

        _options.Tenants.Add("another-tenant");
        Assert.False((await tenants.DescribeAsync("tenant-1")).Enabled);
        Assert.True((await tenants.DescribeAsync("another-tenant")).Running);
    }

    [Fact]
    public async Task ImportedClaims_AreComparedAndStored_OthersAreSkipped()
    {
        Store(Imported("cpcds-1"));
        var native = ShadowDiscrepancyComparerTests.CorePaid(80m, 60m, 20m);
        native.ClaimVersionId = "cho-native";
        native.ExternalSource = null;
        Store(native);

        var response = await Run(new() { ClaimIds = ["cpcds-1", "cho-native", "missing"] });

        var result = Assert.Single(response.Results);
        Assert.Equal(ShadowDiscrepancyKind.Match, result.Kind);
        Assert.Equal(response.Run.Id, result.RunId);
        Assert.Equal(2, response.Run.Skipped.Count);
        Assert.Equal(1m, response.Run.MatchRate);
        await _store.Received(1).SaveRunAsync(response.Run, Arg.Is<IReadOnlyList<ShadowAdjudicationResult>>(r => r.Count == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheClaimTheStoreHolds_IsNeverHandedToThePipeline()
    {
        var core = Imported("cpcds-1");
        Store(core);
        AdapterClaim? seen = null;
        _pipeline.EvaluateShadowAsync(Arg.Any<string>(), Arg.Any<AdapterClaim>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                seen = ci.ArgAt<AdapterClaim>(1);
                seen.BenefitPlanId = "resolved-by-the-pipeline";
                seen.ClaimLines[0].ChargeAmount = 1m;
                return Passed(seen, 80m, 60m, 20m);
            });

        await Run(new() { ClaimIds = ["cpcds-1"] });

        Assert.NotNull(seen);
        Assert.Null(seen!.AdjudicationResult);
        Assert.Null(seen.ClaimLines[0].AdjudicationResult);
        Assert.Equal(ClaimStatus.Submitted, seen.Status);
        Assert.Equal(ClaimStatus.Paid, core.Status);
        Assert.Equal(100m, core.ClaimLines[0].ChargeAmount);
        Assert.Null(core.BenefitPlanId);
        Assert.Equal(60m, core.AdjudicationResult!.PayerPayment);
        await _claims.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Fact]
    public async Task ABatchRun_PicksTheClaimsOfThatImportBatch_UpToTheLimit()
    {
        foreach (var id in new[] { "cpcds-1", "cpcds-2", "cpcds-3" })
        {
            Store(Imported(id));
            await _transactions.CreateAsync(new ClaimImportTransaction
            {
                TenantId = "tenant-1", ImportBatchId = "batch-1", ClaimId = id, Format = "cpcds",
            });
        }
        await _transactions.CreateAsync(new ClaimImportTransaction
        {
            TenantId = "tenant-1", ImportBatchId = "batch-1", ClaimId = null, Outcome = "Rejected",
        });
        await _transactions.CreateAsync(new ClaimImportTransaction
        {
            TenantId = "tenant-1", ImportBatchId = "batch-2", ClaimId = "cpcds-9",
        });

        var response = await Run(new() { ImportBatchId = "batch-1", Limit = 2 });

        Assert.Equal(2, response.Run.ClaimsRequested);
        Assert.Equal("batch-1", response.Run.ImportBatchId);
        Assert.All(response.Results, r => Assert.NotEqual("cpcds-9", r.ClaimId));
    }

    [Fact]
    public async Task AFailingClaim_IsRecorded_AndTheRunCarriesOn()
    {
        Store(Imported("cpcds-1"));
        Store(Imported("cpcds-2"));
        _pipeline.EvaluateShadowAsync(Arg.Any<string>(), Arg.Is<AdapterClaim>(c => c.ClaimVersionId == "cpcds-1"),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<ClaimAdjudicationContext>(_ => throw new InvalidOperationException("boom"));

        var response = await Run(new() { ClaimIds = ["cpcds-1", "cpcds-2"] });

        Assert.Equal(["cpcds-1: InvalidOperationException"], response.Run.Failed);
        Assert.Single(response.Results);
    }

    [Fact]
    public async Task ANotFinalCoreClaim_IsNotRunThroughThePipeline()
    {
        var pended = Imported("cpcds-1");
        pended.Status = ClaimStatus.Pended;
        Store(pended);

        var response = await Run(new() { ClaimIds = ["cpcds-1"] });

        Assert.Equal(ShadowDiscrepancyKind.NotComparable, Assert.Single(response.Results).Kind);
        await _pipeline.DidNotReceiveWithAnyArgs().EvaluateShadowAsync(default!, default!, default, default, default);
        Assert.Null(response.Run.MatchRate);
    }

    [Fact]
    public void TheRunTotals_SeparateUndecidedFromDecided()
    {
        var run = new ShadowAdjudicationRun();
        ShadowAdjudicationService.Summarize(run,
        [
            new() { Kind = ShadowDiscrepancyKind.Match },
            new() { Kind = ShadowDiscrepancyKind.PaidVariance, PaidVariance = -30m, AbsolutePaidVariance = 30m },
            new() { Kind = ShadowDiscrepancyKind.AllowedVariance, PaidVariance = 10m, AbsolutePaidVariance = 10m },
            new() { Kind = ShadowDiscrepancyKind.Match },
            new() { Kind = ShadowDiscrepancyKind.ChoUndecided },
            new() { Kind = ShadowDiscrepancyKind.NotComparable },
        ]);

        Assert.Equal(6, run.ClaimsCompared);
        Assert.Equal(0.5m, run.MatchRate);
        Assert.Equal(0.2m, run.UndecidedRate);
        Assert.Equal(-20m, run.NetPaidVariance);
        Assert.Equal(40m, run.AbsolutePaidVariance);
        Assert.Equal(2, run.CountsByKind[ShadowDiscrepancyKind.Match]);
        Assert.Equal(0, run.CountsByKind[ShadowDiscrepancyKind.OutcomeMismatch]);
    }

    private Task<ShadowAdjudicationRunResponse> Run(ShadowAdjudicationRunRequest request) =>
        new ShadowAdjudicationService(
                _claims, _transactions, _pipeline, _store, Tenants(),
                NullLogger<ShadowAdjudicationService>.Instance)
            .RunAsync("tenant-1", request, "actor-1", CancellationToken.None);

    // ── background runs ─────────────────────────────────────────────────

    [Fact]
    public async Task ABackgroundRun_AdjudicatesTheWholeBatch_InPages_AndSummarizes()
    {
        _options.MaxClaimsPerRun = 2;
        var store = new InMemoryShadowAdjudicationRepository();
        for (var i = 1; i <= 5; i++)
        {
            Store(Imported($"cpcds-{i}"));
            await _transactions.CreateAsync(new ClaimImportTransaction
            {
                TenantId = "tenant-1", ImportBatchId = "batch-1", ClaimId = $"cpcds-{i}",
            });
        }
        var service = Service(store);

        var queued = await service.QueueAsync("tenant-1", new() { ImportBatchId = "batch-1", Background = true }, "actor-1", default);
        Assert.Equal(ShadowRunStatus.Queued, queued.Status);
        Assert.Empty(await store.ListResultClaimIdsAsync("tenant-1", queued.Id));

        var taken = await store.TakeNextBackgroundRunAsync(DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5));
        Assert.NotNull(taken);
        Assert.Null(await store.TakeNextBackgroundRunAsync(DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5)));
        await service.ProcessBackgroundRunAsync(taken!, default);

        var run = await store.GetRunAsync("tenant-1", queued.Id);
        Assert.Equal(ShadowRunStatus.Completed, run!.Status);
        Assert.Equal(5, run.ClaimsRequested);
        Assert.Equal(5, run.ClaimsProcessed);
        Assert.Equal(5, run.ClaimsCompared);
        Assert.Equal(1m, run.MatchRate);
        Assert.Null(run.LeaseUntilUtc);
        // Three pages of at most two claims.
        await _pipeline.Received(5).EvaluateShadowAsync("tenant-1", Arg.Any<AdapterClaim>(), "actor-1", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ABackgroundRun_ResumesAfterTheClaimsItAlreadyHas()
    {
        var store = new InMemoryShadowAdjudicationRepository();
        foreach (var id in new[] { "cpcds-1", "cpcds-2", "gone" })
            await _transactions.CreateAsync(new ClaimImportTransaction { TenantId = "tenant-1", ImportBatchId = "batch-1", ClaimId = id });
        Store(Imported("cpcds-1"));
        Store(Imported("cpcds-2"));
        var run = new ShadowAdjudicationRun
        {
            TenantId = "tenant-1", ImportBatchId = "batch-1", Background = true, Status = ShadowRunStatus.Running,
            ClaimsRequested = 50_000, Skipped = ["gone: not found"],
        };
        await store.SaveRunStateAsync(run);
        await store.AppendResultsAsync([new ShadowAdjudicationResult { TenantId = "tenant-1", RunId = run.Id, ClaimId = "cpcds-1", Kind = ShadowDiscrepancyKind.Match }]);

        await Service(store).ProcessBackgroundRunAsync(run, default);

        // Only cpcds-2 is adjudicated now; the earlier result and the skip are kept.
        await _pipeline.Received(1).EvaluateShadowAsync(Arg.Any<string>(), Arg.Any<AdapterClaim>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Equal(2, run.ClaimsCompared);
        Assert.Single(run.Skipped);
        Assert.Equal(3, run.ClaimsProcessed);
    }

    [Fact]
    public async Task ABackgroundRun_FailsWithAReason_WhenShadowWasTurnedOffMeanwhile()
    {
        var store = new InMemoryShadowAdjudicationRepository();
        var run = new ShadowAdjudicationRun { TenantId = "tenant-1", ImportBatchId = "batch-1", Background = true, Status = ShadowRunStatus.Running };
        _options.Enabled = false;

        await Service(store).ProcessBackgroundRunAsync(run, default);

        Assert.Equal(ShadowRunStatus.Failed, run.Status);
        Assert.Contains("off in this deployment", run.Error);
    }

    [Fact]
    public async Task ARunningRunWhoseLeasePassed_IsTakenOver()
    {
        var store = new InMemoryShadowAdjudicationRepository();
        var run = new ShadowAdjudicationRun
        {
            TenantId = "tenant-1", Background = true, Status = ShadowRunStatus.Running, LeaseUntilUtc = DateTime.UtcNow.AddMinutes(1),
        };
        await store.SaveRunStateAsync(run);

        Assert.Null(await store.TakeNextBackgroundRunAsync(DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5)));
        Assert.NotNull(await store.TakeNextBackgroundRunAsync(DateTime.UtcNow.AddMinutes(2), DateTime.UtcNow.AddMinutes(7)));
    }

    [Fact]
    public async Task TheWorker_MarksARunFailed_WhenProcessingThrows_AndSaysWhenNothingWaits()
    {
        var store = new InMemoryShadowAdjudicationRepository();
        var service = Substitute.For<IShadowAdjudicationService>();
        service.ProcessBackgroundRunAsync(Arg.Any<ShadowAdjudicationRun>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IShadowAdjudicationRepository>(services, store);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, service);
        var tenantContext = new AdjudicationTenantContext();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IAdjudicationTenantContext>(services, tenantContext);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var worker = new ShadowAdjudicationWorker(
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(provider),
            Options.Create(_options), NullLogger<ShadowAdjudicationWorker>.Instance);

        Assert.False(await worker.RunNextAsync(default));

        var run = new ShadowAdjudicationRun { TenantId = "tenant-1", Background = true, Status = ShadowRunStatus.Queued };
        await store.SaveRunStateAsync(run);
        Assert.True(await worker.RunNextAsync(default));
        Assert.Equal("tenant-1", tenantContext.TenantId);

        var stored = await store.GetRunAsync("tenant-1", run.Id);
        Assert.Equal(ShadowRunStatus.Failed, stored!.Status);
        Assert.Contains("InvalidOperationException", stored.Error);
    }

    private ShadowAdjudicationService Service(IShadowAdjudicationRepository store) =>
        new(_claims, _transactions, _pipeline, store, Tenants(), NullLogger<ShadowAdjudicationService>.Instance);

    private ShadowAdjudicationTenants Tenants() => new(Options.Create(_options), _tenantSettings);

    private void Store(Claim claim) =>
        _claims.GetLatestVersionAsync(claim.ClaimVersionId, Arg.Any<DateTime>()).Returns(claim);

    private static Claim Imported(string chainId)
    {
        var claim = ShadowDiscrepancyComparerTests.CorePaid(80m, 60m, 20m);
        claim.ClaimVersionId = chainId;
        claim.Id = chainId + "-v1";
        return claim;
    }

    private static ClaimAdjudicationContext Passed(AdapterClaim claim, decimal allowed, decimal paid, decimal liability)
    {
        var context = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.ClaimVersionId,
            Claim = claim,
            IsShadow = true,
            MemberLookup = MemberLookupOutcome.Found,
            AdjudicationResult = new AdjudicationResult
            {
                AllowedAmount = allowed, PayerPayment = paid, PatientResponsibility = liability,
            },
            LineAdjudicationResults =
            [
                new LineAdjudicationResult { AllowedAmount = allowed, PaidAmount = paid, PatientResponsibility = liability },
            ],
        };
        context.StageResults.Add(ClaimAdjudicationStageResult.Pass(BenefitCalculationStage.StageName));
        return context;
    }
}
