using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.TokenService.Directory;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CloudHealthOffice.TokenService.Tests;

/// <summary>
/// POST /v1/invitations/redeem: the Entra token is validated as for the
/// exchange, tenant-service decides the redemption, and the user then gets a
/// CHO token for the invited tenant through the normal exchange rules.
/// </summary>
public sealed class InvitationRedemptionTests : IDisposable
{
    private const string GuestOid = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string GuestEmail = "pat.guest@partner.example";
    private const string Code = "Zk3c9QmT2xY7bL0pN4vR8sW1aE6dH5jU3fG2kM9nB0q";

    private readonly TokenServiceFactory _factory;
    private FakeTenantService Directory => _factory.TenantService;

    public InvitationRedemptionTests() : this(new TokenServiceFactory())
    {
    }

    private InvitationRedemptionTests(TokenServiceFactory factory)
    {
        _factory = factory;
        Directory.AddTenant("acme", Ids.AcmeDirectory);
    }

    public void Dispose() => _factory.Dispose();

    private static string GuestToken(string oid = GuestOid, string? scp = "Cho.Token", string? idtyp = null)
        => TestEntra.Token(Ids.GuestDirectory, oid, scp: scp, idtyp: idtyp, username: GuestEmail);

    private static Task<HttpResponseMessage> RedeemAsync(HttpClient client, string code = Code)
        => client.PostAsJsonAsync("/v1/invitations/redeem", new { code });

    /// <summary>tenant-service redeems: links the invited (Invited) user and activates it.</summary>
    private DirectoryUser InvitedUserThatRedeems()
    {
        var invited = Directory.AddUser("acme", GuestEmail, status: "Invited", roles: "ClaimsExaminer");
        Directory.Redeem = body =>
        {
            invited.AzureAdObjectId = body["oid"]!.GetValue<string>();
            invited.AzureAdTenantId = body["tid"]!.GetValue<string>();
            invited.Status = "Active";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { tenantId = "acme", userId = invited.Id }),
            };
        };
        return invited;
    }

    private static HttpResponseMessage Refusal(HttpStatusCode status, string error, string? invitedEmail = null)
        => new(status)
        {
            Content = invitedEmail == null
                ? JsonContent.Create(new { error })
                : JsonContent.Create(new { error, invitedEmail }),
        };

    [Fact]
    public async Task Valid_redemption_returns_a_cho_token_for_the_invited_tenant()
    {
        var invited = InvitedUserThatRedeems();

        var response = await RedeemAsync(_factory.ClientWith(GuestToken()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.JsonAsync();
        body.GetProperty("tenant_id").GetString().Should().Be("acme");
        body.GetProperty("token_type").GetString().Should().Be("Bearer");
        body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("ClaimsExaminer");
        body.GetProperty("user").GetProperty("id").GetString().Should().Be(invited.Id);
        var jwt = new JsonWebToken(body.GetProperty("access_token").GetString());
        jwt.Subject.Should().Be(invited.Id);
        jwt.GetClaim("tenant_id").Value.Should().Be("acme");

        // tenant-service got the code and the identity from the validated token, over a service token.
        var call = Directory.Redemptions.Should().ContainSingle().Subject;
        call["code"]!.GetValue<string>().Should().Be(Code);
        call["tid"]!.GetValue<string>().Should().Be(Ids.GuestDirectory);
        call["oid"]!.GetValue<string>().Should().Be(GuestOid);
        call["email"]!.GetValue<string>().Should().Be(GuestEmail);
        Directory.AuthorizationHeaders.Should().AllSatisfy(h => new JsonWebToken(h![7..]).Subject.Should().Be("token-service"));
    }

    public static TheoryData<string, string> RejectedTokens() => new()
    {
        { "app-only (idtyp=app)", TestEntra.Token(Ids.GuestDirectory, GuestOid, idtyp: "app", username: GuestEmail) },
        { "app-only (no scp)", TestEntra.Token(Ids.GuestDirectory, GuestOid, scp: null, username: GuestEmail) },
        { "wrong scope", TestEntra.Token(Ids.GuestDirectory, GuestOid, scp: "User.Read", username: GuestEmail) },
        { "wrong audience", TestEntra.Token(Ids.GuestDirectory, GuestOid, aud: "api://other", username: GuestEmail) },
        { "issuer for another directory", TestEntra.Token(Ids.GuestDirectory, GuestOid, username: GuestEmail,
            issuer: $"https://login.microsoftonline.com/{Ids.AcmeDirectory}/v2.0") },
    };

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task Unacceptable_entra_tokens_are_401_and_nothing_is_redeemed(string why, string token)
    {
        InvitedUserThatRedeems();

        var response = await RedeemAsync(_factory.ClientWith(token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, why);
        (await response.JsonAsync()).GetProperty("error").GetString().Should().Be("invalid_token");
        Directory.Redemptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_token_is_401()
    {
        (await RedeemAsync(_factory.CreateClient())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Directory.Redemptions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not_found", HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Gone, "expired", HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Gone, "revoked", HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict, "already_redeemed", HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Conflict, "identity_in_use", HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Forbidden, "email_mismatch", HttpStatusCode.Forbidden)]
    public async Task Refusals_map_to_400_403_409_with_the_error_code(HttpStatusCode upstream, string error, HttpStatusCode expected)
    {
        Directory.Redeem = _ => Refusal(upstream, error, error == "email_mismatch" ? "p***@partner.example" : null);

        var response = await RedeemAsync(_factory.ClientWith(GuestToken()));

        response.StatusCode.Should().Be(expected);
        var body = await response.JsonAsync();
        body.GetProperty("error").GetString().Should().Be(error);
        if (error == "email_mismatch")
            body.GetProperty("invitedEmail").GetString().Should().Be("p***@partner.example");
        else
            body.TryGetProperty("invitedEmail", out _).Should().BeFalse();
        body.TryGetProperty("access_token", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"code":""}""")]
    [InlineData("""not json""")]
    public async Task Missing_or_malformed_body_is_400_invalid_request(string json)
    {
        var client = _factory.ClientWith(GuestToken());
        var response = await client.PostAsync("/v1/invitations/redeem",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.JsonAsync()).GetProperty("error").GetString().Should().Be("invalid_request");
        Directory.Redemptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Tenant_service_outage_is_503()
    {
        Directory.Redeem = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var response = await RedeemAsync(_factory.ClientWith(GuestToken()));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Tenant_service_refusing_the_caller_is_an_outage_not_an_answer()
    {
        Directory.Redeem = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { error = "forbidden" }) };

        (await RedeemAsync(_factory.ClientWith(GuestToken()))).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Redeemed_but_tenant_inactive_is_403_no_access()
    {
        Directory.Tenants.Single().IsActive = false;
        InvitedUserThatRedeems();

        var response = await RedeemAsync(_factory.ClientWith(GuestToken()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.JsonAsync()).GetProperty("error").GetString().Should().Be("no_access");
    }

    [Fact]
    public async Task Attempts_are_rate_limited_per_entra_identity()
    {
        var client = _factory.ClientWith(GuestToken());
        for (var i = 0; i < 10; i++)
            (await RedeemAsync(client)).StatusCode.Should().Be(HttpStatusCode.BadRequest, $"attempt {i + 1} is within the limit");

        var limited = await RedeemAsync(client);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await limited.JsonAsync()).GetProperty("error").GetString().Should().Be("rate_limited");
        Directory.Redemptions.Should().HaveCount(10, "a limited attempt never reaches tenant-service");

        // Another identity has its own allowance.
        (await RedeemAsync(_factory.ClientWith(GuestToken(oid: "cccccccc-0000-0000-0000-000000000003"))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        _factory.Logs.Entries.Should().Contain(e => e.Category == "CloudHealthOffice.TokenService.Audit"
            && e.Message.Contains("rate_limited") && e.Message.Contains(GuestOid));
    }

    [Fact]
    public async Task Every_attempt_is_audited_without_the_code_or_any_email_address()
    {
        InvitedUserThatRedeems();
        var client = _factory.ClientWith(GuestToken());

        (await RedeemAsync(client, "WrongCodeWrongCodeWrongCodeWrongCodeWrong12")).StatusCode.Should().Be(HttpStatusCode.OK);
        Directory.Redeem = _ => Refusal(HttpStatusCode.Forbidden, "email_mismatch", "p***@partner.example");
        await RedeemAsync(client);
        Directory.Redeem = _ => Refusal(HttpStatusCode.NotFound, "not_found");
        await RedeemAsync(client);

        var audit = _factory.Logs.Entries.Where(e => e.Category == "CloudHealthOffice.TokenService.Audit"
                                                     && e.Message.Contains("CHO invitation")).ToList();
        audit.Should().HaveCount(3);
        audit.Should().Contain(e => e.Message.Contains("redeemed") && e.Message.Contains("tenant=acme"));
        audit.Should().Contain(e => e.Message.Contains("reason=email_mismatch"));
        audit.Should().Contain(e => e.Message.Contains("reason=not_found"));

        _factory.Logs.Entries.Should().NotContain(e =>
            e.Message.Contains(Code) || e.Message.Contains("WrongCodeWrongCode") || e.Message.Contains("@partner.example"));
    }

    // ── Invited users get nothing without redeeming ─────────────────────

    [Fact]
    public async Task An_invited_user_is_never_linked_by_email_nor_issued_a_token()
    {
        // Same directory as the tenant and the right address: a normal first
        // sign-in would link an Active user, but not an Invited one.
        var invited = Directory.AddUser("acme", "pat@acme.example", status: "Invited", roles: "TenantAdmin");
        var token = TestEntra.Token(Ids.AcmeDirectory, GuestOid, username: "pat@acme.example");

        var exchange = await _factory.ClientWith(token).ExchangeAsync("acme");
        var tenants = await _factory.ClientWith(token).GetAsync("/v1/token/tenants");

        exchange.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Directory.Links.Should().BeEmpty();
        invited.AzureAdObjectId.Should().BeEmpty();
        (await tenants.JsonAsync()).GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("Invited")]
    [InlineData("Disabled")]
    public async Task A_linked_user_that_is_not_active_gets_no_token(string status)
    {
        Directory.AddUser("acme", GuestEmail, GuestOid, Ids.GuestDirectory, status, "ClaimsExaminer");

        (await _factory.ClientWith(GuestToken()).ExchangeAsync("acme")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_disabled_unlinked_user_is_not_linked_by_email()
    {
        Directory.AddUser("acme", "pat@acme.example", status: "Disabled", roles: "ClaimsExaminer");
        var token = TestEntra.Token(Ids.AcmeDirectory, GuestOid, username: "pat@acme.example");

        (await _factory.ClientWith(token).ExchangeAsync("acme")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Directory.Links.Should().BeEmpty();
    }
}
