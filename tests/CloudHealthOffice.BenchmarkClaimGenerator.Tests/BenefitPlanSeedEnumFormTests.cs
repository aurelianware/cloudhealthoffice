using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Output;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

/// <summary>
/// Seeded benefit plans carry <c>planType</c> and <c>lineOfBusiness</c> in the form
/// benefit-plan-service stores and filters on (D-31): the shared Cosmos serializer's
/// camelCase string enums. PascalCase values would never match a search.
/// </summary>
public class BenefitPlanSeedEnumFormTests
{
    public enum PlanTypeProbe { Medicaid, PPO, HDHP }

    [Theory]
    [InlineData(PlanTypeProbe.Medicaid)]
    [InlineData(PlanTypeProbe.PPO)]
    [InlineData(PlanTypeProbe.HDHP)]
    public void StoredEnumName_MatchesTheSharedSerializersEnumConverter(PlanTypeProbe value)
    {
        // The converter CosmosSystemTextJsonSerializer registers.
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        var stored = JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(value, options));

        Assert.Equal(stored, CosmosDbSeeder.StoredEnumName(value.ToString()));
    }

    [Fact]
    public async Task SeededPlans_UseTheStoredEnumForm()
    {
        var seeder = new CapturingSeeder();

        await seeder.SeedBenefitPlansAsync(SyntheticBenefitPlanGenerator.Generate(42));

        Assert.NotEmpty(seeder.Plans);
        Assert.All(seeder.Plans, doc =>
        {
            Assert.Equal("medicaid", doc.GetProperty("planType").GetString());
            Assert.Equal("medicaid", doc.GetProperty("lineOfBusiness").GetString());
        });
    }

    private sealed class CapturingSeeder : CosmosDbSeeder
    {
        public CapturingSeeder() : base("unused-connection-string") { }

        public List<JsonElement> Plans { get; } = new();

        protected override Task WriteDocumentsAsync(
            string containerName, List<object> documents, CancellationToken cancellationToken)
        {
            if (containerName == ContainerNames.BenefitPlans)
                Plans.AddRange(documents.Select(d => JsonSerializer.SerializeToElement(d)));
            return Task.CompletedTask;
        }
    }
}
