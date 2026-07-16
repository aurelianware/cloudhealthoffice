# CHO.TerminologyService

FHIR ConceptMap/$translate and CodeSystem/$lookup terminology microservice for Cloud Health Office.

Translates between SNOMED CT, ICD-10-CM, and CPT code systems, and provides
display metadata for known code systems — the built-in terminology layer that
ships with CHO for CMS-0057 compliance.

## Why This Exists

The CMS-0057 mandate (Jan 2027) requires health plans to support Da Vinci CRD/DTR/PAS
workflows. These workflows operate in FHIR with SNOMED CT coding, but payer CAPS systems
(QNXT, Facets, HealthEdge) run on CPT/ICD. Every health plan needs a translation layer.

This service ships as a built-in CHO microservice — vendor-neutral, FHIR-native,
with plan-specific override support for state Medicaid rules (TMPPM, etc.).

## Architecture

```
┌─────────────────────────────────────────────────┐
│  Data Sources (free via UMLS license)           │
│  NLM SNOMED→ICD-10-CM  │  AMA CPT↔SNOMED      │
│  SNOMED Intl ICD-10/11  │  Plan CSV overrides   │
└──────────────┬──────────────────────────────────┘
               │
┌──────────────▼──────────────────────────────────┐
│  CHO.TerminologyService                         │
│  ┌─────────────────┐  ┌──────────────────────┐  │
│  │ Map Syndication  │→ │ ConceptMap Store     │  │
│  │ Loader (RF2/CSV) │  │ (MongoDB versioned)  │  │
│  └─────────────────┘  └──────────┬───────────┘  │
│  ┌─────────────────┐  ┌──────────▼───────────┐  │
│  │ CodeSystem      │→ │ Code Display Catalog │  │
│  │ seed/import     │  │ (MongoDB versioned)  │  │
│  └─────────────────┘  └──────────┬───────────┘  │
│  ┌─────────────────┐  ┌──────────▼───────────┐  │
│  │ FHIR $translate │← │ Context Rule Engine   │  │
│  │ API endpoint    │  │ (age/gender/state)    │  │
│  └─────────────────┘  └──────────────────────┘  │
│  ┌────────────────────────────────────────────┐  │
│  │ FHIR $lookup API endpoint                  │  │
│  │ CodeSystem catalog → ConceptMap fallback   │  │
│  └────────────────────────────────────────────┘  │
│  ┌────────────────────────────────────────────┐  │
│  │ Plan-Specific Overrides                    │  │
│  │ (TMPPM, Medicaid state rules, local codes) │  │
│  └────────────────────────────────────────────┘  │
└──────────────┬──────────────────────────────────┘
               │
┌──────────────▼──────────────────────────────────┐
│  CHO Consumers                                   │
│  CRD Server  │  PAS Server (278↔FHIR)  │  DTR  │
└─────────────────────────────────────────────────┘
```

## Quick Start

```bash
# Build
dotnet build

# Run locally (requires MongoDB on localhost:27017)
dotnet run

# Docker
docker build -t cho-terminology-service .
docker run -p 5080:5080 \
  -e TerminologyService__MongoConnectionString=mongodb://host.docker.internal:27017 \
  cho-terminology-service
```

## API Usage

### Translate a code

```bash
# Simple: SNOMED → ICD-10-CM
GET /fhir/ConceptMap/$translate?system=http://snomed.info/sct&code=390840006&target=http://hl7.org/fhir/sid/icd-10-cm

# With patient context (age/gender rules)
GET /fhir/ConceptMap/$translate?system=http://snomed.info/sct&code=390840006&target=http://hl7.org/fhir/sid/icd-10-cm&age=5&gender=male&state=TX

# With plan-specific overrides
GET /fhir/ConceptMap/$translate?system=http://snomed.info/sct&code=390840006&target=http://hl7.org/fhir/sid/icd-10-cm&tenantId=tenant-001
```

### Response

```json
{
  "result": true,
  "matches": [
    {
      "equivalence": "equivalent",
      "concept": {
        "system": "http://hl7.org/fhir/sid/icd-10-cm",
        "code": "Z23",
        "display": "Encounter for immunization"
      },
      "isContextResolved": false,
      "isOverride": false,
      "source": "NLM"
    }
  ],
  "mapVersionId": "NLM-SNOMED-ICD10CM-202603-20260325120000",
  "translatedAt": "2026-03-25T12:00:00Z"
}
```

### Batch translate (for 278→FHIR conversion)

```bash
POST /fhir/ConceptMap/$batch-translate
Content-Type: application/json

[
  { "system": "http://snomed.info/sct", "code": "390840006", "targetSystem": "http://hl7.org/fhir/sid/icd-10-cm" },
  { "system": "http://snomed.info/sct", "code": "871751006", "targetSystem": "http://hl7.org/fhir/sid/icd-10-cm" }
]
```

### Look up code display metadata

```bash
GET /fhir/CodeSystem/$lookup?system=http://hl7.org/fhir/sid/icd-10-cm&code=E11.65
```

Response:

```json
{
  "result": true,
  "system": "http://hl7.org/fhir/sid/icd-10-cm",
  "code": "E11.65",
  "display": "Type 2 diabetes mellitus with hyperglycemia",
  "mapVersionId": "mcc-seed-2026",
  "source": "BuiltInIcd10CmCatalog",
  "lookedUpAt": "2026-07-15T20:30:00Z"
}
```

The lookup path checks the code-system display catalog first. If no display is
available there, it falls back to display text present on active ConceptMap
entries. This keeps ICD-10-CM claim displays independent from SNOMED-to-ICD
crosswalk files, whose target displays may be blank in RF2 source data.

The built-in MCC/demo ICD-10-CM seed uses the shared
`SyntheticIcd10CmCatalog` reference data also used by claims-service as its
fail-soft fallback when TerminologyService is unavailable, so the startup seed
and local fallback do not drift independently.

### Load a crosswalk map

```bash
# Load NLM SNOMED→ICD-10-CM RF2 file
curl -X POST "http://localhost:5080/admin/maps/load?format=RF2&mapName=NLM-SNOMED-ICD10CM&version=202603&sourceSystem=http://snomed.info/sct&targetSystem=http://hl7.org/fhir/sid/icd-10-cm" \
  --data-binary @der2_iisssccRefset_ExtendedMapFull_US.txt

# Load plan-specific overrides (CSV)
curl -X POST "http://localhost:5080/admin/maps/load?format=CSV&mapName=Plan-TMPPM-Overrides&version=2026Q1&sourceSystem=http://snomed.info/sct&targetSystem=http://hl7.org/fhir/sid/icd-10-cm&tenantId=tenant-001&isOverride=true" \
  --data-binary @plan_tmppm_overrides.csv
```

### Override CSV format

```csv
source_code,source_display,target_code,target_display,equivalence,priority,rule_type,rule_value
390840006,BCG vaccine,Z23,Encounter for immunization,equivalent,1,,
73583000,Epicondylitis,M77.10,Lateral epicondylitis unspecified,equivalent,1,statespecific,TX
```

## Data Sources

| Source | Format | Cost | Coverage |
|--------|--------|------|----------|
| NLM SNOMED→ICD-10-CM | RF2 | Free (UMLS license) | ~119K concepts |
| SNOMED Intl ICD-10 | RF2 | Free (MLDS) | Clinical findings, events |
| AMA CPT↔SNOMED | CSV/Custom | AMA license required | Procedures |
| Plan overrides | CSV | N/A | Custom per tenant |

**CPT licensing note**: CHO uses a "bring your own license" model for CPT data.
The customer provides their AMA-licensed CPT crosswalk file; CHO's loader ingests it.
CHO does not redistribute AMA-copyrighted content.

## Kubernetes Deployment

```bash
# Deploy to AKS cloudhealthoffice namespace
kubectl apply -f k8s/deployment.yaml

# Verify
kubectl -n cloudhealthoffice get pods -l app=terminology-service
kubectl -n cloudhealthoffice logs -l app=terminology-service --tail=50
```

## Integration Points

- **CRD Server**: Calls $translate to check if a SNOMED-coded procedure requires auth
  in the plan's CPT/ICD-based benefit configuration
- **PAS Server**: Calls $batch-translate during 278↔FHIR conversion to map all codes
  in a prior auth request
- **DTR Engine**: Calls $translate to resolve questionnaire answer codes back to
  the plan's coding system

## License

BSL 1.1 — consistent with all CHO products.
