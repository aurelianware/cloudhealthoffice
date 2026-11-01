using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using MongoDB.Driver;
using PremiumBillingService.Models;
using PremiumBillingService.StateCapitation;

namespace PremiumBillingService.Tests.StateCapitation;

/// <summary>The state capitation store on a real mongod: tenant scoping, month replacement, one payment per trace.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class StateCapitationStoreMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("state_capitation_store");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private sealed class Accessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private StateCapitationStoreMongo Store(string tenant)
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = tenant;
        return new StateCapitationStoreMongo(_db, new Accessor { HttpContext = http });
    }

    private static readonly DateTime Jan = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task EachTenantSeesOnlyItsOwn_AndAMonthIsReplacedWhole()
    {
        await Store("t1").ReplaceExpectedAsync("TX", [DateOnly.FromDateTime(Jan)],
            [new() { MemberId = "A", Month = Jan, RateCell = "X" }, new() { MemberId = "B", Month = Jan, RateCell = "X" }]);
        await Store("t2").ReplaceExpectedAsync("TX", [DateOnly.FromDateTime(Jan)],
            [new() { MemberId = "Z", Month = Jan, RateCell = "X" }]);
        await Store("t1").ReplaceExpectedAsync("TX", [DateOnly.FromDateTime(Jan)],
            [new() { MemberId = "A", Month = Jan, RateCell = "Y" }]);

        var t1 = await Store("t1").GetExpectedAsync("TX", new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 1));
        t1.Should().ContainSingle().Which.Should().Be(new ExpectedMemberMonth("A", new DateOnly(2027, 1, 1), "Y"));
        (await Store("t2").GetExpectedAsync("TX", new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 1)))
            .Should().ContainSingle().Which.MemberId.Should().Be("Z");
    }

    [Fact]
    public async Task APaymentIsRecordedOncePerTrace_AndRatesUpsertByCellAndStart()
    {
        var payment = new StoredStatePayment { State = "CA", TraceNumber = "T1", PaymentDate = Jan, Advice = new RemittanceAdvice { TraceNumber = "T1" } };
        (await Store("t1").TryAddPaymentAsync(payment)).Should().BeTrue();
        (await Store("t1").TryAddPaymentAsync(new StoredStatePayment { State = "CA", TraceNumber = "T1", Advice = new RemittanceAdvice { TraceNumber = "T1" } }))
            .Should().BeFalse();
        (await Store("t2").TryAddPaymentAsync(new StoredStatePayment { State = "CA", TraceNumber = "T1", Advice = new RemittanceAdvice { TraceNumber = "T1" } }))
            .Should().BeTrue("another tenant's trace numbers are its own");

        await Store("t1").UpsertRatesAsync("CA", [new() { RateCell = "LA-CHILD", EffectiveFrom = Jan, Pmpm = 180m }]);
        await Store("t1").UpsertRatesAsync("CA", [new() { RateCell = "la-child", EffectiveFrom = Jan, Pmpm = 181m }]);

        (await Store("t1").GetRatesAsync("CA")).Should().ContainSingle().Which.Pmpm.Should().Be(181m);
        (await Store("t1").GetPaymentsAsync("CA")).Should().ContainSingle();
    }
}
