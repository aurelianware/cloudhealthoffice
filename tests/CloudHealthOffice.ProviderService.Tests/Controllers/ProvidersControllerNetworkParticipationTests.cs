using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Controllers;

/// <summary>
/// Regression coverage for <c>POST /api/v1/providers/{id}/network-participations</c>
/// against an Active provider. Before this fix, <see cref="IProviderRepository.UpdateAsync"/>
/// (see <see cref="InMemoryProviderRepository"/>) always rejected non-Draft rows with
/// <see cref="ProviderVersionStateException"/>, which the controller translated to a bare
/// 409 — silently leaving the participation unadded, because the only other endpoint that
/// could produce an editable Draft (<c>POST /amend</c>) had no paired endpoint to populate
/// that Draft's contents before activating it. The fix makes the endpoint self-healing: an
/// Active provider is auto-amended, edited, and activated within the same call.
/// </summary>
public class ProvidersControllerNetworkParticipationTests
{
    private const string TenantId = "tenant-a";
    private const string ProviderId = "p-001";

    private readonly InMemoryProviderRepository _providerRepository;
    private readonly InMemoryProviderTransitionRepository _transitions;
    private readonly FakeProviderVersionEventPublisher _events;
    private readonly ProviderVersioningService _versioning;
    private readonly PanelGatingValidator _panelGatingValidator;
    private readonly ProvidersController _controller;

    public ProvidersControllerNetworkParticipationTests()
    {
        _providerRepository = new InMemoryProviderRepository { TenantId = TenantId };
        _transitions = new InMemoryProviderTransitionRepository();
        _events = new FakeProviderVersionEventPublisher();
        _versioning = new ProviderVersioningService(
            _providerRepository, _transitions, _events,
            NullLogger<ProviderVersioningService>.Instance);
        _panelGatingValidator = new PanelGatingValidator(
            NullLogger<PanelGatingValidator>.Instance,
            new StaticOptionsMonitor(new NetworkParticipationBackfillOptions()));

        SeedActiveProviderWithOneParticipation();

        _controller = new ProvidersController(
            providerRepository: _providerRepository,
            versioning: _versioning,
            adapterFactory: null!,
            integrityProjection: null!,
            panelGatingValidator: _panelGatingValidator,
            credentialing: null!,
            logger: NullLogger<ProvidersController>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = TenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };
    }

    [Fact]
    public async Task AddNetworkParticipation_against_active_provider_returns_200_not_409()
    {
        var result = await _controller.AddNetworkParticipation(ProviderId, NewParticipation("mcc-demo-network"));

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var provider = ok!.Value as Provider;
        provider.Should().NotBeNull();
        provider!.VersionState.Should().Be(ProviderVersionState.Active);
    }

    [Fact]
    public async Task AddNetworkParticipation_against_active_provider_preserves_existing_participations()
    {
        var result = await _controller.AddNetworkParticipation(ProviderId, NewParticipation("mcc-demo-network"));

        var provider = ((OkObjectResult)result.Result!).Value as Provider;
        provider!.NetworkParticipations.Should().HaveCount(2);
        provider.NetworkParticipations.Should().Contain(p => p.NetworkId == "mcc-local-network");
        provider.NetworkParticipations.Should().Contain(p => p.NetworkId == "mcc-demo-network");
    }

    [Fact]
    public async Task AddNetworkParticipation_against_active_provider_amends_and_activates_a_new_version()
    {
        var result = await _controller.AddNetworkParticipation(ProviderId, NewParticipation("mcc-demo-network"));

        var provider = ((OkObjectResult)result.Result!).Value as Provider;
        provider!.VersionNumber.Should().Be(2);
        provider.PredecessorVersionId.Should().NotBeNullOrEmpty();

        _transitions.Items.Should().Contain(t => t.TransitionType == ProviderTransitionType.Amend);
        _events.Events.Should().Contain(e => e.EventType == ProviderVersionEventType.ProviderVersionActivated);
    }

    [Fact]
    public async Task AddNetworkParticipation_against_unknown_provider_returns_404()
    {
        var result = await _controller.AddNetworkParticipation("does-not-exist", NewParticipation("mcc-demo-network"));

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    /// <summary>
    /// Regression coverage for the ExcludedProviderDenied fixture collision:
    /// when the MCC validator's run-scoped NPI generation collides with an
    /// existing provider, correcting that provider's integrity fields (the
    /// actual source the adjudication-path integrity gate reads) requires
    /// the same self-healing amend behavior as AddNetworkParticipation.
    /// </summary>
    [Fact]
    public async Task UpdateProvider_against_active_provider_returns_200_not_409()
    {
        var toUpdate = BuildUpdatePayload(integrityScore: 0, integrityRating: "Blocked", credentialingStatus: CredentialingStatus.Denied);

        var result = await _controller.UpdateProvider(ProviderId, toUpdate);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var provider = ok!.Value as Provider;
        provider.Should().NotBeNull();
        provider!.VersionState.Should().Be(ProviderVersionState.Active);
        provider.IntegrityScore.Should().Be(0);
        provider.IntegrityRating.Should().Be("Blocked");
        provider.CredentialingStatus.Should().Be(CredentialingStatus.Denied);
    }

    [Fact]
    public async Task UpdateProvider_against_active_provider_amends_and_activates_a_new_version()
    {
        var toUpdate = BuildUpdatePayload(integrityScore: 0, integrityRating: "Blocked", credentialingStatus: CredentialingStatus.Denied);

        var result = await _controller.UpdateProvider(ProviderId, toUpdate);

        var provider = ((OkObjectResult)result.Result!).Value as Provider;
        provider!.VersionNumber.Should().Be(2);
        provider.PredecessorVersionId.Should().NotBeNullOrEmpty();

        _transitions.Items.Should().Contain(t => t.TransitionType == ProviderTransitionType.Amend);
        _events.Events.Should().Contain(e => e.EventType == ProviderVersionEventType.ProviderVersionActivated);
    }

    [Fact]
    public async Task UpdateProvider_against_unknown_provider_returns_404()
    {
        var toUpdate = BuildUpdatePayload(integrityScore: 0, integrityRating: "Blocked", credentialingStatus: CredentialingStatus.Denied);

        var result = await _controller.UpdateProvider("does-not-exist", toUpdate);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    private static Provider BuildUpdatePayload(int integrityScore, string integrityRating, CredentialingStatus credentialingStatus) => new()
    {
        TenantId = TenantId,
        NPI = "1234567890",
        ProviderType = ProviderType.Individual,
        FirstName = "Test",
        LastName = "Provider",
        PrimarySpecialty = "Internal Medicine",
        TaxonomyCode = "207R00000X",
        IntegrityScore = integrityScore,
        IntegrityRating = integrityRating,
        CredentialingStatus = credentialingStatus,
        NetworkParticipations = new List<NetworkParticipation>
        {
            new()
            {
                PlanId = null,
                NetworkId = "mcc-local-network",
                LineOfBusiness = LineOfBusiness.Medicaid,
                NetworkTier = "InNetwork",
                EffectiveDate = DateTime.UtcNow.AddYears(-2),
                AcceptingNewPatients = true,
            }
        },
    };

    private static NetworkParticipation NewParticipation(string networkId) => new()
    {
        PlanId = null,
        NetworkId = networkId,
        LineOfBusiness = LineOfBusiness.Medicaid,
        NetworkTier = "InNetwork",
        EffectiveDate = DateTime.UtcNow.AddYears(-1),
        AcceptingNewPatients = true,
    };

    private void SeedActiveProviderWithOneParticipation()
    {
        _providerRepository.CreateAsync(new Provider
        {
            Id = ProviderId,
            ProviderId = ProviderId,
            TenantId = TenantId,
            NPI = "1234567890",
            ProviderType = ProviderType.Individual,
            FirstName = "Test",
            LastName = "Provider",
            PrimarySpecialty = "Internal Medicine",
            TaxonomyCode = "207R00000X",
            VersionId = ProviderId,
            VersionNumber = 1,
            VersionState = ProviderVersionState.Active,
            NetworkParticipations = new List<NetworkParticipation>
            {
                new()
                {
                    PlanId = null,
                    NetworkId = "mcc-local-network",
                    LineOfBusiness = LineOfBusiness.Medicaid,
                    NetworkTier = "InNetwork",
                    EffectiveDate = DateTime.UtcNow.AddYears(-2),
                    AcceptingNewPatients = true,
                }
            },
        }).GetAwaiter().GetResult();
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<NetworkParticipationBackfillOptions>
    {
        public StaticOptionsMonitor(NetworkParticipationBackfillOptions value) => CurrentValue = value;
        public NetworkParticipationBackfillOptions CurrentValue { get; }
        public NetworkParticipationBackfillOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<NetworkParticipationBackfillOptions, string?> listener) => null;
    }
}
