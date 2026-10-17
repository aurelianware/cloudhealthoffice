using AuthorizationService.Models;
using CloudHealthOffice.Testing.Mongo;
using MongoDB.Driver;

namespace AuthorizationService.Tests.Persistence;

/// <summary>
/// D-07 / G-04: the SLA watchdog against a REAL repository, resolved from the
/// real host's service collection, for two tenants. It runs with no ambient
/// request (as a hosted service does), so the tenant can only come from the
/// watchdog itself.
///
/// <para>
/// Before the fix the watchdog called the tenant-less
/// <c>GetOpenAuthorizationsAsync()</c>, which reads the tenant from
/// <c>HttpContext.Items</c>; with no request it threw
/// <c>InvalidOperationException("TenantId not found in request context")</c>
/// on every tick and escalated nothing.
/// </para>
/// </summary>
public abstract class SlaWatchdogRealStoreTestsBase : IAsyncLifetime
{
    private readonly string _tenantA = "ta-" + Guid.NewGuid().ToString("N")[..10];
    private readonly string _tenantB = "tb-" + Guid.NewGuid().ToString("N")[..10];

    private AuthorizationServiceHost _host = null!;

    private protected abstract Task<AuthorizationServiceHost> CreateHostAsync();

    private protected virtual Task CleanupAsync() => Task.CompletedTask;

    public async Task InitializeAsync() => _host = await CreateHostAsync();

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        await CleanupAsync();
    }

    [Fact]
    public async Task Watchdog_EscalatesOverdueAuthorizations_ForEveryTenant_WithNoRequestContext()
    {
        // Tenant A
        var aUrgentWarning = await SeedAsync(_tenantA, "U", AuthorizationStatus.Submitted, hoursAgo: 50);
        var aStandardBreach = await SeedAsync(_tenantA, null, AuthorizationStatus.InReview, hoursAgo: 170);
        var aUrgentNotDue = await SeedAsync(_tenantA, "U", AuthorizationStatus.Pended, hoursAgo: 10);
        var aClosed = await SeedAsync(_tenantA, "U", AuthorizationStatus.Approved, hoursAgo: 100);
        // Tenant B
        var bUrgentCritical = await SeedAsync(_tenantB, "U", AuthorizationStatus.Submitted, hoursAgo: 65);
        var bStandardWarning = await SeedAsync(_tenantB, "E", AuthorizationStatus.Pended, hoursAgo: 125);

        _host.ClearRequestContext();
        await _host.Watchdog.EvaluateAllAuthorizationsAsync();

        (await ReadAsync(_tenantA, aUrgentWarning)).SlaEscalation.Should().Be(SlaEscalationLevel.Warning);
        (await ReadAsync(_tenantA, aStandardBreach)).SlaEscalation.Should().Be(SlaEscalationLevel.Breach);
        (await ReadAsync(_tenantA, aUrgentNotDue)).SlaEscalation.Should().Be(SlaEscalationLevel.None);
        (await ReadAsync(_tenantA, aClosed)).SlaEscalation.Should().Be(SlaEscalationLevel.None);
        (await ReadAsync(_tenantB, bUrgentCritical)).SlaEscalation.Should().Be(SlaEscalationLevel.Critical);
        (await ReadAsync(_tenantB, bStandardWarning)).SlaEscalation.Should().Be(SlaEscalationLevel.Warning);

        foreach (var (tenant, id) in new[]
                 {
                     (_tenantA, aUrgentWarning), (_tenantA, aStandardBreach),
                     (_tenantB, bUrgentCritical), (_tenantB, bStandardWarning),
                 })
        {
            var stored = await ReadAsync(tenant, id);
            stored.SlaEscalatedAt.Should().NotBeNull();
            // Strict isolation: an escalation never moves a record to another tenant.
            stored.TenantId.Should().Be(tenant);
        }

        // Nothing was written into the other tenant's partition.
        (await ReadAsync(_tenantB, aUrgentWarning, allowMissing: true)).Should().BeNull();
        (await ReadAsync(_tenantA, bUrgentCritical, allowMissing: true)).Should().BeNull();
    }

    [Fact]
    public async Task Watchdog_SecondRun_ChangesNothing()
    {
        var id = await SeedAsync(_tenantA, "U", AuthorizationStatus.Submitted, hoursAgo: 66);

        _host.ClearRequestContext();
        await _host.Watchdog.EvaluateAllAuthorizationsAsync();
        var first = await ReadAsync(_tenantA, id);
        first.SlaEscalation.Should().Be(SlaEscalationLevel.Critical);

        _host.ClearRequestContext();
        await _host.Watchdog.EvaluateAllAuthorizationsAsync();
        var second = await ReadAsync(_tenantA, id);

        second.SlaEscalation.Should().Be(SlaEscalationLevel.Critical);
        second.SlaEscalatedAt.Should().Be(first.SlaEscalatedAt);
    }

    [Fact]
    public async Task Watchdog_NeverLowersAnEscalation()
    {
        // Already at Breach, but the (resumed) clock only reaches Warning now.
        var id = await SeedAsync(_tenantA, "U", AuthorizationStatus.InReview, hoursAgo: 49,
            escalation: SlaEscalationLevel.Breach);

        _host.ClearRequestContext();
        await _host.Watchdog.EvaluateAllAuthorizationsAsync();

        (await ReadAsync(_tenantA, id)).SlaEscalation.Should().Be(SlaEscalationLevel.Breach);
    }

    private async Task<string> SeedAsync(
        string tenant, string? levelOfService, AuthorizationStatus status, double hoursAgo,
        SlaEscalationLevel escalation = SlaEscalationLevel.None)
    {
        var (scope, repository) = _host.RepositoryFor(tenant);
        try
        {
            var auth = NewAuthorization(levelOfService, status, DateTime.UtcNow.AddHours(-hoursAgo));
            auth.SlaEscalation = escalation;
            var created = await repository.CreateAsync(auth);
            return created.Id;
        }
        finally
        {
            scope.Dispose();
            _host.ClearRequestContext();
        }
    }

    private async Task<Authorization> ReadAsync(string tenant, string id) =>
        (await ReadAsync(tenant, id, allowMissing: false))!;

    private async Task<Authorization?> ReadAsync(string tenant, string id, bool allowMissing)
    {
        var (scope, repository) = _host.RepositoryFor(tenant);
        try
        {
            var auth = await repository.GetByIdAsync(id);
            if (!allowMissing) auth.Should().NotBeNull($"authorization {id} was seeded for tenant {tenant}");
            return auth;
        }
        finally
        {
            scope.Dispose();
            _host.ClearRequestContext();
        }
    }

    internal static Authorization NewAuthorization(
        string? levelOfService, AuthorizationStatus status, DateTime submitted,
        LineOfBusiness lineOfBusiness = LineOfBusiness.Medicaid) => new()
    {
        Id = Guid.NewGuid().ToString(),
        AuthorizationNumber = $"AUTH-{Guid.NewGuid():N}",
        MemberId = "MBR-001",
        PatientFirstName = "Test",
        PatientLastName = "Member",
        PatientDateOfBirth = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        LineOfBusiness = lineOfBusiness,
        RequestingProviderNPI = "1234567890",
        AuthorizationType = AuthorizationType.PreAuthorization,
        ServiceTypeCode = "1",
        LevelOfService = levelOfService,
        RequestedServiceDateFrom = submitted.AddDays(1),
        RequestedServices = { new RequestedService { ProcedureCode = "99213" } },
        Status = status,
        SubmittedDate = submitted,
        CreatedDate = submitted,
        LastUpdatedDate = submitted,
    };
}

/// <summary>The watchdog on MongoDB, via EphemeralMongo.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class SlaWatchdogMongoTests(MongoRunnerFixture mongo) : SlaWatchdogRealStoreTestsBase
{
    private readonly string _databaseName = "as_watchdog_" + Guid.NewGuid().ToString("N")[..12];

    private protected override Task<AuthorizationServiceHost> CreateHostAsync() =>
        Task.FromResult(AuthorizationServiceHost.Mongo(mongo.ConnectionString, _databaseName));

    private protected override Task CleanupAsync() => mongo.Client.DropDatabaseAsync(_databaseName);
}
