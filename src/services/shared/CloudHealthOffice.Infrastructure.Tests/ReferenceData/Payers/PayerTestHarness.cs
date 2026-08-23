using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;

internal static class PayerTestHarness
{
    public static InMemoryPayerReferenceStore CreateStore(bool seed = true)
    {
        var store = new InMemoryPayerReferenceStore();
        if (seed)
        {
            store.UpsertManyAsync(SyntheticPayerSeed.Create(DateTimeOffset.UnixEpoch), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        return store;
    }

    public static IPayerReferenceService CreateService(IPayerReferenceStore? store = null, bool seed = true) =>
        new PayerReferenceService(store ?? CreateStore(seed), NullLogger<PayerReferenceService>.Instance);

    public static StediPayerResolver CreateResolver(
        IOptions<StediGatewayOptions> options,
        IPayerReferenceService? payers = null) =>
        new(payers ?? CreateService(), options, NullLogger<StediPayerResolver>.Instance);
}
