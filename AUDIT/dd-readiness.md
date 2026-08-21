# CloudHealthOffice — Investor Due-Diligence Readiness Audit

**Prepared for:** internal (pre-investor) review
**Date:** 2026-08-21
**Scope:** `aurelianware/cloudhealthoffice` @ branch `claude/cloudhealthoffice-dd-audit-ieqv5o` (HEAD `a9a1241`)
**Method:** static read of code, docs, site copy, CI, and git history. **No code or docs were modified** — this is a findings-only pass.
**Repo scale audited:** ~318K lines of C# across 1,714 files, 35 services, 13 engines, 102 Blazor pages, 51 test projects / ~509 test files, 73 TS/JS files.

---

## 1. Executive Summary

**Overall readiness call: 🟡 YELLOW** — *substantively strong, with a small, concentrated set of credibility risks that are all fixable in days, not months.*

The important truth for an investor conversation: **the engineering substance is real and better than most source-available healthcare projects, and the core documentation is unusually honest.** The adjudication pipeline is a genuine, staged, tenant-aware, event-driven system that has been run end-to-end against a real 1,000,000-claim corpus with published, seeded, reproducible evidence. The engines behind it (Benefit, NCCI/MUE, COB, Scrub, Fee Schedule, Prior-Auth) are substantial real code, not stubs. The README, roadmap, benchmark methodology, and CMS-0057-F readiness matrix consistently *under-claim* and label gaps explicitly — exactly the posture the "evidence-first" positioning promises.

The audit is **yellow, not green**, because a handful of items visibly contradict that honest posture and would each independently give a healthcare investor's technical or security reviewer pause:

1. **One marketing page overclaims hard** where the rest of the repo is careful (`src/site/assessment.html`: "99.9% uptime SLA," "unlimited multi-payer scale," "100% elimination," "resistance to adoption is futile") — directly contradicting the README's own "local Kubernetes evidence, not a production cloud capacity claim."
2. **Multi-tenant isolation is header-trusted, and most services have no authentication.** Tenant identity falls back to an attacker-settable `X-Tenant-ID` header, and 28 of 35 services (including claims, member, eligibility, coverage, payment) wire no auth at all. This is *defensible* as an internal-trust-boundary design but is not currently enforced or documented as one — and "one tenant could read another tenant's PHI" is the single scariest sentence in healthcare DD.
3. **A hardcoded database password sits in a Production-labeled manifest** (`reference-data-service`), inconsistent with the clean `REPLACE_WITH_*` discipline used everywhere else.
4. **Benchmark framing drifts across documents** — the headline 1M-claim result is simultaneously presented as achieved (README, episode 15) and as a "Stretch Goal" (roadmap), and a third doc cites episode 16 with different numbers.
5. **A stray source `.zip` and Cosmos-vs-Mongo parity gaps** muddy the otherwise clean architecture story.

None of these is a fraud or a fatal flaw. All five are the kind of thing that, left in place, lets a skeptical reviewer say "if *this* is overstated, what else is?" — which is precisely the credibility risk the company says it is built to avoid.

### Top 5 things to fix before any investor sees the repo

| # | Fix | Why it matters | Effort |
| --- | --- | --- | --- |
| 1 | Rewrite or remove `src/site/assessment.html`. Delete the "99.9% SLA," "unlimited scale," "100% elimination," and "resistance… is futile" language; align it to the README's hedged, evidence-first tone. | It's the one asset that reads as vaporware and undercuts the entire "we don't overclaim" thesis. | Low |
| 2 | Rotate and remove the hardcoded `POSTGRES_PASSWORD: "CloudHealthOffice2026!"` from `src/services/reference-data-service/k8s/reference-data-service-deployment.yaml`; replace with a `REPLACE_WITH_*` template like the other secrets. Run TruffleHog/Gitleaks against **full origin history** (this checkout is a 50-commit shallow clone). | A committed credential in a "Production" manifest is a red flag a security reviewer will find in five minutes. | Low |
| 3 | Write a one-page **security trust-boundary/threat model** and set `RequireTenantId = true` for production; make explicit that internal services assume an authenticating gateway, or add authn to them. | Turns a scary implicit posture into a defensible documented one. | Medium |
| 4 | Reconcile benchmark claims to a single source of truth (README, `docs/benchmarks/README.md`, `docs/POSITIONING.md`, `docs/roadmap/README.md` all disagree on the current top result). | Inconsistent numbers on your flagship proof point is the worst place to be inconsistent. | Low |
| 5 | Remove `CHO-ProviderEnrollment-PriorAuthRuleEngine.zip` from the repo root; correct the README architecture diagram's X12 claim (276/277/278 are not parsed); add a short "Cosmos vs MongoDB parity" note. | Small hygiene items that each read as sloppiness in a DD data room. | Low |

### What's genuinely impressive (say this, with evidence)

- **A real staged adjudication engine.** `ClaimAdjudicationOrchestrator` (`src/services/claims-service/Services/Adjudication/ClaimAdjudicationOrchestrator.cs`) runs 8 registered stages (`Program.cs:384-391`) with idempotency, per-stage short-circuit, tenant pinning, log-injection sanitization, and degraded-mode audit handling. This is production-shaped code.
- **A reproducible 1,000,000-claim benchmark with an answer key.** Generator, validator, runner, and run script all present; per-episode evidence carries commit SHA, seed, exact command, and raw artifacts (`docs/million-claim-challenge/podcast/episode-015/`).
- **Honest core documentation.** The roadmap separates implemented/active/future/stretch; the CMS-0057-F matrix has a "buyer-facing wording" column distinguishing "implemented" from "integration required" from "phase 2"; `src/site/trust.html` explicitly states it does **not** claim SOC 2 or HITRUST.
- **A real, end-to-end AI claims examiner** (`claims-examiner-service`): typed Anthropic client, Kafka pend-consumer, prompt builder, advisory write-back — not a "we use AI" slide.
- **They audit themselves.** `LICENSING_AUDIT.md` documents a self-run correction of 50+ "open-source"→"source-available" errors; CI runs TruffleHog + Gitleaks + a PHI-validation workflow.

---

## 2. Claim-vs-Reality Reconciliation (most important)

Legend: **IMPLEMENTED** = real and exercised · **PARTIAL** = happy-path / limited / phase-1 · **SCAFFOLD** = stub / `NotImplementedException` / TODO · **ABSENT** = claimed, no code.

| # | Claim (source) | Implementing location | Status | Evidence | Recommended action |
|---|---|---|---|---|---|
| 1 | "Adjudication Pipeline" with Benefit/Fee/NCCI/COB/Scrub/Persistence stages (README architecture diagram) | `ClaimAdjudicationOrchestrator.cs`; 8 stages registered `claims-service/Program.cs:384-391` | **IMPLEMENTED** | Real staged orchestrator; all 8 registered stages are the *real* stages (Scrubbing, ProviderIntegrity, NetworkCredentialing, BenefitCalculation, NcciEdits, CoB, AiExamination, Persistence). "StubStage" names survive only in doc comments. Exercised by the 1M MCC run. | Keep. Best asset in the repo. |
| 2 | Benefit administration / cost-share / accumulators (README Key Features) | `src/engines/CloudHealthOffice.BenefitEngine` (4,686 LOC); `BenefitCalculationStage.cs` | **IMPLEMENTED (Phase-1 scoped)** | Real engine invoked in Replace mode. **Caveat:** mode-aware `CalculateWithModeAsync` throws `NotImplementedException` ("Augment-mode ships in Phase 2") — `HttpBenefitCalculationEngineClient.cs:121`; internal `ChoBenefitPlanProvider` also stubbed (`BenefitEngineRegistration.cs:222`) but the live path resolves plans via the benefit-plan-service HTTP resolver. | Keep; label augment-mode/legacy-compare as roadmap. |
| 3 | NCCI / MUE edits (README) | `src/engines/CloudHealthOffice.NcciEngine` (1,693 LOC); `NcciEditsStage.cs` (required stage) | **IMPLEMENTED** | PTP + MUE edits with per-tenant enforcement modes (Pend/Deny/SoftValidation). | Keep. |
| 4 | Coordination of Benefits (README) | `src/engines/CloudHealthOffice.CobEngine` (501 LOC); `CoordinationOfBenefitsStage.cs` | **PARTIAL** | Phase-1 **detection-only**: CHO-primary adjudicates; CHO-secondary/tertiary produces a Pend `cob-secondary-not-supported-phase-1`. `ICobCalculationService` "registered but unused." | Relabel COB as "primary-payer adjudication + secondary detection"; secondary calc = roadmap. |
| 5 | Claims scrubbing (README) | `src/engines/CloudHealthOffice.ClaimsScrubEngine` (1,344 LOC); `ScrubbingStage.cs` (required) | **IMPLEMENTED** | Structural validation, 277CA-aligned rejects. | Keep. |
| 6 | Fee schedules / pricing (README; PricingApi) | `src/engines/CloudHealthOffice.FeeScheduleEngine` (1,988 LOC); `src/services/CloudHealthOffice.PricingApi` | **IMPLEMENTED (verify wiring)** | Substantial engine + a Pricing API service. Not a distinct registered pipeline stage — pricing is applied within benefit calc. | Keep; confirm/desc how fee schedule feeds the live adjudication path. |
| 7 | Provider network / credentialing checks (README) | `NetworkCredentialingStage.cs` → provider-service | **PARTIAL** | Stage is real and calls provider-service for credentialing + network tier. **Caveat:** `IProviderAdapter.IsInNetwork*` throws on *every* adapter today (`IProviderAdapter.cs:48`; `ChoProviderAdapter.cs:86` `NetworkPlaceholderTodo`) — network-membership resolution is limited. | Verify how much network-tier logic is live vs. defaulting to In-Network; relabel accordingly. |
| 8 | AI claims examination (implied by claims-examiner-service; AiExaminationStage) | `src/services/claims-examiner-service` (AnthropicClient, Kafka consumer, ExaminerOrchestrator); `AiExaminationStage.cs` | **IMPLEMENTED (advisory, BestEffort)** | Real async pipeline: NCCI-modifier pends → Kafka `claims.pended.v1` → Anthropic call → advisory write-back. Default mode is advisory only, no payment effect. | Keep; describe as advisory decision-support, not autonomous adjudication. |
| 9 | X12 837 (claims) intake (README diagram + Key Features "834/837") | `claims-service/EDI/Inbound/X12837Parser.cs`, `X12Tokenizer.cs`, `X12837ClaimMapper.cs` | **IMPLEMENTED** | Real tokenizer + parser + claim mapper; 837 on-ramp feeds the orchestrator. | Keep. |
| 10 | X12 834 (enrollment) (README) | `enrollment-import-service/Services/Edi/Enrollment834EdiParser.cs`; `BenchmarkClaimGenerator/Output/X12_834Writer.cs` | **IMPLEMENTED** | Real 834 parser + writer, plan-code mapping repo. | Keep. |
| 11 | X12 270/271 (eligibility) (README diagram) | `eligibility-service/Services/Edi270Parser.cs`, `Edi271Generator.cs` | **IMPLEMENTED** | Real 270 parser + 271 generator. | Keep. |
| 12 | X12 276/277 and 278 as inputs (README architecture diagram lists "276/277, 278") | *No C# parser found.* Only SFTP transport scaffolding in `infrastructure/argo-workflows/x12-276-ingest.yaml`, `x12-277-rfai.yaml` | **ABSENT (as X12) / SCAFFOLD** | No 276/277/278/835 transaction parser in `src`. `docs/deployment/COSMOS-DB-DEPLOYMENT.md:295-297` itself lists "835 / 277 / 278 … (coming soon)." The **278 capability** is covered instead via FHIR PAS (below), not X12. | Correct the README diagram: 837/834/270/271 supported; 276/277/278/835 X12 = roadmap. |
| 13 | FHIR R4 projections + CMS-0057-F PAS/CRD/DTR/Bulk (README; CMS matrix) | `fhir-service/Controllers/` — PasController (346 LOC), DtrController (247), CrdController (143), BulkExportController (133); `src/fhir` mappings | **IMPLEMENTED (surfaces) / integration-required** | Real controllers with auth. CMS-0057-F readiness matrix honestly frames these as "technical readiness for implementation," not certified compliance. | Keep; the matrix is the model for how to phrase everything. |
| 14 | Legacy CAPS adapters — Facets / QNXT / HealthEdge / ChangeHealthcare / Availity (implied by "deploy alongside Facets, QNXT, HealthEdge") | `*/Adapters/*` across claims/benefit-plan/provider/eligibility services | **SCAFFOLD (intentional)** | ~90 of the 121 `NotImplementedException` hits are here; every adapter method throws with a migration-TODO message. Clearly labeled as not-yet-shipped. | Acceptable **iff** no doc claims live vendor integration. Confirm sales copy doesn't imply working Facets/QNXT connectors. |
| 15 | Multi-tenancy / "unlimited tenant support, complete logical isolation" (`site/insights.html`, `assessment.html`) | `TenantMiddleware.cs`; per-query `.Eq(TenantId)` filters; per-tenant Mongo/Cosmos containers | **PARTIAL** | Data-layer filtering is disciplined but **not centrally enforced** (0 EF global query filters — Mongo/Cosmos, not EF); tenant identity falls back to an unauthenticated header. See §4. | Drop "unlimited"; describe isolation honestly (per-tenant containers + per-query filters). |
| 16 | Mass Adjudication console / operator drilldown (README screenshots) | `src/portal` (102 razor pages); `MassAdjudicationRunsController.cs` | **IMPLEMENTED** | Real console with run summaries, claim drilldown; screenshots in episode evidence. **Caveat:** some portal pages are placeholders (`ProviderVerification.razor`: "Coming soon" while the backend service/engine exist). | Keep; sweep portal for "coming soon" pages before demos. |
| 17 | Million Claim Challenge benchmark (README; benchmarks) | `src/CloudHealthOffice.BenchmarkClaimGenerator`, `src/tools/mcc-runner`, `src/tools/mcc-platform-validator`, `scripts/run-mcc-local-k8s.sh` | **IMPLEMENTED + REPRODUCIBLE** | Full harness present; §7. | Keep; consolidate the numbers (§7). |
| 18 | Payments: Stripe Connect / NACHA / capitation / FFS / premium billing (four product lines; `assessment.html`) | `capitation-service`, `ffs-service`, `premium-billing-service`, `payment-service` (`StripeConnectService`, `NachaCreditFileService` per CHANGELOG) | **PRESENT (not deep-verified)** | Real services with tests referenced in CHANGELOG; not individually exercised in this pass. | Spot-verify before making revenue-cycle claims to investors. |
| 19 | "HIPAA-compliant" / SOC 2 / HITRUST | — | **CORRECTLY NOT CLAIMED** | `trust.html:506` explicitly disclaims SOC 2 Type II / HITRUST certification; LICENSE HIPAA notice says "makes no warranty of HIPAA compliance." | Keep this discipline; make sure `assessment.html` SLA language doesn't reintroduce an implied production/HIPAA guarantee. |
| 20 | "99.9% uptime SLA," "Azure 99.9% SLA + geo-redundancy," "100% elimination" (`assessment.html`) | — | **ABSENT / OVERCLAIM** | No running SLA'd production service exists; README says results are "local Kubernetes evidence, not a production cloud capacity claim." | Cut or relabel as "target architecture," not current guarantee. |

### Stub / TODO inventory tied to claims

- **121 `NotImplementedException`**, ~90 in intentional, clearly-labeled legacy CAPS migration adapters (claims #14). The remainder are honestly captioned Phase-2 boundaries: benefit augment-mode (#2), COB secondary (#4), Cosmos claim-adjustment persistence (§6), NPPES `NppesHttpAdapter.cs:135`, and QNXT accumulator reversal/reset (`BenefitEngineRegistration.cs:284-289`).
- **128 TODO, 0 FIXME, 8 "coming soon."** The "coming soon" hits are roadmap-honest (portal pages, sales case studies, Cosmos-path services) — none masquerade as done.
- Net: **very few undisclosed stubs.** The gap is not hidden scaffolding in the code — it's a couple of *doc/site* claims that run ahead of the code (#12, #15, #20).

---

## 3. 🔴 SECRETS & PHI ACROSS GIT HISTORY (highest severity — read first)

**Bottom line: one real committed credential; no real PHI found. But the scan window was limited — see the caveat.**

### Findings

| Severity | What | Location | Still reachable? | Notes |
|---|---|---|---|---|
| 🟠 **Medium** | Hardcoded DB password `CloudHealthOffice2026!` | `src/services/reference-data-service/k8s/reference-data-service-deployment.yaml:18` (a `Secret` with `stringData.POSTGRES_PASSWORD`) in a ConfigMap block set to `ASPNETCORE_ENVIRONMENT: "Production"` | **Yes — live at HEAD and present since the first commit** (`git log -S` → introduced in `3ebdc41`). | It's a self-hosted default for the reference-data Postgres, not a real cloud credential — but it's a literal secret in a "Production"-labeled manifest, contradicting the `REPLACE_WITH_*` discipline used in `infrastructure/k8s/secrets/*`. Rotate + template it. |
| 🟢 Info | k8s secret manifests | `infrastructure/k8s/secrets/{database,backend-api,clearinghouse-sftp,kafka-sasl,s3-credentials}-secret.yaml` | n/a | All values are `REPLACE_WITH_*` placeholders. Clean. |
| 🟢 Info | SSN/MRN/member-like values | `member-service/.../PiiIdentifierDedupeTests.cs` (`111-22-3333`, `444-55-6666`), `src/fhir/examples.ts` (`456-78-9012`) | n/a | All obviously synthetic test values. No real PHI. |
| 🟢 Info | Connection strings / keys in tests & docs | `HealthCheckExtensionsTests.cs`, `PricingApiServiceTests.cs`, `payer-to-payer-api.test.ts`, site HTML examples | n/a | Fake fixtures (`AccountKey=dGVzdA==` = base64 "test"), `key-abc`, `UseDevelopmentStorage=true`. Clean. |
| 🟢 Info | `appsettings*.json`, `docker-compose*.yml` | repo-wide | n/a | No real API keys, Anthropic keys, or DB passwords; env-var substitution used. Clean. |

The `password=password` / `$PASSWORD` matches in `infrastructure/argo-workflows/*.yaml` are variable references (SFTP creds injected from env/params), **not** hardcoded secrets.

### ⚠️ Scan-coverage caveat (important, state this honestly to investors)

This working checkout is a **shallow clone (50 commits, oldest 2026-07-30)** whose first commit is a single 3,270-file / 652K-line bulk import — i.e., the pre-July-2026 history is **not present in this checkout**. The scan above is definitive for the 50 commits available and for HEAD, but a **full-history** secret scan must be run against origin to be conclusive. Reassuringly, CI already runs TruffleHog and Gitleaks (`.github/workflows/security-scan.yml`, `pre-approval-checks.yml`, `pr-lint.yml`) and there is a dedicated `phi-validation.yml` workflow — so the control exists; confirm it runs `--since-commit`/full-history mode and review its historical results.

**Actions:** (1) rotate + template the Postgres password; (2) run `trufflehog git file://. --since-commit <root>` and `gitleaks detect` against full origin history and attach the clean report to the data room; (3) keep the PHI-validation CI gate.

---

## 4. Security & Auth Posture

**Overall: 🟡 PARTIAL — hardened at the interoperability edge, header-trusted internally.**

### Auth model

- **Authenticated (7/35 services):** `fhir-service`, `smart-auth-service`, `authorization-service`, `attachment-service`, `trading-partner-service`, `idcard-service`, `reference-data-service` — these wire `AddAuthentication`/JWT and use `[Authorize]` (18 `[Authorize]` usages, concentrated in fhir-service). This is the CMS-0057-F / SMART-on-FHIR / external-partner edge, and it is the right place to have auth. **Hardened.**
- **Unauthenticated (28/35 services):** including the PHI-bearing core — `claims-service`, `member-service`, `eligibility-service`, `coverage-service`, `payment-service`, `benefit-plan-service`, `claims-examiner-service`, etc. No `AddAuthentication`, no `[Authorize]`. They rely entirely on `TenantMiddleware` for tenant scoping and assume an authenticating gateway/mesh in front. **Roadmap/undocumented.**

### Multi-tenancy isolation

`TenantMiddleware.ExtractTenantId` (`src/services/shared/CloudHealthOffice.Infrastructure/Middleware/TenantMiddleware.cs:70-99`) resolves tenant identity with this precedence:

1. JWT claim (`tenant_id` / `extension_TenantId` / `GroupSid`) — **only when `context.User.Identity.IsAuthenticated`**;
2. else the **`X-Tenant-ID` request header**;
3. else the **`X-Dev-Tenant-ID` header**;
4. else, if `RequireTenantId == false` (**the default**), silently fall back to `DefaultTenantId = "default-tenant"`.

Consequences a security reviewer will raise:

- For the 28 unauthenticated services, `IsAuthenticated` is always false, so **tenant identity comes entirely from a client-supplied header**. Anyone who can reach the service can send `X-Tenant-ID: <victim-tenant>` and be treated as that tenant. There is no check that the caller is authorized for the tenant it names. **This is the cross-tenant-PHI-read risk in one sentence.**
- `RequireTenantId` defaults to `false` ("lenient mode for backward compatibility"), so a *missing* tenant context resolves to `default-tenant` rather than returning 401. No service in the repo sets it to `true`.
- Data-layer isolation itself is **disciplined but not centrally enforced**: there are **0 EF global query filters** (persistence is MongoDB/Cosmos, not EF Core), so isolation depends on every repository query explicitly including `.Eq(x => x.TenantId, tenantId)` plus per-tenant containers. That pattern is followed consistently in the code reviewed (e.g. `MassAdjudicationRunRepository.cs:134,144,162`, `ClaimImportTransactionRepositoryMongo.cs:30`), but a single omitted filter in any future query = a leak, with no backstop.

**Honest summary by area:**

| Area | Posture |
|---|---|
| External FHIR / SMART / partner APIs | **Hardened** (JWT + `[Authorize]`) |
| Internal core services (claims/member/eligibility/coverage/payment) | **Partial / roadmap** — no authn; header-trusted tenant |
| Tenant data filtering | **Partial** — disciplined per-query + per-tenant containers, no central enforcement |
| Tenant identity source | **Roadmap** — unauthenticated header fallback; `RequireTenantId` defaults false |
| Log-injection / input hygiene | **Good** — consistent `SanitizeForLog` newline stripping in orchestrator + middleware |

**This is a defensible architecture** (internal services behind an authenticating ingress is a normal microservice pattern), but the repo neither documents that trust boundary nor enforces it in code. **Actions:** publish a trust-boundary/threat model; set `RequireTenantId = true` for production configs; either add authn to internal services or gate them so header-based tenancy is impossible from outside the mesh; add a defense-in-depth check that the authenticated principal is entitled to the requested tenant.

---

## 5. Licensing & IP Hygiene

**Overall: 🟢 GREEN (two minor hygiene nits).**

- **BSL 1.1 is correctly applied.** `LICENSE`: Licensor Aurelianware, Inc.; Licensed Work "Cloud Health Office"; Change Date `2030-03-08`; Change License Apache 2.0; **Additional Use Grant present** (non-production use for eval/dev/test/staging). Consistent with `NOTICE`, `LICENSE_SUMMARY.md`, and `package.json` (`"license": "BUSL-1.1"`). Supports a clean "we own our IP, source-available not open-source" story.
- **No conflicting license headers** — zero MIT/GPL/proprietary copyright headers from third parties in `src`; no `vendor/`, `third-party/`, or `externals/` directories.
- **Self-audited.** `LICENSING_AUDIT.md` (dated 2026-03-27) documents correcting 50+ "open-source"→"source-available" mislabels across 30+ files — good-faith IP hygiene an investor will appreciate.
- **npm supply-chain gate present:** `.audit-ci.json` (moderate threshold) with a single documented advisory allowlist (`GHSA-w5hq-g745-h8pq`).

Nits:

1. **Stray committed archive:** `CHO-ProviderEnrollment-PriorAuthRuleEngine.zip` (93 KB) at repo root contains `CloudHealthOffice.ProviderEnrollmentService/*.cs` — a **duplicate snapshot of source that already lives at `src/engines/CloudHealthOffice.ProviderEnrollmentService`**. Not a legal problem, but it bloats the repo, invites drift, and reads as sloppiness in a data room. Remove it.
2. **Canonical-form deviation (cosmetic):** the BSL "Additional Use Grant" is placed at the bottom of `LICENSE` rather than in the parameter header block; the grant is present and unambiguous, so this is purely stylistic. Apache 2.0 as Change License satisfies the BSL GPL-compatibility covenant (compatible with GPLv3, "a later version").

---

## 6. Architecture & Build Integrity

**Overall: 🟡 YELLOW — coherent architecture and mature CI; a clean build could not be independently confirmed in this environment, and the git graph doesn't tell an incremental story.**

### Build

- **Could not run a clean build here** — no .NET SDK is available in this audit environment (`dotnet` not installed), and a 318K-LOC / 35-service solution restore/build is heavy. This must be verified by actually building from a clean checkout before DD.
- **CI does build and test everything.** `.github/workflows/test-dotnet.yml` discovers every test project and runs `dotnet restore` → `build --no-restore` → `test` on ubuntu with .NET 8, uploads Cobertura coverage, and runs Jest for TS with Codecov. `quality-gate.yml` adds smoke tests + structural "sanity" validation. `security-scan.yml`, `phi-validation.yml`, `pr-validation.yml`, `codecov.yml` round it out. This is a mature pipeline.
- **Recommendation:** capture a clean `docker compose --profile core up` + `curl /health/live` run and a green full-CI run link for the data room; confirm the per-project test loop fails the build on any project failure (the loop is written to continue-and-collect).

### Dead / duplicate paths

- **MongoDB vs Cosmos DB — both live, config-switched, *not* dead code.** `AddChoInfrastructure` (`shared/.../ServiceCollectionExtensions.cs`) wires **either** a `MongoClient` **or** a `CosmosClient` based on configuration. This is an intentional dual-backend, but it is a **real maintenance + parity surface**, and parity is not complete: `ClaimAdjustmentRepository.CreateAsync` throws `NotImplementedException` on Cosmos ("requires MongoDB… Cosmos persistence is deferred", `:229`) and `GetByIdAsync` silently returns null (`:261`). So **capability 5.12 (claim adjustments) is broken on the Cosmos backend** even though Cosmos is a first-class, documented deployment target (`docs/deployment/COSMOS-DB-DEPLOYMENT.md`).
- **A third persistence tech:** `reference-data-service` uses **PostgreSQL** (its own StatefulSet), separate from the Mongo/Cosmos duo.
- In-memory repositories (`InMemoryClaimImportTransactionRepository`, etc.) coexist with Mongo repos for dev/test — normal, not dead.
- **Action:** publish a one-line "MongoDB is the reference/tested backend; Cosmos is supported with these known gaps; Postgres is used only by reference-data-service" statement so the persistence story is unambiguous.

### Tests & coverage

- **Breadth is real:** 51 test `.csproj`, ~509 test files. Auto-refreshed metric (`docs/guides/FEATURES.md:18`) claims **"5,515 automated tests across 44 test projects."** The test **count** is machine-generated by CI (honest mechanism); the **44 vs 51 project count** is a minor inconsistency worth reconciling. `FEATURES.md:357-358` also states "100% coverage (FHIR module)" / "19/19 tests" — true but a small, module-scoped sample; don't let it imply repo-wide 100%.

### Commit-history story

- The visible history is **50 commits (from 2026-07-30), fronted by a single 3,270-file / 652K-insertion bulk import** — so the git graph itself does **not** narrate an incremental build. This is either a squashed/re-homed history or the boundary of a shallow clone.
- **Mitigant:** the *narrative* exists elsewhere — a detailed 45 KB `CHANGELOG.md`, ADRs, and 13 dated "Million Claim Challenge" episodes (005–017) documenting incremental evolution and even *failures found and fixed* (episode 15's 23-platform-failure investigation). That episodic, evidence-first trail is more credible than most git logs — but a DD reviewer will still note the "big-bang" initial commit. Be ready to explain the history's provenance.

---

## 7. Benchmark Reproducibility

**Overall: 🟢 GREEN on method and artifacts; 🟡 one framing-consistency fix.**

**A third party can re-run this.** All required pieces are present:

- **Data generator:** `src/CloudHealthOffice.BenchmarkClaimGenerator` — professional/institutional/dental/edge-case claim generators plus synthetic member/provider/benefit-plan/fee-schedule/accumulator generators, seeded (`Seed: 42`, `seed 20260725`, etc.).
- **Harness/runner:** `src/tools/mcc-runner`, `src/tools/mcc-platform-validator` (detailed fixture/scoring components), and `scripts/run-mcc-local-k8s.sh`.
- **Instructions + evidence:** `docs/benchmarks/README.md` specifies a reproducibility checklist (commit SHA, commands, environment, claim count, parallelism, seed, timed phases, validation counts, unsupported breakdown, raw output). Per-episode packets carry the **exact command line, seed, dashboard run ID, and raw validator output** (e.g. `docs/million-claim-challenge/podcast/episode-008/benchmark-results.txt`, `episode-015/raw-evidence-1m-runs.txt`).
- **Honesty of the methodology is a selling point:** `docs/benchmarks/README.md` "Limitations To Preserve" explicitly states local Docker-Desktop results are *not* production capacity claims, unsupported scenarios are roadmap not successes, and payment accuracy is a *separate gate* from disposition correctness. The scoring separates paid/denied/pended/mismatched/unsupported/platform-failures/false-pends/payment-delta.
- **The 1M claim is backed by artifacts:** episode-015 run 2 = 1,000,000/1,000,000 processed, 0 platform failures, 129,981/130,000 workflow checks, payment gate exact within $0.01 — matching the README headline.

**Reproducibility caveat (state it):** correctness gates (workflow checks, payment deltas) are seed-reproducible; **throughput numbers (claims/sec, P95/P99) are hardware-dependent** on the original developer workstation and won't reproduce exactly elsewhere. The docs already say this — keep it.

**The one fix — reconcile the numbers across four documents that currently disagree on the "current best result":**

| Document | What it presents as current |
|---|---|
| `README.md` | Full 1M corpus run, **episode 15**, 129,981/130,000, 0 platform failures |
| `docs/benchmarks/README.md` | Tops out at the **100K** run (not updated for 1M) |
| `docs/POSITIONING.md:288` | **Episode 16**, 155.89 claims/sec, 129,980/130,000, 122 observation timeouts — "episode 15 remains the strict zero-failure baseline" |
| `docs/roadmap/README.md` | "**Full one-million-claim benchmark with strict correctness gates**" listed under **Stretch Goals** |

Having your flagship proof point simultaneously "done (ep15)," "done differently (ep16)," "not in the benchmark index," and "a stretch goal" is exactly the inconsistency to eliminate before DD. Pick one canonical current result, update all four, and move the achieved 1M milestone out of "Stretch Goals."

---

## 8. Prioritized Remediation Punch List (severity × effort)

**Do before any investor sees the repo (high severity and/or high credibility, low effort):**

| Item | Severity | Effort | Section |
|---|---|---|---|
| Rotate + template the `CloudHealthOffice2026!` Postgres password | High | Low | §3 |
| Rewrite/remove `assessment.html` overclaims (SLA/unlimited/100%/"resistance is futile") | High (credibility) | Low | §2 #20, §8 |
| Run full-history TruffleHog/Gitleaks on origin; attach clean report | High | Low | §3 |
| Reconcile benchmark result across README/benchmarks/POSITIONING/roadmap | High (credibility) | Low | §7 |
| Correct README architecture diagram X12 claim (276/277/278 not parsed) | Medium (credibility) | Low | §2 #12 |
| Remove stray `CHO-ProviderEnrollment-PriorAuthRuleEngine.zip` | Low | Low | §5 |
| Drop "unlimited tenant" / "complete logical isolation" absolutes from site copy | Medium (credibility) | Low | §2 #15 |

**Do before a technical/security deep-dive (medium-high severity, medium effort):**

| Item | Severity | Effort | Section |
|---|---|---|---|
| Write security trust-boundary/threat model; set `RequireTenantId=true` for prod | High | Medium | §4 |
| Add authn to internal services **or** prove-and-document the gateway boundary | High | Medium–High | §4 |
| Add defense-in-depth "principal entitled to tenant" check | Medium | Medium | §4 |
| Document Mongo/Cosmos/Postgres backend roles + Cosmos parity gaps | Medium | Low | §6 |
| Independently confirm clean-checkout build + attach green full-CI link | Medium | Low–Medium | §6 |
| Sweep portal for "coming soon" pages before live demos | Medium | Low | §2 #16 |

**Roadmap-honesty (label clearly; no code needed now):**

| Item | Severity | Effort | Section |
|---|---|---|---|
| Relabel COB (primary + secondary-detection; secondary calc = Phase 2) | Low | Low | §2 #4 |
| Relabel benefit augment-mode / legacy-compare as Phase 2 | Low | Low | §2 #2 |
| Verify + describe live network-tier resolution vs. In-Network default | Medium | Low–Medium | §2 #7 |
| Confirm no sales copy implies working Facets/QNXT/HealthEdge connectors | Medium | Low | §2 #14 |
| Reconcile "44 vs 51" test-project count; scope the "100% FHIR coverage" claim | Low | Low | §6 |
| Fix/close Cosmos claim-adjustment persistence gap (or document as Mongo-only) | Medium | Medium | §6 |

---

## 9. Draft "What's Real vs. What's Roadmap" Capability Statement

*(Drop-in copy for the repo / data room. Intentionally under-claims. Verify §2 items 6, 7, 18 before publishing revenue-cycle and network specifics.)*

> ### CloudHealthOffice — What's Real vs. What's Roadmap
>
> CloudHealthOffice is an active, source-available (BSL 1.1) payer-administration platform. We publish reproducible evidence and label gaps explicitly. The following reflects the state of the code, not aspiration.
>
> **Real today (implemented and exercised):**
> - A **staged claims-adjudication pipeline** — structural scrubbing, provider integrity, network/credentialing, benefit calculation, NCCI/MUE edits, coordination-of-benefits detection, AI advisory examination, and persistence — running as an event-driven, multi-tenant service.
> - **Real adjudication engines:** Benefit, NCCI/MUE, Claims-Scrub, Coordination-of-Benefits, and Fee-Schedule (thousands of lines each, unit-tested).
> - **X12 intake for 837 (claims), 834 (enrollment), and 270/271 (eligibility)**, with real parsers.
> - **FHIR R4 surfaces for CMS-0057-F**: Prior-Authorization Support (PAS `$submit`), CRD, DTR, and Bulk Export, with SMART-on-FHIR/OAuth scope enforcement.
> - An **AI claims-examiner** that produces advisory NCCI-modifier recommendations via a real model integration (decision support, not autonomous payment).
> - An **operations portal** with a Mass Adjudication console: run summaries, claim-level drilldown, and payment/lifecycle evidence.
> - The **Million Claim Challenge**: a seeded synthetic-data generator, validator, and runner, with published per-run evidence — including a **full 1,000,000-claim local Kubernetes run** (0 platform failures; payment gate exact within $0.01; workflow checks 129,981/130,000). Results are **local engineering benchmarks, not production capacity claims.**
>
> **Roadmap (designed, partially built, or integration-required — not yet production-complete):**
> - **Coordination-of-Benefits when CHO is secondary/tertiary** (primary adjudication + secondary *detection* ship today; secondary *calculation* is next).
> - **Benefit augment/compare-against-legacy mode** (replace-mode ships today).
> - **X12 276/277 (claim status), 278 (prior auth), and 835 (remittance)** as X12 transactions (the 278 capability is available today via FHIR PAS; 837/834/270/271 are the X12 transactions parsed today).
> - **Legacy CAPS adapters (Facets, QNXT, HealthEdge) and clearinghouse adapters (Change Healthcare, Availity):** interface scaffolding is in place; production connectors are implemented per engagement.
> - **CMS-0057-F production compliance:** we provide the technical readiness surfaces; production compliance requires payer source-system integration, configuration, and the payer's own legal/compliance review. We do **not** claim certified compliance, and we hold no SOC 2 Type II or HITRUST certification today.
> - **Cosmos DB backend parity** with MongoDB (MongoDB is the reference/tested backend; some capabilities are MongoDB-only today).
> - **Cloud production benchmarks and SLAs:** all current performance evidence is local Kubernetes. We make no uptime-SLA or production-capacity claim.
>
> If any statement here is contradicted by code, treat the code as the source of truth and open an issue.

---

*End of audit. Findings only — no repository files were modified. Re-run the full-history secret scan and a clean-checkout build to close the two items this environment could not execute directly (§3 caveat, §6 build).*
