# Florida AHCA / SMMC 3.0 Compliance

> **Status:** Generally Available — PR [#634](https://github.com/aurelianware/cloudhealthoffice/pull/634)  
> **Regulatory basis:** FL §627.6131 · AHCA SMMC 3.0 Contract · FL FMMIS Companion Guides  
> **Affects:** Medicaid MCOs operating under Florida AHCA contracts

---

## Overview

Cloud Health Office ships native support for Florida's Agency for Health Care Administration (AHCA) regulatory requirements, including the February 2025 **SMMC 3.0** program rollout. Florida is one of the four highest-priority states for Medicaid managed care compliance and represents the largest immediate opportunity for health plans modernizing off legacy systems.

This document covers the four areas of FL AHCA compliance built into the platform and how to configure them for your tenant.

---

## Background: SMMC 3.0

On February 1, 2025, AHCA implemented new Statewide Medicaid Managed Care (SMMC) 3.0 contracts with all Florida health and dental plans. These five-year contracts (2025-2030) introduced several new requirements:

- Mandatory assignment of all Florida Medicaid members to a managed care plan
- Enhanced prior authorization reform timelines
- The Managed Medical Assistance Physician Incentive Program (MPIP) - 106.3% of Medicare rates for qualifying services to members under age 21
- Stricter encounter data submission requirements via FMMIS (Florida Medicaid Management Information System)
- Continuity of care obligations during plan transitions

For health plans running legacy claims administration systems, SMMC 3.0 compliance requires significant platform changes. Cloud Health Office addresses all of these out of the box.

---

## What's Included

### 1. FL Tenant Compliance Configuration

Each tenant on Cloud Health Office has a `TenantComplianceConfig` document that stores state-specific regulatory parameters. For FL MCO tenants, this includes:

| Parameter | FL Default | Regulatory Source |
|-----------|-----------|-------------------|
| Prompt pay - electronic claims | 35 days | FL S627.6131 |
| Prompt pay - paper claims | 45 days | FL S627.6131 |
| Late payment penalty rate | 10% annualized | FL S627.6131 |
| Prior auth - urgent | 72 hours | CMS-0057-F / AHCA |
| Prior auth - standard | 5 business days | AHCA SMMC contract |
| Appeal - standard | 30 days | FL MCO contract |
| Appeal - expedited | 72 hours | FL MCO contract |
| Encounter submission window | 60 days from adjudication | AHCA MCO contract |
| MPIP enhanced rate | 1.063x Medicare rate | FL AHCA MPIP 2025-2026 |

**Configure your tenant:**

```http
PUT /api/compliance-config/{tenantId}
Content-Type: application/json

{
  "tenantId": "your-fl-mco-tenant",
  "stateCode": "FL",
  "fmmisSubmitterId": "YOUR_FMMIS_ID",
  "mpipEnabled": true,
  "stateConfig": {
    "promptPayElectronicDays": 35,
    "promptPayPaperDays": 45,
    "encounterSubmissionDays": 60,
    ...
  }
}
```

Retrieve your current config:

```http
GET /api/compliance-config/{tenantId}
GET /api/compliance-config/{tenantId}/state   # state params only
```

---

### 2. FMMIS EDI Adapter (FL 837P / 837I)

Florida AHCA requires MCOs to submit encounter data to the Florida Medicaid Management Information System (FMMIS) using 837P and 837I transactions with FL-specific companion guide requirements. These deviate from the standard X12 spec in three important ways:

**Subscriber rule:** All Florida Medicaid enrollees are primary subscribers. The 2000B loop subscriber must always be the member themselves - no 2000C dependent loop is generated.

**FL Medicaid Provider Number:** The 2010AA Billing Provider loop must include a `REF*1D` segment containing the provider's Florida Medicaid Provider Number (distinct from NPI).

**FMMIS receiver ID:** `ISA08` must be `FMMIS` rather than the standard payer NPI.

**File naming:** Batch files must follow the convention `FMMIS.{SubmitterId}.{yyyyMMdd_HHmmss}.dat`.

Cloud Health Office applies all four rules automatically when a claim belongs to a tenant with `StateCode = "FL"` and `GenerateFmmisTransaction = true`. The underlying X12 EDI pipeline is shared; the FL adapter is a transformation layer on top.

To retrieve the FMMIS-compliant 837 for an adjudicated claim:

```http
GET /api/claims/{tenantId}/{claimId}/edi/fmmis
```

---

### 3. Encounter Submission Service

Florida AHCA MCO contracts require encounter data to be submitted to FMMIS within **60 days of adjudication**. Cloud Health Office manages this automatically via the `encounter-submission-service`.

**How it works:**

1. When a claim is adjudicated (Approved or Paid), a Kafka event fires.
2. The `AdjudicationCompletedConsumer` creates an `EncounterSubmission` record with `SubmissionDeadline = adjudicatedAt + 60 days`.
3. A background worker runs every 4 hours (configurable), scanning for upcoming deadlines.
4. Claims within 7 days of their deadline are flagged as `DeadlineWarning` and a Kafka event is published to alert your operations team.
5. Batch FMMIS files are assembled and staged to Azure Blob (SFTP transport is configurable).
6. 999 acknowledgment responses from AHCA update submission statuses automatically.

**Operational endpoints:**

```http
# Pending submissions for your tenant, ordered by deadline
GET /api/encounters/{tenantId}/pending

# Dashboard summary (counts by status)
GET /api/encounters/{tenantId}/summary

# Submissions within 7 days of deadline
GET /api/encounters/{tenantId}/deadline-warnings

# Process a 999 acknowledgment from AHCA
POST /api/encounters/{tenantId}/acknowledge

# Manually retry a rejected submission
POST /api/encounters/{tenantId}/retry/{submissionId}
```

**Submission statuses:**

| Status | Meaning |
|--------|---------|
| `Pending` | Created, waiting for batch window |
| `Batched` | Included in a staged FMMIS file |
| `Submitted` | File transmitted to AHCA |
| `Accepted` | 999 AK9*A received |
| `PartialAccept` | 999 AK9*E received - some transactions rejected |
| `Rejected` | 999 AK9*R received - full batch rejected |
| `DeadlineWarning` | Deadline <= 7 days, not yet submitted |

---

### 4. SMMC 3.0 MPIP Enhanced Rates

Florida's Managed Medical Assistance Physician Incentive Program (MPIP) requires MCOs to reimburse qualifying providers at **106.3% of the Medicare Physician Fee Schedule** for services rendered to members under age 21.

**Qualification rules:**

- **Specialist physicians** auto-qualify for all services to members under 21 - no performance benchmarking required.
- **Primary Care Physicians and OB/GYNs** must meet AHCA performance benchmarks to qualify. AHCA publishes the qualified provider list each October 1.
- Members **age 21 or older** never receive MPIP enhancement regardless of provider type.

**How it's applied:**

During adjudication, after the base allowed amount is calculated from the fee schedule, Cloud Health Office calls the MPIP rate service. If the member was under 21 at the service date and the provider qualifies (auto or benchmarked), the allowed amount is multiplied by 1.063. The multiplier is recorded on `ClaimLine.MpipMultiplierApplied` for audit purposes.

```json
// Example ClaimLine after adjudication with MPIP applied
{
  "procedureCode": "99213",
  "baseAllowedAmount": 110.00,
  "mpipMultiplierApplied": 1.063,
  "allowedAmount": 116.93,
  "paidAmount": 116.93
}
```

**Managing provider qualifications:**

```http
# List qualified providers for current FL fiscal year
GET /api/mpip/{tenantId}/providers

# Check rate for a specific provider/date/member age combination
GET /api/mpip/{tenantId}/rate-check?providerId=X&serviceDate=2026-04-01&memberAge=19

# Bulk import AHCA's October 1 qualified provider list
POST /api/mpip/{tenantId}/bulk-import

# Add or update a single provider qualification
PUT /api/mpip/{tenantId}/providers/{providerId}
```

**FL fiscal year periods:** MPIP qualification runs October 1 - September 30. The platform automatically maps service dates to the correct period when evaluating rates. Plans must reassess PCP/OB/GYN eligibility each April 1.

---

## Configuration Checklist for FL MCO Tenants

Before going live with a Florida Medicaid MCO tenant, verify these steps:

- [ ] `TenantComplianceConfig` upserted with `StateCode = "FL"` and your `FmmisSubmitterId`
- [ ] `MpipEnabled = true` if your SMMC 3.0 contract requires MPIP enhanced rates
- [ ] AHCA October 1 qualified provider list imported via `POST /api/mpip/{tenantId}/bulk-import`
- [ ] Provider records include `FlMedicaidProviderNumber` for all billing providers
- [ ] Member records have `MedicaidId` populated (used as NM109 in 837 subscriber loop)
- [ ] Azure Blob staging container `fmmis-staging` created and connection string in Key Vault
- [ ] `encounter-submission-service` Kubernetes deployment active with `Worker__IntervalHours` configured
- [ ] Operations team subscribed to `encounter-deadline-warning` Kafka topic (or alerting configured)
- [ ] 999 acknowledgment webhook/endpoint configured to receive FMMIS responses

---

## Encounter Submission Timing Reference

```
Claim adjudicated (Day 0)
    |
    +-- Day 0: EncounterSubmission created (Status = Pending)
    |
    +-- Day 53+: Worker flags as DeadlineWarning if not yet submitted
    |            encounter-deadline-warning Kafka event fired
    |
    +-- Day 58-60: Worker auto-batches for urgent submission
    |              FMMIS.{SubmitterId}.{timestamp}.dat staged to blob
    |
    +-- Day 60: AHCA contractual deadline
    |
    +-- Post-submission: 999 acknowledgment received
                         Status -> Accepted | PartialAccept | Rejected
```

---

## Testing FL AHCA Compliance

An end-to-end integration test (`FL_Medicaid_Claim_EncounterSubmission_E2E`) validates the complete workflow:

```bash
dotnet test tests/CloudHealthOffice.FlAhca.E2ETests/ --verbosity normal
```

The test covers:
- FMMIS 837P generation with all four companion guide rules
- MPIP 1.063x rate for specialist + member age 19
- MPIP 1.0x (no enhancement) for member age 22
- 60-day encounter submission window auto-created on adjudication
- FMMIS batch file naming convention
- 999 Accepted acknowledgment processing
- Deadline warning trigger at <=7 days

---

## Regulatory References

| Reference | Description |
|-----------|-------------|
| FL S627.6131 | Florida prompt pay statute |
| AHCA SMMC 3.0 | SMMC 3.0 program overview and plan contracts |
| AHCA MPIP 2025-2026 | MPIP qualified provider list and rate guidance |
| FMMIS 837P Companion Guide | FL-specific 837P transaction requirements |
| 42 CFR S438.242 | Federal encounter data submission requirement |
| CMS-0057-F | Federal prior authorization API rule (all states) |

---

## Related Documentation

- [State Compliance Matrix - Big 4 States](./state-compliance-matrix.md) - CA / NY / FL / IL side-by-side
- [CMS-0057-F Compliance Guide](./cms-0057-f-compliance.md) - Federal prior auth API requirements
- [EDI Transaction Reference](./edi-transactions.md) - 837P/837I/835/270/271/278 implementation details
- [Prior Authorization API](./prior-auth-api.md) - 72-hour urgent / 5-day standard PA workflows
- [Multi-Tenant Configuration](./multi-tenant-config.md) - Tenant setup and state code assignment
- [Encounter Submission Service](./encounter-submission-service.md) - Full API reference

---

*Last updated: April 2026 - Cloud Health Office v4.1 - Apache 2.0 License*
