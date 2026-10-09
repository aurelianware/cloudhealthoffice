using ArService.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace ArService.Tests.Models;

/// <summary>
/// Documents written by a later build (or by the legacy reconciliation tool) carry elements
/// an earlier build does not know. Every persisted model ignores them, so rolling back to
/// this build or later still reads those documents instead of failing every request.
/// </summary>
public class IgnoreExtraElementsTests
{
    [Fact]
    public void CashPosting_WithUnknownElementsAtEveryLevel_Deserializes()
    {
        var doc = new BsonDocument
        {
            { "_id", "cp-1" }, { "TenantId", "t" }, { "PostingNumber", "CP-1" },
            { "SomeFutureField", 1 },
            { "Applications", new BsonArray { new BsonDocument { { "ArBalanceId", "bal-1" }, { "FutureAppField", true } } } },
            { "LegacyReconciliation", new BsonDocument
                {
                    { "Status", 2 }, { "FutureRecField", "x" },
                    { "Applications", new BsonArray { new BsonDocument { { "ApplicationIndex", 0 }, { "FutureDecisionField", 1 } } } }
                } }
        };

        var posting = BsonSerializer.Deserialize<CashPosting>(doc);

        posting.Id.Should().Be("cp-1");
        posting.Applications.Single().ArBalanceId.Should().Be("bal-1");
        posting.LegacyReconciliation!.Applications.Single().ApplicationIndex.Should().Be(0);
    }

    [Fact]
    public void ArBalance_WithUnknownElements_Deserializes()
    {
        var doc = new BsonDocument
        {
            { "_id", "bal-1" }, { "TenantId", "t" }, { "GlAccountId", "gl" }, { "FutureBalanceField", 1 },
            { "PostingEntries", new BsonArray { new BsonDocument { { "EntryId", "cash-cp-1-0" }, { "FutureEntryField", 1 } } } }
        };

        var balance = BsonSerializer.Deserialize<ArBalance>(doc);

        balance.PostingEntries.Single().EntryId.Should().Be("cash-cp-1-0");
    }

    [Theory]
    [InlineData(typeof(ArAdjustment))]
    [InlineData(typeof(ArBatchRule))]
    [InlineData(typeof(GlAccount))]
    [InlineData(typeof(GlSegmentCodes))]
    [InlineData(typeof(PremiumSplitConfig))]
    [InlineData(typeof(PlanSplitOverride))]
    public void OtherPersistedModels_IgnoreExtraElements(Type type)
    {
        BsonClassMap.LookupClassMap(type).IgnoreExtraElements.Should().BeTrue();
    }
}
