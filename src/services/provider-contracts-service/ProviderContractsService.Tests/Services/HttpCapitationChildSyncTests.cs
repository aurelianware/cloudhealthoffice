using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderContractsService.Models;
using ProviderContractsService.Services;

namespace ProviderContractsService.Tests.Services;

public class HttpCapitationChildSyncTests
{
    private sealed class Stub(HttpResponseMessage answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            return answer;
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://capitation-service.cloudhealthoffice/")
        };
    }

    private static ProviderContract Contract() => new()
    {
        Id = "pc-1", TenantId = "tenant-tx", ContractNumber = "PC-1", ProviderNPI = "1234567890",
        ProviderName = "Lone Star Pediatrics", LineOfBusiness = LineOfBusiness.Medicaid
    };

    [Fact]
    public async Task Sends_TheCopiedFields_ForTheContractsTenant_WithTheServiceIdentity()
    {
        var stub = new Stub(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { rateConfigsUpdated = 4, syncedAt = DateTime.UtcNow })
        });
        var sut = new HttpCapitationChildSync(new Factory(stub), NullLogger<HttpCapitationChildSync>.Instance);

        var result = await sut.SyncAsync(Contract());

        result.Succeeded.Should().BeTrue();
        result.RateConfigsUpdated.Should().Be(4);
        stub.Request!.Method.Should().Be(HttpMethod.Put);
        stub.Request.RequestUri!.AbsolutePath.Should().Be("/api/v1/internal/capitation/contracts/pc-1/parent-fields");
        stub.Request.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be("tenant-tx");
        stub.Request.Options.TryGetValue(ChoOutboundTokenHandler.ServiceIdentityRequired, out var svc).Should().BeTrue();
        svc.Should().BeTrue("capitation admits only this service's token, not the contracts user's");
        using var body = JsonDocument.Parse(stub.Body!);
        body.RootElement.GetProperty("lineOfBusiness").GetString().Should().Be("Medicaid");
        body.RootElement.GetProperty("providerName").GetString().Should().Be("Lone Star Pediatrics");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ARefusal_IsAFailure(HttpStatusCode status)
    {
        var sut = new HttpCapitationChildSync(new Factory(new Stub(new HttpResponseMessage(status))),
            NullLogger<HttpCapitationChildSync>.Instance);

        var result = await sut.SyncAsync(Contract());

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(((int)status).ToString());
    }
}
