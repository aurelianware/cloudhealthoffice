using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CloudHealthOffice.TokenService.Tests;

/// <summary>
/// POST /v1/signup: the portal's anonymous signup, authenticated by the user's
/// Entra token. tenant-service gets the token's directory, object id and
/// username, never values from the body; the body chooses only organization,
/// tier and Stripe ids.
/// </summary>
public sealed class SignupTests : IDisposable
{
    private const string Oid = "cccccccc-0000-0000-0000-000000000003";
    private readonly TokenServiceFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Signed(string? username = "owner@newplan.example", string? scp = "Cho.Token")
        => _factory.ClientWith(TestEntra.Token(Ids.GuestDirectory, Oid, scp: scp, username: username));

    private static object Body() => new
    {
        organizationName = "New Plan",
        tier = "starter",
        stripeCustomerId = "cus_ABC12345",
        stripeSubscriptionId = "sub_ABC12345",
        // Not part of the form: never forwarded.
        azureTenantId = Ids.AcmeDirectory,
        tid = Ids.AcmeDirectory,
        email = "attacker@evil.example",
        subscriptionStatus = "Active",
        isDemo = true,
    };

    [Fact]
    public async Task Signup_ForwardsTheTokensIdentity_NotTheBodys()
    {
        var response = await Signed().PostAsJsonAsync("/v1/signup", Body());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["tenantId"]!.GetValue<string>().Should().Be("tenant-new");
        var sent = _factory.TenantService.Signups.Should().ContainSingle().Subject.AsObject();
        sent["tid"]!.GetValue<string>().Should().Be(Ids.GuestDirectory);
        sent["oid"]!.GetValue<string>().Should().Be(Oid);
        sent["email"]!.GetValue<string>().Should().Be("owner@newplan.example");
        sent["organizationName"]!.GetValue<string>().Should().Be("New Plan");
        sent.ContainsKey("subscriptionStatus").Should().BeFalse();
        sent.ContainsKey("isDemo").Should().BeFalse();
        sent.ContainsKey("azureTenantId").Should().BeFalse();
        // token-service's own service token, not the user's Entra token.
        _factory.TenantService.AuthorizationHeaders.Last().Should().StartWith("Bearer ");
    }

    [Fact]
    public async Task Signup_WithoutAnEntraToken_Is401()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/v1/signup", Body());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.TenantService.Signups.Should().BeEmpty();
    }

    [Fact]
    public async Task Signup_DirectoryAlreadySubscribed_Is409()
    {
        _factory.TenantService.Signup = _ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent.Create(new { error = "already_subscribed" }),
        };

        var response = await Signed().PostAsJsonAsync("/v1/signup", Body());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Signup_TenantServiceDown_Is503()
    {
        _factory.TenantService.Down = true;

        var response = await Signed().PostAsJsonAsync("/v1/signup", Body());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Signup_WithoutAUsername_Is400()
    {
        var response = await Signed(username: null).PostAsJsonAsync("/v1/signup", Body());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.TenantService.Signups.Should().BeEmpty();
    }
}
