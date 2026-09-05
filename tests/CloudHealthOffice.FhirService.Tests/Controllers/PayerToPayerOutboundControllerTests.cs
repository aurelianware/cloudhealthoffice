using FhirService.Controllers;
using FhirService.Models.PayerToPayer;
using FhirService.Services.PayerToPayer.Outbound;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// How the outbound Payer-to-Payer surface reports each structured failure. The
/// mapping matters because the categories mean genuinely different things to a
/// caller: the peer refused, the peer is down, the payer is not configured, or —
/// the case this fixture exists for — CHO retrieved the data and then failed to
/// store it, which is neither a missing member nor a peer problem.
/// </summary>
public class PayerToPayerOutboundControllerTests
{
    private sealed class FixedOutboundService : IPayerToPayerOutboundService
    {
        private readonly PayerToPayerOutboundResult _result;
        public FixedOutboundService(PayerToPayerOutboundResult result) => _result = result;

        public Task<PayerToPayerOutboundResult> InitiateAsync(
            PayerToPayerOutboundRequest request, CancellationToken ct = default)
            => Task.FromResult(_result);
    }

    private static PayerToPayerOutboundController ControllerReturning(
        PayerToPayerOutboundStatus status, PayerToPayerOutboundFailure failure)
    {
        var controller = new PayerToPayerOutboundController(
            new FixedOutboundService(new PayerToPayerOutboundResult
            {
                Exchange = new PayerToPayerOutboundExchange
                {
                    ExchangeId = "exch-1",
                    TenantId = "test-tenant",
                    MemberId = "pat-001",
                    TargetPayerId = "PRIOR-PLAN",
                    Status = status,
                    Failure = failure,
                },
            }));

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = "test-tenant";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static PayerToPayerInitiateRequestDto Body() =>
        new() { MemberId = "pat-001", TargetPayerId = "PRIOR-PLAN" };

    [Fact]
    public async Task IngestionFailure_IsReportedAsAServerErrorNotAMissingMember()
    {
        // The exchange succeeded and the storage did not. Reporting 404 would tell
        // the caller the member does not exist, which is false and would send
        // anyone debugging it in the wrong direction.
        var controller = ControllerReturning(
            PayerToPayerOutboundStatus.Failed, PayerToPayerOutboundFailure.IngestionFailed);

        var result = await controller.Initiate(Body(), CancellationToken.None) as ObjectResult;

        result!.StatusCode.Should().Be(500);
        var outcome = result.Value.Should().BeOfType<OperationOutcome>().Subject;
        outcome.Issue.Should().ContainSingle().Which.Code.Should().Be(OperationOutcome.IssueType.Transient);
        outcome.Issue[0].Diagnostics.Should().Contain("retried");
        // No store, driver, or payload detail leaks to the caller.
        outcome.Issue[0].Diagnostics.Should().NotContainAny("Mongo", "exception", "Bundle");
    }

    [Theory]
    [InlineData(PayerToPayerOutboundFailure.TargetPayerNotConfigured, 422)]
    [InlineData(PayerToPayerOutboundFailure.LocalCoverageAmbiguous, 422)]
    [InlineData(PayerToPayerOutboundFailure.RemoteUnauthorized, 502)]
    [InlineData(PayerToPayerOutboundFailure.RemoteUnavailable, 502)]
    [InlineData(PayerToPayerOutboundFailure.InvalidRemoteResponse, 502)]
    // Cross-tenant and unknown member both collapse to 404 so the endpoint cannot
    // be used to probe which members exist where.
    [InlineData(PayerToPayerOutboundFailure.MemberNotFound, 404)]
    [InlineData(PayerToPayerOutboundFailure.TenantMismatch, 404)]
    public async Task EachFailureCategory_MapsToItsOwnStatus(
        PayerToPayerOutboundFailure failure, int expectedStatus)
    {
        var controller = ControllerReturning(PayerToPayerOutboundStatus.Failed, failure);

        var result = await controller.Initiate(Body(), CancellationToken.None) as ObjectResult;

        result!.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task NotAuthorized_IsForbidden()
    {
        var controller = ControllerReturning(
            PayerToPayerOutboundStatus.NotAuthorized, PayerToPayerOutboundFailure.NotAuthorized);

        var result = await controller.Initiate(Body(), CancellationToken.None) as ObjectResult;

        result!.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task TargetPayerIdIsRequired_AndNoUrlCanBeSupplied()
    {
        var controller = ControllerReturning(
            PayerToPayerOutboundStatus.Completed, PayerToPayerOutboundFailure.None);

        var result = await controller.Initiate(
            new PayerToPayerInitiateRequestDto { MemberId = "pat-001" }, CancellationToken.None) as ObjectResult;

        result!.StatusCode.Should().Be(400);
        // The DTO offers no way to name a location — targeting is by payer id only.
        typeof(PayerToPayerInitiateRequestDto).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "MemberId", "TargetPayerId", "TransitionKey", "AsOfDate" });
    }
}
