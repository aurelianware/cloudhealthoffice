CREATE TABLE IF NOT EXISTS canonical_reference_codes (
    storage_key VARCHAR(700) PRIMARY KEY,
    id VARCHAR(200) NOT NULL,
    tenant_id VARCHAR(200),
    code_system VARCHAR(100) NOT NULL,
    code_system_uri VARCHAR(500),
    code VARCHAR(100) NOT NULL,
    version VARCHAR(100),
    display VARCHAR(500),
    description TEXT,
    category VARCHAR(200),
    effective_from DATE NOT NULL,
    effective_to DATE,
    active BOOLEAN NOT NULL DEFAULT TRUE,
    source_id VARCHAR(200) NOT NULL,
    source_version VARCHAR(100) NOT NULL,
    license_classification VARCHAR(40) NOT NULL,
    exposure_classification VARCHAR(40) NOT NULL,
    imported_at TIMESTAMPTZ NOT NULL,
    checksum VARCHAR(200) NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_canonical_reference_lookup
    ON canonical_reference_codes(code_system, code, effective_from);
CREATE INDEX IF NOT EXISTS idx_canonical_reference_tenant
    ON canonical_reference_codes(tenant_id);
CREATE INDEX IF NOT EXISTS idx_canonical_reference_source
    ON canonical_reference_codes(source_id, source_version, checksum);

CREATE TABLE IF NOT EXISTS canonical_reference_data_imports (
    import_key VARCHAR(600) PRIMARY KEY,
    source_id VARCHAR(200) NOT NULL,
    source_version VARCHAR(100) NOT NULL,
    checksum VARCHAR(200) NOT NULL,
    imported_at TIMESTAMPTZ NOT NULL,
    record_count INTEGER NOT NULL,
    CONSTRAINT uq_canonical_reference_import UNIQUE (source_id, source_version, checksum)
);
