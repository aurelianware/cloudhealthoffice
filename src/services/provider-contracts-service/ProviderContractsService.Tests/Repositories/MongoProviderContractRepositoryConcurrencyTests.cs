using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using ProviderContractsService.Controllers;
using ProviderContractsService.Models;
using ProviderContractsService.Repositories;

namespace ProviderContractsService.Tests.Repositories;

/// <summary>
/// Optimistic concurrency on the MongoDB provider contract repository, against
/// a real mongod. <c>UpdateAsync</c> replaced the contract unconditionally, so
/// of two concurrent amendments the later silently erased the earlier, and an
/// edit read before a terminate turned the contract back to Active. The
/// replace is now conditional on the revision it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoProviderContractRepositoryConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-pc-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("pc_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static ICurrentActor Actor => Mock.Of<ICurrentActor>(a => a.UserId == "token-user" && a.TenantId == Tenant);

    private MongoProviderContractRepository Repo()
        => new(_db, Actor, Mock.Of<ILogger<MongoProviderContractRepository>>());

    private Task<ProviderContract> SeedAsync(string id) => Repo().CreateAsync(new ProviderContract
    {
        Id = id,
        ContractNumber = "CTR-1234567890-2026",
        ProviderNPI = "1234567890",
        ProviderName = "Dr. Chen",
        ProviderType = ProviderType.Individual,
        LineOfBusiness = LineOfBusiness.Commercial,
        PaymentMethodology = PaymentMethodology.FeeForService,
        NetworkStatus = NetworkParticipationStatus.Participating,
        Status = ProviderContractStatus.Active,
        EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    });

    private async Task<ProviderContract> ReadAsync(string id) => (await Repo().GetByIdAsync(id))!;

    private static ContractAmendment Amendment(string type) => new()
    {
        Id = Guid.NewGuid().ToString(),
        AmendmentType = type,
        Description = type,
        EffectiveDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedAt = DateTime.UtcNow,
    };

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
    public async Task TwoConcurrentAmendments_FromTheSameRead_ExactlyOneWins_TheOtherConflicts()
    {
        await SeedAsync("pc-1");
        var a = await ReadAsync("pc-1");
        var b = await ReadAsync("pc-1");
        a.Amendments.Add(Amendment("RateChange"));
        b.Amendments.Add(Amendment("ServiceAddition"));

        var outcomes = await Task.WhenAll(
            Try(() => Repo().UpdateAsync(a)),
            Try(() => Repo().UpdateAsync(b)));

        outcomes.Count(o => o is null).Should().Be(1, "exactly one write wins");
        outcomes.Count(o => o is ProviderContractConcurrencyException).Should().Be(1, "the other gets a conflict, not a silent overwrite");
        outcomes.OfType<ProviderContractConcurrencyException>().Single().Should().BeAssignableTo<ConcurrencyConflictException>("409 at the API");
        var stored = await ReadAsync("pc-1");
        stored.Amendments.Should().ContainSingle();
        stored.Revision.Should().Be(1);
    }

    [Fact]
    public async Task AnEditReadBeforeATerminate_Conflicts_InsteadOfReactivatingTheContract()
    {
        await SeedAsync("pc-1");
        var staleEdit = await ReadAsync("pc-1");
        var terminate = await ReadAsync("pc-1");
        terminate.Status = ProviderContractStatus.Terminated;
        terminate.TerminationReason = "network exit";
        await Repo().UpdateAsync(terminate);

        staleEdit.ProviderName = "Dr. Chen, MD";
        var act = () => Repo().UpdateAsync(staleEdit);

        await act.Should().ThrowAsync<ProviderContractConcurrencyException>();
        staleEdit.Revision.Should().Be(0, "the in-memory revision is restored to the one read");
        var stored = await ReadAsync("pc-1");
        stored.Status.Should().Be(ProviderContractStatus.Terminated, "the termination is not undone");
        stored.ProviderName.Should().Be("Dr. Chen");
    }

    [Fact]
    public async Task AContractWrittenBeforeTheRevisionExisted_UpdatesOnce_ThenCarriesARevision()
    {
        await SeedAsync("pc-1");
        await _db.GetCollection<BsonDocument>("provider_contracts").UpdateOneAsync(
            new BsonDocument("_id", "pc-1"), new BsonDocument("$unset", new BsonDocument("Revision", "")));
        var legacy = await ReadAsync("pc-1");
        legacy.Revision.Should().Be(0);
        legacy.ProviderName = "first write after the upgrade";

        await Repo().UpdateAsync(legacy);

        var stored = await ReadAsync("pc-1");
        stored.Revision.Should().Be(1);
        stored.ProviderName.Should().Be("first write after the upgrade");
    }

    [Fact]
    public async Task UpdatingAMissingContract_IsStillANoOp_NotAConflict()
    {
        var ghost = new ProviderContract { Id = "NOPE", ProviderNPI = "1234567890" };

        await Repo().UpdateAsync(ghost);

        (await Repo().GetByIdAsync("NOPE")).Should().BeNull();
        ghost.Revision.Should().Be(0);
    }

    [Fact]
    public async Task APutWhoseBodyHasNoRevision_StillUpdatesAContractThatWasAlreadyAmended()
    {
        await SeedAsync("pc-1");
        var amended = await ReadAsync("pc-1");
        amended.Amendments.Add(Amendment("RateChange"));
        await Repo().UpdateAsync(amended);

        var controller = new ProviderContractsController(Repo(), Mock.Of<ILogger<ProviderContractsController>>());
        var body = await ReadAsync("pc-1");
        body.Revision = 0; // the portal does not send it
        body.ProviderName = "Dr. Chen, MD";

        var result = await controller.UpdateContract("pc-1", body);

        result.Result.Should().BeOfType<OkObjectResult>();
        var stored = await ReadAsync("pc-1");
        stored.ProviderName.Should().Be("Dr. Chen, MD");
        stored.Amendments.Should().ContainSingle("the amendment made before the PUT survives");
        stored.Revision.Should().Be(2);
    }
}
