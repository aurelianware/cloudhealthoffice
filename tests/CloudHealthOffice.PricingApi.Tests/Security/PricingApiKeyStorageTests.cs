using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.PricingApi.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Moq;

namespace CloudHealthOffice.PricingApi.Tests.Security;

/// <summary>
/// Keys are stored hashed and shown once; admin actions take a key id; the
/// limiter partitions by the authenticated caller; import errors do not echo
/// exception text.
/// </summary>
public class PricingApiKeyStorageTests : IDisposable
{
    private const string Tenant = "tenant-a";
    private readonly PricingApiFactory _factory = new();
    private readonly List<ApiKeyRecord> _created = new();

    public PricingApiKeyStorageTests()
    {
        _factory.ApiKeyRepository.Setup(r => r.CreateAsync(It.IsAny<ApiKeyRecord>()))
            .ReturnsAsync((ApiKeyRecord r) => { _created.Add(r); return r; });
        _factory.FeeScheduleRepository.Setup(r => r.GetAllSchedulesAsync()).ReturnsAsync(new List<FeeScheduleInfo>());
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient PlatformAdmin()
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("admin-1", ChoRolePermissions.PlatformAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    [Fact]
    public async Task CreateApiKey_StoresOnlyHashAndPrefix_AndShowsKeyOnce()
    {
        var response = await PlatformAdmin().PostAsJsonAsync("/api/v1/admin/api-keys",
            new { tenantName = "Acme Health", tier = "free" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        var apiKey = data.GetProperty("apiKey").GetString()!;
        apiKey.Should().StartWith("cho_").And.HaveLength(36);

        var stored = _created.Should().ContainSingle().Subject;
        stored.KeyHash.Should().Be(ApiKeyHashing.Hash(apiKey));
        stored.KeyPrefix.Should().Be(apiKey[..12]);
        stored.KeyId.Should().StartWith("pk_");
        JsonSerializer.Serialize(stored).Should().NotContain(apiKey);

        var key = data.GetProperty("key");
        key.GetProperty("keyId").GetString().Should().Be(stored.KeyId);
        key.ToString().Should().NotContain(stored.KeyHash);
    }

    [Fact]
    public async Task ListApiKeys_NeverReturnsKeyOrHash()
    {
        var record = Record("cho_0123456789abcdef0123456789abcdef");
        _factory.ApiKeyRepository.Setup(r => r.ListAsync()).ReturnsAsync([record]);

        var body = await (await PlatformAdmin().GetAsync("/api/v1/admin/api-keys")).Content.ReadAsStringAsync();

        body.Should().Contain(record.KeyId).And.Contain(record.KeyPrefix);
        body.Should().NotContain(record.KeyHash).And.NotContain("0123456789abcdef0123456789abcdef");
    }

    [Fact]
    public async Task DeactivateApiKey_ByKeyId_Deactivates()
    {
        var record = Record("cho_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        _factory.ApiKeyRepository.Setup(r => r.GetByIdAsync(record.KeyId)).ReturnsAsync(record);

        var response = await PlatformAdmin().DeleteAsync("/api/v1/admin/api-keys/" + record.KeyId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.ApiKeyRepository.Verify(r => r.DeactivateAsync(record.KeyId, "admin-1", It.IsAny<DateTimeOffset>()), Times.Once);
    }

    [Fact]
    public async Task DeactivateApiKey_WithTheKeyInTheUrl_Is404AndDeactivatesNothing()
    {
        const string key = "cho_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var record = Record(key);
        _factory.ApiKeyRepository.Setup(r => r.GetByIdAsync(record.KeyId)).ReturnsAsync(record);
        _factory.ApiKeyRepository.Setup(r => r.GetByHashAsync(record.KeyHash)).ReturnsAsync(record);

        var response = await PlatformAdmin().DeleteAsync("/api/v1/admin/api-keys/" + key);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.ApiKeyRepository.Verify(r => r.DeactivateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task RateLimiter_MadeUpKeys_ShareTheCallersBucket()
    {
        using var factory = LimitedTo(2);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-API-Key", $"cho_madeup{i:D26}");
            statuses.Add((await client.GetAsync("/api/v1/fee-schedules")).StatusCode);
        }

        // Before: each made-up key was a bucket of its own, so none was ever limited.
        statuses.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task RateLimiter_ValidKey_HasItsOwnBucket()
    {
        const string key = "cho_cccccccccccccccccccccccccccccccc";
        _factory.ApiKeyRepository.Setup(r => r.GetByHashAsync(ApiKeyHashing.Hash(key))).ReturnsAsync(Record(key));
        using var factory = LimitedTo(2);

        // Exhaust the anonymous (client address) bucket.
        for (var i = 0; i < 3; i++)
            await factory.CreateClient().GetAsync("/api/v1/fee-schedules");

        var keyed = factory.CreateClient();
        keyed.DefaultRequestHeaders.Add("X-API-Key", key);
        (await keyed.GetAsync("/api/v1/fee-schedules")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("rbrvs")]
    [InlineData("opps")]
    [InlineData("drg")]
    public async Task Upload_Failure_DoesNotReturnExceptionText(string kind)
    {
        const string secret = "mongodb://pricing:Sup3rSecret@10.0.0.5/cho_pricing";
        _factory.FeeScheduleLoaderService.Setup(l => l.SeedMedicareRbrvs(It.IsAny<string>(), It.IsAny<int>())).ThrowsAsync(new InvalidOperationException(secret));
        _factory.FeeScheduleLoaderService.Setup(l => l.SeedMedicareOpps(It.IsAny<string>(), It.IsAny<int>())).ThrowsAsync(new InvalidOperationException(secret));
        _factory.FeeScheduleLoaderService.Setup(l => l.SeedMedicareDrg(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>())).ThrowsAsync(new InvalidOperationException(secret));

        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("HCPCS,WORK RVU\n99213,1.3\n"u8.ToArray()), "file", "rates.csv");
        var response = await PlatformAdmin().PostAsync($"/api/v1/admin/fee-schedules/upload/{kind}", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("IMPORT_FAILED");
        body.Should().NotContain("Sup3rSecret").And.NotContain("mongodb://");
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> LimitedTo(int permits) =>
        _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RateLimiting:PermitLimit"] = permits.ToString(),
                ["RateLimiting:QueueLimit"] = "0",
                ["RateLimiting:WindowSeconds"] = "600",
            })));

    private static ApiKeyRecord Record(string key) => new()
    {
        Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
        KeyId = "pk_" + ApiKeyHashing.Hash(key)[..32],
        KeyHash = ApiKeyHashing.Hash(key),
        KeyPrefix = ApiKeyHashing.Prefix(key),
        TenantName = "Acme Health",
        Tier = PricingTier.Free,
        MonthlyLimit = 1_000,
        CreatedAt = DateTimeOffset.UtcNow,
        IsActive = true,
    };
}
