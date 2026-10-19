using CapitationService.Controllers;
using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Tests.Support;
using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CapitationService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB capitation contract (rate config)
/// repository, against a real mongod. <c>UpdateAsync</c> replaced the contract
/// unconditionally, so of two concurrent rate edits the later silently erased
/// the earlier, and an edit read before a terminate turned the contract back
/// to Active. The replace is now conditional on the revision it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class CapitationContractRepositoryMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-cap-contract-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("cap_contract_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    /// <summary>One request's context (not the AsyncLocal-backed HttpContextAccessor: these tests run requests at once).</summary>
    private sealed class RequestContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static IHttpContextAccessor Request()
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        return new RequestContext { HttpContext = http };
    }

    private CapitationContractRepositoryMongo Repo()
        => new(_db, Request(), Mock.Of<ILogger<CapitationContractRepositoryMongo>>());

    private Task<CapitationContract> SeedAsync(string id) => Repo().CreateAsync(new CapitationContract
    {
        Id = id,
        ContractId = "pc-1",
        ContractNumber = "CAP-1234567890-2026",
        ProviderNPI = "1234567890",
        ProviderName = "Dr. Chen",
        ContractType = ContractType.PrimaryCareOnly,
        LineOfBusiness = LineOfBusiness.Commercial,
        Status = CapitationRateConfigStatus.Active,
        EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        WithholdPercentage = 0.10m,
        CreatedBy = "admin",
    });

    private async Task<CapitationContract> ReadAsync(string id) => (await Repo().GetByIdAsync(id))!;

    private static async Task<Exception?> Try(Func<Task> write)
    {
        try
        {
            await write();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task TwoConcurrentRateEdits_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        await SeedAsync("cc-1");
        var a = await ReadAsync("cc-1");
        var b = await ReadAsync("cc-1");
        a.WithholdPercentage = 0.05m;
        b.WithholdPercentage = 0.15m;

        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpdateAsync(a)),
            Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.Count(o => o is CapitationConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        outcomes.OfType<CapitationConcurrencyException>().Single().Should().BeAssignableTo<ConcurrencyConflictException>("409 at the API");
        var stored = await ReadAsync("cc-1");
        stored.WithholdPercentage.Should().BeOneOf(0.05m, 0.15m);
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task ARateEditReadBeforeATerminate_Conflicts_InsteadOfReactivatingTheContract()
    {
        await SeedAsync("cc-1");
        var staleEdit = await ReadAsync("cc-1");
        var terminate = await ReadAsync("cc-1");
        terminate.Status = CapitationRateConfigStatus.Terminated;
        await Repo().UpdateAsync(terminate);

        staleEdit.WithholdPercentage = 0.20m;
        var act = () => Repo().UpdateAsync(staleEdit);

        var thrown = (await act.Should().ThrowAsync<CapitationConcurrencyException>()).Which;
        thrown.Entity.Should().Be("Capitation contract");
        staleEdit.Revision.Should().Be(0, "the in-memory revision is restored to the one read");
        var stored = await ReadAsync("cc-1");
        stored.Status.Should().Be(CapitationRateConfigStatus.Terminated, "the termination is not undone");
        stored.WithholdPercentage.Should().Be(0.10m);
    }

    [Fact]
    public async Task AContractWrittenBeforeTheRevisionExisted_UpdatesOnce_ThenCarriesARevision()
    {
        await SeedAsync("cc-1");
        await _db.GetCollection<BsonDocument>("capitation-contracts").UpdateOneAsync(
            new BsonDocument("_id", "cc-1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync("cc-1");
        legacy.Revision.Should().Be(0);
        legacy.WithholdPercentage = 0.12m;

        await Repo().UpdateAsync(legacy);

        var stored = await ReadAsync("cc-1");
        stored.Revision.Should().Be(1);
        stored.WithholdPercentage.Should().Be(0.12m);
    }

    [Fact]
    public async Task UpdatingAMissingContract_IsStillANoOp_NotAConflict()
    {
        var ghost = new CapitationContract { Id = "NOPE", TenantId = Tenant, ContractNumber = "CAP-NOPE" };

        await Repo().UpdateAsync(ghost);

        (await Repo().GetByIdAsync("NOPE")).Should().BeNull();
        ghost.Revision.Should().Be(0);
    }

    [Fact]
    public async Task APutWhoseBodyHasNoRevision_StillUpdatesAContractThatWasAlreadyEdited()
    {
        await SeedAsync("cc-1");
        var first = await ReadAsync("cc-1");
        first.WithholdPercentage = 0.11m;
        await Repo().UpdateAsync(first);

        var controller = new CapitationContractsController(
            Repo(), new TestActor(tenantId: Tenant), Mock.Of<ILogger<CapitationContractsController>>());
        var body = await ReadAsync("cc-1");
        body.Revision = 0; // the portal does not send it
        body.WithholdPercentage = 0.13m;

        var result = await controller.UpdateContract("cc-1", body);

        result.Result.Should().BeOfType<OkObjectResult>();
        var stored = await ReadAsync("cc-1");
        stored.WithholdPercentage.Should().Be(0.13m);
        stored.Revision.Should().Be(2);
    }
}
