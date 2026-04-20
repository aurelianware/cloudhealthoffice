using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// Verifies that smart-auth-service registers <see cref="JsonStringEnumConverter"/>
/// via the shared <c>AddCloudHealthOfficeJsonOptions()</c> helper. Portal clients
/// and sibling services expect enums as strings; the framework default (integer)
/// has bitten us before (PRs #656, #657).
/// </summary>
public class SharedJsonOptionsSmokeTests : IClassFixture<SmartAuthTestFixture>
{
    private readonly SmartAuthTestFixture _fixture;

    public SharedJsonOptionsSmokeTests(SmartAuthTestFixture fixture) => _fixture = fixture;

    [Fact]
    public void JsonStringEnumConverter_IsRegistered()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptions<JsonOptions>>().Value;

        Assert.Contains(options.JsonSerializerOptions.Converters,
            c => c is JsonStringEnumConverter);
    }
}
