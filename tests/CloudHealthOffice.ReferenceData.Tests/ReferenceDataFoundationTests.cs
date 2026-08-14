using CloudHealthOffice.ReferenceData.Domain;
using CloudHealthOffice.ReferenceData.Mapping;
using CloudHealthOffice.ReferenceData.Persistence;
using CloudHealthOffice.ReferenceData.Security;
using FluentAssertions;
using Xunit;

namespace CloudHealthOffice.ReferenceData.Tests;

public class ReferenceDataFoundationTests
{
    [Theory]
    [InlineData("ICD-10-CM", "http://hl7.org/fhir/sid/icd-10-cm")]
    [InlineData("Provider Taxonomy", "http://nucc.org/provider-taxonomy")]
    public void Registry_returns_verified_uri(string system, string uri)
    {
        CodeSystemRegistry.TryGet(system, out var definition).Should().BeTrue();
        definition.CanonicalUri.Should().Be(uri);
    }

    [Fact]
    public void Canonical_coding_maps_to_fhir_wire_fields()
    {
        var mapped = FhirCompatibleCodingMapper.Map(new ChoCoding
        {
            CodeSystem = "CDT", CodeSystemUri = "http://www.ada.org/cdt",
            Code = "D2740", Version = "2026", Display = null
        });

        mapped.System.Should().Be("http://www.ada.org/cdt");
        mapped.Code.Should().Be("D2740");
        mapped.Version.Should().Be("2026");
        mapped.Display.Should().BeNull();
    }

    [Fact]
    public async Task Lookup_resolves_effective_version_and_allows_missing_description()
    {
        var repository = new InMemoryReferenceDataRepository();
        await repository.ImportAsync([
            Code("old", "2025", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), "a"),
            Code("current", "2026", new DateOnly(2026, 1, 1), null, "b")
        ]);

        var found = await repository.GetAsync("CDT", "D2740", new DateOnly(2026, 8, 1));
        found.Should().NotBeNull();
        found!.Coding.Version.Should().Be("2026");
        found.Description.Should().BeNull();
    }

    [Fact]
    public async Task Search_supports_exact_prefix_text_category_and_pagination()
    {
        var repository = new InMemoryReferenceDataRepository();
        await repository.ImportAsync([
            Code("1", "2026", new DateOnly(2026, 1, 1), null, "same", "D0120", "Diagnostic", "Periodic exam"),
            Code("2", "2026", new DateOnly(2026, 1, 1), null, "same", "D0140", "Diagnostic", "Limited exam")
        ]);

        var result = await repository.SearchAsync(new ReferenceDataQuery
        {
            CodeSystem = "CDT", Search = "D01", SearchMode = ReferenceSearchMode.Prefix,
            Category = "Diagnostic", Page = 2, PageSize = 1
        });
        result.Total.Should().Be(2);
        result.Items.Should().ContainSingle().Which.Coding.Code.Should().Be("D0140");
    }

    [Fact]
    public async Task Tenant_records_are_isolated_but_global_records_are_visible()
    {
        var repository = new InMemoryReferenceDataRepository();
        await repository.ImportAsync([Code("global", "2026", new DateOnly(2026, 1, 1), null, "g")]);
        await repository.ImportAsync([Code("tenant", "2026", new DateOnly(2026, 1, 1), null, "t") with { TenantId = "tenant-a", Coding = new ChoCoding { CodeSystem = "CDT", Code = "D1110", Version = "2026" } }]);

        (await repository.GetAsync("CDT", "D1110", new DateOnly(2026, 1, 1), tenantId: "tenant-b")).Should().BeNull();
        (await repository.GetAsync("CDT", "D2740", new DateOnly(2026, 1, 1), tenantId: "tenant-b")).Should().NotBeNull();
    }

    [Fact]
    public async Task Checksum_makes_import_idempotent()
    {
        var repository = new InMemoryReferenceDataRepository();
        var records = new[] { Code("1", "2026", new DateOnly(2026, 1, 1), null, "checksum") };
        (await repository.ImportAsync(records)).AlreadyImported.Should().BeFalse();
        var second = await repository.ImportAsync(records);
        second.AlreadyImported.Should().BeTrue();
        second.ImportedCount.Should().Be(0);
    }

    [Fact]
    public void Licensed_description_is_not_publicly_exposed()
    {
        var licensed = Code("1", "2026", new DateOnly(2026, 1, 1), null, "x", display: "Licensed display")
            with { Description = "Licensed description" };

        ReferenceDataExposurePolicy.CanRead(licensed, new(false)).Should().BeFalse();
        var redacted = ReferenceDataExposurePolicy.Redact(licensed, new(false));
        redacted.Coding.Code.Should().Be("D2740");
        redacted.Coding.Display.Should().BeNull();
        redacted.Description.Should().BeNull();
    }

    private static ReferenceCode Code(string id, string version, DateOnly from, DateOnly? to, string checksum, string code = "D2740", string? category = null, string? display = null) => new()
    {
        Id = id,
        Coding = new ChoCoding { CodeSystem = "CDT", Code = code, Version = version, Display = display },
        Description = null,
        Category = category,
        EffectiveFrom = from,
        EffectiveTo = to,
        SourceId = "test",
        SourceVersion = version,
        LicenseClassification = LicenseClassification.Licensed,
        ExposureClassification = ExposureClassification.TenantRestricted,
        ImportedAt = DateTimeOffset.UtcNow,
        Checksum = checksum
    };
}
