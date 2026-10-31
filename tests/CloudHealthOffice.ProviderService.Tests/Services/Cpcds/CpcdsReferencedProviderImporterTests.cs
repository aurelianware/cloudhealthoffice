using FluentAssertions;
using Moq;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;
using ProviderService.Services.Cpcds;

namespace CloudHealthOffice.ProviderService.Tests.Services.Cpcds;

public class CpcdsReferencedProviderImporterTests
{
    private const string Tenant = "tenant-a";

    private readonly Mock<IProviderRepository> _providers = new();
    private readonly Mock<IProviderVersioningService> _versioning = new();
    private readonly Mock<INppesLookupClient> _nppes = new();
    private readonly List<Provider> _created = [];

    public CpcdsReferencedProviderImporterTests()
    {
        _versioning.Setup(v => v.CreateDraftAsync(It.IsAny<Provider>(), It.IsAny<string>()))
            .ReturnsAsync((Provider p, string _) =>
            {
                p.ProviderId = "prov-" + p.NPI;
                p.VersionId = "v1";
                _created.Add(p);
                return p;
            });
        _versioning.Setup(v => v.ActivateVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string id, string _, string _) => _created.Single(p => p.ProviderId == id));
        _nppes.Setup(n => n.LookupAsync(Tenant, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string npi, CancellationToken _) => Person(npi));
    }

    private static string Sample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cloudhealthoffice-main.sln")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "docs/integration/samples/cpcds/claims.csv"));
    }

    private static NppesRecord Person(string npi) => new()
    {
        Npi = npi,
        EnumerationType = NppesEntityType.Individual,
        ProviderFirstName = "Dana",
        ProviderLastName = "Reyes",
        ProviderCredential = "MD",
        Taxonomies = [new() { Code = "207Q00000X", Description = "Family Medicine", IsPrimary = true }],
        Addresses =
        [
            new() { AddressPurpose = "MAILING", AddressLine1 = "PO Box 1", City = "Salem", State = "OR", PostalCode = "97301" },
            new() { AddressPurpose = "LOCATION", AddressLine1 = "1 Main St", AddressLine2 = "Suite 2", City = "Eugene", State = "OR", PostalCode = "974010000" },
        ],
    };

    private CpcdsReferencedProviderImporter Importer() => new(_providers.Object, _versioning.Object, _nppes.Object);

    private Task<CpcdsProviderImportResult> Import(string content, bool dryRun = false, Dictionary<string, string>? map = null) =>
        Importer().ImportAsync(Tenant, content, map, dryRun, "importer");

    [Fact]
    public async Task Every_distinct_NPI_in_the_provider_columns_is_read_once_with_its_roles()
    {
        var result = await Import(Sample(), dryRun: true);

        result.FileErrors.Should().BeEmpty();
        result.Providers.Select(p => p.Npi).Should().BeEquivalentTo(
            ["1234567893", "1497758544", "1902803802", "1588667638"]);
        result.Providers.Single(p => p.Npi == "1902803802").Roles.Should().Equal("billing", "performing");
    }

    [Fact]
    public async Task Unknown_NPIs_become_reference_only_providers_described_from_NPPES()
    {
        var result = await Import("Claim billing provider NPI\n1588667638\n");

        result.Created.Should().Be(1);
        var provider = _created.Single();
        provider.ReferenceOnly.Should().BeTrue();
        provider.TenantId.Should().Be(Tenant);
        provider.ProviderType.Should().Be(ProviderType.Individual);
        (provider.FirstName, provider.LastName, provider.Credentials).Should().Be(("Dana", "Reyes", "MD"));
        provider.TaxonomyCode.Should().Be("207Q00000X");
        provider.Address.Should().Be("1 Main St, Suite 2");
        provider.City.Should().Be("Eugene");
        provider.AcceptingNewPatients.Should().BeFalse();
        provider.NetworkParticipations.Should().BeEmpty();
        result.Providers.Single().ProviderId.Should().Be("prov-1588667638");
        _versioning.Verify(v => v.ActivateVersionAsync("prov-1588667638", "v1", "importer"), Times.Once);
    }

    [Fact]
    public async Task An_organization_NPI_becomes_an_organization()
    {
        _nppes.Setup(n => n.LookupAsync(Tenant, "1497758544", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NppesRecord { Npi = "1497758544", EnumerationType = NppesEntityType.Organization, OrganizationName = "Valley Hospital" });

        await Import("Claim billing provider NPI\n1497758544\n");

        var provider = _created.Single();
        provider.ProviderType.Should().Be(ProviderType.Organization);
        provider.OrganizationName.Should().Be("Valley Hospital");
        provider.FirstName.Should().BeNull();
    }

    [Fact]
    public async Task Providers_CHO_already_has_are_left_alone()
    {
        _providers.Setup(p => p.GetByNPIAsync("1588667638"))
            .ReturnsAsync(new Provider { NPI = "1588667638", ProviderType = ProviderType.Individual });

        var result = await Import("Claim billing provider NPI\n1588667638\n");

        result.AlreadyKnown.Should().Be(1);
        _created.Should().BeEmpty();
        _nppes.Verify(n => n.LookupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NPIs_NPPES_does_not_know_failed_lookups_and_non_NPIs_are_reported_not_created()
    {
        _nppes.Setup(n => n.LookupAsync(Tenant, "1111111112", It.IsAny<CancellationToken>())).ReturnsAsync((NppesRecord?)null);
        _nppes.Setup(n => n.LookupAsync(Tenant, "2222222220", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NppesUnavailableException("NPPES lookup returned 503."));

        var result = await Import("Claim billing provider NPI\n1111111112\n2222222220\nABC\n");

        result.NotInNppes.Should().Be(1);
        result.LookupFailed.Should().Be(1);
        result.Invalid.Should().Be(1);
        _created.Should().BeEmpty();
    }

    [Fact]
    public async Task Dry_run_reports_what_would_be_created_and_writes_nothing()
    {
        var result = await Import(Sample(), dryRun: true);

        result.Created.Should().Be(4);
        _versioning.Verify(v => v.CreateDraftAsync(It.IsAny<Provider>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_column_map_finds_renamed_columns_and_a_file_without_provider_columns_is_refused()
    {
        var mapped = await Import("BILL_NPI\n1588667638\n",
            map: new() { ["Claim billing provider NPI"] = "BILL_NPI" });
        mapped.Created.Should().Be(1);

        var refused = await Import("Claim unique identifier\nC-1\n");
        refused.FileErrors.Should().ContainSingle().Which.Should().Contain("Claim billing provider NPI");
    }
}
