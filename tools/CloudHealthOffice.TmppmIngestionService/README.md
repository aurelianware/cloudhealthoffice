# CloudHealthOffice.TmppmIngestionService

Automated ingestion pipeline for the Texas Medicaid Provider Procedures Manual (TMPPM). Downloads TMPPM chapters from TMHP, extracts prior authorization rules, and persists them to Cosmos DB as `ConceptMapEntry` overrides for the CHO TerminologyService and CRD Server.

This is the operational moat: CHO tracks TMPPM changes monthly while Cognizant's customers do it by hand.

## Architecture

```
TMHP Website (PDFs)
       │
       ▼
TmhpChapterDownloader ──→ tmppm-data/{edition}/*.pdf (SHA256 tracked)
       │
       ▼
extract_section.py (PyMuPDF) ──→ Section text + CPT/HCPCS codes
       │
       ▼
LlmAssistedParser (Claude API) ──→ Structured TmppmPaRule JSON
       │
       ▼
TmppmRuleStore ──→ Cosmos DB (MongoDB API)
       │
       ├─ tmppm_pa_rules collection (raw extracted rules)
       ├─ tmppm_editions collection (version tracking + SHA256)
       ├─ tmppm_diff_reports collection (month-over-month deltas)
       └─ concept_map_entries collection (ConceptMapEntry overrides)
                │
                ▼
        CRD Server / PriorAuthService (runtime queries)
```

## Prerequisites

- .NET 8 SDK
- Python 3.9+ with PyMuPDF (`pip3 install PyMuPDF`)
- Access to CHO Cosmos DB (MongoDB API)
- Anthropic API key (for LLM-assisted extraction)
- `kubectl` access to the `cloudhealthoffice` namespace (for secrets)

## Setup

### 1. Navigate and restore

```bash
cd ~/cloudhealthoffice/tools/CloudHealthOffice.TmppmIngestionService
dotnet restore
```

### 2. Set up Cosmos DB connection

Pull the connection string from Kubernetes secrets:

```bash
export CHO_TMPPM_MONGODB__CONNECTIONSTRING="$(kubectl get secret mongodb-secret -n cloudhealthoffice -o jsonpath='{.data.connectionString}' | base64 -d)"
```

Or set it directly in `Config/appsettings.json`:

```json
"MongoDB": {
    "ConnectionString": "your-cosmos-connection-string",
    "DatabaseName": "cho_terminology"
}
```

### 3. Set up Anthropic API key

Get your key from [console.anthropic.com](https://console.anthropic.com/) → API Keys → Create Key.

```bash
export ANTHROPIC_API_KEY=sk-ant-api03-your-key-here
```

Or set it in `Config/appsettings.json`:

```json
"Anthropic": {
    "ApiKey": "sk-ant-api03-your-key-here"
}
```

### 4. Install Python dependency

```bash
pip3 install PyMuPDF
```

## Commands

All commands run from the service directory:

```bash
cd ~/cloudhealthoffice/tools/CloudHealthOffice.TmppmIngestionService
```

### Download TMPPM chapters

Downloads the 8 priority TMPPM PDF chapters from TMHP and stores them locally with SHA256 hashes for change detection.

```bash
dotnet run -- download 2026 4
```

Output: `tmppm-data/2026-04/*.pdf`

### Parse a specific section

Test extraction on a single TMPPM section. Uses PyMuPDF for text extraction and regex for code detection.

```bash
python3 Parsers/extract_section.py tmppm-data/2026-04/<chapter>.pdf <section_ref> | python3 -m json.tool
```

Examples:

```bash
# Hypoglossal Nerve Stimulators — CPT 64582, 64583, 64584
python3 Parsers/extract_section.py tmppm-data/2026-04/2_13_med_specs_and_phys_srvs.pdf 9.2.46.14 | python3 -m json.tool

# Bariatric Surgery — extensive clinical criteria (BMI, comorbidities, psych eval)
python3 Parsers/extract_section.py tmppm-data/2026-04/2_13_med_specs_and_phys_srvs.pdf 9.2.8.1 | python3 -m json.tool

# HBOT — CPT 99183 + HCPCS G0277 with session limits per indication
python3 Parsers/extract_section.py tmppm-data/2026-04/2_13_med_specs_and_phys_srvs.pdf 9.2.33.1 | python3 -m json.tool

# Transplants — general PA requirements and contraindications
python3 Parsers/extract_section.py tmppm-data/2026-04/2_13_med_specs_and_phys_srvs.pdf 9.2.51.1 | python3 -m json.tool
```

Output fields:

| Field | Description |
|---|---|
| `sectionRef` | TMPPM section number |
| `found` | Whether the section was located in the PDF |
| `textLength` | Extracted text length in characters |
| `text` | Full section text |
| `cptCodes` | Extracted 5-digit CPT codes |
| `hcpcsCodes` | Extracted HCPCS Level II codes (letter + 4 digits) |
| `dxCodes` | Extracted ICD-10 diagnosis codes |
| `paRequired` | Whether "prior authorization" language was detected |

### Run full ingestion pipeline

Downloads chapters, detects changes, parses PA sections, and persists to Cosmos DB with tenant scoping.

```bash
dotnet run -- ingest 2026 4 --tenant txmco01
```

This will:

1. Download all 8 TMPPM chapters from TMHP
2. Compare SHA256 hashes against the previous edition (if any) to identify changed chapters
3. Parse changed chapters for PA sections using the hybrid regex + LLM strategy
4. Persist extracted rules to the `tmppm_pa_rules` collection
5. Publish rules as `ConceptMapEntry` overrides to the `concept_map_entries` collection
6. Save edition metadata with SHA256 hashes for next month's change detection

## Monthly refresh workflow

When TMHP publishes a new TMPPM edition (typically the last day of each month):

```bash
dotnet run -- ingest 2026 5 --tenant txmco01
```

The pipeline automatically:

- Downloads the new edition
- Compares SHA256 hashes against the previous edition to find changed chapters
- Only re-parses chapters that actually changed
- Generates a diff report (added/modified/removed rules)

## TMPPM chapters covered

| Chapter | PDF | CHO Service |
|---|---|---|
| Vol. 1 Sec 5: Prior Authorizations | `1_05_prior_authorization.pdf` | PriorAuthService, CRD Server |
| Vol. 2: Ambulance Services | `2_01_ambulance_services.pdf` | PriorAuthService |
| Vol. 2: Behavioral Health | `2_02_behavioral_health.pdf` | PriorAuthService, BenefitsEngine |
| Vol. 2: DME & Supplies | `2_06_dme_and_supplies.pdf` | PriorAuthService, BenefitsEngine |
| Vol. 2: Hospital Services | `2_11_inpatient_outpatient_hosp_srvs.pdf` | PriorAuthService, AdjudicationEngine |
| Vol. 2: Med Specialists & Physicians | `2_13_med_specs_and_phys_srvs.pdf` | PriorAuthService, TerminologyService |
| Vol. 2: PT/OT/Speech Therapy | `2_16_pt_ot_st_srvs.pdf` | PriorAuthService, BenefitsEngine |
| Vol. 2: Radiology & Lab | `2_17_radiology_and_lab_srvs.pdf` | PriorAuthService, AdjudicationEngine |

## Cosmos DB collections

| Collection | Purpose |
|---|---|
| `tmppm_pa_rules` | Raw extracted PA rules with clinical criteria, codes, age limits |
| `tmppm_editions` | Edition metadata with SHA256 per chapter for change detection |
| `tmppm_diff_reports` | Month-over-month delta reports |
| `concept_map_entries` | ConceptMapEntry overrides queried by CRD Server at runtime |

## Project structure

```
tools/CloudHealthOffice.TmppmIngestionService/
├── Config/
│   └── appsettings.json              # MongoDB + Anthropic config
├── Loaders/
│   └── TmhpChapterDownloader.cs      # Downloads PDFs, SHA256 tracking
├── Models/
│   └── TmppmModels.cs                # TmppmPaRule, TmppmEdition, ConceptMapEntryOverride
├── Parsers/
│   ├── extract_section.py            # PyMuPDF section extractor (primary)
│   ├── TmppmPdfParser.cs             # C# regex parser (backup)
│   ├── LlmAssistedParser.cs          # Claude API structured extraction
│   └── HybridParser.cs               # Regex-first, LLM-fallback strategy
├── Services/
│   ├── IngestionPipeline.cs           # Main orchestrator
│   └── TmppmRuleStore.cs             # Cosmos DB persistence
├── Program.cs                         # CLI entry point
├── TmppmIngestionService.csproj
├── .gitignore
├── README.md
└── tmppm-data/                        # Downloaded PDFs (gitignored)
    └── 2026-04/
        ├── 1_05_prior_authorization.pdf
        ├── 2_01_ambulance_services.pdf
        ├── 2_02_behavioral_health.pdf
        ├── 2_06_dme_and_supplies.pdf
        ├── 2_11_inpatient_outpatient_hosp_srvs.pdf
        ├── 2_13_med_specs_and_phys_srvs.pdf
        ├── 2_16_pt_ot_st_srvs.pdf
        └── 2_17_radiology_and_lab_srvs.pdf
```

## Validated extractions

These sections have been tested and confirmed working:

| Section | Category | CPT Codes | HCPCS | Text Size | Notes |
|---|---|---|---|---|---|
| §9.2.46.14 | Hypoglossal Nerve Stimulators | 64582, 64583, 64584 | — | 1,355 chars | 64583/64584 do NOT require PA |
| §9.2.8.1 | Bariatric Surgery | — (LLM enrichment needed) | — | 8,793 chars | BMI ≥35 adults, ≥40 pediatric; 13 comorbidities |
| §9.2.33.1 | Hyperbaric Oxygen Therapy | 99183 | G0277 | 1,658 chars | Session limits per indication in table |
| §9.2.51.1 | Organ Transplants (General) | — (category-level) | — | 2,431 chars | Contraindications, 3-day pre/6-week post window |

## Why automated extraction

Generic payer-platform prior-auth modules typically ship without state-specific Medicaid rules, leaving plan staff to track TMPPM monthly changes by hand. This service closes that gap:

- **Automated** — TMPPM changes detected and extracted monthly via SHA256 + PDF parsing
- **Structured** — Rules persisted as FHIR-queryable ConceptMapEntry overrides
- **Tenant-scoped** — Each Texas MCO tenant gets TX-specific rules via `TenantId` + `State=TX` + `IsOverride=true`
- **Scalable** — Same architecture replicates to any state (FL, NY, CA) by adding a new state code
- **LLM-enriched** — Claude API backfills CPT codes for criteria-only sections like bariatric surgery

## Adding a new state (e.g., Florida)

1. Add the state's Medicaid manual chapters to `TmhpChapterDownloader.KnownChapters`
2. Update the PDF URLs to point to the state's publication site
3. Run: `dotnet run -- ingest 2026 4 --tenant <tenant>`
4. The `MapRule.State = "FL"` overrides are automatically scoped

## Deploying as AKS CronJob

For automated monthly execution on the CHO AKS cluster:

```yaml
apiVersion: batch/v1
kind: CronJob
metadata:
  name: tmppm-ingestion
  namespace: cloudhealthoffice
spec:
  schedule: "0 6 1 * *"  # 1st of each month at 6am UTC
  jobTemplate:
    spec:
      template:
        spec:
          containers:
          - name: tmppm-ingestion
            image: cho.azurecr.io/tmppm-ingestion:latest
            command: ["dotnet", "TmppmIngestionService.dll", "ingest", "2026", "4", "--tenant", "txmco01"]
            env:
            - name: CHO_TMPPM_MONGODB__CONNECTIONSTRING
              valueFrom:
                secretKeyRef:
                  name: mongodb-secret
                  key: connectionString
            - name: ANTHROPIC_API_KEY
              valueFrom:
                secretKeyRef:
                  name: cho-secrets
                  key: ANTHROPIC_API_KEY
          restartPolicy: OnFailure
```

## Troubleshooting

**"Section not found"** — The C# PdfPig parser splits section numbers and titles across lines. Use the Python extractor (`extract_section.py`) which handles this correctly via PyMuPDF.

**Cosmos DB timeout / "Connection refused localhost:27017"** — The connection string isn't set. Verify with:
```bash
echo $CHO_TMPPM_MONGODB__CONNECTIONSTRING
```
If empty, re-export from Kubernetes:
```bash
export CHO_TMPPM_MONGODB__CONNECTIONSTRING="$(kubectl get secret mongodb-secret -n cloudhealthoffice -o jsonpath='{.data.connectionString}' | base64 -d)"
```
Or paste it directly into `Config/appsettings.json`.

**No CPT codes extracted** — Some sections (e.g., bariatric surgery §9.2.8.1) describe clinical criteria without listing specific procedure codes inline. The LLM enrichment step backfills these by identifying the standard CPT codes for the service category.

**PyMuPDF not found** — Install with `pip3 install PyMuPDF`.

**Anthropic API errors** — Verify your key is set: `echo $ANTHROPIC_API_KEY`. Check that it starts with `sk-ant-`. API costs are minimal (~$1 for a full handbook extraction).
