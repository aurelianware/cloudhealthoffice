using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

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
            .GetRequiredService<IOptions<MvcJsonOptions>>().Value;

        Assert.Contains(options.JsonSerializerOptions.Converters,
            c => c is JsonStringEnumConverter);
    }
}
