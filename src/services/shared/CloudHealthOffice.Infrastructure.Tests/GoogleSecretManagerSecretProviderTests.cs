using System.Net;
using System.Text;
using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests;

public sealed class GoogleSecretManagerSecretProviderTests
{
    private static SecretProviderOptions Options(params string[] names) => new()
    {
        Provider = SecretProviderType.GoogleSecretManager,
        GoogleSecretManagerProjectId = "demo-project",
        GoogleSecretManagerSecretNames = names.Length == 0 ? ["appeal-body-encryption-key-v1"] : names
    };
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        public List<string?> Tokens { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Urls.Add(request.RequestUri!.AbsoluteUri);
            Tokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(response(request, Urls.Count));
        }
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static GoogleSecretManagerSecretProvider Build(Handler handler, SecretProviderOptions? options = null) =>
        new(options ?? Options(), new HttpClient(handler), _ => Task.FromResult("synthetic-token"),
            NullLogger<GoogleSecretManagerSecretProvider>.Instance);
    private const string ValidPayload = "{\"payload\":{\"data\":\"MTIzNDU2Nzg5\",\"dataCrc32c\":\"3808858755\"}}";

    [Fact]
    public async Task Access_uses_fixed_Google_host_ADC_token_and_verifies_the_payload()
    {
        var handler = new Handler((_, _) => Json(ValidPayload));
        using var provider = Build(handler);
        (await provider.GetSecretAsync("appeal-body-encryption-key-v1")).Should().Be("123456789");
        handler.Urls.Should().Equal("https://secretmanager.googleapis.com/v1/projects/demo-project/secrets/appeal-body-encryption-key-v1/versions/latest:access");
        handler.Tokens.Should().Equal("synthetic-token");
        GoogleSecretManagerSecretProvider.Crc32C("123456789"u8).Should().Be(0xE3069283u);
    }

    [Fact]
    public async Task Explicit_versions_are_preserved()
    {
        var handler = new Handler((_, _) => Json(ValidPayload));
        using var provider = Build(handler);
        await provider.GetSecretByVersionAsync("appeal-body-encryption-key-v1", "7");
        handler.Urls.Single().Should().EndWith("/versions/7:access");
    }

    [Theory]
    [InlineData("{\"payload\":{\"data\":\"MTIzNDU2Nzg5\",\"dataCrc32c\":\"0\"}}")]
    [InlineData("{\"payload\":{\"data\":\"MTIzNDU2Nzg5\"}}")]
    [InlineData("{\"payload\":{\"data\":\"bad-secret-content!\",\"dataCrc32c\":\"0\"}}")]
    public async Task Corrupt_or_unverifiable_payloads_fail_closed(string body)
    {
        using var provider = Build(new Handler((_, _) => Json(body)));
        var act = () => provider.GetSecretAsync("appeal-body-encryption-key-v1");
        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().NotContain("bad-secret-content");
    }

    [Fact]
    public async Task Missing_is_null_but_permission_failure_is_not_missing()
    {
        using var missing = Build(new Handler((_, _) => Json("{}", HttpStatusCode.NotFound)));
        (await missing.GetSecretAsync("appeal-body-encryption-key-v1")).Should().BeNull();
        (await missing.HealthCheckAsync()).Should().BeFalse();
        using var denied = Build(new Handler((_, _) => Json("private secret response", HttpStatusCode.Forbidden)));
        var act = () => denied.GetSecretAsync("appeal-body-encryption-key-v1");
        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().NotContain("private secret response");
        (await denied.HealthCheckAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Unconfigured_names_and_path_injection_never_reach_Google()
    {
        var handler = new Handler((_, _) => Json(ValidPayload));
        using var provider = Build(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetSecretAsync("unrelated-secret"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetSecretByVersionAsync("appeal-body-encryption-key-v1", "1/../../other"));
        handler.Urls.Should().BeEmpty();
    }

    [Fact]
    public async Task Prefix_reads_only_allowlisted_names_without_project_discovery()
    {
        var handler = new Handler((_, _) => Json(ValidPayload));
        using var provider = Build(handler, Options("appeal-body-encryption-key-v1", "unrelated-key"));
        var values = await provider.GetSecretsAsync("appeal-");
        values.Should().ContainSingle().Which.Key.Should().Be("appeal-body-encryption-key-v1");
        handler.Urls.Should().ContainSingle().Which.Should().Contain("/versions/latest:access");
    }

    [Fact]
    public async Task Version_listing_pages_filters_disabled_and_orders_newest_first()
    {
        var handler = new Handler((_, n) => Json(n == 1
            ? "{\"versions\":[{\"name\":\"projects/1/secrets/key/versions/1\",\"state\":\"ENABLED\",\"createTime\":\"2026-01-01T00:00:00Z\"}],\"nextPageToken\":\"next+page\"}"
            : "{\"versions\":[{\"name\":\"projects/1/secrets/key/versions/2\",\"state\":\"ENABLED\",\"createTime\":\"2026-02-01T00:00:00Z\"},{\"name\":\"projects/1/secrets/key/versions/3\",\"state\":\"DISABLED\"}]}"));
        using var provider = Build(handler);
        (await provider.ListSecretVersionsAsync("appeal-body-encryption-key-v1")).Select(v => v.Version).Should().Equal("2", "1");
        handler.Urls[1].Should().Contain("pageToken=next%2Bpage");
    }

    [Fact]
    public async Task Repeated_page_tokens_are_refused()
    {
        using var provider = Build(new Handler((_, _) => Json("{\"versions\":[],\"nextPageToken\":\"same\"}")));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.ListSecretVersionsAsync("appeal-body-encryption-key-v1"));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_health()
    {
        using var provider = Build(new Handler((_, _) => Json(ValidPayload)));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.HealthCheckAsync(canceled.Token));
    }

    [Fact]
    public void Registration_resolves_without_fetching_credentials_or_secrets()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["SecretProvider:Provider"] = "GoogleSecretManager", ["SecretProvider:GoogleSecretManagerProjectId"] = "demo-project",
            ["SecretProvider:GoogleSecretManagerSecretNames:0"] = "appeal-body-encryption-key-v1"
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSecretProvider(config);
        using var container = services.BuildServiceProvider();
        container.GetRequiredService<ISecretProvider>().Should().BeOfType<GoogleSecretManagerSecretProvider>();
    }

    [Fact]
    public void Missing_project_or_allowlist_is_a_configuration_error()
    {
        var handler = new Handler((_, _) => Json(ValidPayload));
        Assert.Throws<InvalidOperationException>(() => Build(handler, Options() with { GoogleSecretManagerProjectId = "../wrong" }));
        Assert.Throws<InvalidOperationException>(() => Build(handler, Options() with { GoogleSecretManagerSecretNames = [] }));
    }
}
