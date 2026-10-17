using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CloudHealthOffice.FhirService.Tests.Integration;

/// <summary>
/// fhir-service requires MongoDB in every environment (ADR 017). A Development
/// host used to start with no database and register in-process stores, a
/// passthrough enrollment gate and a no-op rule engine; it now refuses to start,
/// the same as a production host.
/// </summary>
public class DatabaseRequiredAtStartupTests
{
    [Fact]
    public void DevelopmentHostWithNoMongoConnectionString_RefusesToStart()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("MongoDb:ConnectionString", "");
        });

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>()
            .WithMessage("fhir-service requires a database. Configure MongoDb:ConnectionString.");
    }
}
