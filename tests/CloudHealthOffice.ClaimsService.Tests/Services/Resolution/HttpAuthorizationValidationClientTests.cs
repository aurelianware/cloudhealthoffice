using System.Net;
using ClaimsService.Services;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.ClaimsService.Tests.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Resolution;

public sealed class HttpAuthorizationValidationClientTests
{
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
            "1234567890");

        Assert.NotNull(result);
        Assert.False(result!.IsValid);
        Assert.Equal("AUTH-EXPIRED", result.AuthorizationNumber);
        Assert.Equal("Authorization expired or not yet active", result.ValidationMessage);
        Assert.NotNull(captured);
        Assert.Equal(UpstreamClientNames.AuthorizationService, handler.LastClientName);
        Assert.Equal("tenant-1", captured!.Headers.GetValues("X-Tenant-ID").Single());
        Assert.Contains("/api/authorizations/AUTH-EXPIRED/validate", captured.RequestUri!.PathAndQuery);
        Assert.Contains("procedureCode=99213", captured.RequestUri.PathAndQuery);
        Assert.Contains("providerNpi=1234567890", captured.RequestUri.PathAndQuery);
        Assert.Contains("serviceDate=", captured.RequestUri.PathAndQuery);
    }

    [Fact]
    public async Task ValidateAsync_NotFound_ReturnsNull()
    {
        var handler = FakeHttpMessageHandler.Status(HttpStatusCode.NotFound);
        var sut = CreateClient(handler);

        var result = await sut.ValidateAsync(
            "tenant-1",
            "AUTH-UNKNOWN",
            "99213",
            DateTime.UtcNow,
            "1234567890");

        Assert.Null(result);
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
