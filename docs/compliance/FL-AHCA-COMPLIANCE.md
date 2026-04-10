# Florida AHCA Compliance Guide — SMMC 3.0 / FMMIS

> **Status:** Implemented  
> **Regulatory basis:** FL Stat. §627.6131 · AHCA SMMC 3.0 Contract · FMMIS Companion Guides  
> **Affects:** Medicaid MCOs operating under Florida AHCA SMMC 3.0 contracts (2025–2030)

---

## Overview

Florida's Agency for Health Care Administration (AHCA) administers the Statewide Medicaid Managed Care (SMMC) program. On February 1, 2025, AHCA launched SMMC 3.0 — new 5-year MCO contracts with enhanced requirements. Cloud Health Office supports FL Medicaid MCOs operating under these contracts through four compliance modules.

---

## Regulatory Requirements Implemented

| Requirement | Regulation | Implementation | Status |
|-------------|-----------|----------------|--------|
| Encounter Data Submission (60-day window) | AHCA SMMC MCO Contract §X | EncounterSubmissionService | ✅ Implemented |
| FMMIS 837P/837I Companion Guide | AHCA FMMIS EDI Spec | FmmisClaimTransformer | ✅ Implemented |
| FL Subscriber-as-Primary rule | FMMIS Companion Guide §1.2 | FmmisClaimTransformer | ✅ Implemented |
| FL Medicaid Provider ID (REF*1D) | FMMIS Companion Guide §1.2 | FmmisClaimTransformer | ✅ Implemented |
| Prompt Pay — Electronic (35 days) | FL Stat. §627.6131 | TenantComplianceConfig | ✅ Config |
| Prompt Pay — Paper (45 days) | FL Stat. §627.6131 | TenantComplianceConfig | ✅ Config |
| Prior Auth — Urgent (72 hrs) | CMS-0057-F + FL AHCA | AuthorizationService | ✅ Implemented |
| Prior Auth — Standard (5 days) | FL AHCA SMMC Contract | TenantComplianceConfig | ✅ Config |
| MPIP Enhanced Rates (106.3% Medicare) | FL AHCA SMMC 3.0 MPIP | MpipRateService | ✅ Implemented |
| MPIP Auto-Qualify Specialists (under-21) | FL AHCA MPIP §3 | MpipRateService | ✅ Implemented |

---

## Architecture — Four Compliance Modules

### Module 1: FL Tenant Config Schema (`services/reference-data-service`)

- `TenantComplianceConfig` — holds FL-specific deadlines, FMMIS credentials, MPIP flag
- `StateComplianceConfig` — embedded value object with all FL regulatory timeline values
- API: `GET/PUT /api/compliance-config/{tenantId}`
- Seed data: `Data/seed/fl-ahca-config.json`

### Module 2: FMMIS EDI Adapter (`services/claims-service/EDI/Florida/`)

- `FmmisCompanionGuide` — static class with FMMIS constants and validation
- `FmmisClaimTransformer` — applies FL-specific 837 deviations:
  - All enrollees are primary subscribers (no 2000C dependent loop)
  - FL Medicaid Provider Number added to 2010AA loop as REF*1D
  - ISA08 = "FMMIS"
- `FmmisFileBuilder` — assembles batch files: `FMMIS.{SubmitterId}.{yyyyMMdd_HHmmss}.dat`

### Module 3: Encounter Submission Service (`services/encounter-submission-service`)

- Background worker polls every 4 hours for pending encounter submissions
- Calculates 60-day submission deadline from claim adjudication date
- Fires `encounter-deadline-warning` Kafka event when deadline within 7 days
- Stages batch files to Azure Blob; SFTP transport to FMMIS is a follow-on task
- Processes 999 acknowledgment responses and updates submission status
- Auto-creates submission records when claims-service publishes `adjudication-completed` event

### Module 4: SMMC 3.0 MPIP Rate Engine (`services/provider-service`)

- `MpipProviderQualification` — tracks provider qualification per AHCA fiscal year (Oct–Sep)
- `MpipRateService` — applies 106.3% Medicare rate multiplier to allowed amounts:
  - Specialist + member age < 21 → auto-qualify → 1.063x
  - PCP/OB/GYN + AHCA-qualified + member age < 21 → 1.063x
  - Member age ≥ 21 → always 1.0x
- Bulk import endpoint for annual AHCA qualified provider list (Oct 1)
- Multiplier stored on `ClaimLine.MpipMultiplierApplied` for audit trail

---

## FL MCO Tenant Configuration

Example `TenantComplianceConfig` for a FL Medicaid MCO:

```json
{
  "tenantId": "fl-mco-example",
  "stateCode": "FL",
  "fmmisSubmitterId": "SUBMITTER_ID",
  "fmmisInterchangeSenderId": "SENDER_ID",
  "mpipEnabled": true,
  "stateConfig": {
    "promptPayElectronicDays": 35,
    "promptPayPaperDays": 45,
    "promptPayPenaltyRateAnnual": 0.10,
    "claimAcknowledgmentDays": 0,
    "priorAuthUrgentHours": 72,
    "priorAuthStandardDays": 5,
    "appealStandardDays": 30,
    "appealExpeditedHours": 72,
    "encounterSubmissionDays": 60
  }
}
```

> **Note:** Replace `SUBMITTER_ID` and `SENDER_ID` with the values assigned by AHCA during MCO contract setup.

---

## FMMIS Encounter Submission Flow

```
Claim Adjudicated (Approved/Paid)
        │
        ▼
AdjudicationCompletedConsumer (Kafka)
        │
        ▼
EncounterSubmissionService.CreateSubmissionRecord()
  SubmissionDeadline = AdjudicatedAt + 60 days
  Status = Pending
        │
        ▼  (every 4 hours — EncounterSubmissionWorker)
        ├── Deadline within 7 days? → Status = DeadlineWarning
        │                             Publish encounter-deadline-warning event
        │
        └── Deadline within 48 hours? → BuildFmmisSubmissionBatch()
                  │
                  ▼
            FmmisClaimTransformer.Transform()
            FmmisFileBuilder.Build()
            Write to Azure Blob staging
            Status = Batched → Submitted
                  │
                  ▼
            ProcessAcknowledgment() (999 response)
            Status = Accepted | PartialAccept | Rejected
```

---

## Target FL Medicaid MCOs (SMMC 3.0)

The following plans operate under AHCA's SMMC 3.0 contracts (2025–2030) and are potential Cloud Health Office customers:

- Sunshine Health (Centene)
- Molina Healthcare of Florida
- Humana Medical Plan (Medicaid)
- Simply Healthcare Plans (Anthem)
- Staywell Health Plan of Florida (WellCare/Centene)
- Florida Community Care (LTC)

---

## Known Gaps / Future Work

- SFTP transport to FMMIS (currently stages to Azure Blob — Q3 2026)
- AHCA IMR (Independent Medical Review) outbound integration — Q4 2026
- FL AHCA value-based payment (VBP) reporting — 2027 roadmap
- FL-specific COB (Medicaid as payer of last resort) — Q3 2026

---

## References

- [AHCA SMMC 3.0 Program](https://ahca.myflorida.com/medicaid/statewide-medicaid-managed-care)
- FMMIS 837P Companion Guide (Molina FL reference) — publicly available from AHCA
- [FL Statute §627.6131 (Prompt Pay)](https://www.flsenate.gov/Laws/Statutes/2023/627.6131)
- [FL AHCA MPIP 2025-2026](https://ahca.myflorida.com/medicaid/statewide-medicaid-managed-care/mma-physician-incentive-program-mpip/agency-mpip-model-2025-2026)
- [CMS-0057-F Prior Authorization Rule](https://www.cms.gov/newsroom/fact-sheets/cms-interoperability-and-prior-authorization-final-rule-cms-0057-f)
