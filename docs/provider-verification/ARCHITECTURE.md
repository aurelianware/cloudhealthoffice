# CloudHealthOffice.ProviderVerificationService

**Service #29** — Multi-source provider verification and integrity scoring engine.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                    Blazor Portal / REST API                        │
│  Provider Profile Card │ Network Mgmt │ Claims Pre-Check │ Batch  │
└──────────────────────────┬──────────────────────────────────────────┘
                           │
              ┌────────────▼────────────┐
              │  ProviderVerification   │
              │     Orchestrator        │
              │  (parallel fan-out)     │
              └──┬───┬───┬───┬───┬───┬─┘
                 │   │   │   │   │   │
    ┌────────────┘   │   │   │   │   └────────────┐
    ▼                ▼   ▼   ▼   ▼                ▼
┌────────┐  ┌──────┐ ┌─────┐ ┌───────┐ ┌───────┐ ┌──────┐
│ NPPES  │  │ LEIE │ │PECOS│ │ Open  │ │Medcr  │ │ FSMB │
│Registry│  │ SAM  │ │     │ │Paymts │ │Utiliz │ │(paid)│
│  API   │  │ .gov │ │     │ │       │ │       │ │      │
└───┬────┘  └──┬───┘ └──┬──┘ └───┬───┘ └───┬───┘ └──┬───┘
    │          │        │        │         │        │
    ▼          ▼        ▼        ▼         ▼        ▼
┌─────────────────────────────────────────────────────────┐
│         IntegrityScoreCalculator                        │
│   NPI ─── Exclusion ─── Medicare ─── License ─── COI   │
│   30w      30w           15w          15w        10w    │
│                                                         │
│   → Composite Score (0-100) + Rating + Flags            │
└─────────────────────────────────────────────────────────┘
```

## Project Structure

```
src/
├── CloudHealthOffice.ProviderVerificationEngine/    # Class library (the "engine")
│   ├── Models/
│   │   └── Entities.cs                              # All entity models
│   ├── DataSources/
│   │   ├── IAdapters.cs                             # Adapter interfaces
│   │   └── Nppes/
│   │       └── NppesHttpAdapter.cs                  # NPPES API implementation
│   ├── Scoring/
│   │   └── IntegrityScoreCalculator.cs              # Composite scoring
│   └── ProviderVerificationOrchestrator.cs          # Core orchestrator
│
├── CloudHealthOffice.ProviderVerificationService/   # ASP.NET microservice
│   ├── Program.cs                                   # Minimal API + DI
│   └── appsettings.json                             # Configuration
│
└── (future)
    ├── CloudHealthOffice.ProviderVerificationService.Workers/
    │   ├── NppesBulkSyncWorker.cs                   # Weekly NPPES V2 file sync
    │   ├── LeieSyncWorker.cs                        # Monthly LEIE download
    │   ├── PecosSyncWorker.cs                       # Monthly PECOS CSV sync
    │   └── OpenPaymentsSyncWorker.cs                # Annual Open Payments sync
    │
    └── CloudHealthOffice.ProviderVerificationEngine.Tests/
        ├── Scoring/IntegrityScoreCalculatorTests.cs
        └── DataSources/NppesHttpAdapterTests.cs
```

## Data Sources Reference

| Source | Cost | Auth | Freshness | Data |
|--------|------|------|-----------|------|
| **NPPES Registry API** | Free | None | Daily | NPI, name, taxonomy, addresses, endpoints |
| **NPPES Bulk Files (V2)** | Free | None | Weekly | Full NPI database (~8M records) |
| **NLM Clinical Tables** | Free | None | Periodic | NUCC taxonomy → Medicare specialty crosswalk |
| **OIG LEIE** | Free | None | Monthly | Excluded individuals/entities |
| **SAM.gov Exclusions** | Free | API key (free reg) | Daily | Federal debarment/exclusions |
| **CMS Preclusion List** | Free | None | Monthly | Medicare Advantage/Part D preclusions |
| **PECOS Public Enrollment** | Free | None | Monthly | Medicare FFS enrollment status |
| **CMS Open Payments** | Free | None | Annual (+refresh) | Industry payments to providers |
| **Medicare Utilization** | Free | None | Annual | Provider-level claims/payment data |
| **Part D Prescriber** | Free | None | Annual | Drug prescribing patterns, opioid flags |
| **FSMB PDC** | **Paid** | OAuth2 | Monthly | State licenses, disciplinary actions, DEA |
| **State License Boards** | Varies | Varies | Real-time | Direct license verification (56 jurisdictions) |
| **ABMS Board Cert** | **Paid** | API key | Real-time | Board certification status |

## Implementation Roadmap

### Phase 1: NPPES + Scoring (PR #1 — ship immediately)
- [x] Entity models
- [x] NPPES HTTP adapter with Luhn validation
- [x] Integrity score calculator (NPI dimension only)
- [x] Orchestrator with tier-based verification
- [x] Minimal API endpoints
- [ ] NBomber load test (p99 < 500ms for single NPI lookup)
- [ ] Dockerfile + Helm chart (mirrors existing CHO services)
- [ ] README badge: NPPES API uptime

### Phase 2: Exclusion Screening (PR #2)
- [ ] LEIE downloadable DB parser (CSV → PostgreSQL)
- [ ] Monthly LEIE sync background worker
- [ ] SAM.gov API adapter (requires free API key registration)
- [ ] Fuzzy name matching for LEIE (Levenshtein + Soundex)
- [ ] Exclusion dimension scoring
- [ ] Network-wide batch screening endpoint

### Phase 3: PECOS + Open Payments (PR #3)
- [ ] PECOS bulk CSV sync (data.cms.gov download)
- [ ] Open Payments SODA API adapter
- [ ] Medicare enrollment dimension scoring
- [ ] Conflict-of-interest dimension scoring
- [ ] Provider profile enrichment in Blazor portal

### Phase 4: Medicare Utilization (PR #4)
- [ ] Physician & Other Supplier utilization adapter
- [ ] Part D prescriber data adapter
- [ ] Opioid prescribing rate flagging
- [ ] Utilization pattern analytics in portal

### Phase 5: FSMB Premium Tier (PR #5 — requires FSMB contract)
- [ ] FSMB OAuth2 client
- [ ] License verification adapter
- [ ] Disciplinary action tracking
- [ ] DEA registration check
- [ ] Board certification via ABMS (separate contract)
- [ ] License dimension scoring

### Phase 6: Claims Integration
- [ ] Pre-adjudication integrity check hook
  (ClaimsEngine calls /api/v1/providers/{npi}/integrity-score
   before processing — if Rating == Blocked, auto-deny with
   CARC 16 / RARC N591)
- [ ] ProviderContract entity enrichment
  (link ProviderVerificationRecord to ProviderContract master record)
- [ ] Network adequacy reporting
  (aggregate integrity scores across contracted network)

## Integration with Existing Cloud Health Office Services

### ProviderContract (PRs #557-566)
The ProviderVerificationRecord links to the existing ProviderContract
master record via NPI. When a provider is verified, the integrity score
is cached on the contract record for fast claims-time lookup. Contract
renewal workflows can require re-verification as a gate.

### TerminologyService
The NLM Clinical Tables crosswalk enriches NPPES taxonomy codes into
Medicare provider types — this feeds into the TerminologyService's
existing SNOMED/ICD-10/CPT infrastructure. Provider specialty resolution
during claims pricing can use verified taxonomy data rather than
trusting the claim's submitted taxonomy.

### FeeScheduleEngine / RateResolutionService
The Pricing API can incorporate provider integrity as a routing signal.
For example: providers with verified Medicare enrollment get Medicare
fee schedule rates; providers without PECOS enrollment get flagged
for manual rate assignment.

### ClaimsEngine (future)
Pre-adjudication check: if a rendering provider's integrity score is
Blocked (LEIE exclusion), the claim is auto-denied per 42 CFR 1001.
This is a CMS audit requirement that most health plans handle manually.

## Competitive Positioning

**What QNXT/Facets/HealthEdge do today**: Manual credentialing workflows.
Provider data lives in a static provider file maintained by operations
staff. No real-time verification. No composite scoring. LEIE checks are
a separate bolt-on (often Verisys or ProviderTrust at $50K+/year).

**What CHO does**: Automated, multi-source provider verification as a
native microservice. Real-time NPPES validation on every provider
interaction. Monthly exclusion screening with zero manual effort.
Composite integrity scoring that feeds directly into claims adjudication,
network management, and compliance reporting. All built into the
platform — no separate SaaS contract needed.

**Gartner checkbox**: Provider Data Management ✓, Exclusion Monitoring ✓,
Automated Credentialing ✓, Compliance Analytics ✓.

## API Quick Reference

```bash
# Full verification (Standard tier)
GET /api/v1/providers/1234567893/verify

# Full verification (Premium tier with FSMB)
GET /api/v1/providers/1234567893/verify?tier=Premium

# Lightweight integrity score (for claims pre-check)
GET /api/v1/providers/1234567893/integrity-score

# Direct NPPES lookup
GET /api/v1/providers/1234567893/nppes

# NPPES search
GET /api/v1/providers/search/nppes?lastName=Smith&state=TX&limit=20

# Batch verification (up to 100 NPIs)
POST /api/v1/providers/verify/batch
{
  "npis": ["1234567893", "1234567894", ...],
  "tier": "Standard"
}
```
