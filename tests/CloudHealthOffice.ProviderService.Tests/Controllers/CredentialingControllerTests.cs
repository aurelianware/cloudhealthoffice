using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Controllers;

/// <summary>
/// Endpoint-shape coverage for <see cref="CredentialingController"/>: each
/// route surfaces the right status code on happy path, validation
/// failure, and publisher exhaustion.
/// </summary>
public class CredentialingControllerTests
{
    private const string TenantId = "tenant-a";
    private const string ProviderId = "p-001";

    private readonly InMemoryCredentialingEventRepository _eventRepository = new();
    private readonly FakeCredentialingEventPublisher _publisher;
    private readonly InMemoryProviderRepository _providerRepository;
    private readonly CredentialingService _service;
    private readonly CredentialingController _controller;

    public CredentialingControllerTests()
    {
        _publisher = new FakeCredentialingEventPublisher(_eventRepository);
        _providerRepository = new InMemoryProviderRepository { TenantId = TenantId };
        SeedActiveProvider();
        _service = new CredentialingService(
            _eventRepository, _publisher, _providerRepository,
            new CredentialingProjector(),
            NullLogger<CredentialingService>.Instance);
        _controller = new CredentialingController(_service, NullLogger<CredentialingController>.Instance);

        // As UseChoAuthentication() leaves it: tenant and subject from the token.
        var ctx = TestHelpers.TokenHttpContext.For(TenantId);
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };
    }

    [Fact]
    public async Task SubmitApplication_returns_201_on_happy_path()
    {
        var result = await _controller.SubmitApplication(
            ProviderId,
            new SubmitApplicationRequest { ApplicationSource = "Manual" },
            CancellationToken.None);
        var created = result.Result as CreatedResult;
        created.Should().NotBeNull();
        created!.Value.Should().BeOfType<CredentialingEvent>();
    }

    [Fact]
    public async Task SubmitApplication_records_the_token_subject_not_an_X_User_Id_header()
    {
        _controller.HttpContext.Request.Headers["X-User-Id"] = "forged-user";

        var result = await _controller.SubmitApplication(
            ProviderId,
            new SubmitApplicationRequest { ApplicationSource = "Manual" },
            CancellationToken.None);

        var evt = (CredentialingEvent)((CreatedResult)result.Result!).Value!;
        evt.ActorId.Should().Be(TestHelpers.TokenHttpContext.DefaultUserId);
    }

    [Fact]
    public async Task Write_without_a_token_subject_is_refused_even_with_an_X_User_Id_header()
    {
        // Without a token there is no actor; a header never stands in for one.
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = TenantId;
        ctx.Request.Headers["X-User-Id"] = "forged-user";
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        var act = () => _controller.SubmitApplication(
            ProviderId,
            new SubmitApplicationRequest { ApplicationSource = "Manual" },
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _eventRepository.Store.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitApplication_returns_400_when_application_already_open()
    {
        await _controller.SubmitApplication(
            ProviderId,
            new SubmitApplicationRequest { ApplicationSource = "Manual" },
            CancellationToken.None);

        var second = await _controller.SubmitApplication(
            ProviderId,
            new SubmitApplicationRequest
            {
                ApplicationSource = "Manual",
                SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            },
            CancellationToken.None);
        second.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetStatus_returns_unknown_for_provider_with_no_chain()
    {
        var result = await _controller.GetStatus("never-credentialed", CancellationToken.None);
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var projection = ok!.Value as CredentialingProjectionResult;
        projection.Should().NotBeNull();
        projection!.Status.Should().Be(CredentialingStatus.Unknown);
    }

    [Fact]
    public async Task GetHistory_returns_paged_descending_with_continuation()
    {
        await _controller.SubmitApplication(
            ProviderId,
            new SubmitApplicationRequest
            {
                ApplicationSource = "Manual",
                SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            },
            CancellationToken.None);
        await _controller.RecordPrimarySourceVerification(
            ProviderId,
            new RecordPrimarySourceVerificationRequest
            {
                VerificationVendor = "CAQH",
                VerifiedItems = new[] { "License" },
                VerifiedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            },
            CancellationToken.None);

        var result = await _controller.GetHistory(ProviderId, cursor: null, limit: 1, CancellationToken.None);
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var page = ok!.Value as CredentialingHistoryPage;
        page.Should().NotBeNull();
        page!.Items.Should().HaveCount(1);
        page.ContinuationToken.Should().NotBeNull();
    }

    [Fact]
    public async Task RecordDecision_returns_201_with_DelegatedAuthority_synthesizing_application()
    {
        var result = await _controller.RecordDecision(
            ProviderId,
            new RecordDecisionRequest
            {
                Decision = CredentialingDecision.Approved,
                DecisionAuthorityType = DecisionAuthorityType.DelegatedAuthority,
                DecisionAuthorityId = "delegated-actor",
                CredentialingDate = DateTime.UtcNow,
                RecredentialingDueDate = DateTime.UtcNow.AddYears(2),
            },
            CancellationToken.None);
        result.Result.Should().BeOfType<CreatedResult>();
    }

    [Fact]
    public async Task TriggerRecredentialing_returns_400_without_prior_approval()
    {
        var result = await _controller.TriggerRecredentialing(
            ProviderId,
            new TriggerRecredentialingRequest { Reason = "DueDateElapsed" },
            CancellationToken.None);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
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
