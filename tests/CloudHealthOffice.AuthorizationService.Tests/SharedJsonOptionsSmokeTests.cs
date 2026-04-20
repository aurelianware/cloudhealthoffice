using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using AuthorizationService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Authorization = AuthorizationService.Models.Authorization;

namespace CloudHealthOffice.AuthorizationService.Tests;

/// <summary>
/// Verifies that authorization-service registers <see cref="JsonStringEnumConverter"/>
/// via the shared <c>AddCloudHealthOfficeJsonOptions()</c> helper. Portal clients
/// and sibling services expect enums as strings; the framework default (integer)
/// has bitten us before (PRs #656, #657).
/// </summary>
public class SharedJsonOptionsSmokeTests : IClassFixture<AuthorizationApiFactory>
{
    private readonly AuthorizationApiFactory _factory;

    public SharedJsonOptionsSmokeTests(AuthorizationApiFactory factory) => _factory = factory;

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
    /// End-to-end proof: an Authorization with <c>Status = Approved</c> must serialize
    /// the enum as the string "Approved", not the integer 4. Without the shared
    /// converter this assertion would have failed before this PR.
    /// </summary>
    [Fact]
    public async Task EnumField_RoundTripsAsString()
    {
        var id = Guid.NewGuid().ToString();
        _factory.AuthorizationRepository.GetByIdAsync(id).Returns(new Authorization
        {
            Id = id,
            TenantId = "test-tenant",
            AuthorizationNumber = "AUTH-ENUM-RT",
            MemberId = "MBR-001",
            Status = AuthorizationStatus.Approved,
            CreatedDate = DateTime.UtcNow
        });

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.IssueToken());

        var response = await client.GetAsync($"/api/authorizations/{id}");
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("\"status\":\"Approved\"", body);
    }
}
