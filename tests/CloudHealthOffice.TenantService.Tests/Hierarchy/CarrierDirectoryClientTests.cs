using System.Net;
using System.Text;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.TenantService.Tests.Hierarchy;

/// <summary>
/// The carrier directory as its consumers get it over the wire
/// (plan-configuration.md §12.3). <see cref="CarrierIdentityTests"/> covers the
/// record and its serialized shape; this covers the client's own decisions,
/// which are the ones the fail-closed argument rests on.
///
/// <para>
/// The distinction that matters: an empty list and an unreachable
/// tenant-service must not look the same. An empty list means the tenant has
/// no carrier stating anything, and every consumer falls back on its configured
/// values. An unreachable tenant-service means nobody got to look, and a
/// consumer that fell back then could originate an ACH file or a letter in the
/// wrong legal entity's name — so the client throws instead.
/// </para>
/// </summary>
public class CarrierDirectoryClientTests
{
    private const string Tenant = "tenant-1";

    private const string TwoCarriers = """
        [
          {"id":"tenant-1|DEFAULT","code":"DEFAULT","name":"Synthetic Health","active":true,"isDefault":true,
           "legalName":"SYNTHETIC HEALTH INC","nachaImmediateOrigin":"1234567890"},
          {"id":"tenant-1|HMO","code":"HMO","name":"the HMO","active":true,"isDefault":false,
           "legalName":"ACME HEALTH PLAN OF TEXAS INC","payerIds":{"835":"ACME835"}}
        ]
        """;

    private static (HttpCarrierDirectory Directory, Recorder Wire) Build(
        HttpStatusCode status = HttpStatusCode.OK, string body = TwoCarriers, int cacheSeconds = 300)
    {
        var wire = new Recorder(status, body);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpCarrierDirectory.HttpClientName))
            .Returns(() => new HttpClient(wire) { BaseAddress = new Uri("http://tenant-service") });
        return (new HttpCarrierDirectory(factory.Object, new MemoryCache(new MemoryCacheOptions()),
            new CarrierDirectoryOptions { CacheSeconds = cacheSeconds }, NullLogger<HttpCarrierDirectory>.Instance), wire);
    }

    [Fact]
    public async Task It_lists_the_tenants_carriers_and_names_the_tenant_for_the_outbound_token()
    {
        var (directory, wire) = Build();

        await directory.DefaultAsync(Tenant);

        // The list, not a single carrier: listing is what materializes a
        // tenant's default carrier, so a tenant that has never had one written
        // still gets an answer.
        wire.Paths.Should().Equal("/api/v1/carriers");
        wire.Tenants.Should().Equal(Tenant);
    }

    [Fact]
    public async Task The_default_carrier_is_the_one_marked_default_not_the_first_listed()
    {
        var (directory, _) = Build(body: """
            [
              {"id":"tenant-1|HMO","code":"HMO","name":"the HMO","active":true,"isDefault":false},
              {"id":"tenant-1|DEFAULT","code":"DEFAULT","name":"Synthetic Health","active":true,"isDefault":true}
            ]
            """);

        (await directory.DefaultAsync(Tenant))!.Code.Should().Be("DEFAULT");
    }

    [Fact]
    public async Task With_no_carrier_marked_default_an_active_one_is_preferred_to_a_retired_one()
    {
        var (directory, _) = Build(body: """
            [
              {"id":"tenant-1|OLD","code":"OLD","name":"retired","active":false,"isDefault":false},
              {"id":"tenant-1|HMO","code":"HMO","name":"the HMO","active":true,"isDefault":false}
            ]
            """);

        (await directory.DefaultAsync(Tenant))!.Code.Should().Be("HMO");
    }

    [Fact]
    public async Task A_carrier_is_read_by_id_and_an_unknown_id_is_simply_not_there()
    {
        var (directory, _) = Build();

        (await directory.GetAsync(Tenant, "tenant-1|HMO"))!.LetterheadName.Should().Be("ACME HEALTH PLAN OF TEXAS INC");
        (await directory.GetAsync(Tenant, "tenant-1|HMO"))!.PayerIdFor("835").Should().Be("ACME835");
        (await directory.GetAsync(Tenant, "tenant-1|NOPE")).Should().BeNull();
        (await directory.GetAsync(Tenant, "  ")).Should().BeNull("a blank id is not worth a round trip");
    }

    [Fact]
    public async Task A_tenant_with_no_carriers_is_an_answer_not_a_failure()
    {
        // Both shapes tenant-service can give for "nothing here": an empty list
        // and a 404. Each means every consumer keeps its configured values.
        foreach (var (status, body) in new[] { (HttpStatusCode.OK, "[]"), (HttpStatusCode.NotFound, "") })
        {
            var (directory, _) = Build(status, body);

            (await directory.DefaultAsync(Tenant)).Should().BeNull();
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_tenant_service_that_could_not_answer_throws_rather_than_reading_as_no_carrier(HttpStatusCode status)
    {
        var (directory, _) = Build(status, "");

        await directory.Invoking(d => d.DefaultAsync(Tenant)).Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task A_tenant_service_that_cannot_be_reached_at_all_throws_too()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpCarrierDirectory.HttpClientName))
            .Returns(() => new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://tenant-service") });
        var directory = new HttpCarrierDirectory(factory.Object, new MemoryCache(new MemoryCacheOptions()),
            new CarrierDirectoryOptions(), NullLogger<HttpCarrierDirectory>.Instance);

        await directory.Invoking(d => d.DefaultAsync(Tenant)).Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task A_failed_read_is_not_cached_as_an_empty_list()
    {
        // Otherwise one outage would turn into five minutes of files originated
        // under the configured entity, which is the failure the throw prevents.
        var wire = new Recorder(HttpStatusCode.ServiceUnavailable, "");
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpCarrierDirectory.HttpClientName))
            .Returns(() => new HttpClient(wire) { BaseAddress = new Uri("http://tenant-service") });
        var directory = new HttpCarrierDirectory(factory.Object, new MemoryCache(new MemoryCacheOptions()),
            new CarrierDirectoryOptions(), NullLogger<HttpCarrierDirectory>.Instance);

        await directory.Invoking(d => d.DefaultAsync(Tenant)).Should().ThrowAsync<HttpRequestException>();
        wire.Succeed(TwoCarriers);

        (await directory.DefaultAsync(Tenant))!.Code.Should().Be("DEFAULT");
    }

    [Fact]
    public async Task The_list_is_read_once_per_tenant_and_a_tenants_answer_is_its_own()
    {
        var (directory, wire) = Build();

        await directory.DefaultAsync(Tenant);
        await directory.DefaultAsync(Tenant);
        await directory.GetAsync(Tenant, "tenant-1|HMO");
        await directory.DefaultAsync("tenant-2");

        wire.Tenants.Should().Equal(Tenant, "tenant-2");
    }

    [Fact]
    public async Task A_cache_of_no_seconds_still_caches_for_a_second_rather_than_not_at_all()
    {
        var (directory, wire) = Build(cacheSeconds: 0);

        await directory.DefaultAsync(Tenant);
        await directory.DefaultAsync(Tenant);

        wire.Paths.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_read_with_no_tenant_is_a_programming_error_not_a_round_trip()
    {
        var (directory, wire) = Build();

        await directory.Invoking(d => d.DefaultAsync("  ")).Should().ThrowAsync<ArgumentException>();
        wire.Paths.Should().BeEmpty();
    }

    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private HttpStatusCode _status = status;
        private string _body = body;

        public List<string> Paths { get; } = new();
        public List<string> Tenants { get; } = new();

        public void Succeed(string body)
        {
            _status = HttpStatusCode.OK;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Tenants.Add(request.Headers.TryGetValues("X-Tenant-ID", out var values) ? values.First() : string.Empty);
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Name or service not known (tenant-service:80)");
    }
}
