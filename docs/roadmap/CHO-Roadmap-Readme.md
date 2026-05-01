# CHO Development Roadmap

**Last updated:** 2026-03-14
**Maintainer:** Mark Phillips (@markphillips)

## Overview

This roadmap is organized around **commercial readiness** — what gets CHO to a paying pilot fastest. Features are sequenced by their impact on demonstrating credibility, proving correctness, and enabling deployment, not by implementation difficulty.

### Roadmap Phases

|Phase         |Timeline   |Goal                                               |Status   |
|--------------|-----------|---------------------------------------------------|---------|
|**Sprint 1–2**|Complete   |Engine buildout (12 PRs + 3 greenfield workstreams)|✅ Done   |
|**Phase 1**   |Weeks 1–4  |Hardening & credibility                            |🔄 Active |
|**Phase 2**   |Weeks 5–8  |FHIR convergence & CMS-0057-F demo readiness       |⏳ Planned|
|**Phase 3**   |Weeks 9–12 |Operational readiness for pilot deployment         |⏳ Planned|
|**Phase 4**   |Weeks 13–20|First pilot, first revenue, conference materials   |⏳ Planned|

-----

## Completed Work (Sprints 1–2)

All 12 original roadmap PRs and 3 post-roadmap greenfield workstreams are complete. See <CHO-ENHANCEMENT-STATUS.md> for the full tracker.

### Engines Delivered

|Engine                  |Tests|Key Capabilities                                                    |
|------------------------|-----|--------------------------------------------------------------------|
|BenefitCalculationEngine|23   |HDHP/HSA, aggregate family accumulators, No Surprises Act OON       |
|FeeScheduleEngine       |17   |Rate resolution, provider contract/network-aware pricing, DRG       |
|NcciEngine              |24   |CCI edits, MUE limits, modifier validation, seed data               |
|CobEngine               |24   |Birthday rule, MSP, active-employment, complementary/non-duplication|
|EncounterEngine         |44   |837P/837I transformer, original/corrected/void, COB OI, ISA/GS batch|
|RiskAdjustmentEngine    |52   |CMS-HCC v28, HHS-HCC crosswalk, hierarchy resolution, batch scoring |
|DocumentStore           |13   |IDocumentStore abstraction, Azure Blob + InMemory providers         |

### EDI Stack

Complete X12 transaction support: 270/271, 276/277, 278, 834, 835, 837 (P/I encounter), 824, 277CA — with Argo workflow ingestion pipelines for each.

-----

## Phase 1: Hardening & Credibility (Weeks 1–4)

> **Goal:** Make what exists demonstrably correct and deployable.

### 1A. Golden-Path Integration Test *(P0)*

- [ ] Create `tests/CloudHealthOffice.E2E/` xUnit project
- [ ] Implement golden-path test: submit claim → adjudicate → pay → generate 835
- [ ] Wire into GitHub Actions (`.github/workflows/e2e-tests.yml`)
- [ ] Validate against docker-compose stack

**Why:** One automated test that proves the core value proposition works end-to-end. This is the first thing a prospect’s technical evaluator will ask for.

### 1B. Service Controller Tests *(P0)*

- [ ] `tests/CloudHealthOffice.ClaimsService.Tests/` — SubmitClaim, GetById, Search, 277CA generation
- [ ] `tests/CloudHealthOffice.AdjudicationController.Tests/` — adjudicate, calculate-benefits, resolve-rates, ncci-check
- [ ] `tests/CloudHealthOffice.PaymentService.Tests/` — payment creation, 835 generation, download

**Why:** The engines are well-tested (281 methods), but the HTTP layer stitching them together has zero coverage. De-risks the core adjudication pipeline.

**Target:** 30–40 new test methods across the three projects.

### 1C. Shared Infrastructure Library *(P0)*

- [ ] Create `src/services/shared/CloudHealthOffice.Infrastructure/`
- [ ] Extract: TenantMiddleware, health checks, MongoDB connection factory, error responses, logging config
- [ ] Add DI extension: `AddChoInfrastructure()` / `UseChoInfrastructure()`
- [ ] Migrate claims-service as proof-of-concept
- [ ] Migrate 2 additional services

**Why:** TenantMiddleware is copy-pasted into every service. A shared library ensures consistency, reduces bugs, and accelerates new service development.

### 1D. Remove Legacy Logic Apps *(P3)* -- COMPLETE

- [x] Delete `infrastructure/logicapps/` (21 JSON files)
- [x] Delete `src/logicapps/` if present
- [x] Update any documentation references
- [x] Create ADR: `docs/adr/004-remove-logic-apps.md`

**Why:** Repo hygiene. Logic Apps were replaced by Argo Workflows on AKS + C# microservices. See [ADR-004](../adr/004-remove-logic-apps.md).

### 1E. Health Checks Across All Services *(P2)*

- [ ] Add ASP.NET health checks to all 23 services
- [ ] `/health/live` (liveness) + `/health/ready` (dependency checks)
- [ ] Update Dockerfiles with HEALTHCHECK instruction
- [ ] Update docker-compose and Helm values

**Why:** Table stakes for Container Apps / AKS deployment. Currently only claims-service has a health check.

-----

## Phase 2: FHIR Convergence & CMS-0057-F Demo (Weeks 5–8)

> **Goal:** Deliver a demonstrable CMS-0057-F compliance story — CHO’s primary market differentiator.

### 2A. Port FHIR Mappers to C# *(P1)*

- [ ] Port `patient-access-mapper.ts` → C# in fhir-service (Patient, Coverage, EOB)
- [ ] Port `provider-directory-api.ts` → C# in fhir-service (Practitioner, Organization, Location)
- [ ] Port `compliance-checker.ts` → C# `Cms0057ComplianceChecker` service
- [ ] Port TypeScript tests to xUnit
- [ ] Validate against OpenAPI specs in `api/openapi/`

**Why:** CMS-0057-F compliance is CHO’s key differentiator, but the FHIR logic is TypeScript while everything else is C#. This split-brain prevents the FHIR layer from calling C# engines directly and complicates deployment.

### 2B. FHIR Compliance Demo Harness *(P1)*

- [ ] Create `scripts/demo/cms-0057-f-demo.sh`
- [ ] Sample payloads in `scripts/demo/fixtures/`
- [ ] Exercises: enrollment → eligibility → prior auth → claim → adjudicate → FHIR query → compliance check
- [ ] Designed for live prospect demos and conference presentations

**Why:** The demo that closes deals. A prospect needs to see the full lifecycle — member enrolled, claim processed, data available via FHIR APIs — in under 15 minutes.

### 2C. Claims Scrubbing / NCCI Consolidation *(P2)*

- [ ] Create `src/engines/CloudHealthOffice.ClaimsScrubEngine/`
- [ ] Port 20+ TypeScript validation rules (DC, CV, DL, AL, PV, MV categories) to C#
- [ ] Port ~40 TypeScript tests to xUnit
- [ ] Wire into AdjudicationController: scrub → NCCI → benefit calc → rate resolution
- [x] Decommission `claims-scrubbing-service` — scrubbing runs in-process via `CloudHealthOffice.ClaimsScrubEngine` (capability 5.4)

**Why:** Unifies pre-adjudication validation under one runtime. Eliminates the TypeScript/C# split for the claims pipeline.

-----

## Phase 3: Operational Readiness (Weeks 9–12)

> **Goal:** Make CHO deployable and operable for a pilot customer.

### 3A. Augment/Replace Mode Toggle *(P1)*

- [ ] Create `src/engines/CloudHealthOffice.OperatingMode/`
- [ ] Define `IOperatingMode`, `AugmentResult<T>`, per-tenant engine configuration
- [ ] Implement in BenefitCalculationEngine and FeeScheduleEngine
- [ ] Store mode config per-tenant in tenant-service
- [ ] API: `GET/PUT /api/tenants/{id}/operating-mode`

**Why:** Core to CHO’s adoption story. Health plans won’t rip-and-replace their adjudication system on day one. Augment mode lets them run CHO alongside QNXT, validate results match, then gradually cut over engine by engine.

### 3B. Observability Stack *(P1)*

- [ ] Add OpenTelemetry to `CloudHealthOffice.Infrastructure`
- [ ] Custom spans for adjudication pipeline (scrub → NCCI → benefit → rate → COB)
- [ ] Metrics: claim processing latency, EDI throughput, error rates, auto-adjudication rate
- [ ] `docker-compose.observability.yml` with Jaeger + Prometheus + Grafana
- [ ] Pre-built dashboards: claims SLA, EDI volume, engine performance, tenant activity

**Why:** A pilot customer’s ops team needs visibility into claim processing. Without observability, production issues are invisible until a provider calls to complain.

### 3C. Tenant Onboarding Automation *(P2)*

- [ ] Create `scripts/onboard-tenant.sh` CLI tool
- [ ] Automate: tenant creation, reference data seeding, default benefit plan, fee schedule, operating mode
- [ ] Include `--dry-run` flag
- [ ] Test with a fictional second tenant to validate multi-tenancy isolation

**Why:** Manual tenant setup is error-prone and unscalable. Automation proves multi-tenancy works and reduces pilot deployment time.

### 3D. Security Hardening Pass *(P2)*

- [ ] Audit against `docs/security/SECURITY-HARDENING-ROADMAP.md`
- [ ] Validate Vault integration for all secrets
- [ ] RBAC on all service endpoints
- [ ] Port `hipaaLogger.ts` to C# (PHI audit logging)
- [ ] Verify PHI validation GitHub Action effectiveness

-----

## Phase 4: Commercial & Growth (Weeks 13–20)

> **Goal:** First pilot, first revenue, first external validation.

### 4A. Pilot Deployment Playbook

- [ ] Consolidate deployment docs into single `PILOT-DEPLOYMENT-GUIDE.md`
- [ ] Include: infrastructure provisioning, configuration, deployment steps, go-live checklist
- [ ] Test the guide end-to-end on a fresh Azure subscription

### 4B. Migration Wizard Completion

- [ ] Complete `TriZettoOpenAccessClient` in migration-wizard
- [ ] QNXT → CHO mapping: benefit plans, fee schedules, providers, auth rules
- [ ] Gap analysis report generator
- [ ] Support CSV/JSON import for offline evaluation

### 4C. Portal Demo Polish

- [ ] Dashboard page with real operational metrics
- [ ] Claims page with adjudication status visualization
- [ ] Members page with eligibility verification
- [ ] Authorizations page with 278 workflow status

### 4D. Encounter & Risk Adjustment REST Services

- [ ] encounter-service: submission lifecycle, batch dispatch, 837 download, resubmission
- [ ] risk-adjustment-service: measurement-year collection, per-member score, plan-level RAF

### 4E. Conference-Ready Materials

- [ ] Finalize Medium article (HIMSS26 timing)
- [ ] Polish pitch deck content (`docs/sales-materials/PITCH-DECK-CONTENT.md`)
- [ ] 15-minute live demo script
- [ ] Recorded screencast

-----

## Architecture Decisions

|ADR                                 |Decision                                  |Status  |
|------------------------------------|------------------------------------------|--------|
|[001](../adr/001-argo-vs-airflow.md)|Argo Workflows over Airflow               |Accepted|
|[002](../adr/002-kafka-vs-nats.md)  |Kafka over NATS                           |Accepted|
|[003](../adr/003-pyx12-library.md)  |pyx12 for X12 parsing                     |Accepted|
|[004](../adr/004-remove-logic-apps.md)|Remove Azure Logic Apps                   |Accepted|
|005 (planned)                       |Converge TypeScript → C# (FHIR, scrubbing)|Pending |
|006 (planned)                       |Augment/Replace operating mode pattern    |Pending |

-----

## Key Metrics

|Metric                        |Baseline (Sprint 2 exit)|Phase 1 Target|Phase 4 Target|
|------------------------------|------------------------|--------------|--------------|
|Automated test methods        |281                     |400+          |600+          |
|Services with controller tests|3                       |6             |15            |
|Golden-path E2E tests         |0                       |1             |3             |
|Services with health checks   |1                       |10            |23            |
|FHIR APIs in C#               |0                       |2             |5             |
|Grafana dashboards            |2                       |5             |8             |

-----

## Related Documents

- [Enhancement Checklist](CHO-ENHANCEMENT-CHECKLIST.md) — Sprint 1–2 execution tracker
- [Enhancement Status](CHO-ENHANCEMENT-STATUS.md) — Sprint 1–2 detailed status
- [Copilot Prompts](CHO-COPILOT-PROMPTS.md) — AI-assisted development prompts for each roadmap item
- [Assessment](../../CHO-Comprehensive-Assessment-and-Roadmap.md) — Full codebase assessment