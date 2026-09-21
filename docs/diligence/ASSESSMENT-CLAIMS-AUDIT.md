# assessment.html — Claim-by-Claim Verification

**Date:** 2026-09-21
**Scope:** `src/site/assessment.html` as it stood before the rewrite in this change.
**Method:** every capability, performance, compliance and integration claim on the page,
checked against the code in this repository.

This document is kept because the rewrite is only trustworthy if the reasoning
behind it is inspectable. Where a claim was removed, this says why.

Verdicts: **Implemented** · **Partial** · **Roadmap** (designed/stubbed, not shipped) · **False** (contradicted by code, or no code at all)

## A. Capability claims

| # | Claim | Verdict | Evidence | Proposed wording |
|---|---|---|---|---|
| 1 | "837P / 837I / 837D" claims intake | **Implemented** | `claims-service/EDI/Inbound/X12837Parser.cs`, `X12Tokenizer.cs`, `X12837ClaimMapper.cs`; exercised by the 1M MCC run | Keep. "X12 837P/I/D intake with a real tokenizer, parser and claim mapper." |
| 2 | "Eligibility Verification (270/271)" | **Implemented** | `eligibility-service/Services/Edi270Parser.cs`, `Edi271Generator.cs` | Keep. |
| 3 | 834 enrollment | **Implemented** | `enrollment-import-service/Services/Edi/Enrollment834EdiParser.cs` | Keep (page doesn't currently claim it — it should). |
| 4 | "Real-Time Prior Authorization (278)" | **Partial** | FHIR PAS is real: `fhir-service/Controllers/PasController.cs`, `DtrController.cs`, `CrdController.cs`. **No X12 278 parser exists.** | "Prior authorization via FHIR PAS (CMS-0057-F surfaces). X12 278 as a transaction is on the roadmap." |
| 5 | "Claim Status … X215 Authorization Inquiry / X217 Authorization Request" | **False** | No 276, 277, 278 or 835 parser anywhere in `src`. "X215" is not a 5010 designation for claim status (276/277 is). Only SFTP transport scaffolding exists. | Remove the section, or move to roadmap as "X12 276/277 claim status — roadmap." |
| 6 | "X12 Transaction Sets: 005010X212 (277), 005010X217 (278), 005010X222 (837)" | **Partial** | 005010X222 (837) is real. 277 and 278 are not parsed. **Correction after review:** 005010X221A1 (835) *generation* IS implemented — `payment-service/Services/EraGeneratorService.cs` (329 lines) builds real ST\*835/BPR/CLP/SVC segments, with `BatchEraGeneratorService.cs` (395 lines) for batch. Inbound 835 parsing is not implemented. The first pass of this audit recorded 835 as roadmap, which under-claimed it. | "005010X222 (837P/I/D), 834, 270/271 implemented; 005010X221A1 (835) generated outbound; 276/277/278 and inbound 835 parsing on the roadmap." |
| 7 | "NCPDP Support: Telecom D.0, Script 10.6" | **False** | **Zero NCPDP files in the repository.** `git ls-files \| grep -i ncpdp` returns nothing. | Delete. |
| 8 | "HL7 v2.x: Extensible via workflow adapters" | **False** | No HL7 v2 code or adapter exists. | Delete, or "HL7 v2.x — roadmap." |
| 9 | "Appeals Integration — 277 RFAI generation, 275 attachment processing" | **Partial** | `rfai-service` and `attachment-service` are real services with tests; CDex controller exists. But no X12 277/275 *parser* — the RFAI path is FHIR/CDex, not X12. | "Appeals and RFAI workflows via FHIR CDex, with attachment handling. X12 275/277 transport is roadmap." |
| 10 | "Automated claims adjudication systems / HealthEdge correlation" (appears 4×) | **Roadmap — by design** | Every adapter throws: `HealthEdgeClaimAdapter.cs` (51 lines, 8 `NotImplementedException`), same for `QnxtClaimAdapter.cs`, `FacetsClaimAdapter.cs`. Per the product owner this is **deliberate**: each Facets/QNXT/HealthEdge installation differs by version and configuration, so a generic connector would have to be rewritten per engagement anyway and would be a liability. The interface is the deliverable; the connector is project work. | "Adapter interfaces for Facets, QNXT and HealthEdge are defined and stable. Connector implementation is engagement work by design. No vendor connector ships in the box." |
| 11 | "Integration Points: Clearinghouse, Change Healthcare, Optum 360, Inovalon" | **False** | `ChangeHealthcareEligibilityAdapter.cs` and `AvailityEligibilityAdapter.cs` are 46-line stubs that throw. **Optum 360 and Inovalon have no code at all.** | "Clearinghouse adapter interfaces (Availity, Change Healthcare) are scaffolded and not yet implemented." Drop Optum 360 and Inovalon entirely. |
| 12 | "Deterministic replay for failed transactions" | **Partial** | Event-sourced repositories and Kafka consumers with idempotency exist (`AppealEventRepositoryMongo.cs`, `ClaimPendedConsumer.cs`); no general replay facility for X12 transactions | "Idempotent, event-sourced claim processing with per-stage short-circuit." |
| 13 | Four product lines (Public Tools / Transactional / Managed Data / Platform) | **Partial** | `PricingApi`, `capitation-service` (44 files, Stripe Connect + NACHA), `premium-billing-service` (38 files, NACHA), `payment-service` (28 files) are real. **`ffs-service` has 2 files** (`Program.cs` + one model) — a skeleton. | Keep the framing; don't imply FFS is shipped. |

## B. Multi-tenancy and security

| # | Claim | Verdict | Evidence | Proposed wording |
|---|---|---|---|---|
| 14 | "Unlimited payer tenant support" | **False** | No scale evidence at any tenant count. Absolute claim is unsupportable. | "Multi-tenant by design, with per-tenant data partitioning." |
| 15 | "Complete logical isolation per tenant" | **False** | `TenantMiddleware.cs:140` — `RequireTenantId = false` by default, **no service sets it true**. Tenant identity falls back to the client-supplied `X-Tenant-ID` header. 28 of 35 services wire no authentication. Isolation is per-query `.Eq(TenantId)` filters with no central enforcement (0 EF global filters). | "Tenant-scoped data access through per-tenant partitioning and per-query tenant filters. Internal services assume an authenticating gateway; tenant isolation is not yet enforced independently of that boundary." |
| 16 | "Shared infrastructure, zero shared secrets" | **False** | Until this week, a single SFTP password was committed and shared across three job manifests. | Delete. |
| 17 | "Managed identities only — no secrets in code" | **False** | Directly contradicted: hardcoded SFTP password and MongoDB credential at HEAD until PR #1184. | "Workload identity for Azure Key Vault and Kubernetes auth for HashiCorp Vault; no long-lived credentials stored in the application." |
| 18 | "Private Endpoints Everywhere / no public IPs" | **Partial** | `infrastructure/azure/modules/private-endpoints.bicep` and `networking.bicep` exist — as templates. No deployed environment is evidenced. | "Reference Bicep templates deploy private endpoints for PaaS dependencies." |
| 19 | "Key Vault Premium with HSM-backed keys" | **Implemented (as template)** | `app-keyvault.bicep:20` — `param skuName string = 'premium'` | "Reference templates provision Key Vault Premium (HSM-backed)." |
| 20 | "TLS 1.3 in transit, AES-256 at rest" | **Partial** | Standard Azure PaaS defaults; not independently configured or verified in repo | Fold into "encryption in transit and at rest via platform defaults." |
| 21 | "Conditional Access policies enforced / MFA required" | **False (not in repo)** | No Conditional Access or MFA configuration in any `.bicep` or code. These are customer Entra ID tenant settings. | "Integrates with your Entra ID tenant; Conditional Access and MFA are configured by the customer." |
| 22 | "DCR-based PHI redaction in Application Insights" | **Partial** | Redaction code exists (`src/ai/redaction.ts`, `src/security/hipaaLogger.ts`, `SanitizeForLog` in the orchestrator). No Data Collection Rule is defined in the Bicep. | "PHI-aware log redaction in application code." |
| 23 | "7-year retention minimum / immutability policies" | **Partial** | `infrastructure/helm/…/values.yaml:398` — `dataRetentionDays: 2555`. No immutability policy resource found. | "Configurable retention (default 7 years)." Drop immutability until implemented. |
| 24 | "Unbreachable security posture" | **False** | Not a claim any system can make. | Delete. |
| 25 | "Residual Risk: Negligible" | **False** | Unsupportable, and contradicted by §15 and §17. | Delete the whole "Threat Model … Mitigated" block or reframe as "controls we implement." |
| 26 | "Security That Actually Passes Audits" (heading) | **False** | No audit has been passed. `trust.html` explicitly disclaims SOC 2 and HITRUST. | "Security architecture." |

## C. Performance, SLA and ROI

| # | Claim | Verdict | Evidence | Proposed wording |
|---|---|---|---|---|
| 27 | "99.9% uptime SLA" / "99.9% uptime SLA target" (2×) | **False** | No production service, no SLA, no uptime measurement anywhere in the repo. README states results are "local Kubernetes evidence, not a production cloud capacity claim." | Delete. Replace with the real MCC evidence (below). |
| 28 | "Service disruption: Mitigated via Azure 99.9% SLA + geo-redundancy" | **False** | Cites Azure's SLA as if it were the product's. No geo-redundant deployment is evidenced. | Delete. |
| 29 | "Sub-2-minute prior authorization processing" | **Roadmap / unevidenced** | No prior-auth latency benchmark exists | Delete or label as a design target. |
| 30 | "Sub-second response times" (eligibility) | **Roadmap / unevidenced** | No eligibility latency benchmark | Delete or label as a design target. |
| 31 | "Prior authorization: 12 days → <2 minutes (99.86% faster)" | **False** | No measurement. Internally inconsistent: `WEBSITE-PHASE2-COMPLETE.md` says "12 days → 90 minutes" on one line and "<2 minutes" on another. | Delete. |
| 32 | "Claims status: 24 hours → <5 seconds", "Eligibility: 15 minutes → <1 second" | **False** | No measurement | Delete. |
| 33 | "60-80% staff time reclamation" (3×) | **False** | Appears only in marketing/sales copy. `demo-script.md:437` uses "60-80%" as a *demo-to-proposal conversion target* — a different metric entirely. | Delete. |
| 34 | "Manual faxing/phone calls: 100% elimination", "Manual data entry errors: 100% elimination" | **False** | No measurement; absolutes are unsupportable | Delete. |
| 35 | "X12 validation errors: 95% reduction", "Trading partner violations: 99% reduction", "Error remediation: 70% reduction" | **False** | No measurement | Delete. |
| 36 | TCO: "$4.25M vs $750k" 5-year projection | **Unevidenced model** | No sourcing for any input | Keep only as an explicitly labelled illustrative model with stated assumptions. |
| 37 | **MCC results — not currently on the page** | **Implemented** | Episode 15 run 2: 1,000,000 claims, 0 platform failures, 129,981/130,000 workflow checks, 20,000/20,000 payments exact within $0.01, 123.81 claims/sec. Episode 16: 155.89 claims/sec. Local Docker Desktop Kubernetes. | **Add this.** It is the strongest evidence in the repo and the page omits it entirely. |

## D. Deployment claims

| # | Claim | Verdict | Evidence | Proposed wording |
|---|---|---|---|---|
| 38 | `node dist/scripts/cli/payer-onboarding-wizard.js` (Step 1) | **False — the command does not work** | **No such file.** `dist/scripts/cli/` contains `interactive-wizard.js` and `payer-generator-cli.js`. Also wrong in `platform.html`, `COMMERCIALIZATION.md`, `CONFIG-TO-WORKFLOW-GENERATOR.md`. | Correct to `interactive-wizard.js` — and test it. |
| 39 | `./configure-hipaa-trading-partners.ps1` (Step 4) | **Partial** | Exists only at `docs/features/configure-hipaa-trading-partners.ps1`, not at the path implied by the generated output | Correct the path. |
| 40 | "Total: ~50 minutes" / "Sub-1-hour deployment" | **Unevidenced** | No timed deployment evidence; step 1 currently fails | Remove the total until a clean run is timed end to end. |
| 41 | "No Custom Code Required — entirely configuration-driven" | **Partial** | Generator is real (`scripts/cli/payer-generator-cli.ts`), but vendor connectors require implementation | "Infrastructure and workflow generation is configuration-driven; core-system connectors are built per engagement." |
| 42 | "Battle-tested Argo Workflows runtime" | **Partial/misleading** | Argo templates exist (`infrastructure/argo-workflows/`), but the adjudication pipeline that the MCC exercises is **.NET services on Kubernetes with Kafka** — `run-mcc-local-k8s.sh` does not reference Argo at all. | Describe the actual architecture: .NET microservices on Kubernetes, event-driven. Argo is used for X12 file-transport workflows. |

## E. Compliance claims

| # | Claim | Verdict | Evidence | Proposed wording |
|---|---|---|---|---|
| 43 | "HIPAA Technical Safeguards Implementation" (heading) | **Partial** | Body text is already honest ("Reference controls implemented in code and infrastructure templates; deployment validation required"). The *heading* over-promises. | "HIPAA technical safeguards — reference implementation." |
| 44 | "Compliance violations: Mitigated via immutable audit logs" | **False** | No immutability policy implemented | Delete. |
| 45 | "FHIR R4: Implemented API surfaces…" | **Implemented** | 31 controllers incl. `PasController`, `DtrController`, `CrdController`, `BulkExportController`, `PayerToPayer*` | Keep — this wording is already accurate. |
| 46 | No SOC 2 / HITRUST claimed | **Correct as-is** | `trust.html:506` explicitly disclaims both | Keep, and mirror the disclaimer onto this page. |
| 47 | "Zero PHI exposure incidents" (success metric) | **Unevidenced** | No production deployment, so trivially true and meaningless | Delete. |

## F. Tone / framing

| # | Claim | Verdict | Proposed wording |
|---|---|---|---|
| 48 | "why resistance to adoption is futile" | **Delete** | Reads as unserious; the rest of the repo is evidence-first. |
| 49 | "Resistance is futile." | **Delete** | Same. |
| 50 | "Cloud Health Office represents the inevitable evolution…" (2×) | **Delete** | Assertion, not evidence. |
| 51 | "Organizations that resist adoption will face: … Staff attrition" | **Delete** | Fear-based, unsupported. |
| 52 | "Capabilities Beyond Question" (heading) | **Reframe** | "Platform capabilities." |
| 53 | "Just emerged from the void" (subtitle) | **Reframe** | Replace with the document date and scope. |
| 54 | "Manual EDI processing is now optional. The transformation begins today." | **Reframe** | State what the platform does. |
| 55 | "Cloud Health Office v1.0.0 — The Sentinel" | **Stale** | A `v1.0.0` tag exists, but the repo has since tagged `v2.0.0`, `v3.0.0` and `v4.0.0`. The page advertises a version three majors behind. Update or drop the version string. |
