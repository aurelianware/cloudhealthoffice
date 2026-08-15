# Reference data database migrations

The reference data service applies embedded, forward-only PostgreSQL migrations before it starts accepting traffic. Each migration runs in a transaction while holding the `cloudhealthoffice.reference-data-schema` advisory lock, which makes startup safe when multiple replicas are deployed together. Applied migration IDs are recorded in `reference_data_schema_migrations`.

## Deployment

The runtime database role must be allowed to create and alter objects in the target database. Configure the service with `ConnectionStrings__PostgreSQL`; `${POSTGRES_PASSWORD}` in that value is replaced from the `POSTGRES_PASSWORD` environment variable.

After deployment, verify the rollout and migration:

```sql
SELECT migration_id, applied_at
FROM reference_data_schema_migrations
ORDER BY applied_at;

SELECT to_regclass('public.canonical_reference_codes'),
       to_regclass('public.canonical_reference_data_imports'),
       to_regclass('public.idx_canonical_reference_lookup');
```

The pod does not become available if a migration fails. Check the service logs for the migration ID and the PostgreSQL error before retrying the rollout.

## Rollback

Migration `20260814_001_canonical_reference_data` is additive: it creates canonical tables and indexes without changing the legacy tables. Rolling back the application image is therefore safe; leave the new tables and migration ledger in place so a later deployment can resume without reapplying the migration. Do not delete tables as part of an application rollback. Any future destructive schema reversal must be a separately reviewed migration with a data-retention plan.

## PostgreSQL integration test

CI provisions PostgreSQL 16 and sets `REFERENCE_DATA_TEST_POSTGRES`. To run the same test locally, start a disposable PostgreSQL database and run:

```bash
REFERENCE_DATA_TEST_POSTGRES='Host=localhost;Port=5432;Database=referencedata_tests;Username=postgres;Password=postgres' \
  dotnet test tests/CloudHealthOffice.ReferenceDataService.Tests/CloudHealthOffice.ReferenceDataService.Tests.csproj
```

The test applies the migration concurrently, checks the canonical tables and lookup index, and verifies that simultaneous duplicate imports produce one import and one idempotent result.
