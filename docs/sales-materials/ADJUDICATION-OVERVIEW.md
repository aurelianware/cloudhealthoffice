# Cloud Health Office Adjudication Platform Overview

## For Health Plan Decision Makers

Cloud Health Office (CHO) adjudicates claims today through an evidence-backed, multi-engine pipeline that processes professional and institutional claims end-to-end — from pre-payment validation through benefit cost-sharing to provider payment.

---

## Product Line Context

This document describes the adjudication pipeline that anchors CHO's Platform Engagement product line — specifically the substrate that Layer 2 — Progressive Modernization and Layer 3 — Full CAPS Platform engagements depend on. Adjudication is what a health plan buys when they buy Platform Engagement. Public Tools, Transactional Services, and Managed Data Services exercise subsets of the same engines (the FeeScheduleEngine in particular) but do not require the orchestrated pipeline described here. For the canonical positioning across all four product lines, see [POSITIONING.md](../POSITIONING.md).

---

## What's Running Today

### A Real Adjudication Pipeline, Not a Prototype

Cloud Health Office processes claims through a 7-step orchestrated workflow that runs nine specialized engines in sequence:

1. **Pre-payment validation** — Claim scrubbing catches submission errors before any pricing runs
2. **NCCI/MUE edits** — CMS-standard bundling edits and maximum unit enforcement
3. **Provider integrity screening** — OIG exclusion list, SAM.gov, and NPPES verification
4. **Prior authorization enforcement** — State-specific PA rules (TX Medicaid STAR/STAR+PLUS/CHIP with Gold Card exemption)
5. **Terminology crosswalk** — Plan-specific procedure code resolution
6. **Fee schedule pricing** — Six pricing methods (RVU, flat rate, percent-of-billed, per-diem, DRG case rate, capitation) with CMS modifier adjustments
7. **Benefit cost-sharing** — Full waterfall: service category → coverage check → deductible → copay → coinsurance → OOP max
8. **Coordination of benefits** — Both complementary and non-duplication COB models
9. **Enrollment verification** — State Medicaid enrollment gate (TX, FL, CA, NY with CAQH integration)

Every engine is a standalone component — tested independently, composed via dependency injection, and observable through OpenTelemetry.

### AI-Assisted Claims Examination

When the deterministic NCCI engine detects a bundling edit, CHO doesn't just deny the claim. The AI Claims Examiner reviews the clinical documentation — modifiers, diagnosis codes, service dates, provider history — and produces a structured recommendation for human review.

The examiner is conservative by design: it never auto-applies, it always explains its reasoning with specific claim evidence, and its safe default is "escalate to human." This is augmentation, not replacement, of clinical judgment.

---

## The Progressive Migration Path

### No Big-Bang Replacement Required

CHO's Operating Mode pattern lets you run any engine in **shadow mode** alongside your existing core admin system. For each claim type and line of business, you choose:

| Mode | What happens |
|------|-------------|
| **Shadow (Augment)** | CHO processes the claim in parallel. Both results compared. Legacy system stays authoritative. Discrepancies tracked. |
| **Live (Replace)** | CHO is authoritative. Legacy system not consulted. |
| **Legacy Only** | CHO doesn't process this claim type. Routes to existing system. |

You can flip one claim type at a time. Start with Texas Medicaid professional claims in shadow mode. Once you see consistent parity — typically within 30 days — flip to Replace. Then move to institutional, then CHIP, then STAR+PLUS.

### What This Means for QNXT Migration

Every engine you need to replace QNXT already exists as a standalone component. The path from "running alongside QNXT" to "QNXT decommissioned" is configuration, not development:

1. **Month 1–2:** Shadow mode on TX Medicaid 837P. Compare results daily.
2. **Month 3:** Flip to Replace when discrepancy rate < 1%. QNXT no longer processes these claims.
3. **Month 4–6:** Expand to 837I, CHIP, STAR+PLUS in the same pattern.
4. **Month 7+:** All claim types on CHO. Begin QNXT decommission.

---

## Architecture Highlights

### Modular Engine Composition

Each adjudication engine is a class library composed via dependency injection. Engines don't know about each other — the AdjudicationController orchestrates them. This means:

- **Test each engine independently** with unit tests against known scenarios
- **Swap implementations** per tenant (e.g., different fee schedules for different plans)
- **Add engines** to the pipeline without modifying existing ones
- **Run engines in shadow mode** individually — not all-or-nothing

### Multi-Tenant from Day One

Every request carries a tenant context. Plans, fee schedules, accumulator state, PA rules, and operating mode configuration are all tenant-scoped. A single deployment serves multiple health plans with complete data isolation.

### Full Observability

Every step in the pipeline emits OpenTelemetry spans with structured tags — claim type, operating mode, routing decision, engine outcomes, latency. You can trace a single claim's path through all nine engines, or aggregate across millions of claims to spot systematic issues.

---

## Key Differentiators

| Capability | CHO | Traditional Core Admin |
|-----------|-----|----------------------|
| Engine-level shadow mode | Per claim type, per LOB | All or nothing |
| AI claims examination | Built-in, human-in-the-loop | Bolt-on or manual |
| Progressive migration | Configuration change | Multi-year project |
| Multi-tenant | Native | Retrofit |
| Cloud-native deployment | Kubernetes + Argo Workflows | On-premise or lift-and-shift |
| State-specific rules | TX Medicaid rules + Gold Card built in | Custom development |
| Provider integrity | OIG/LEIE/SAM.gov/PECOS integrated | Separate system |
| Observability | OpenTelemetry native | Log files |

---

## Test Coverage

25 test projects with 84 test files covering:

- AdjudicationController end-to-end scenarios
- BenefitEngine cost-sharing waterfall
- FeeScheduleEngine pricing methods
- NcciEngine PTP pair and MUE edits
- ClaimsExaminerService AI advisory
- CobEngine complementary and non-duplication
- ProviderVerificationEngine multi-source scoring
- PriorAuthRuleEngine TX Medicaid rules
- ProviderEnrollmentService state Medicaid gates
- E2E workflows and load tests
