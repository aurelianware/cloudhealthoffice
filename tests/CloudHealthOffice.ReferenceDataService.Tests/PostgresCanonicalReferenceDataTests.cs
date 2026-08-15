using CloudHealthOffice.ReferenceData.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReferenceDataService.Migrations;
using ReferenceDataService.Repositories;
using ReferenceDataService.Repositories.Canonical;
using Xunit;

namespace CloudHealthOffice.ReferenceDataService.Tests;

public sealed class PostgresCanonicalReferenceDataTests
{
    [PostgresFact]
    public async Task Migration_and_repository_work_against_postgresql()
    {
        var connectionString = Environment.GetEnvironmentVariable("REFERENCE_DATA_TEST_POSTGRES")
            ?? throw new InvalidOperationException("REFERENCE_DATA_TEST_POSTGRES is not configured.");

        var options = new DbContextOptionsBuilder<ReferenceDataContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using (var firstContext = new ReferenceDataContext(options))
        await using (var secondContext = new ReferenceDataContext(options))
        {
            var firstMigration = new ReferenceDataSchemaMigrator(
                firstContext, NullLogger<ReferenceDataSchemaMigrator>.Instance);
            var secondMigration = new ReferenceDataSchemaMigrator(
                secondContext, NullLogger<ReferenceDataSchemaMigrator>.Instance);
            await Task.WhenAll(firstMigration.ApplyAsync(), secondMigration.ApplyAsync());
        }

        await AssertSchemaAsync(connectionString);

        var suffix = Guid.NewGuid().ToString("N");
        var records = new[] { Code(suffix) };
        await using var winnerContext = new ReferenceDataContext(options);
        await using var duplicateContext = new ReferenceDataContext(options);
        var results = await Task.WhenAll(
            new CanonicalReferenceDataRepository(winnerContext).ImportAsync(records),
            new CanonicalReferenceDataRepository(duplicateContext).ImportAsync(records));

        results.Should().ContainSingle(result => !result.AlreadyImported && result.ImportedCount == 1);
        results.Should().ContainSingle(result => result.AlreadyImported && result.ImportedCount == 0);

        await using var lookupContext = new ReferenceDataContext(options);
        var found = await new CanonicalReferenceDataRepository(lookupContext).GetAsync(
            "integration", suffix, new DateOnly(2026, 8, 14));
        found.Should().NotBeNull();
        found!.Id.Should().Be(suffix);
    }

    private static async Task AssertSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                to_regclass('public.canonical_reference_codes') IS NOT NULL
                AND to_regclass('public.canonical_reference_data_imports') IS NOT NULL
                AND to_regclass('public.idx_canonical_reference_lookup') IS NOT NULL
                AND (SELECT COUNT(*) FROM reference_data_schema_migrations
                     WHERE migration_id = '20260814_001_canonical_reference_data') = 1;
            """;
        (await command.ExecuteScalarAsync()).Should().Be(true);
    }

    private static ReferenceCode Code(string suffix) => new()
    {
        Id = suffix,
        Coding = new ChoCoding
        {
            CodeSystem = "integration",
            Code = suffix,
            Version = "2026",
            Display = "PostgreSQL integration test"
        },
        EffectiveFrom = new DateOnly(2026, 1, 1),
        Active = true,
        SourceId = $"integration-{suffix}",
        SourceVersion = "2026",
        LicenseClassification = LicenseClassification.Public,
        ExposureClassification = ExposureClassification.PublicReference,
        ImportedAt = DateTimeOffset.UtcNow,
        Checksum = suffix
    };
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REFERENCE_DATA_TEST_POSTGRES")))
            Skip = "REFERENCE_DATA_TEST_POSTGRES is not configured.";
    }
}
