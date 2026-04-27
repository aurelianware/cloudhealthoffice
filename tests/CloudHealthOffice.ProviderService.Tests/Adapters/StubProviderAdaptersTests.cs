using ProviderService.Adapters;
using ProviderService.Models;

namespace CloudHealthOffice.ProviderService.Tests.Adapters;

/// <summary>
/// Each stub adapter must throw <see cref="NotImplementedException"/> with a
/// platform-specific TODO marker pointing at the architecture doc, so anyone
/// following the trace from a stack trace lands directly on the migration plan.
/// </summary>
public class StubProviderAdaptersTests
{
    public static IEnumerable<object[]> Stubs => new[]
    {
        new object[] { (IProviderAdapter)new QnxtProviderAdapter(), "qnxt", "TODO(qnxt-provider)" },
        new object[] { (IProviderAdapter)new FacetsProviderAdapter(), "facets", "TODO(facets-provider)" },
        new object[] { (IProviderAdapter)new HealthEdgeProviderAdapter(), "healthedge", "TODO(healthedge-provider)" },
    };

    [Theory]
    [MemberData(nameof(Stubs))]
    public void Platform_identifier_is_stable(IProviderAdapter adapter, string expectedPlatform, string _)
    {
        adapter.Platform.Should().Be(expectedPlatform);
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task Every_method_throws_with_migration_todo(
        IProviderAdapter adapter, string _, string expectedTodoMarker)
    {
        var request = new ProviderAdapterRequest { TenantId = "tenant-a", Npi = "1234567890", ProviderId = "p-1" };

        await AssertThrows(() => adapter.GetProviderAsync(request), expectedTodoMarker);
        await AssertThrows(() => adapter.GetProviderByNpiAsync(request), expectedTodoMarker);
        await AssertThrows(() => adapter.GetNetworkAsync(request), expectedTodoMarker);
        await AssertThrows(() => adapter.GetNetworkRosterAsync(request), expectedTodoMarker);
        await AssertThrows(() => adapter.SearchProvidersAsync(request), expectedTodoMarker);
    }

    private static async Task AssertThrows(Func<Task> act, string expectedTodoMarker)
    {
        var ex = await act.Should().ThrowAsync<NotImplementedException>();
        ex.Which.Message.Should().Contain(expectedTodoMarker);
        ex.Which.Message.Should().Contain("docs/architecture/provider-adapter-pattern.md");
    }
}
