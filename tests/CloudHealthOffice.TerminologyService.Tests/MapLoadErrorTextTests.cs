using System.Text;
using CHO.TerminologyService.Services;
using CHO.TerminologyService.Services.Loaders;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.TerminologyService.Tests;

/// <summary>
/// A map load that fails unexpectedly (the database, a driver) reports a
/// fixed error; the load result is returned to the caller, so the exception
/// text stays in the log.
/// </summary>
public sealed class MapLoadErrorTextTests
{
    private const string Secret = "mongodb://admin:hunter2@db:27017";

    private static MapLoadOptions Options() => new()
    {
        MapName = "PLAN-MAP",
        Version = "2026-01",
        SourceSystem = "http://snomed.info/sct",
        TargetSystem = "http://hl7.org/fhir/sid/icd-10-cm",
    };

    [Fact]
    public async Task Csv_load_whose_repository_fails_reports_a_fixed_error_without_the_exception_text()
    {
        var repository = Substitute.For<IConceptMapRepository>();
        repository.BulkInsertAsync(Arg.Any<List<CHO.TerminologyService.Models.ConceptMapEntry>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException(Secret + " timed out"));
        var loader = new CsvMapLoader(repository, NullLogger<CsvMapLoader>.Instance);

        var result = await loader.LoadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("source_code,target_code\n44054006,E11.9\n")), Options());

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be(MapLoadResult.FatalLoadError);
        string.Join("; ", result.Errors).Should().NotContain("hunter2");
    }
}
