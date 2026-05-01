using CloudHealthOffice.ProviderService.Tests.Fakes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Controllers;

/// <summary>
/// Capability 5.6 — endpoint-shape coverage for
/// <c>GET /api/v1/providers/{id}/credentialing/status-as-of</c>.
/// </summary>
public class CredentialingControllerStatusAsOfTests
{
    private const string TenantId = "tenant-a";
    private const string ProviderId = "p-001";

    private readonly InMemoryCredentialingEventRepository _eventRepository = new();
    private readonly FakeCredentialingEventPublisher _publisher;
    private readonly InMemoryProviderRepository _providerRepository;
    private readonly CredentialingService _service;
    private readonly CredentialingController _controller;

    public CredentialingControllerStatusAsOfTests()
    {
        _publisher = new FakeCredentialingEventPublisher(_eventRepository);
        _providerRepository = new InMemoryProviderRepository { TenantId = TenantId };
        SeedActiveProvider();
        _service = new CredentialingService(
            _eventRepository, _publisher, _providerRepository,
            new CredentialingProjector(),
            NullLogger<CredentialingService>.Instance);
        _controller = new CredentialingController(_service, NullLogger<CredentialingController>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = TenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };
    }

    [Fact]
    public async Task Returns_400_when_asOfDate_missing()
    {
        var result = await _controller.GetStatusAsOf(ProviderId, asOfDate: null, CancellationToken.None);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Returns_200_with_unknown_status_for_provider_with_no_chain()
    {
        var result = await _controller.GetStatusAsOf(
            "never-credentialed",
            asOfDate: DateTime.UtcNow,
            CancellationToken.None);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var body = ok!.Value as CredentialingStatusResponse;
        body.Should().NotBeNull();
        body!.Status.Should().Be("Unknown");
        body.ProviderId.Should().Be("never-credentialed");
    }

    [Fact]
    public async Task Different_asOf_dates_against_same_chain_return_different_status()
    {
        var submittedAt = DateTimeOffset.UtcNow.AddYears(-2);
        var decidedAt = submittedAt.AddDays(30);
        var dueDate = decidedAt.AddYears(1);

        await _service.SubmitApplicationAsync(
            TenantId, ProviderId,
            new SubmitApplicationRequest { ApplicationSource = "Manual", SubmittedAt = submittedAt },
            "actor", null);
        await _service.RecordDecisionAsync(
            TenantId, ProviderId,
            new RecordDecisionRequest
            {
                Decision = CredentialingDecision.Approved,
                DecisionAuthorityType = DecisionAuthorityType.MedicalDirector,
                DecisionAuthorityId = "md-1",
                DecidedAt = decidedAt,
                CredentialingDate = decidedAt.UtcDateTime,
                RecredentialingDueDate = dueDate.UtcDateTime,
            },
            "actor", null);

        var earlyResult = await _controller.GetStatusAsOf(
            ProviderId, asOfDate: decidedAt.UtcDateTime.AddDays(7), CancellationToken.None);
        var lateResult = await _controller.GetStatusAsOf(
            ProviderId, asOfDate: dueDate.UtcDateTime.AddYears(1), CancellationToken.None);

        var early = (earlyResult.Result as OkObjectResult)!.Value as CredentialingStatusResponse;
        var late = (lateResult.Result as OkObjectResult)!.Value as CredentialingStatusResponse;

        early!.Status.Should().Be("Approved");
        late!.Status.Should().Be("Expired");
    }

    [Fact]
    public async Task Echoes_asOfDate_in_response()
    {
        var asOf = new DateTime(2025, 1, 15, 12, 30, 0, DateTimeKind.Utc);
        var result = await _controller.GetStatusAsOf(ProviderId, asOf, CancellationToken.None);
        var ok = result.Result as OkObjectResult;
        var body = ok!.Value as CredentialingStatusResponse;

        body.Should().NotBeNull();
        body!.AsOfDate.Should().Be(asOf);
    }

    private void SeedActiveProvider()
    {
        _providerRepository.CreateAsync(new Provider
        {
            Id = ProviderId,
            ProviderId = ProviderId,
            VersionId = ProviderId + "-v1",
            VersionNumber = 1,
            VersionState = ProviderVersionState.Active,
            TenantId = TenantId,
            NPI = "1234567890",
            ProviderType = ProviderType.Individual,
            FirstName = "Test",
            LastName = "Adams",
            Status = ProviderStatus.Active,
        }).GetAwaiter().GetResult();
    }
}
