# Managed FHIR Store → BigQuery → Vertex AI

How CHO's FHIR projections reach a plan's analytics and AI stack on Google
Cloud. Decision record: [ADR 023](../adr/023-managed-fhir-store-projection.md).

## Pipeline

```
claims-service ── claims.finalized.v1 ──▶ fhir-service (ManagedFhirStoreProjectionConsumer)
       ▲                                         │
       └──── GET fhir/ExplanationOfBenefit/{id} ─┘
                                                 │ PUT ExplanationOfBenefit/{id}
                                                 ▼
                              Cloud Healthcare API FHIR store (R4)
                               │            │                 │
                   BigQuery streaming   Pub/Sub changes   :export → Cloud Storage (NDJSON)
                               ▼
                     BigQuery dataset cho_fhir (SQL-on-FHIR, ANALYTICS_V2)
                               │
                 BigQuery ML ──┴── remote model ──▶ Vertex AI (Gemini)
```

1. **Adjudication finalizes a claim.** claims-service emits
   `claims.finalized.v1`, carrying the claim's version chain id.
2. **fhir-service publishes the EOB.** Its consumer (own consumer group, so it
   never competes with accumulator-service) fetches the EOB claims-service
   projects and PUTs it into the FHIR store at the chain id. Only the tenant in
   `ManagedFhirStore:TenantId` is published.
3. **The store streams to BigQuery.** Each write appears as a row in the
   `ExplanationOfBenefit` table of the `cho_fhir` dataset within seconds.
4. **Analytics and AI run in BigQuery.** SQL, BigQuery ML, and remote models
   backed by Vertex AI operate on those tables in the plan's own project.

Nothing in steps 3 and 4 is CHO code: it is store configuration in
`infrastructure/gcp/fhir-store.tf`.

## What is published today

| Resource | Source | Trigger | Status |
| --- | --- | --- | --- |
| ExplanationOfBenefit | claims-service projection | `claims.finalized.v1` | Implemented |
| Patient | member-service projection | member events | Planned |
| Coverage | coverage-service projection | enrollment events | Planned |
| Practitioner, Organization | provider-service projection | provider events | Planned |

Until Patient and Coverage are published, BigQuery joins use the reference
ids on the EOB (`patient`, `insurance.coverage`) rather than resolved rows.

## Turning it on (GCP demo)

1. Apply Terraform with the store (and optionally Vertex AI):

   ```bash
   cd infrastructure/gcp
   terraform apply -var="project_id=YOUR_PROJECT" \
     -var="enable_managed_fhir_store=true" -var="enable_vertex_ai=true"
   ```

2. Set the GitHub variables from the outputs:

   | Variable | Value |
   | --- | --- |
   | `MANAGED_FHIR_STORE` | `terraform output -raw managed_fhir_store_id` |
   | `MANAGED_FHIR_STORE_SERVICE_ACCOUNT` | `terraform output -raw managed_fhir_store_service_account` |
   | `MANAGED_FHIR_STORE_TENANT` | the CHO tenant to publish (default `demo`) |

3. Deploy (`deploy-gcp-gke.yml`) with Kafka on. fhir-service runs as
   `fhir-service-sa`, bound to the `cho-fhir-store` Google service account.

4. Adjudicate a claim, then check the store and BigQuery:

   ```bash
   STORE=$(terraform output -raw managed_fhir_store_id)
   curl -s -H "Authorization: Bearer $(gcloud auth print-access-token)" \
     "https://healthcare.googleapis.com/v1/$STORE/fhir/ExplanationOfBenefit?_count=5" | jq '.total'

   bq query --use_legacy_sql=false \
     'SELECT COUNT(*) FROM `YOUR_PROJECT.cho_fhir.ExplanationOfBenefit`'
   ```

Configuration fhir-service reads (set by the deploy):

```
ManagedFhirStore:Provider          None | GcpHealthcare | InMemory (Development)
ManagedFhirStore:TenantId          the one tenant this store holds (required when on)
ManagedFhirStore:Gcp:ProjectId     ManagedFhirStore:Gcp:Location
ManagedFhirStore:Gcp:DatasetId     ManagedFhirStore:Gcp:FhirStoreId
Kafka:ManagedFhirStore:GroupId     optional; default fhir-service.managed-fhir-store
```

## Querying the streamed EOBs

The streaming tables follow Google's SQL-on-FHIR analytics schema: one table
per resource type, columns shaped like the FHIR resource, repeated elements as
arrays. Confirm column names with `bq show --schema` before building on them;
the schema can grow as resources gain elements.

Recent adjudication outcomes:

```sql
SELECT
  id,
  status,
  outcome,
  created,
  meta.lastUpdated AS published_at
FROM `YOUR_PROJECT.cho_fhir.ExplanationOfBenefit`
ORDER BY meta.lastUpdated DESC
LIMIT 50;
```

Totals by category (submitted, benefit, member liability) across claims:

```sql
SELECT
  coding.code AS total_category,
  SUM(t.amount.value) AS amount
FROM `YOUR_PROJECT.cho_fhir.ExplanationOfBenefit` AS eob,
  UNNEST(eob.total) AS t,
  UNNEST(t.category.coding) AS coding
GROUP BY total_category
ORDER BY amount DESC;
```

An adjusted claim is written again at the same id. Depending on the streaming
configuration a table can hold more than one row per `id`; to analyze only the
current version of each EOB, keep the latest row per `id` by
`meta.lastUpdated`.

## Vertex AI over the FHIR tables

With `enable_vertex_ai = true`, Terraform creates a BigQuery Cloud resource
connection (`cho-vertex-ai`) whose service account holds
`roles/aiplatform.user`. BigQuery ML can then call Vertex AI models from SQL.

Register a remote model once (use a current Gemini endpoint name from the
Vertex AI model garden):

```sql
CREATE OR REPLACE MODEL `YOUR_PROJECT.cho_fhir.gemini`
  REMOTE WITH CONNECTION `YOUR_PROJECT.us.cho-vertex-ai`
  OPTIONS (ENDPOINT = 'GEMINI_ENDPOINT_NAME');
```

Plain-language explanations of denied claims, for member service staff:

```sql
SELECT id, ml_generate_text_llm_result AS explanation
FROM ML.GENERATE_TEXT(
  MODEL `YOUR_PROJECT.cho_fhir.gemini`,
  (
    SELECT
      id,
      CONCAT(
        'Explain in two sentences, for a health plan member, why this claim ',
        'was processed the way it was. ExplanationOfBenefit: ',
        TO_JSON_STRING(eob)) AS prompt
    FROM `YOUR_PROJECT.cho_fhir.ExplanationOfBenefit` AS eob
    WHERE outcome IN ('error', 'partial')
    LIMIT 20
  ),
  STRUCT(256 AS max_output_tokens, TRUE AS flatten_json_output));
```

Models that never leave BigQuery work without the connection: for example
`CREATE MODEL ... OPTIONS (model_type = 'kmeans')` over per-claim features
(totals, item counts, service dates) to cluster unusual billing patterns, or
`model_type = 'boosted_tree_classifier'` to predict outcomes on historical
EOBs. For notebooks and custom training, Vertex AI Workbench reads the same
tables.

**Data handling.** The demo stores synthetic members only. With real member
data, the FHIR store and BigQuery dataset are PHI stores (BAA, CMEK, VPC
Service Controls), and a prompt built from an EOB is a disclosure to the model
service: confirm the plan's agreements cover the model before running
`ML.GENERATE_TEXT` on real data. A de-identified copy of the store
(`gcloud healthcare fhir-stores deidentify`) is the usual source for
exploratory ML.

## Operations

- **Retries.** A claims-service outage or a store failure (throttling, 5xx)
  retries the same message every few seconds; nothing is skipped.
- **Rejections.** A resource the store refuses (for example, after enabling an
  ImplementationGuide that the projection does not yet meet) is logged as
  `Managed FHIR store ... rejected ...` with issue codes and committed. Alert
  on that log line before production use.
- **Backfill.** The consumer group starts from the earliest retained offset,
  so the first deployment publishes the claims still in the topic. Older
  claims need a replay of `claims.finalized.v1` or a bulk load.
- **Export.** `IManagedFhirStore.StartExportAsync` exports the store to the
  `managed_fhir_export_bucket` output as NDJSON; exports expire after
  `managed_fhir_export_retention_days`.
