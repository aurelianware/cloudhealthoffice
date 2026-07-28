using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using ProviderService.HostedServices;
using ProviderService.Models;

namespace CloudHealthOffice.ProviderService.Tests.HostedServices;

public class ProviderQueryIndexInitializerTests
{
    [Fact]
    public void Provider_indexes_cover_integrity_and_roster_sort_shapes()
    {
        var serializer = BsonSerializer.SerializerRegistry.GetSerializer<Provider>();
        var rendered = ProviderQueryIndexInitializer.BuildProviderIndexes()
            .Select(index => index.Keys.Render(
                new RenderArgs<Provider>(serializer, BsonSerializer.SerializerRegistry)))
            .ToList();

        rendered.Should().Contain(index =>
            index.ElementCount == 2
            && index.Contains("ProviderId")
            && index.Contains("_id")
            && index["ProviderId"] == 1
            && index["_id"] == 1);
        rendered.Should().Contain(index =>
            index.ElementCount == 3
            && index.Contains("LastName")
            && index.Contains("OrganizationName")
            && index.Contains("_id")
            && index["LastName"] == 1
            && index["OrganizationName"] == 1
            && index["_id"] == 1);
    }
}
