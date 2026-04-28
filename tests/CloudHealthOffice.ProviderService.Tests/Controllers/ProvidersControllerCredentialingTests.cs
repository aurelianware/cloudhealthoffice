using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Controllers;

/// <summary>
/// Regression coverage for the legacy
/// <c>PUT /api/v1/providers/{id}/credentialing</c> endpoint after the
/// 5.6 rewire. Pre-rewire this endpoint called
/// <see cref="IProviderRepository.UpdateAsync"/> on Active providers and
/// always returned 409 (the bug capability 5.6 fixes). Post-rewire the
/// endpoint funnels through the event-sourced credentialing workflow
/// with <see cref="DecisionAuthorityType.DelegatedAuthority"/> and
/// always succeeds on Active providers.
/// </summary>
public class ProvidersControllerCredentialingTests
{
    private const string TenantId = "tenant-a";
    private const string ProviderId = "p-001";

    private readonly InMemoryCredentialingEventRepository _eventRepository = new();
    private readonly FakeCredentialingEventPublisher _publisher;
    private readonly InMemoryProviderRepository _providerRepository;
    private readonly CredentialingService _credentialing;
    private readonly ProvidersController _controller;

    public ProvidersControllerCredentialingTests()
    {
        _publisher = new FakeCredentialingEventPublisher(_eventRepository);
        _providerRepository = new InMemoryProviderRepository { TenantId = TenantId };
        SeedActiveProvider();
        _credentialing = new CredentialingService(
            _eventRepository, _publisher, _providerRepository,
            new CredentialingProjector(),
            NullLogger<CredentialingService>.Instance);

        _controller = new ProvidersController(
            providerRepository: _providerRepository,
            versioning: null!,
            adapterFactory: null!,
            integrityProjection: null!,
            panelGatingValidator: null!,
            credentialing: _credentialing,
            logger: NullLogger<ProvidersController>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = TenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };
    }

    [Fact]
    public async Task Legacy_PUT_credentialing_against_active_provider_returns_200_not_409()
    {
        // The headline regression: pre-5.6 this returned 409 because
        // the underlying UpdateAsync rejects non-Draft rows. Post-5.6
        // the endpoint emits a DecisionRecorded event with
        // DelegatedAuthority and patches the projection.
        var result = await _controller.UpdateCredentialing(
            ProviderId,
            new CredentialingUpdateRequest
            {
                Status = CredentialingStatus.Approved,
                CredentialingDate = DateTime.UtcNow,
                RecredentialingDueDate = DateTime.UtcNow.AddYears(2),
            },
            CancellationToken.None);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var provider = ok!.Value as Provider;
        provider.Should().NotBeNull();
        provider!.CredentialingStatus.Should().Be(CredentialingStatus.Approved);
    }

    [Fact]
    public async Task Legacy_PUT_credentialing_writes_DecisionRecorded_event_with_DelegatedAuthority()
    {
        await _controller.UpdateCredentialing(
            ProviderId,
            new CredentialingUpdateRequest
            {
                Status = CredentialingStatus.Approved,
                CredentialingDate = DateTime.UtcNow,
                RecredentialingDueDate = DateTime.UtcNow.AddYears(2),
            },
            CancellationToken.None);

        _eventRepository.Store
            .Should().Contain(e =>
                e.TenantId == TenantId
                && e.ProviderId == ProviderId
                && e.EventType == CredentialingEventType.DecisionRecorded);

        // The synthesized application event must be present too.
        _eventRepository.Store
            .Should().Contain(e =>
                e.TenantId == TenantId
                && e.ProviderId == ProviderId
                && e.EventType == CredentialingEventType.ApplicationSubmitted
                && e.EventId.StartsWith("synthesized-application:"));
    }

    [Fact]
    public async Task Legacy_PUT_credentialing_with_unsupported_status_returns_400()
    {
        // Pending / Expired / Suspended have no event-chain analogue.
        // The endpoint must surface 400 with a hint, not silently
        // succeed or 500.
        var result = await _controller.UpdateCredentialing(
            ProviderId,
            new CredentialingUpdateRequest
            {
                Status = CredentialingStatus.Suspended,
                CredentialingDate = DateTime.UtcNow,
            },
            CancellationToken.None);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Legacy_PUT_credentialing_returns_404_when_provider_missing()
    {
        var result = await _controller.UpdateCredentialing(
            "missing-provider",
            new CredentialingUpdateRequest
            {
                Status = CredentialingStatus.Approved,
            },
            CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Legacy_PUT_credentialing_is_idempotent_for_same_decision()
    {
        var when = DateTime.UtcNow;
        var request = new CredentialingUpdateRequest
        {
            Status = CredentialingStatus.Approved,
            CredentialingDate = when,
            RecredentialingDueDate = when.AddYears(2),
        };

        await _controller.UpdateCredentialing(ProviderId, request, CancellationToken.None);
        await _controller.UpdateCredentialing(ProviderId, request, CancellationToken.None);

        // Two events: synthesized application + decision. Retrying the
        // same instant must not double-write either.
        _eventRepository.Store
            .Count(e => e.TenantId == TenantId && e.ProviderId == ProviderId)
            .Should().Be(2);
    }

    private void SeedActiveProvider()
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
        }).GetAwaiter().GetResult();
    }
}
