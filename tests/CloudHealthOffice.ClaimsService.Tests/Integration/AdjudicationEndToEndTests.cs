using System.Net;
using System.Net.Http.Json;
using ClaimsService.Adapters;
using ClaimsService.HostedServices;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.NcciEngine.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Integration;

/// <summary>
/// End-to-end coverage for capability 5.5: POST <c>/api/v1/claims</c>
/// triggers a Service Bus message that the adjudication orchestrator
/// consumes via <see cref="InMemoryMessageBus"/>; the pipeline runs
/// BenefitCalculationStage against a substitute engine and PersistenceStage
/// writes through the projection-bypass repository method.
///
/// <para>
/// Test posture (Decision 19): InMemoryMessageBus stands in for
/// Service Bus; subscription filter rules are not exercised at this
/// layer — they're a Bicep concern verified by <c>az bicep build</c>
/// in CI. The producer side is asserted against
/// <see cref="SendOptions.Properties"/> in the unit-level
/// <see cref="ClaimsService.Tests.Services.ClaimSubmissionServiceMessageBusEmissionTests"/>.
/// </para>
/// </summary>
public class AdjudicationEndToEndTests : IAsyncLifetime
{
    private readonly AdjudicationApiFactory _factory = new();
    private HttpClient _client = default!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-1");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PostClaim_TriggersAdjudicationPipeline_AndPersistenceBypassRecordsResult()
    {
        var planId = Guid.NewGuid().ToString();
        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        // Send the typed AdapterClaim so System.Text.Json's enum binder
        // resolves LineOfBusiness/ClaimType correctly. Anonymous-object
        // payloads with string enum values fail the controller's model
        // binding (no JsonStringEnumConverter on the inbound path).
        var inbound = new AdapterClaim
        {
            ClaimNumber = "E2E-001",
            MemberId = "MEM-1",
            // Luhn-valid NPI (PV001) and a diagnosis code (DC004) —
            // both required by capability 5.4's ScrubbingStage which
            // now runs at Order=100 ahead of BenefitCalculation.
            BillingProviderNPI = "1234567893",
            BenefitPlanId = planId,
            LineOfBusiness = LineOfBusiness.Commercial,
            ClaimType = ClaimType.Professional,
            PlaceOfServiceCode = "11",
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            DiagnosisCodes = new List<AdapterDiagnosisCode>
            {
                new() { Code = "Z00.00", PointerNumber = 1 },
            },
            ClaimLines = new List<AdapterClaimLine>
            {
                new()
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ChargeAmount = 200m,
                    Units = 1m,
                    ServiceDateFrom = serviceDate,
                    ServiceDateTo = serviceDate,
                    DiagnosisPointers = new List<int> { 1 },
                }
            },
        };

        var response = await _client.PostAsJsonAsync("/api/v1/claims", inbound);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Drain the in-memory bus by polling for the persistence-stage
        // write. With InMemoryMessageBus the dispatch is an async pump,
        // so a short polling window covers the typical single-digit-
        // millisecond handler runtime without flakiness.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && _factory.ProjectionWrites.Count == 0)
        {
            await Task.Delay(20);
        }

        Assert.NotEmpty(_factory.ProjectionWrites);
        var write = _factory.ProjectionWrites[0];
        Assert.Equal("tenant-1", write.TenantId);
        Assert.Equal(150m, write.Result.AllowedAmount);
        Assert.Equal(120m, write.Result.PayerPayment);
        Assert.Equal(30m, write.Result.PatientResponsibility);
        Assert.Single(write.LineResults);
    }

    /// <summary>
    /// Custom factory that opts into the Service Bus subscription, swaps
    /// the engine for a deterministic substitute, and records the
    /// persistence-bypass write so the test can observe it without
    /// standing up Cosmos / Mongo.
    /// </summary>
    private sealed class AdjudicationApiFactory : WebApplicationFactory<Program>
    {
        public IClaimRepository Repository { get; } = Substitute.For<IClaimRepository>();
        public List<ProjectionWrite> ProjectionWrites { get; } = new();

        private Claim? _lastCreated;

        public AdjudicationApiFactory()
        {
            Repository.CreateAsync(Arg.Any<Claim>())
                .Returns(ci =>
                {
                    var c = ci.Arg<Claim>();
                    if (string.IsNullOrEmpty(c.Id)) c.Id = Guid.NewGuid().ToString();
                    if (string.IsNullOrEmpty(c.ClaimVersionId)) c.ClaimVersionId = c.Id;
                    if (c.VersionNumber == 0) c.VersionNumber = 1;
                    c.VersionState = ClaimVersionState.Submitted;
                    _lastCreated = c;
                    return c;
                });

            // Adjudication orchestrator routes through IClaimAdapter →
            // ChoClaimAdapter → IClaimRepository.GetLatestVersionAsync(...).
            // Return the most recently created claim to keep the pipeline
            // running against the same row the submission produced.
            Repository.GetLatestVersionAsync(
                    Arg.Any<string>(), Arg.Any<DateTime>())
                .Returns(_ => _lastCreated);

            // Explicitly matches all projection parameters (not just the leading 5) —
            // this test's pipeline runs the real CoordinationOfBenefitsStage
            // against the real HttpCoverageClient, which is unreachable in
            // this test host and degrades to a Pend (Decision 7, default
            // CobMode=PendForSecondary). PersistenceStage now resolves that
            // to isPend=true and passes non-null PendDetails, so a stub that
            // only specified the first 5 args (implicitly matching
            // pendDetails=null/isPend=false) would silently stop matching.
            Repository.UpdateAdjudicationProjectionAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<AdjudicationResult>(),
                    Arg.Any<IReadOnlyList<LineAdjudicationResult>>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<PendDetails?>(),
                    Arg.Any<bool>(),
                    Arg.Any<ClaimStatus?>(),
                    Arg.Any<string?>())
                .Returns(ci =>
                {
                    ProjectionWrites.Add(new ProjectionWrite(
                        ci.ArgAt<string>(0),
                        ci.ArgAt<string>(1),
                        ci.ArgAt<AdjudicationResult>(2),
                        ci.ArgAt<IReadOnlyList<LineAdjudicationResult>>(3)));
                    return true;
                });
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Messaging:Backend"] = "InMemory",
                });
            });
            builder.ConfigureServices(services =>
            {
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(IClaimRepository)
                             || d.ServiceType.FullName?.Contains("Cosmos") == true
                             || d.ServiceType.FullName?.Contains("Mongo") == true
                             || d.ImplementationType?.FullName?.Contains("Cosmos") == true
                             || d.ImplementationType?.FullName?.Contains("Mongo") == true
                             || d.ImplementationType == typeof(ClaimIndexInitializer)
                             || d.ImplementationType == typeof(MassAdjudicationRunIndexInitializer)
                             || d.ImplementationType == typeof(ClaimVersionEventIndexInitializer)
                             || d.ImplementationType == typeof(ClaimAdjustmentIndexInitializer)
                             || d.ServiceType == typeof(IClaimVersionEventPublisher)
                             || d.ServiceType == typeof(IBenefitCalculationEngine)
                             || d.ServiceType == typeof(IBenefitPlanResolver)
                             || d.ServiceType == typeof(IMemberResolver))
                    .ToList();
                foreach (var descriptor in toRemove)
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton(Repository);
                services.AddSingleton(Substitute.For<IClaimVersionEventPublisher>());

                // 5.12a — IClaimAdjustmentRepository is removed by the
                // Cosmos/Mongo substring filter above (both impls have those
                // tokens in their type names). IClaimAdjustmentService still
                // depends on it, so register substitutes for DI validation.
                // This integration test exercises the submission/adjudication
                // path, not the adjustment workflow, so substitutes never
                // get invoked.
                services.AddSingleton(Substitute.For<IClaimAdjustmentRepository>());
                services.AddSingleton(Substitute.For<IClaimAdjustmentService>());

                // ClaimImportTransactionRepositoryMongo is caught by the
                // "Mongo" substring filter above (nothing 837-related runs
                // in this test — it only exercises POST /api/v1/claims —
                // but ClaimsV1Controller's constructor still needs every
                // dependency resolvable to construct the controller at all).
                services.AddSingleton(Substitute.For<IClaimImportTransactionRepository>());

                // 5.7 — NcciEngine's repository implementation gets removed
                // by the Cosmos/Mongo filter above. INcciEditService still
                // depends on INcciRepository — register a substitute so
                // ServiceProvider validation succeeds. The substitute
                // returns null from GetEditPair / GetMueEntry, which makes
                // the engine's ScrubAsync return Passed=true with zero
                // failures (the missing-table soft-pass posture). The 5.5
                // E2E test exercises BenefitCalculation, not NCCI failure
                // paths — that's covered by AdjudicationWithNcciEndToEndTests.
                services.AddSingleton(Substitute.For<INcciRepository>());

                var engine = Substitute.For<IBenefitCalculationEngine>();
                engine.CalculateAsync(
                        Arg.Any<BenefitResolutionRequest>(),
                        Arg.Any<CancellationToken>())
                    .Returns(new BenefitResolutionResult
                    {
                        Success = true,
                        Totals = new ClaimTotals
                        {
                            TotalBilled = 200m,
                            TotalAllowed = 150m,
                            TotalDeductible = 0m,
                            TotalCoinsurance = 30m,
                            TotalCopay = 0m,
                            TotalMemberResponsibility = 30m,
                            TotalPlanPaid = 120m,
                        },
                        Lines = new List<LineBenefitResult>
                        {
                            new()
                            {
                                LineNumber = 1,
                                IsCovered = true,
                                ServiceTypeCode = "1",
                                ServiceTypeDescription = "Office",
                                AllowedAmount = 150m,
                                PlanPaidAmount = 120m,
                                MemberResponsibility = 30m,
                            }
                        },
                    });
                services.AddSingleton(engine);

                var planResolver = Substitute.For<IBenefitPlanResolver>();
                planResolver.GetPlanAsync(
                        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(ci => new ResolvedBenefitPlan
                    {
                        Id = ci.ArgAt<string>(1),
                        PlanGuid = Guid.TryParse(ci.ArgAt<string>(1), out var g) ? g : Guid.NewGuid(),
                        PlanName = "Stub Plan",
                    });
                services.AddSingleton(planResolver);

                var memberResolver = Substitute.For<IMemberResolver>();
                memberResolver.GetMemberAsync(
                        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(ci => new ResolvedMember
                    {
                        MemberId = ci.ArgAt<string>(1),
                        SubscriberMemberId = ci.ArgAt<string>(1),
                        IsSubscriber = true,
                        // Required for capability 5.4's ScrubbingStage:
                        // engine rule DC002 (Subscriber DOB Required) is
                        // an Error and rejects the claim before it
                        // reaches BenefitCalculationStage when DOB is
                        // missing.
                        DateOfBirth = new DateTime(1980, 6, 15, 0, 0, 0, DateTimeKind.Utc),
                    });
                services.AddSingleton(memberResolver);
            });
        }
    }

    public record ProjectionWrite(
        string TenantId,
        string ClaimVersionId,
        AdjudicationResult Result,
        IReadOnlyList<LineAdjudicationResult> LineResults);
}
