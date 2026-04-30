using System.Net.Http;
using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Services;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

/// <summary>
/// Coverage for the canonical orchestrator introduced in capability 5.3.
/// Validates: structural validation rules, total-charge computation,
/// adapter routing via factory, ClaimVersionSubmitted event emission,
/// degraded-mode behavior when emission fails, and 501-shaped result
/// when the resolved adapter throws NotImplementedException.
/// </summary>
public class ClaimSubmissionServiceTests
{
    private readonly IClaimAdapter _adapter = Substitute.For<IClaimAdapter>();
    private readonly IClaimVersionEventPublisher _publisher = Substitute.For<IClaimVersionEventPublisher>();
    private readonly IMessageBus _messageBus = Substitute.For<IMessageBus>();
    private readonly ClaimAdapterFactory _factory;
    private readonly ClaimSubmissionService _sut;

    public ClaimSubmissionServiceTests()
    {
        _adapter.Platform.Returns("cho");

        // Real cache with mocked dependencies — the IHttpClientFactory mock
        // returns null on CreateClient, which trips the cache's catch-all
        // fallback to the "cho" default platform. Deterministic without
        // making an HTTP call.
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
    public async Task Submit_HappyPath_CallsAdapter_EmitsEvent_ReturnsCreatedClaim()
    {
        var inbound = BuildClaim();
        AdapterClaim? capturedRequest = null;

        _adapter
            .SubmitClaimAsync(Arg.Any<ClaimSubmissionAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = ci.Arg<ClaimSubmissionAdapterRequest>();
                capturedRequest = req.Claim;
                req.Claim.Id = "new-claim-id";
                req.Claim.ClaimVersionId = "new-claim-id";
                req.Claim.VersionNumber = 1;
                req.Claim.VersionState = ClaimVersionState.Submitted;
                return new ClaimAdapterResponse
                {
                    Platform = "cho",
                    Claim = req.Claim,
                };
            });

        var result = await _sut.SubmitAsync(inbound, "tenant-1", "actor-9", "corr-1");

        Assert.True(result.Success);
        Assert.NotNull(result.Claim);
        Assert.Equal("new-claim-id", result.Claim!.Id);
        Assert.Equal("tenant-1", capturedRequest!.TenantId);

        await _publisher.Received(1).PublishVersionSubmittedAsync(
            Arg.Is<Claim>(c => c.Id == "new-claim-id" && c.ClaimVersionId == "new-claim-id"),
            "actor-9",
            "corr-1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_ComputesTotalChargeFromLines_BeforeAdapterCall()
    {
        var inbound = BuildClaim();
        inbound.TotalChargeAmount = 1m; // bogus caller-supplied value
        inbound.ClaimLines = new List<AdapterClaimLine>
        {
            new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 150m, Units = 2,
                    ServiceDateFrom = inbound.ServiceDateFrom, ServiceDateTo = inbound.ServiceDateTo },
            new() { LineNumber = 2, ProcedureCode = "85025", ChargeAmount = 35.50m, Units = 1,
                    ServiceDateFrom = inbound.ServiceDateFrom, ServiceDateTo = inbound.ServiceDateTo },
        };

        decimal? capturedTotal = null;
        _adapter
            .SubmitClaimAsync(Arg.Any<ClaimSubmissionAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedTotal = ci.Arg<ClaimSubmissionAdapterRequest>().Claim.TotalChargeAmount;
                return new ClaimAdapterResponse { Platform = "cho", Claim = ci.Arg<ClaimSubmissionAdapterRequest>().Claim };
            });

        var result = await _sut.SubmitAsync(inbound, "tenant-1", "actor", null);
        Assert.True(result.Success);
        Assert.Equal(150m * 2 + 35.50m, capturedTotal);
    }

    [Fact]
    public async Task Submit_MissingMemberId_ReturnsValidationFailure()
    {
        var inbound = BuildClaim();
        inbound.MemberId = string.Empty;

        var result = await _sut.SubmitAsync(inbound, "tenant-1", "actor", null);

        Assert.False(result.Success);
        Assert.Equal(ClaimSubmissionFailureKind.Validation, result.FailureKind);
        Assert.Contains(result.Errors, e => e.Field == "MemberId" && e.Code == "Required");
        await _adapter.DidNotReceiveWithAnyArgs().SubmitClaimAsync(default!, default);
        await _publisher.DidNotReceiveWithAnyArgs().PublishVersionSubmittedAsync(default!, default, default, default);
    }

    [Fact]
    public async Task Submit_MissingBillingProviderNPI_ReturnsValidationFailure()
    {
        var inbound = BuildClaim();
        inbound.BillingProviderNPI = string.Empty;

        var result = await _sut.SubmitAsync(inbound, "tenant-1", "actor", null);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Field == "BillingProviderNPI" && e.Code == "Required");
    }

    [Fact]
    public async Task Submit_ZeroLines_ReturnsValidationFailure()
    {
        var inbound = BuildClaim();
        inbound.ClaimLines.Clear();

        var result = await _sut.SubmitAsync(inbound, "tenant-1", "actor", null);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Field == "ClaimLines" && e.Code == "MinCount");
    }

    [Fact]
    public async Task Submit_LineWithoutProcedureCode_ReturnsValidationFailure()
    {
        var inbound = BuildClaim();
        inbound.ClaimLines[0].ProcedureCode = string.Empty;

        var result = await _sut.SubmitAsync(inbound, "tenant-1", "actor", null);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.Field == "ClaimLines[0].ProcedureCode" && e.Code == "Required");
    }

    [Fact]
    public async Task Submit_ServiceDateFromAfterServiceDateTo_ReturnsValidationFailure()
    {
        var inbound = BuildClaim();
        inbound.ServiceDateFrom = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc);
        inbound.ServiceDateTo = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await _sut.SubmitAsync(inbound, "tenant-1", "actor", null);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.Field == "ServiceDateFrom" && e.Code == "InvalidDateRange");
    }

    [Fact]
    public async Task Submit_AdapterThrowsNotImplemented_ReturnsAdapterNotImplemented()
    {
        _adapter
            .SubmitClaimAsync(Arg.Any<ClaimSubmissionAdapterRequest>(), Arg.Any<CancellationToken>())
            .Throws(new NotImplementedException("QNXT claim adapter not yet implemented."));

        var result = await _sut.SubmitAsync(BuildClaim(), "tenant-1", "actor", null);

        Assert.False(result.Success);
        Assert.Equal(ClaimSubmissionFailureKind.NotImplemented, result.FailureKind);
        Assert.Contains(result.Errors, e => e.Code == "AdapterNotImplemented");
        await _publisher.DidNotReceiveWithAnyArgs().PublishVersionSubmittedAsync(default!, default, default, default);
    }

    [Fact]
    public async Task Submit_EventEmissionFails_StillReturnsSuccess()
    {
        // Degraded-mode: claim row in main store is system of record;
        // event publisher failure must not fail the submission. Mirrors
        // the Kafka publisher's documented posture.
        _adapter
            .SubmitClaimAsync(Arg.Any<ClaimSubmissionAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = ci.Arg<ClaimSubmissionAdapterRequest>();
                req.Claim.Id = "claim-x";
                req.Claim.ClaimVersionId = "claim-x";
                req.Claim.VersionNumber = 1;
                return new ClaimAdapterResponse { Platform = "cho", Claim = req.Claim };
            });

        _publisher
            .PublishVersionSubmittedAsync(Arg.Any<Claim>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Mongo append-only stream unavailable"));

        var result = await _sut.SubmitAsync(BuildClaim(), "tenant-1", "actor", null);

        Assert.True(result.Success);
        Assert.NotNull(result.Claim);
        Assert.Equal("claim-x", result.Claim!.Id);
    }

    [Fact]
    public async Task Submit_ForcesTenantIdOntoClaim()
    {
        var inbound = BuildClaim();
        inbound.TenantId = "spoofed-tenant";

        AdapterClaim? captured = null;
        _adapter
            .SubmitClaimAsync(Arg.Any<ClaimSubmissionAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.Arg<ClaimSubmissionAdapterRequest>().Claim;
                return new ClaimAdapterResponse { Platform = "cho", Claim = ci.Arg<ClaimSubmissionAdapterRequest>().Claim };
            });

        await _sut.SubmitAsync(inbound, "real-tenant", "actor", null);

        Assert.NotNull(captured);
        Assert.Equal("real-tenant", captured!.TenantId);
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
