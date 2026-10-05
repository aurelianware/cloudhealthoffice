using System.Net;
using EnrollmentImportService.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EnrollmentImportService.Tests.Services;

public class HttpBenefitPlanServiceClientTests
{
    private sealed class StubHandler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private static HttpBenefitPlanServiceClient Client(HttpStatusCode status, string body = "")
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpBenefitPlanServiceClient.HttpClientName))
            .Returns(() => new HttpClient(new StubHandler(status, body)) { BaseAddress = new Uri("http://benefit-plan") });
        return new HttpBenefitPlanServiceClient(factory.Object, NullLogger<HttpBenefitPlanServiceClient>.Instance);
    }

    [Fact]
    public async Task Resolve_Mapped_ReturnsPlanId()
    {
        var planId = await Client(HttpStatusCode.OK, "{\"planId\":\"plan-1\"}")
            .ResolvePlanIdAsync("t1", "GRP", "HLT", "PPO");

        planId.Should().Be("plan-1");
    }

    [Fact]
    public async Task Resolve_NotFound_IsAGap()
    {
        var planId = await Client(HttpStatusCode.NotFound).ResolvePlanIdAsync("t1", "GRP", "HLT", "PPO");

        planId.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Resolve_RefusedOrUnavailable_FailsInsteadOfReportingAGap(HttpStatusCode status)
    {
        var act = () => Client(status).ResolvePlanIdAsync("t1", "GRP", "HLT", "PPO");

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(status);
    }
}
