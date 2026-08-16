using BenefitPlanService.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Tests.Middleware;

public sealed class EstimateApiSecurityMiddlewareTests
{
    [Fact]
    public async Task EstimateOnly_RejectsMissingKey()
    {
        var context = Context("/api/v1/adjudication/estimate");
        var called = false;
        var middleware = new EstimateApiSecurityMiddleware(_ => { called = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context, Options(true, true, "expected-secret"));

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task EstimateOnly_AllowsValidKey()
    {
        var context = Context("/api/v1/adjudication/estimate");
        context.Request.Headers["X-Api-Key"] = "expected-secret";
        var called = false;
        var middleware = new EstimateApiSecurityMiddleware(_ => { called = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context, Options(true, true, "expected-secret"));

        Assert.True(called);
    }

    [Fact]
    public async Task EstimateOnly_HidesOtherApplicationEndpoints()
    {
        var context = Context("/api/v1/benefitplans");
        context.Request.Headers["X-Api-Key"] = "expected-secret";
        var called = false;
        var middleware = new EstimateApiSecurityMiddleware(_ => { called = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context, Options(true, true, "expected-secret"));

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task HealthEndpoint_DoesNotRequireKey()
    {
        var context = Context("/health/live");
        var called = false;
        var middleware = new EstimateApiSecurityMiddleware(_ => { called = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context, Options(true, true, "expected-secret"));

        Assert.True(called);
    }

    [Fact]
    public void EstimateOnlyMode_RequiresEnabledFlag()
    {
        var options = new EstimateApiOptions
        {
            Enabled = false,
            EstimateOnly = true
        };

        Assert.False(options.IsEstimateOnlyEnabled);
    }

    private static DefaultHttpContext Context(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return context;
    }

    private static IOptions<EstimateApiOptions> Options(bool enabled, bool estimateOnly, string apiKey) =>
        Microsoft.Extensions.Options.Options.Create(new EstimateApiOptions
        {
            Enabled = enabled,
            EstimateOnly = estimateOnly,
            ApiKey = apiKey
        });
}
