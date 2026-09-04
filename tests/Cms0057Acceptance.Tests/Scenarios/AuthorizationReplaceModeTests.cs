using AuthorizationService.Backends;
using AuthorizationService.Models;
using FluentAssertions;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// Replace mode — Cloud Health Office as the AUTHORITATIVE authorization
/// backend. These scenarios exercise the REAL production
/// <see cref="ChoAuthorizationBackend"/> (the same class the running
/// authorization-service binds) backed by an in-memory repository fixture, and
/// prove that Cloud Health Office owns the prior-authorization workflow without
/// requiring QNXT, Facets, or HealthEdge.
///
/// This is the PRODUCT-CAPABILITY dimension: PAS-03 etc. are PASSABLE on the CHO
/// backend. The INTEGRATION-CAPABILITY dimension (QNXT Augment) is a GAP,
/// asserted separately in AuthorizationAugmentModeTests / GapAdapterTests.
///
/// Traceability:
///   backend     src/services/authorization-service/Backends/ChoAuthorizationBackend.cs
///   repository  src/services/authorization-service/Repositories/AuthorizationRepository.cs (Cosmos/Mongo in prod)
///   model       src/services/authorization-service/Models/Authorization.cs
///   metrics     src/services/authorization-service/Models/AuthorizationsSummaryCalculator.cs
/// </summary>
public class AuthorizationReplaceModeTests
{
    private static Authorization NewRequest(string number, string cpt = "27447") => new()
    {
        TenantId = AcceptanceContext.TenantId,
        AuthorizationNumber = number,
        MemberId = "MBR-pat-001",
        LineOfBusiness = LineOfBusiness.Medicaid,
        AuthorizationType = AuthorizationType.PreAuthorization,
        ServiceTypeCode = "1",
        RequestingProviderNPI = "1234567890",
        ServicingProviderNPI = "1987654321",
        PatientFirstName = "Pat",
        PatientLastName = "Synthetic",
        PatientDateOfBirth = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        RequestedServiceDateFrom = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
        RequestedServiceDateTo = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
        Status = AuthorizationStatus.Submitted,
        SubmittedDate = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
        RequestedServices =
        {
            new RequestedService { ProcedureCode = cpt, RequestedUnits = 2 },
        },
    };

    [Fact]
    [Trait("Scenario", "PAS-03")]
    [Trait("Backend", "Replace")]
    public async Task PAS03_Replace_CreatesAndPersistsAuthorizationInCho()
    {
        var repo = new InMemoryAuthorizationRepository();
        var backend = new ChoAuthorizationBackend(repo);

        backend.BackendKey.Should().Be("cho");
        backend.IsAuthoritative.Should().BeTrue();

        var request = NewRequest("PAS-REPLACE-03");
        request.Id = Guid.NewGuid().ToString();

        var created = await backend.CreateAsync(request);

        created.Id.Should().NotBeNullOrEmpty();
        created.AuthorizationNumber.Should().Be("PAS-REPLACE-03");
        created.MemberId.Should().Be("MBR-pat-001");
        created.RequestingProviderNPI.Should().Be("1234567890");
        created.RequestedServices.Should().ContainSingle().Which.ProcedureCode.Should().Be("27447");
        created.StatusHistory.Should().ContainSingle().Which.Status.Should().Be(AuthorizationStatus.Submitted);
        repo.CreateCount.Should().Be(1, "the CHO-native backend persisted the record");
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    [Trait("Backend", "Replace")]
    public async Task PAS04_Replace_AuthorizationIsRetrievableWithStableIdAfterPersistence()
    {
        var repo = new InMemoryAuthorizationRepository();
        var backend = new ChoAuthorizationBackend(repo);

        var request = NewRequest("PAS-REPLACE-04");
        request.Id = "auth-stable-id-04";
        await backend.CreateAsync(request);

        // Mutating the caller's object must not change the persisted record.
        request.MemberId = "MUTATED";

        var fetched = await backend.GetByNumberAsync("PAS-REPLACE-04");
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be("auth-stable-id-04");           // stable id
        fetched.MemberId.Should().Be("MBR-pat-001");            // survived persistence, not mutated
        fetched.Status.Should().Be(AuthorizationStatus.Submitted);
    }

    [Fact]
    [Trait("Scenario", "PAS-06")]
    [Trait("Backend", "Replace")]
    public async Task PAS06_Replace_LifecycleAndDecisionHistoryArePersisted()
    {
        var repo = new InMemoryAuthorizationRepository();
        var backend = new ChoAuthorizationBackend(repo);

        var created = await backend.CreateAsync(NewRequest("PAS-REPLACE-06"));

        await backend.UpdateStatusAsync(
            created, AuthorizationStatus.Approved, reviewDecision: "A1", reason: null);

        var fetched = await backend.GetByNumberAsync("PAS-REPLACE-06");
        fetched!.Status.Should().Be(AuthorizationStatus.Approved);       // lifecycle survives, not only in the HTTP response
        fetched.ReviewDecision.Should().Be("A1");
        fetched.ReviewedDate.Should().NotBeNull();                        // decision timestamp recorded
        fetched.StatusHistory.Should().HaveCount(2);                      // append-only history preserved
        fetched.StatusHistory.Last().Status.Should().Be(AuthorizationStatus.Approved);
    }

    [Fact]
    [Trait("Scenario", "PAS-05")]
    [Trait("Backend", "Replace")]
    public async Task PAS05_Replace_DenialPersistsCodedReason()
    {
        var repo = new InMemoryAuthorizationRepository();
        var backend = new ChoAuthorizationBackend(repo);

        var created = await backend.CreateAsync(NewRequest("PAS-REPLACE-05"));
        await backend.UpdateStatusAsync(
            created, AuthorizationStatus.Denied, reviewDecision: "A3",
            reason: "Does not meet clinical criteria for the requested imaging.");

        var fetched = await backend.GetByNumberAsync("PAS-REPLACE-05");
        fetched!.Status.Should().Be(AuthorizationStatus.Denied);
        fetched.DenialReason.Should().NotBeNullOrWhiteSpace();
        fetched.DenialReason!.ToLowerInvariant().Should().NotBe("not medically necessary");
    }

    [Fact]
    [Trait("Scenario", "METRICS-01")]
    [Trait("Backend", "Replace")]
    public async Task METRICS01_Replace_MetricsDeriveFromPersistedChoAuthorization()
    {
        var repo = new InMemoryAuthorizationRepository();
        var backend = new ChoAuthorizationBackend(repo);

        var created = await backend.CreateAsync(NewRequest("PAS-REPLACE-METRICS"));
        // Decide it 3 days after submission.
        created.ReviewedDate = created.SubmittedDate.AddDays(3);
        await backend.UpdateStatusAsync(created, AuthorizationStatus.Approved, "A1", null);

        // Metric derives from the CHO-owned persisted record, not a test-only object.
        var persisted = await backend.GetByNumberAsync("PAS-REPLACE-METRICS");
        var turnaround = AuthorizationsSummaryCalculator.CalculateTurnaroundDays(persisted!);
        turnaround.Should().Be(3.0);
    }
}
