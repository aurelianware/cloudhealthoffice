using System.Net.Http;
using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Models.Messaging;
using ClaimsService.Services;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

/// <summary>
/// Capability 5.5 — verifies the dual-emit modification on
/// <see cref="ClaimSubmissionService"/>: after a successful submission,
/// the service emits a <see cref="ClaimVersionSubmittedMessage"/> onto
/// the <c>claim-version-events</c> Service Bus topic with the correct
/// MessageType property and a deterministic MessageId, AND survives
/// Service Bus emission failures without failing the submission
/// (degraded-mode parity with the existing Mongo emit).
/// </summary>
public class ClaimSubmissionServiceMessageBusEmissionTests
{
    private readonly IClaimAdapter _adapter = Substitute.For<IClaimAdapter>();
    private readonly IClaimVersionEventPublisher _publisher = Substitute.For<IClaimVersionEventPublisher>();
    private readonly IMessageBus _messageBus = Substitute.For<IMessageBus>();
    private readonly ClaimAdapterFactory _factory;
    private readonly ClaimSubmissionService _sut;

    public ClaimSubmissionServiceMessageBusEmissionTests()
    {
        _adapter.Platform.Returns("cho");

        var cache = new ClaimTenantConfigCache(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IConfiguration>(),
            NullLogger<ClaimTenantConfigCache>.Instance);

        _factory = new ClaimAdapterFactory(
            new[] { _adapter },
            cache,
            NullLogger<ClaimAdapterFactory>.Instance);

        _sut = new ClaimSubmissionService(
            _factory, _publisher, _messageBus, NullLogger<ClaimSubmissionService>.Instance);
    }

    [Fact]
    public async Task Submit_HappyPath_EmitsClaimVersionSubmittedMessage()
    {
        StubAdapterCreatesClaim("ver-7", versionNumber: 1);

        ClaimVersionSubmittedMessage? captured = null;
        SendOptions? capturedOptions = null;
        _messageBus
            .When(b => b.SendAsync(
                Arg.Any<string>(),
                Arg.Any<ClaimVersionSubmittedMessage>(),
                Arg.Any<SendOptions?>(),
                Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                captured = ci.Arg<ClaimVersionSubmittedMessage>();
                capturedOptions = ci.Arg<SendOptions?>();
            });

        var result = await _sut.SubmitAsync(BuildClaim(), "tenant-x", "actor-1", "corr-99");

        Assert.True(result.Success);
        await _messageBus.Received(1).SendAsync(
            "claim-version-events",
            Arg.Any<ClaimVersionSubmittedMessage>(),
            Arg.Any<SendOptions?>(),
            Arg.Any<CancellationToken>());

        Assert.NotNull(captured);
        Assert.Equal("tenant-x", captured!.TenantId);
        Assert.Equal("ver-7", captured.ClaimVersionId);
        Assert.Equal("actor-1", captured.ActorId);
        Assert.Equal("corr-99", captured.CorrelationId);

        Assert.NotNull(capturedOptions);
        Assert.Equal("submitted:ver-7", capturedOptions!.MessageId);
        Assert.NotNull(capturedOptions.Properties);
        Assert.Equal("ClaimVersionSubmitted", capturedOptions.Properties!["MessageType"]);
    }

    [Fact]
    public async Task Submit_MessageBusEmissionFails_StillReturnsSuccess()
    {
        StubAdapterCreatesClaim("ver-9", versionNumber: 1);
        _messageBus
            .SendAsync(
                Arg.Any<string>(),
                Arg.Any<ClaimVersionSubmittedMessage>(),
                Arg.Any<SendOptions?>(),
                Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Service Bus down"));

        var result = await _sut.SubmitAsync(BuildClaim(), "tenant-x", "actor-1", null);

        Assert.True(result.Success);
        Assert.Equal("ver-9", result.Claim!.ClaimVersionId);
    }

    [Fact]
    public async Task Submit_ValidationFailure_DoesNotEmit()
    {
        var claim = BuildClaim();
        claim.MemberId = string.Empty;

        var result = await _sut.SubmitAsync(claim, "tenant-x", "actor", null);

        Assert.False(result.Success);
        await _messageBus.DidNotReceiveWithAnyArgs().SendAsync(
            default!, default(ClaimVersionSubmittedMessage)!, default, default);
    }

    [Fact]
    public async Task Submit_AdapterNotImplemented_DoesNotEmit()
    {
        _adapter
            .SubmitClaimAsync(Arg.Any<ClaimSubmissionAdapterRequest>(), Arg.Any<CancellationToken>())
            .Throws(new NotImplementedException("vendor adapter not wired"));

        var result = await _sut.SubmitAsync(BuildClaim(), "tenant-x", "actor", null);

        Assert.False(result.Success);
        await _messageBus.DidNotReceiveWithAnyArgs().SendAsync(
            default!, default(ClaimVersionSubmittedMessage)!, default, default);
    }

    private void StubAdapterCreatesClaim(string claimVersionId, int versionNumber)
    {
        _adapter
            .SubmitClaimAsync(Arg.Any<ClaimSubmissionAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = ci.Arg<ClaimSubmissionAdapterRequest>();
                req.Claim.Id = claimVersionId;
                req.Claim.ClaimVersionId = claimVersionId;
                req.Claim.VersionNumber = versionNumber;
                req.Claim.VersionState = ClaimVersionState.Submitted;
                return new ClaimAdapterResponse { Platform = "cho", Claim = req.Claim };
            });
    }

    private static AdapterClaim BuildClaim()
    {
        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        return new AdapterClaim
        {
            ClaimNumber = "CLM-001",
            MemberId = "MEM-1",
            BillingProviderNPI = "1234567890",
            LineOfBusiness = LineOfBusiness.Commercial,
            ClaimType = ClaimType.Professional,
            PlaceOfServiceCode = "11",
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            ClaimLines = new List<AdapterClaimLine>
            {
                new()
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ChargeAmount = 150m,
                    Units = 1,
                    ServiceDateFrom = serviceDate,
                    ServiceDateTo = serviceDate,
                }
            }
        };
    }
}
