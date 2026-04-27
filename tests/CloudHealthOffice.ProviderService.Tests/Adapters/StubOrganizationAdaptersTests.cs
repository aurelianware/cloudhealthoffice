using ProviderService.Adapters;
using ProviderService.Models;

namespace CloudHealthOffice.ProviderService.Tests.Adapters;

/// <summary>
/// Each stub organization adapter must throw <see cref="NotImplementedException"/>
/// with a platform-specific TODO marker pointing at the architecture doc,
/// matching the behaviour of the provider stubs.
/// </summary>
public class StubOrganizationAdaptersTests
{
    public static IEnumerable<object[]> Stubs => new[]
    {
        new object[] { (IOrganizationAdapter)new QnxtOrganizationAdapter(), "qnxt", "TODO(qnxt-organization)" },
        new object[] { (IOrganizationAdapter)new FacetsOrganizationAdapter(), "facets", "TODO(facets-organization)" },
    };

    [Theory]
    [MemberData(nameof(Stubs))]
    public void Platform_identifier_is_stable(IOrganizationAdapter adapter, string expectedPlatform, string _)
    {
        adapter.Platform.Should().Be(expectedPlatform);
    }

    [Theory]
    [MemberData(nameof(Stubs))]
    public async Task Every_method_throws_with_migration_todo(
        IOrganizationAdapter adapter, string _, string expectedTodoMarker)
    {
        var request = new OrganizationAdapterRequest
        {
            TenantId = "tenant-a",
            OrganizationId = "n-1",
            ParentOrganizationId = "p-1",
        };

        await AssertThrows(() => adapter.GetOrganizationAsync(request), expectedTodoMarker);
        await AssertThrows(() => adapter.GetByParentAsync(request), expectedTodoMarker);
        await AssertThrows(() => adapter.ListAsync(request), expectedTodoMarker);
    }

    private static async Task AssertThrows(Func<Task> act, string expectedTodoMarker)
    {
        var ex = await act.Should().ThrowAsync<NotImplementedException>();
        ex.Which.Message.Should().Contain(expectedTodoMarker);
        ex.Which.Message.Should().Contain("docs/architecture/network-as-organization.md");
    }
}
