using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Adapters;
using ProviderService.Models;

namespace CloudHealthOffice.ProviderService.Tests.Adapters;

/// <summary>
/// Verifies the CHO pass-through adapter against the in-memory repository.
/// These are the regression tests for "no behavior change for current tenants" —
/// every CHO read path must return the same row shape the controller saw before
/// the refactor.
/// </summary>
public class ChoProviderAdapterTests
{
    private const string TenantId = "tenant-a";
    private const string Npi = "1234567890";

    [Fact]
    public void Platform_identifier_is_cho()
    {
        var adapter = NewAdapter(out _);
        adapter.Platform.Should().Be("cho");
    }

    [Fact]
    public async Task GetProviderByNpiAsync_returns_provider_when_found()
    {
        var adapter = NewAdapter(out var repo);
        await Seed(repo, npi: Npi, providerId: "p-1");

        var response = await adapter.GetProviderByNpiAsync(new ProviderAdapterRequest
        {
            TenantId = TenantId,
            Npi = Npi,
        });

        response.Platform.Should().Be("cho");
        response.Provider.Should().NotBeNull();
        response.Provider!.Npi.Should().Be(Npi);
        response.Provider.ProviderId.Should().Be("p-1");
    }

    [Fact]
    public async Task GetProviderByNpiAsync_returns_null_provider_when_not_found()
    {
        var adapter = NewAdapter(out _);

        var response = await adapter.GetProviderByNpiAsync(new ProviderAdapterRequest
        {
            TenantId = TenantId,
            Npi = "0000000000",
        });

        response.Provider.Should().BeNull();
    }

    [Fact]
    public async Task GetProviderByNpiAsync_throws_when_npi_missing()
    {
        var adapter = NewAdapter(out _);

        var act = () => adapter.GetProviderByNpiAsync(new ProviderAdapterRequest
        {
            TenantId = TenantId,
        });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Npi is required*");
    }

    [Fact]
    public async Task GetProviderAsync_returns_provider_by_chain_key()
    {
        var adapter = NewAdapter(out var repo);
        await Seed(repo, npi: Npi, providerId: "p-xyz");

        var response = await adapter.GetProviderAsync(new ProviderAdapterRequest
        {
            TenantId = TenantId,
            ProviderId = "p-xyz",
        });

        response.Provider.Should().NotBeNull();
        response.Provider!.ProviderId.Should().Be("p-xyz");
    }

    [Fact]
    public async Task GetProviderAsync_throws_when_provider_id_missing()
    {
        var adapter = NewAdapter(out _);

        var act = () => adapter.GetProviderAsync(new ProviderAdapterRequest
        {
            TenantId = TenantId,
        });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("ProviderId is required*");
    }

    [Fact]
    public async Task SearchProvidersAsync_returns_roster_envelope()
    {
        var adapter = NewAdapter(out var repo);
        await Seed(repo, npi: "1111111111", providerId: "p-1", lastName: "Smith");
        await Seed(repo, npi: "2222222222", providerId: "p-2", lastName: "Jones");

        var response = await adapter.SearchProvidersAsync(new ProviderAdapterRequest
        {
            TenantId = TenantId,
            Page = 1,
            PageSize = 50,
        });

        response.Platform.Should().Be("cho");
        response.Providers.Should().HaveCount(2);
        response.Providers.Select(p => p.Npi).Should().Contain(new[] { "1111111111", "2222222222" });
    }

    [Fact]
    public async Task GetNetworkAsync_throws_until_capability_5_3_lands()
    {
        var adapter = NewAdapter(out _);

        var act = () => adapter.GetNetworkAsync(new ProviderAdapterRequest
        {
            TenantId = TenantId,
            NetworkId = "any",
        });

        var ex = await act.Should().ThrowAsync<NotImplementedException>();
        ex.Which.Message.Should().Contain("TODO(provider-network-5.3)");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static ChoProviderAdapter NewAdapter(out InMemoryProviderRepository repo)
    {
        repo = new InMemoryProviderRepository { TenantId = TenantId };
        return new ChoProviderAdapter(repo, NullLogger<ChoProviderAdapter>.Instance);
    }

    private static async Task Seed(
        InMemoryProviderRepository repo,
        string npi,
        string providerId,
        string lastName = "Doe")
    {
        await repo.CreateAsync(new Provider
        {
            Id = providerId,
            ProviderId = providerId,
            TenantId = TenantId,
            NPI = npi,
            ProviderType = ProviderType.Individual,
            FirstName = "Jane",
            LastName = lastName,
            PrimarySpecialty = "Internal Medicine",
            TaxonomyCode = "207R00000X",
            CredentialingStatus = CredentialingStatus.Approved,
            Status = ProviderStatus.Active,
            VersionId = providerId,
            VersionNumber = 1,
            VersionState = ProviderVersionState.Active,
        });
    }
}
