using CloudHealthOffice.TradingPartnerService.Models;
using CloudHealthOffice.TradingPartnerService.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;
using Xunit;

namespace CloudHealthOffice.TradingPartnerService.Tests.Security;

/// <summary>
/// A missing tenant is an error, never a query over the empty tenant.
/// </summary>
public class RepositoryTenantGuardTests
{
    private static (TradingPartnerRepositoryMongo Repo, Mock<IMongoCollection<TradingPartner>> Collection) Create()
    {
        var collection = new Mock<IMongoCollection<TradingPartner>>(MockBehavior.Strict);
        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TradingPartner>(It.IsAny<string>(), It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);
        return (new TradingPartnerRepositoryMongo(database.Object, NullLogger<TradingPartnerRepositoryMongo>.Instance), collection);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingTenant_Throws_WithoutTouchingTheCollection(string tenant)
    {
        var (repo, collection) = Create();

        await FluentActions.Awaiting(() => repo.GetByTenantAsync(tenant)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => repo.GetAsync(tenant, "p", "prod")).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => repo.CreateAsync(new TradingPartner { TenantId = tenant })).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => repo.UpdateAsync(new TradingPartner { TenantId = tenant })).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => repo.DeleteAsync("id", tenant)).Should().ThrowAsync<InvalidOperationException>();
        collection.VerifyNoOtherCalls();
    }
}
