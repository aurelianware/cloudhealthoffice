using System.Net;
using System.Text;
using CloudHealthOffice.Infrastructure.Middleware;
using EligibilityService.Adapters;
using EligibilityService.Controllers;
using EligibilityService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CloudHealthOffice.EligibilityService.Tests.Security;

/// <summary>
/// A request without a tenant is an error, never an empty-string tenant, and
/// calls made without a caller name their tenant so a service token can be minted.
/// </summary>
public class TenantSourceTests
{
    private static ControllerContext NoTenantContext() => new() { HttpContext = new DefaultHttpContext() };

    [Fact]
    public async Task TemporalEligibility_WithoutTenant_DoesNotQueryEmptyTenant()
    {
        var temporal = Substitute.For<ITemporalEligibilityService>();
        var controller = new TemporalEligibilityController(
            temporal, Substitute.For<ILogger<TemporalEligibilityController>>())
        {
            ControllerContext = NoTenantContext()
        };

        await Assert.ThrowsAsync<TenantContextMissingException>(
            () => controller.Get("MBR-1", new DateTime(2026, 1, 15)));
        await temporal.DidNotReceiveWithAnyArgs().GetAsOfAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task BatchEligibility_WithoutTenant_DoesNotReadEmptyTenant()
    {
        var batch = Substitute.For<IBatchEligibilityService>();
        var controller = new BatchEligibilityController(
            batch, Substitute.For<ILogger<BatchEligibilityController>>())
        {
            ControllerContext = NoTenantContext()
        };

        await Assert.ThrowsAsync<TenantContextMissingException>(
            () => controller.Get("job-1", CancellationToken.None));
        await batch.DidNotReceiveWithAnyArgs().GetJobAsync(default!, default!, default);
    }

    [Fact]
    public async Task AdapterFactory_TenantServiceCall_NamesTheTenant()
    {
        var handler = new CapturingHandler();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("EligibilityDefault").Returns(new HttpClient(handler));
        var cho = Substitute.For<IEligibilityAdapter>();
        cho.Platform.Returns("cho");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:TenantService"] = "http://tenant-service.cloudhealthoffice/api/v1"
            })
            .Build();
        var factory = new EligibilityAdapterFactory(
            new[] { cho }, httpFactory, configuration, Substitute.For<ILogger<EligibilityAdapterFactory>>());

        await factory.GetAdapterAsync("tenant-worker");

        var request = Assert.Single(handler.Requests);
        Assert.True(request.Headers.TryGetValues("X-Tenant-ID", out var values));
        Assert.Equal("tenant-worker", Assert.Single(values));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
