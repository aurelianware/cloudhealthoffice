using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace CloudHealthOffice.PricingApi.Tests;

/// <summary>
/// Verifies that PricingApi registers <see cref="JsonStringEnumConverter"/> with
/// <see cref="JsonNamingPolicy.CamelCase"/> via the shared
/// <c>AddCloudHealthOfficeJsonOptions(camelCaseEnums: true)</c> helper.
/// PricingApi's published wire format uses camelCase enum names
/// (e.g. "medicareFeeSchedule"); the converter must enforce that contract.
/// </summary>
public class SharedJsonOptionsSmokeTests : IClassFixture<PricingApiFactory>
{
    private readonly PricingApiFactory _factory;

    public SharedJsonOptionsSmokeTests(PricingApiFactory factory) => _factory = factory;

    [Fact]
    public void JsonStringEnumConverter_IsRegistered()
    {
        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptions<MvcJsonOptions>>().Value;

        Assert.Contains(options.JsonSerializerOptions.Converters,
            c => c is JsonStringEnumConverter);
    }

    [Fact]
    public void JsonStringEnumConverter_UsesCamelCaseNamingPolicy()
    {
        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptions<MvcJsonOptions>>().Value;

        var converter = options.JsonSerializerOptions.Converters
            .OfType<JsonStringEnumConverter>()
            .FirstOrDefault();

        Assert.NotNull(converter);

        // Verify camelCase enum output — PricingApi consumers expect
        // "medicareFeeSchedule", not "MedicareFeeSchedule".
        var serializerOptions = new JsonSerializerOptions();
        serializerOptions.Converters.Add(converter);
        var json = JsonSerializer.Serialize(TestEnum.CamelCaseValue, serializerOptions);
        Assert.Equal("\"camelCaseValue\"", json);
    }

    [Fact]
    public void JsonStringEnumConverter_RejectsIntegerEnumValues()
    {
        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptions<MvcJsonOptions>>().Value;

        // allowIntegerValues: false — integer inputs must be rejected so callers
        // cannot bypass the string-enum contract.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<TestEnum>("1", options.JsonSerializerOptions));
    }

    private enum TestEnum { CamelCaseValue }
}
