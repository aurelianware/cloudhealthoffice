using System.Net;
using ClaimsService.Services;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.ClaimsService.Tests.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Resolution;

public sealed class HttpAuthorizationValidationClientTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("MEM-1", null)]
    [InlineData("OTHER", true)]
    [InlineData("mem-1", true)]
    public async Task UnconfirmedMemberComparison_IsUnavailable(string? echoedMember, bool? matches)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            authorizationNumber = "AUTH-1", isValid = true, validatedMemberId = echoedMember, memberMatches = matches
        });
        var sut = CreateClient(FakeHttpMessageHandler.Json(body));
        var result = await sut.ValidateAsync("tenant-1", "AUTH-1", "72148", DateTime.UtcNow, null, "MEM-1");
        Assert.Equal(AuthorizationLookupStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task ConfirmedWrongMember_IsInvalidEvenIfBodyClaimsValid()
    {
        var sut = CreateClient(FakeHttpMessageHandler.Json(
            """{"authorizationNumber":"AUTH-1","isValid":true,"validatedMemberId":"CHILD-1","memberMatches":false}"""));
        var result = await sut.ValidateAsync("tenant-1", "AUTH-1", "72148", DateTime.UtcNow, null, "CHILD-1");
        Assert.Equal(AuthorizationLookupStatus.Invalid, result.Status);
        Assert.DoesNotContain("CHILD-1", result.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task MissingMember_IsUnavailableWithoutHttpRequest(string? member)
    {
        var sent = false;
        var sut = CreateClient(new FakeHttpMessageHandler(_ => { sent = true; throw new InvalidOperationException(); }));
        var result = await sut.ValidateAsync("tenant-1", "AUTH-1", "72148", DateTime.UtcNow, null, member!);
        Assert.Equal(AuthorizationLookupStatus.Unavailable, result.Status);
        Assert.False(sent);
    }

    [Fact]
    public async Task MemberQuery_IsTrimmedAndEscapedWithoutLosingIdentifierCharacters()
    {
        string? query = null;
        var sut = CreateClient(new FakeHttpMessageHandler(req =>
        {
            query = req.RequestUri!.Query;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));
        await sut.ValidateAsync("tenant-1", "AUTH-1", "72148", DateTime.UtcNow, null, "  CHILD+1/&  ");
        Assert.Contains("memberId=CHILD%2B1%2F%26", query);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AmbiguousServiceLine_IsUnavailableEvenIfBodyClaimsValid(bool isValid)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            authorizationNumber = "AUTH-1", isValid, validationCode = "AmbiguousServiceLine",
            validatedMemberId = "MEM-1", memberMatches = true,
            validationMessage = "Select the relevant service line"
        });
        var sut = CreateClient(FakeHttpMessageHandler.Json(body));
        var result = await sut.ValidateAsync("tenant-1", "AUTH-1", "72148", DateTime.UtcNow, "1234567890", memberId: "MEM-1");
        Assert.Equal(AuthorizationLookupStatus.Unavailable, result.Status);
    }

    [Theory]
    [InlineData(0, "72148")]
    [InlineData(-1, "72148")]
    [InlineData(7, null)]
    public async Task InvalidExplicitLine_PendsWithoutCallingProducer(int lineNumber, string? code)
    {
        var sut = CreateClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("must not send")));
        var result = await sut.ValidateAsync("tenant-1", "AUTH-1", code, DateTime.UtcNow, "1234567890", "MEM-1", authorizationLineNumber: lineNumber);
        Assert.Equal(AuthorizationLookupStatus.Unavailable, result.Status);
        Assert.Contains("positive number and procedure", result.Reason);
    }

    [Fact]
    public async Task ValidateAsync_SendsTenantProcedureAndServiceDate()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "authorizationNumber": "AUTH-EXPIRED",
                      "validatedMemberId": "MEM-1", "memberMatches": true,
                      "isValid": false,
                      "status": "Approved",
                      "approvedServiceDateFrom": "2026-01-01T00:00:00Z",
                      "approvedServiceDateTo": "2026-01-31T00:00:00Z",
                      "expirationDate": "2026-01-31T00:00:00Z",
                      "approvedUnits": 1,
                      "validationMessage": "Authorization expired or not yet active"
                    }
                    """,
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        });
        var sut = CreateClient(handler);

        var result = await sut.ValidateAsync(
            "tenant-1",
            "AUTH-EXPIRED",
            "99213",
            new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
            "1234567890", memberId: "MEM-1");

        Assert.Equal(AuthorizationLookupStatus.Invalid, result.Status);
        Assert.Equal("AUTH-EXPIRED", result.Validation!.AuthorizationNumber);
        Assert.Equal("Authorization expired or not yet active", result.Reason);
        Assert.NotNull(captured);
        Assert.Equal(UpstreamClientNames.AuthorizationService, handler.LastClientName);
        Assert.Equal("tenant-1", captured!.Headers.GetValues("X-Tenant-ID").Single());
        Assert.Contains("/api/authorizations/AUTH-EXPIRED/validate", captured.RequestUri!.PathAndQuery);
        Assert.Contains("procedureCode=99213", captured.RequestUri.PathAndQuery);
        Assert.Contains("providerNpi=1234567890", captured.RequestUri.PathAndQuery);
        Assert.Contains("serviceDate=", captured.RequestUri.PathAndQuery);
    }

    [Fact]
    public async Task ValidateAsync_ValidBody_IsValid()
    {
        var sut = CreateClient(FakeHttpMessageHandler.Json(
            """{ "authorizationNumber": "AUTH-1", "isValid": true, "status": "Approved", "validatedMemberId": "MEM-1", "memberMatches": true, "validationMessage": "Authorization valid" }"""));

        var result = await sut.ValidateAsync("tenant-1", "AUTH-1", "99213", DateTime.UtcNow, "1234567890", memberId: "MEM-1");

        Assert.Equal(AuthorizationLookupStatus.Valid, result.Status);
    }

    /// <summary>authorization-service's own answer for an unknown number is definitive (D-04).</summary>
    [Fact]
    public async Task ValidateAsync_ServiceNotFound_IsNotFound()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("Authorization AUTH-UNKNOWN not found"),
        });
        var sut = CreateClient(handler);

        var result = await sut.ValidateAsync("tenant-1", "AUTH-UNKNOWN", "99213", DateTime.UtcNow, "1234567890", memberId: "MEM-1");

        Assert.Equal(AuthorizationLookupStatus.NotFound, result.Status);
    }

    /// <summary>
    /// The service answers a JSON string when the client accepts JSON (claims-service
    /// sends Accept: application/json): escaped characters must still match.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_ServiceNotFoundAsJsonString_WithEscapedCharacters_IsNotFound()
    {
        const string number = "AUTH-\"Q\\UOTE\"";
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize($"Authorization {number} not found"),
                System.Text.Encoding.UTF8, "application/json"),
        });
        var sut = CreateClient(handler);

        var result = await sut.ValidateAsync("tenant-1", number, "99213", DateTime.UtcNow, "1234567890", memberId: "MEM-1");

        Assert.Equal(AuthorizationLookupStatus.NotFound, result.Status);
    }

    /// <summary>A 404 that is not the service's answer (ingress, wrong base address) says nothing about the number.</summary>
    [Fact]
    public async Task ValidateAsync_BareNotFound_IsUnavailable()
    {
        var sut = CreateClient(FakeHttpMessageHandler.Status(HttpStatusCode.NotFound));

        var result = await sut.ValidateAsync("tenant-1", "AUTH-UNKNOWN", "99213", DateTime.UtcNow, "1234567890", memberId: "MEM-1");

        Assert.Equal(AuthorizationLookupStatus.Unavailable, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task ValidateAsync_NonDefinitiveStatus_IsUnavailable(HttpStatusCode status)
    {
        var sut = CreateClient(FakeHttpMessageHandler.Status(status));

        var result = await sut.ValidateAsync("tenant-1", "AUTH-1", "99213", DateTime.UtcNow, "1234567890", memberId: "MEM-1");

        Assert.Equal(AuthorizationLookupStatus.Unavailable, result.Status);
        Assert.Contains(((int)status).ToString(), result.Reason);
    }

    [Fact]
    public async Task ValidateAsync_BodyForAnotherNumber_IsUnavailable()
    {
        var sut = CreateClient(FakeHttpMessageHandler.Json(
            """{ "authorizationNumber": "AUTH-OTHER", "isValid": true, "status": "Approved" }"""));

        var result = await sut.ValidateAsync("tenant-1", "AUTH-1", "99213", DateTime.UtcNow, "1234567890", memberId: "MEM-1");

        Assert.Equal(AuthorizationLookupStatus.Unavailable, result.Status);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    public async Task ValidateAsync_UnreadableBody_IsUnavailable(string body)
    {
        var sut = CreateClient(FakeHttpMessageHandler.Json(body));

        var result = await sut.ValidateAsync("tenant-1", "AUTH-1", "99213", DateTime.UtcNow, "1234567890", memberId: "MEM-1");

        Assert.Equal(AuthorizationLookupStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task ValidateAsync_HttpClientTimeout_IsUnavailable()
    {
        var sut = CreateClient(FakeHttpMessageHandler.Throw(new TaskCanceledException("HttpClient.Timeout")));

        var result = await sut.ValidateAsync("tenant-1", "AUTH-1", "99213", DateTime.UtcNow, "1234567890", memberId: "MEM-1");

        Assert.Equal(AuthorizationLookupStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task ValidateAsync_TransportError_IsUnavailable()
    {
        var sut = CreateClient(FakeHttpMessageHandler.Throw(new HttpRequestException("Connection refused")));

        var result = await sut.ValidateAsync("tenant-1", "AUTH-1", "99213", DateTime.UtcNow, "1234567890", memberId: "MEM-1");

        Assert.Equal(AuthorizationLookupStatus.Unavailable, result.Status);
    }

    /// <summary>The caller's own cancellation is not a lookup result: it propagates (the message is redelivered).</summary>
    [Fact]
    public async Task ValidateAsync_CallerCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sut = CreateClient(new FakeHttpMessageHandler(_ => throw new TaskCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.ValidateAsync("tenant-1", "AUTH-1", "99213", DateTime.UtcNow, "1234567890", "MEM-1", cts.Token));
    }

    private static HttpAuthorizationValidationClient CreateClient(FakeHttpMessageHandler handler)
        => new(
            new NamedHttpClientFactory(handler),
            NullLogger<HttpAuthorizationValidationClient>.Instance);

    private sealed class NamedHttpClientFactory : IHttpClientFactory
    {
        private readonly FakeHttpMessageHandler _handler;

        public NamedHttpClientFactory(FakeHttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            _handler.LastClientName = name;
            return new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://authorization-service.test")
            };
        }
    }
}
