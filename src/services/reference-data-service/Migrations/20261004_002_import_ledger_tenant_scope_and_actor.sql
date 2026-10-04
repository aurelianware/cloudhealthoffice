-- The import ledger decided "already imported" by (source_id, source_version,
-- checksum) alone, so one tenant's batch was skipped because another tenant had
-- imported the same batch. Scope the ledger by tenant (global imports use the
-- scope 'global') and record who imported each batch.

ALTER TABLE canonical_reference_data_imports
    ADD COLUMN IF NOT EXISTS tenant_scope VARCHAR(200);
ALTER TABLE canonical_reference_data_imports
    ADD COLUMN IF NOT EXISTS imported_by VARCHAR(200);

-- Existing rows: the scope is what the import wrote (codes carry the batch's
-- source, version and checksum): its tenant, 'global', or both as
-- 'global+<tenant>' (the same form ReferenceDataImportScope.Of produces).
UPDATE canonical_reference_data_imports AS i
SET tenant_scope = COALESCE(
        (SELECT string_agg(x.scope, '+' ORDER BY (x.scope <> 'global'), x.scope COLLATE "C")
         FROM (SELECT DISTINCT COALESCE(c.tenant_id, 'global') AS scope
               FROM canonical_reference_codes AS c
               WHERE c.source_id = i.source_id
                 AND c.source_version = i.source_version
                 AND c.checksum = i.checksum) AS x),
        'global')
WHERE i.tenant_scope IS NULL;

-- Ledger keys now start with the scope (this migration runs exactly once).
UPDATE canonical_reference_data_imports
SET import_key = tenant_scope || '|' || import_key;

-- The default only serves an older image after a rollback (it does not write
-- the column); this version always writes the scope.
ALTER TABLE canonical_reference_data_imports
    ALTER COLUMN tenant_scope SET DEFAULT 'global';
ALTER TABLE canonical_reference_data_imports
    ALTER COLUMN tenant_scope SET NOT NULL;

ALTER TABLE canonical_reference_data_imports
    DROP CONSTRAINT IF EXISTS uq_canonical_reference_import;
ALTER TABLE canonical_reference_data_imports
    DROP CONSTRAINT IF EXISTS uq_canonical_reference_import_scope;
ALTER TABLE canonical_reference_data_imports
    ADD CONSTRAINT uq_canonical_reference_import_scope
    UNIQUE (tenant_scope, source_id, source_version, checksum);
