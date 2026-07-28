using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Persistence;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Xunit;

namespace CloudHealthOffice.NcciEngine.Tests;

public class NcciMongoIndexInitializerTests
{
    [Fact]
    public void Lookup_indexes_match_sorted_query_shapes()
    {
        var registry = BsonSerializer.SerializerRegistry;
        var pairSerializer = registry.GetSerializer<NcciEditPair>();
        var mueSerializer = registry.GetSerializer<MueEntry>();

        var pair = NcciMongoIndexInitializer.BuildPairIndex().Keys.Render(
            new RenderArgs<NcciEditPair>(pairSerializer, registry));
        var mue = NcciMongoIndexInitializer.BuildMueIndex().Keys.Render(
            new RenderArgs<MueEntry>(mueSerializer, registry));

        Assert.Equal(1, pair["TenantId"].AsInt32);
        Assert.Equal(1, pair["Column1Code"].AsInt32);
        Assert.Equal(1, pair["Column2Code"].AsInt32);
        Assert.Equal(1, pair["EffectiveDate"].AsInt32);
        Assert.Equal(1, mue["TenantId"].AsInt32);
        Assert.Equal(1, mue["ProcedureCode"].AsInt32);
        Assert.Equal(1, mue["EffectiveDate"].AsInt32);
    }
}
