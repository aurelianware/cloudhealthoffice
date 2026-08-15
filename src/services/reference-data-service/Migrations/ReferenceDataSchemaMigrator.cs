using System.Data;
using Microsoft.EntityFrameworkCore;
using ReferenceDataService.Repositories;

namespace ReferenceDataService.Migrations;

public sealed class ReferenceDataSchemaMigrator
{
    private const string MigrationId = "20260814_001_canonical_reference_data";
    private const string ResourceSuffix = $"Migrations.{MigrationId}.sql";
    private readonly ReferenceDataContext _context;
    private readonly ILogger<ReferenceDataSchemaMigrator> _logger;

    public ReferenceDataSchemaMigrator(
        ReferenceDataContext context,
        ILogger<ReferenceDataSchemaMigrator> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var connection = _context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtext('cloudhealthoffice.reference-data-schema'));",
            cancellationToken);
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS reference_data_schema_migrations (
                migration_id VARCHAR(200) PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """, cancellationToken);

        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT EXISTS (SELECT 1 FROM reference_data_schema_migrations WHERE migration_id = @id);";
        var parameter = check.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = MigrationId;
        check.Parameters.Add(parameter);
        if ((bool)(await check.ExecuteScalarAsync(cancellationToken) ?? false))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        _logger.LogInformation("Applying reference data schema migration {MigrationId}", MigrationId);
        await ExecuteAsync(connection, transaction, ReadMigrationSql(), cancellationToken);

        await using var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = "INSERT INTO reference_data_schema_migrations (migration_id) VALUES (@id);";
        var recordParameter = record.CreateParameter();
        recordParameter.ParameterName = "id";
        recordParameter.Value = MigrationId;
        record.Parameters.Add(recordParameter);
        await record.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _logger.LogInformation("Applied reference data schema migration {MigrationId}", MigrationId);
    }

    private static async Task ExecuteAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ReadMigrationSql()
    {
        var assembly = typeof(ReferenceDataSchemaMigrator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        if (resourceName is null)
            throw new InvalidOperationException($"Embedded migration resource '{ResourceSuffix}' was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration resource '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
