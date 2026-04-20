using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EligibilityService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>
/// Verifies that eligibility-service registers <see cref="JsonStringEnumConverter"/>
/// via the shared <c>AddCloudHealthOfficeJsonOptions()</c> helper. Portal clients
/// and sibling services expect enums as strings; the framework default (integer)
/// has bitten us before (PRs #656, #657).
/// </summary>
public class SharedJsonOptionsSmokeTests : IClassFixture<EligibilityApiFactory>
{
    private readonly EligibilityApiFactory _factory;

    public SharedJsonOptionsSmokeTests(EligibilityApiFactory factory) => _factory = factory;

    [Fact]
    public void JsonStringEnumConverter_IsRegistered()
    {
        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptions<JsonOptions>>().Value;

        Assert.Contains(options.JsonSerializerOptions.Converters,
            c => c is JsonStringEnumConverter);
    }

    /// <summary>
    /// End-to-end proof: posting an EligibilityInquiry with <c>"status":"Processing"</c>
    /// (a string) must deserialize to <see cref="EligibilityInquiryStatus.Processing"/>.
    /// Without the shared converter this assertion would have failed before this PR.
    /// </summary>
    [Fact]
    public async Task EnumField_RoundTripsAsString()
    {
        EligibilityInquiry? captured = null;
        _factory.EligibilityService
            .ProcessInquiryAsync(Arg.Do<EligibilityInquiry>(i => captured = i))
            .Returns(new EligibilityResponse
            {
                Id = Guid.NewGuid().ToString(),
                InquiryId = "INQ-ENUM-RT",
                TenantId = "test-tenant",
                CreatedDate = DateTime.UtcNow
            });

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");

        var inquiry = new
        {
            tenantId = "test-tenant",
            subscriberId = "SUB-ENUM-RT",
            status = "Processing"
        };
        var response = await client.PostAsJsonAsync("/api/eligibility/inquiry", inquiry);

        response.EnsureSuccessStatusCode();
        Assert.Equal(EligibilityInquiryStatus.Processing, captured?.Status);
    }
}
