using System.Data;
using Microsoft.EntityFrameworkCore;
using ReferenceDataService.Repositories;

namespace ReferenceDataService.Migrations;

public sealed class ReferenceDataSchemaMigrator
{
    /// <summary>
    /// Forward-only migrations, applied in order. Each is an embedded
    /// <c>Migrations/{id}.sql</c> recorded in <c>reference_data_schema_migrations</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> MigrationIds =
    [
        "20260814_001_canonical_reference_data",
        "20261004_002_import_ledger_tenant_scope_and_actor",
    ];
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

        foreach (var migrationId in MigrationIds)
        {
            await using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT EXISTS (SELECT 1 FROM reference_data_schema_migrations WHERE migration_id = @id);";
            var parameter = check.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = migrationId;
            check.Parameters.Add(parameter);
            if ((bool)(await check.ExecuteScalarAsync(cancellationToken) ?? false))
                continue;

            _logger.LogInformation("Applying reference data schema migration {MigrationId}", migrationId);
            await ExecuteAsync(connection, transaction, ReadMigrationSql(migrationId), cancellationToken);

            await using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO reference_data_schema_migrations (migration_id) VALUES (@id);";
            var recordParameter = record.CreateParameter();
            recordParameter.ParameterName = "id";
            recordParameter.Value = migrationId;
            record.Parameters.Add(recordParameter);
            await record.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("Applied reference data schema migration {MigrationId}", migrationId);
        }

        await transaction.CommitAsync(cancellationToken);
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

    internal static string ReadMigrationSql(string migrationId)
    {
        var resourceSuffix = $"Migrations.{migrationId}.sql";
        var assembly = typeof(ReferenceDataSchemaMigrator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.Ordinal));
        if (resourceName is null)
            throw new InvalidOperationException($"Embedded migration resource '{resourceSuffix}' was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration resource '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
