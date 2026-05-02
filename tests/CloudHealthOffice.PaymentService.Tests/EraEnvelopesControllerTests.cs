using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NSubstitute;
using PaymentService.Controllers;
using PaymentService.Models;
using PaymentService.Repositories;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

public class EraEnvelopesControllerTests : IClassFixture<PaymentApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly IEraEnvelopeRepository _repository;

    public EraEnvelopesControllerTests(PaymentApiFactory factory)
    {
        _repository = factory.EraEnvelopeRepository;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
    }

    private static EraEnvelopeRecord Sample(string id = "env-1") => new()
    {
        Id = id,
        TenantId = "test-tenant",
        PaymentRunId = "run-1",
        TradingPartnerId = "TP-A",
        EdiContent = "ISA*00*          *00*          ~ST*835*0001*005010X221A1~SE*2*0001~",
        ClaimCount = 3,
        TotalPaymentAmount = 1500.00m,
        ControlNumber = "000000001",
        GeneratedAt = DateTime.UtcNow,
        ClaimIds = new List<string> { "c1", "c2", "c3" }
    };

    [Fact]
    public async Task GetById_ReturnsMetadataWithoutEdi()
    {
        _repository.GetByIdAsync("env-1").Returns(Sample());

        var response = await _client.GetAsync("/api/v1/era-envelopes/env-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var metadata = await response.Content.ReadFromJsonAsync<EraEnvelopeMetadata>(Json);
        Assert.NotNull(metadata);
        Assert.Equal("env-1", metadata.Id);
        Assert.Equal("TP-A", metadata.TradingPartnerId);
        Assert.Equal(3, metadata.ClaimCount);
        Assert.True(metadata.EdiByteSize > 0);
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        _repository.GetByIdAsync("missing").Returns((EraEnvelopeRecord?)null);

        var response = await _client.GetAsync("/api/v1/era-envelopes/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetEdi_ReturnsTextPlain()
    {
        _repository.GetByIdAsync("env-1").Returns(Sample());

        var response = await _client.GetAsync("/api/v1/era-envelopes/env-1/edi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("ISA*", body);
    }

    [Fact]
    public async Task GetEdi_NotFound_Returns404()
    {
        _repository.GetByIdAsync("missing").Returns((EraEnvelopeRecord?)null);

        var response = await _client.GetAsync("/api/v1/era-envelopes/missing/edi");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Search_ByPaymentRunId_FiltersResults()
    {
        var env1 = Sample("env-1");
        var env2 = Sample("env-2");
        env2.TradingPartnerId = "TP-B";
        _repository.SearchAsync("run-1", null).Returns(new[] { env1, env2 });

        var response = await _client.GetAsync("/api/v1/era-envelopes?paymentRunId=run-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<EraEnvelopeMetadata>>(Json);
        Assert.NotNull(results);
        Assert.Equal(2, results.Count);
    }
}
