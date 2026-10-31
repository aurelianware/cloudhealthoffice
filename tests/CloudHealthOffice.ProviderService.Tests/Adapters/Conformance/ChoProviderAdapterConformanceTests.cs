using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Adapters;
using ProviderService.Models;

namespace CloudHealthOffice.ProviderService.Tests.Adapters.Conformance;

/// <summary>
/// Runs the provider conformance suite against the CHO reference adapter,
/// which proves the suite itself. A vendor connector adds its own subclass.
/// </summary>
public sealed class ChoProviderAdapterConformanceTests : ProviderAdapterConformanceSuite
{
    private const string Tenant = "tenant-a";

    protected override async Task<ProviderAdapterConformanceFixture> ArrangeAsync()
    {
        var repo = new InMemoryProviderRepository { TenantId = Tenant };
        await repo.CreateAsync(Provider("p-1", "1234567893", Tenant));
        await repo.CreateAsync(Provider("p-2", "1234567901", Tenant));
        await repo.CreateAsync(Provider("p-other", "1234567919", "tenant-b"));

        return new ProviderAdapterConformanceFixture(
            Adapter: new ChoProviderAdapter(repo, NullLogger<ChoProviderAdapter>.Instance),
            TenantId: Tenant,
            ExpectedPlatform: "cho",
            KnownProviderId: "p-1",
            KnownNpi: "1234567893",
            MissingProviderId: "p-404",
            MissingNpi: "1999999984");
    }

    private static Provider Provider(string id, string npi, string tenant) => new()
    {
        Id = id,
        ProviderId = id,
        TenantId = tenant,
        NPI = npi,
        ProviderType = ProviderType.Individual,
        FirstName = "Jane",
        LastName = "Doe",
        PrimarySpecialty = "Internal Medicine",
        TaxonomyCode = "207R00000X",
        CredentialingStatus = CredentialingStatus.Approved,
        Status = ProviderStatus.Active,
        VersionId = id,
        VersionNumber = 1,
        VersionState = ProviderVersionState.Active,
    };
}
