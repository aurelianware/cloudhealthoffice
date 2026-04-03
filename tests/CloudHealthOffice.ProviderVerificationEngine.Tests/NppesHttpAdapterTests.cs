using System.Net;
using System.Text.Json;
using CloudHealthOffice.ProviderVerificationEngine.DataSources;
using CloudHealthOffice.ProviderVerificationEngine.DataSources.Nppes;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests;

public class NppesHttpAdapterTests
{
    [Theory]
    [InlineData("1234567893", true)]   // Valid Luhn check digit
    [InlineData("1497758544", true)]   // Known valid NPI
    [InlineData("1234567890", false)]  // Invalid check digit
    [InlineData("0000000000", false)]  // All zeros
    public void ValidNpi_PassesLuhnCheck(string npi, bool expectedValid)
    {
        // Use reflection to access the private static PassesLuhnCheck method
        var method = typeof(NppesHttpAdapter).GetMethod(
            "PassesLuhnCheck",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var result = (bool)method.Invoke(null, [npi])!;
        Assert.Equal(expectedValid, result);
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("0000000000")]
    [InlineData("9999999999")]
    public void InvalidNpi_FailsLuhnCheck(string npi)
    {
        var method = typeof(NppesHttpAdapter).GetMethod(
            "PassesLuhnCheck",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var result = (bool)method.Invoke(null, [npi])!;
        Assert.False(result);
    }

    [Fact]
    public async Task LookupByNpi_ReturnsProviderData()
    {
        // Arrange: mock HTTP response matching NPPES API format
        var nppesResponse = new
        {
            result_count = 1,
            results = new[]
            {
                new
                {
                    number = 1234567893L,
                    enumeration_type = "NPI-1",
                    basic = new
                    {
                        first_name = "John",
                        last_name = "Smith",
                        credential = "MD",
                        status = "A",
                        enumeration_date = "2005-05-23"
                    },
                    addresses = new[]
                    {
                        new
                        {
                            address_purpose = "LOCATION",
                            address_1 = "123 Main St",
                            city = "Austin",
                            state = "TX",
                            postal_code = "78701",
                            country_code = "US"
                        }
                    },
                    taxonomies = new[]
                    {
                        new
                        {
                            code = "207Q00000X",
                            desc = "Family Medicine",
                            primary = true,
                            state = "TX",
                            license = "MED12345"
                        }
                    },
                    identifiers = Array.Empty<object>(),
                    endpoints = Array.Empty<object>()
                }
            }
        };

        var json = JsonSerializer.Serialize(nppesResponse);
        var handler = new FakeHttpMessageHandler(json);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://npiregistry.cms.hhs.gov/api/")
        };

        var options = Options.Create(new VerificationOptions());
        var adapter = new NppesHttpAdapter(
            httpClient,
            NullLogger<NppesHttpAdapter>.Instance,
            options);

        // Act
        var result = await adapter.LookupByNpiAsync("1234567893");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1234567893", result.Npi);
        Assert.Equal("John", result.ProviderFirstName);
        Assert.Equal("Smith", result.ProviderLastName);
        Assert.Equal(NppesEnumerationType.Individual, result.EnumerationType);
        Assert.Single(result.Taxonomies);
        Assert.Equal("207Q00000X", result.Taxonomies[0].Code);
        Assert.True(result.Taxonomies[0].IsPrimary);
        Assert.Single(result.Addresses);
        Assert.Equal("LOCATION", result.Addresses[0].AddressPurpose);
    }

    [Fact]
    public async Task LookupByNpi_InvalidFormat_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler("{}");
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://npiregistry.cms.hhs.gov/api/")
        };
        var options = Options.Create(new VerificationOptions());
        var adapter = new NppesHttpAdapter(
            httpClient,
            NullLogger<NppesHttpAdapter>.Instance,
            options);

        // Too short
        var result = await adapter.LookupByNpiAsync("12345");
        Assert.Null(result);

        // Non-numeric
        result = await adapter.LookupByNpiAsync("ABCDEFGHIJ");
        Assert.Null(result);
    }

    [Fact]
    public async Task SearchAsync_ReturnsMatchingProviders()
    {
        var nppesResponse = new
        {
            result_count = 2,
            results = new[]
            {
                new
                {
                    number = 1234567893L,
                    enumeration_type = "NPI-1",
                    basic = new { first_name = "John", last_name = "Smith", status = "A" },
                    addresses = Array.Empty<object>(),
                    taxonomies = Array.Empty<object>(),
                    identifiers = Array.Empty<object>(),
                    endpoints = Array.Empty<object>()
                },
                new
                {
                    number = 1497758544L,
                    enumeration_type = "NPI-1",
                    basic = new { first_name = "Jane", last_name = "Smith", status = "A" },
                    addresses = Array.Empty<object>(),
                    taxonomies = Array.Empty<object>(),
                    identifiers = Array.Empty<object>(),
                    endpoints = Array.Empty<object>()
                }
            }
        };

        var json = JsonSerializer.Serialize(nppesResponse);
        var handler = new FakeHttpMessageHandler(json);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://npiregistry.cms.hhs.gov/api/")
        };

        var options = Options.Create(new VerificationOptions());
        var adapter = new NppesHttpAdapter(
            httpClient,
            NullLogger<NppesHttpAdapter>.Instance,
            options);

        var criteria = new NppesSearchCriteria { LastName = "Smith", State = "TX", Limit = 20 };
        var results = await adapter.SearchAsync(criteria);

        Assert.Equal(2, results.Count);
        Assert.Equal("John", results[0].ProviderFirstName);
        Assert.Equal("Jane", results[1].ProviderFirstName);
    }

    [Fact]
    public async Task SearchAsync_EmptyResults_ReturnsEmptyList()
    {
        var json = JsonSerializer.Serialize(new { result_count = 0, results = (object[]?)null });
        var handler = new FakeHttpMessageHandler(json);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://npiregistry.cms.hhs.gov/api/")
        };

        var options = Options.Create(new VerificationOptions());
        var adapter = new NppesHttpAdapter(
            httpClient,
            NullLogger<NppesHttpAdapter>.Instance,
            options);

        var criteria = new NppesSearchCriteria { LastName = "Nonexistent" };
        var results = await adapter.SearchAsync(criteria);

        Assert.Empty(results);
    }

    /// <summary>
    /// Fake HttpMessageHandler for unit testing HTTP calls without WireMock.
    /// </summary>
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        public FakeHttpMessageHandler(string responseJson)
        {
            _responseJson = responseJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
