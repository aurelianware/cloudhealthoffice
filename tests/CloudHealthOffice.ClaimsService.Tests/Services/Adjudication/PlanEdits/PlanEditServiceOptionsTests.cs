using ClaimsService.Services.Adjudication.PlanEdits;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.PlanEdits;

/// <summary>A misconfigured plan edit service stops the service at startup, never at claim time.</summary>
public class PlanEditServiceOptionsTests
{
    private const string Secret = "plan-edit-secret-with-at-least-32-characters";

    private static readonly ChoOutboundHosts ChoHosts = new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Services:MemberServiceUrl"] = "http://member-service:8080" }).Build());

    private static IReadOnlyList<string> Validate(PlanEditTenantOptions tenant, bool allowHttp = false) =>
        new PlanEditServiceOptions { Tenants = { ["tenant-1"] = tenant } }.Validate(ChoHosts, allowHttp);

    private static PlanEditTenantOptions Valid() => new() { Endpoint = "https://edits.plan.example/v1", SigningSecret = Secret };

    [Fact]
    public void A_complete_https_configuration_is_valid()
    {
        Assert.Empty(Validate(Valid()));
        Assert.Empty(new PlanEditServiceOptions().Validate(ChoHosts, allowInsecureHttp: false));
    }

    [Theory]
    [InlineData(null, "an absolute URL is required")]
    [InlineData("/relative", "an absolute URL is required")]
    [InlineData("http://edits.plan.example/v1", "must be https")]
    [InlineData("ftp://edits.plan.example/v1", "must be https")]
    [InlineData("https://user:pw@edits.plan.example/v1", "must not carry credentials")]
    [InlineData("https://edits.plan.example/v1#x", "must not have a fragment")]
    public void Bad_endpoints_are_refused(string? endpoint, string problem)
    {
        var problems = Validate(new PlanEditTenantOptions { Endpoint = endpoint, SigningSecret = Secret });

        Assert.Contains(problems, p => p.Contains(problem));
    }

    [Fact]
    public void Plain_http_is_allowed_only_where_insecure_http_is()
    {
        var tenant = new PlanEditTenantOptions { Endpoint = "http://localhost:5099/edits", SigningSecret = Secret };

        Assert.Empty(Validate(tenant, allowHttp: true));
        Assert.NotEmpty(Validate(tenant, allowHttp: false));
    }

    [Theory]
    [InlineData("https://member-service:8443/edits")]
    [InlineData("https://plan-edits.cloudhealthoffice.svc.cluster.local/v1")]
    public void An_endpoint_on_a_CHO_host_is_refused_because_it_would_receive_CHO_tokens(string endpoint)
    {
        var problems = Validate(new PlanEditTenantOptions { Endpoint = endpoint, SigningSecret = Secret });

        Assert.Contains(problems, p => p.Contains("is a CHO service host"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    public void A_missing_or_short_secret_is_refused(string? secret)
    {
        var problems = Validate(new PlanEditTenantOptions { Endpoint = "https://edits.plan.example/v1", SigningSecret = secret });

        Assert.Contains(problems, p => p.Contains("SigningSecret"));
    }

    [Theory]
    [InlineData(199)]
    [InlineData(15001)]
    public void A_timeout_out_of_range_is_refused(int timeout)
    {
        var tenant = Valid();
        tenant.TimeoutMilliseconds = timeout;

        Assert.Contains(Validate(tenant), p => p.Contains("TimeoutMilliseconds"));
    }

    [Fact]
    public void Undefined_modes_are_refused()
    {
        var tenant = Valid();
        tenant.Mode = (PlanEditEnforcementMode)7;
        tenant.WhenUnavailable = (PlanEditUnavailableMode)7;

        var problems = Validate(tenant);

        Assert.Contains(problems, p => p.Contains(":Mode:"));
        Assert.Contains(problems, p => p.Contains(":WhenUnavailable:"));
    }

    [Fact]
    public void Registration_refuses_to_start_on_a_bad_configuration_and_never_echoes_the_secret()
    {
        var config = PlanEditServiceClientTests.Configuration(endpoint: "http://edits.plan.example/v1", secret: "short-secret-value");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddPlanEditService(config, new PlanEditServiceClientTests.Env("Production")));

        Assert.Contains("must be https", ex.Message);
        Assert.Contains("SigningSecret", ex.Message);
        Assert.DoesNotContain("short-secret-value", ex.Message);
    }

    [Fact]
    public void Registration_with_no_plan_edit_services_registers_the_client_and_starts()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();

        services.AddPlanEditService(new ConfigurationBuilder().Build(), new PlanEditServiceClientTests.Env("Production"));

        var client = services.BuildServiceProvider().GetRequiredService<IPlanEditServiceClient>();
        Assert.Null(client.For("tenant-1"));
    }
}
