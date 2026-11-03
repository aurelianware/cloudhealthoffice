using CloudHealthOffice.Infrastructure.Programs;
using FluentAssertions;
using Moq;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;
using ProviderService.Services.Roster;

namespace CloudHealthOffice.ProviderService.Tests.Services.Roster;

public class NetworkRosterImporterTests
{
    private const string Tenant = "tenant-a";
    private static readonly NetworkRosterImporter.Permissions All = new(CreateNetworks: true, RecordCredentialing: true);

    private readonly Mock<IProviderRepository> _providers = new();
    private readonly Mock<IProviderVersioningService> _versioning = new();
    private readonly Mock<IOrganizationService> _networks = new();
    private readonly Mock<ICredentialingService> _credentialing = new();
    private readonly Mock<INppesLookupClient> _nppes = new();
    private readonly List<Provider> _saved = [];
    private readonly List<Organization> _networkList =
    [
        new() { OrganizationId = "net-gold", Name = "Gold PPO network" },
    ];

    public NetworkRosterImporterTests()
    {
        _networks.Setup(n => n.ListAsync(null, null, null, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((NetworkType? _, LineOfBusiness? _, string? _, int page, int _) =>
                ((IReadOnlyList<Organization>)(page == 1 ? _networkList : []), (int?)null));
        _networks.Setup(n => n.CreateAndActivateAsync(It.IsAny<Organization>(), It.IsAny<string>()))
            .ReturnsAsync((Organization o, string _) => { o.OrganizationId = "net-" + o.Name.Split(' ')[0].ToLowerInvariant(); return o; });
        _versioning.Setup(v => v.CreateDraftAsync(It.IsAny<Provider>(), It.IsAny<string>()))
            .ReturnsAsync((Provider p, string _) => { p.ProviderId = "prov-" + p.NPI; p.VersionId = "v1"; _saved.Add(p); return p; });
        _versioning.Setup(v => v.AmendActiveProviderAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string id, string _) => new Provider
            {
                ProviderId = id, VersionId = "v2", NPI = "1588667638", VersionState = ProviderVersionState.Draft,
                ReferenceOnly = true,
                NetworkParticipations = [.. Existing.NetworkParticipations],
            });
        _providers.Setup(p => p.UpdateAsync(It.IsAny<Provider>()))
            .ReturnsAsync((Provider p) => { _saved.Add(p); return p; });
        _versioning.Setup(v => v.ActivateVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string id, string _, string _) => _saved.Last(p => p.ProviderId == id));
        _nppes.Setup(n => n.LookupAsync(Tenant, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string npi, CancellationToken _) => new NppesRecord
            {
                Npi = npi, EnumerationType = NppesEntityType.Organization, OrganizationName = "Lakeside Clinic",
                Taxonomies = [new() { Code = "261QP2300X", Description = "Primary Care Clinic", IsPrimary = true }],
            });
    }

    private static readonly Provider Existing = new()
    {
        ProviderId = "prov-known", VersionId = "v1", NPI = "1588667638", VersionState = ProviderVersionState.Active,
        ReferenceOnly = true,
        NetworkParticipations =
        [
            new() { NetworkId = "net-gold", PlanId = "PPO-GOLD", LineOfBusiness = LineOfBusiness.Commercial, EffectiveDate = new DateTime(2026, 1, 1) },
        ],
    };

    private NetworkRosterImporter Importer() =>
        new(_providers.Object, _versioning.Object, _networks.Object, _credentialing.Object, _nppes.Object);

    private Task<NetworkRosterImportResult> Import(string content, bool dryRun = false, NetworkRosterImporter.Permissions? may = null) =>
        Importer().ImportAsync(Tenant, content, LineOfBusiness.Commercial, may ?? All, dryRun, "importer", "trace", CancellationToken.None);

    [Fact]
    public async Task A_new_NPI_is_created_from_NPPES_as_a_network_provider_in_the_named_network()
    {
        var result = await Import("NPI,Network,Plan,Effective Date,Term Date,Tier\n1234567893,gold ppo network,PPO-GOLD,2026-01-01,2026-12-31,2\n");

        result.Errors.Should().BeEmpty();
        result.ProvidersCreated.Should().Be(1);
        result.ParticipationsAdded.Should().Be(1);
        var provider = _saved.Single();
        provider.ReferenceOnly.Should().BeFalse();
        provider.OrganizationName.Should().Be("Lakeside Clinic");
        var participation = provider.NetworkParticipations.Single();
        participation.NetworkId.Should().Be("net-gold");
        participation.PlanId.Should().Be("PPO-GOLD");
        participation.NetworkTier.Should().Be("Tier2");
        participation.TerminationDate.Should().Be(new DateTime(2026, 12, 31));
    }

    [Fact]
    public async Task A_known_provider_gets_only_the_participations_it_lacks_and_becomes_a_network_provider()
    {
        _providers.Setup(p => p.GetByNPIAsync("1588667638")).ReturnsAsync(Existing);

        var result = await Import(
            "npi|network|plan|effective_date\n" +
            "1588667638|net-gold|PPO-GOLD|2026-01-01\n" +   // already there
            "1588667638|net-gold|HMO-SILVER|2026-01-01\n");

        result.Unchanged.Should().Be(1);
        result.ParticipationsAdded.Should().Be(1);
        result.MadeNetworkProviders.Should().Be(1);
        var saved = _saved.Single();
        saved.ReferenceOnly.Should().BeFalse();
        saved.NetworkParticipations.Select(p => p.PlanId).Should().Equal("PPO-GOLD", "HMO-SILVER");
        _versioning.Verify(v => v.ActivateVersionAsync("prov-known", "v2", "importer"), Times.Once);
    }

    [Fact]
    public async Task A_missing_network_is_created_once_or_refused_without_networks_write()
    {
        const string file = "NPI,Network,Effective Date\n1234567893,Silver HMO network,2026-01-01\n1588667638,Silver HMO network,2026-01-01\n";

        var created = await Import(file);
        created.NetworksCreated.Should().Equal("Silver HMO network");
        _networks.Verify(n => n.CreateAndActivateAsync(It.Is<Organization>(o => o.Name == "Silver HMO network" && o.TenantId == Tenant), "importer"), Times.Once);

        var refused = await Import(file, may: new(CreateNetworks: false, RecordCredentialing: false));
        refused.Rejected.Should().Be(2);
        refused.Errors.Should().AllSatisfy(e => e.Should().Contain("does not exist"));
    }

    [Fact]
    public async Task A_new_network_is_not_created_when_its_only_provider_is_refused()
    {
        _nppes.Setup(n => n.LookupAsync(Tenant, "1497758544", It.IsAny<CancellationToken>())).ReturnsAsync((NppesRecord?)null);

        var result = await Import("NPI,Network,Effective Date\n1497758544,Bronze network,2026-01-01\n");

        result.Rejected.Should().Be(1);
        result.NetworksCreated.Should().BeEmpty();
        _networks.Verify(n => n.CreateAndActivateAsync(It.IsAny<Organization>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Dry_run_reports_and_writes_nothing()
    {
        var result = await Import("NPI,Network,Effective Date,Credentialed\n1234567893,New network,2026-01-01,yes\n", dryRun: true);

        result.ProvidersCreated.Should().Be(1);
        result.NetworksCreated.Should().Equal("New network");
        result.Credentialed.Should().Be(1);
        _saved.Should().BeEmpty();
        _networks.Verify(n => n.CreateAndActivateAsync(It.IsAny<Organization>(), It.IsAny<string>()), Times.Never);
        _credentialing.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Credentialed_rows_record_a_delegated_approval_from_the_first_effective_date()
    {
        var result = await Import("NPI,Network,Effective Date,Credentialed\n1234567893,net-gold,2026-02-01,Y\n1234567893,net-gold,2026-01-15,Y\n");

        result.Credentialed.Should().Be(1);
        _credentialing.Verify(c => c.RecordDecisionAsync(Tenant, "prov-1234567893",
            It.Is<RecordDecisionRequest>(r => r.Decision == CredentialingDecision.Approved
                && r.DecisionAuthorityType == DecisionAuthorityType.DelegatedAuthority
                && r.CredentialingDate == new DateTime(2026, 1, 15)),
            "importer", "trace", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Credentialing_without_the_permission_is_a_warning_not_a_write()
    {
        var result = await Import("NPI,Network,Effective Date,Credentialed\n1234567893,net-gold,2026-01-01,yes\n",
            may: new(CreateNetworks: false, RecordCredentialing: false));

        result.ParticipationsAdded.Should().Be(1);
        result.Warnings.Should().ContainSingle().Which.Should().Contain("providers:credential");
        _credentialing.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Bad_rows_are_refused_by_line_and_unknown_NPPES_NPIs_too()
    {
        _nppes.Setup(n => n.LookupAsync(Tenant, "1497758544", It.IsAny<CancellationToken>())).ReturnsAsync((NppesRecord?)null);

        var result = await Import(
            "NPI,Network,Effective Date,Term Date,Line of Business\n" +
            "12345,net-gold,2026-01-01,,\n" +
            "1234567893,net-gold,2026-03-01,2026-02-01,\n" +
            "1234567893,net-gold,2026-01-01,,Dental\n" +
            "1497758544,net-gold,2026-01-01,,\n");

        result.Rejected.Should().Be(4);
        result.Errors.Should().Contain(e => e.StartsWith("line 2:") && e.Contains("not 10 digits"));
        result.Errors.Should().Contain(e => e.StartsWith("line 3:") && e.Contains("not after Effective Date"));
        result.Errors.Should().Contain(e => e.StartsWith("line 4:") && e.Contains("Line of Business"));
        result.Errors.Should().Contain(e => e.StartsWith("line 5:") && e.Contains("NPPES has no such NPI"));
    }

    [Fact]
    public async Task A_header_without_the_required_columns_is_a_file_error()
    {
        var result = await Import("NPI,Plan\n1234567893,PPO-GOLD\n");

        result.FileErrors.Should().BeEquivalentTo("The header has no Network column.", "The header has no Effective Date column.");
    }
}
