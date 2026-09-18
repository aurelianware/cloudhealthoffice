# CloudHealthOffice — Investor Readiness Audit (September 2026)

**Date:** 2026-09-18
**Scope:** `aurelianware/cloudhealthoffice` @ `main` (HEAD `9dd308d`)
**Method:** static read of code, manifests, docs and site copy; live GitHub Actions
history and failure-log analysis. A clean .NET build was **not** run — no .NET SDK
is available in this environment (see §5).
**Relationship to the prior audit:** this is a **follow-up** to
`AUDIT/dd-readiness.md` (2026-08-21, open in PR #1104). That audit's findings were
re-verified against today's HEAD. Most remain unfixed; this pass fixes the two that
were breaking CI plus the highest-signal hygiene items.

---

## 1. Headline

**The single most damaging thing an investor could find today was that `main` was
red, and had been for weeks.** Three scheduled workflows — Quality Gate, Test
Metrics, and Security Scan — were all failing on `main`. Both root causes are now
fixed in this branch.

Nothing found in this pass contradicts the prior audit's core judgment: **the
engineering substance is real.** The problems are hygiene, consistency and
CI discipline — not fabricated capability.

| Area | Status |
| --- | --- |
| CI on `main` | 🔴 → 🟢 **fixed here** (was red since at least 2026-09-15) |
| Committed credentials | 🟠 → 🟢 **fixed here** (5 manifests) |
| Repo hygiene (stray archive) | 🟠 → 🟢 **fixed here** |
| Buildability by an outsider | 🔴 → 🟢 **fixed here** (35 Dockerfiles) |
| Marketing claims vs. code | 🟠 **open** — needs a founder decision (§4) |
| Benchmark number consistency | 🟠 **open** (§4) |
| Internal service auth / tenant isolation | 🟡 **open, by design** — needs documenting (§5) |

---

## 2. What was broken, and why it mattered

### 2.1 `main` was red — two independent root causes

**Cause A — a package downgrade turned into a hard restore failure.**

`src/services/benefit-plan-service/benefit-plan-service.csproj` pinned
`MongoDB.Driver` **3.11.1** and `Microsoft.Azure.Cosmos` **3.62.1**, while its
project reference `CloudHealthOffice.BenefitEngine` required **3.11.2** and
**3.63.0**. With warnings-as-errors, NuGet turns that downgrade into `NU1605` and
the restore fails outright:

```
error NU1605: Warning As Error: Detected package downgrade: MongoDB.Driver from 3.11.2 to 3.11.1
error NU1605: Warning As Error: Detected package downgrade: Microsoft.Azure.Cosmos from 3.63.0 to 3.62.1
```

This failed **Security Scan** *and* **Test Metrics**, and cascaded into every test
project that transitively references `benefit-plan-service` (including
`CloudHealthOffice.AdjudicationController.Tests`).

This is the *second occurrence of the same class of bug* — the August audit
recorded an identical `NU1605` break on `MongoDB.Driver` 3.10.0 vs 3.11.0 from
Dependabot PR #1101. The root cause is structural and is still present: see §3.

**Cause B — the build depended on a private registry that no longer answers.**

35 of 46 Dockerfiles defaulted their base-image registry to a **misspelled,
unreachable** Azure Container Registry:

```dockerfile
ARG REGISTRY=clouhealthoffice.azurecr.io   # note: "clou", missing the "d"
FROM ${REGISTRY}/dotnet/sdk:8.0 AS build
```

The Quality Gate **E2E Tests** and **Load Tests** jobs run
`docker compose -f docker-compose.yml up -d` directly, with no `REGISTRY`
build-arg override, so they used that default and got:

```
failed to authorize: ... 401 Unauthorized
target claims-service: failed to solve: clouhealthoffice.azurecr.io/dotnet/sdk:8.0
```

Two things made this worse than a broken job:

1. **`dotnet/sdk` is not a CloudHealthOffice image.** Sourcing a public Microsoft
   base image through a private ACR meant **nobody outside the Azure tenant could
   build the platform** — including an investor's technical diligence reviewer
   running `docker compose up`.
2. The repo *already knew* the right answer. `_build-service-image.yml` and
   `docker-build.yml` both fall back to `mcr.microsoft.com` when ACR auth isn't
   available. Only the Dockerfile defaults were left pointing at the dead host.

### 2.2 Five credentials committed in plaintext

Live at HEAD, in `Secret` manifests for **PHI-bearing services**:

| File | Credential |
| --- | --- |
| `infrastructure/k8s/coverage-service-deployment.yaml` | `securepassword123` |
| `infrastructure/k8s/member-service-deployment.yaml` | `securepassword123` |
| `infrastructure/k8s/sponsor-service-deployment.yaml` | `securepassword123` |
| `infrastructure/k8s/services/attachment-service.yaml` | `securepassword123` |
| `src/services/reference-data-service/k8s/reference-data-service-deployment.yaml` | `CloudHealthOffice2026!` (in an `ASPNETCORE_ENVIRONMENT: "Production"` manifest) |

These target in-cluster, self-hosted datastores — this is **not** a leaked cloud
key or a PHI breach — but "committed password in a Production-labelled manifest
for the member service" is the first thing a healthcare security reviewer greps
for, and it bypassed the repo's own `infrastructure/k8s/secrets/database-secret.yaml`
template.

### 2.3 A stray source archive at the repo root

`CHO-ProviderEnrollment-PriorAuthRuleEngine.zip` (93 KB, 41 source files) was a
stale duplicate snapshot of code already tracked under `src/engines/`. Verified:
**all 41 files exist in tracked source.** `.gitignore` already contains `*.zip`,
so it had been force-added.

---

## 3. What was changed in this branch

| # | Change | Files |
| --- | --- | --- |
| 1 | Aligned `MongoDB.Driver` → 3.11.2 and `Microsoft.Azure.Cosmos` → 3.63.0 to match `BenefitEngine` | 1 |
| 2 | Repointed base-image registry default to `mcr.microsoft.com` | 35 Dockerfiles |
| 3 | Replaced committed credentials with `REPLACE_WITH_*` placeholders + a pointer to the shared secret template | 5 manifests |
| 4 | Removed the stray source archive | 1 |

**Verification performed:**

- Confirmed `benefit-plan-service` was the *only* project pinning a version below
  what `BenefitEngine` requires — every other consumer inherits transitively, so
  this is not whack-a-mole.
- Confirmed all base images referenced via `${REGISTRY}` (`dotnet/sdk:8.0`,
  `dotnet/aspnet:8.0`, `dotnet/runtime:8.0`, and the `-alpine` variants) are valid
  public tags on `mcr.microsoft.com`.
- Confirmed deploy workflows pass `build-args: REGISTRY=...` explicitly, so
  changing the Dockerfile *default* does not affect Azure deploys.
- Re-parsed all 5 patched manifests with a YAML loader — all valid.
- Confirmed every file inside the removed archive exists in tracked source.

**Not verified:** a clean `dotnet build` / `dotnet test`. No .NET SDK is available
in this environment. CI on this branch is the real gate — see §6.

### The structural fix that still needs doing

Change #1 fixes today's break but **not the cause**. `MongoDB.Driver` is pinned
independently in 35 projects:

| Version | Projects |
| --- | --- |
| 3.6.0 | 27 |
| 3.7.1 | 3 |
| 3.11.2 | 4 |
| 2.28.0 | 1 |

Any Dependabot bump that moves one project ahead of a project that references it
reproduces `NU1605`. **Recommendation:** adopt Central Package Management
(`Directory.Packages.props`) so every project resolves one version. This is the
difference between "they fixed it twice" and "it can't happen again" — and a
technical reviewer will read it that way.

---

## 4. Open items that need *your* decision

These are claims about the business, not bugs. I deliberately did not change them.

### 4.1 Marketing copy that contradicts the README

The README is careful and hedged ("local Kubernetes evidence, not a production
cloud capacity claim"). Two site pages are not. The risk is not any single line —
it is that a reviewer who catches one overclaim discounts the honest 95%.

| File:line | Current | Suggested replacement |
| --- | --- | --- |
| `assessment.html:100` | "…and why resistance to adoption is futile." | "…and where the platform is proven today versus still on the roadmap." |
| `assessment.html:304` | "Source-available, cloud-native solutions will dominate by 2027. Resistance is futile." | "We expect source-available, cloud-native platforms to take meaningful payer share as CMS-0057-F deadlines land." |
| `assessment.html:149` | "Complete logical isolation per tenant" | "Per-tenant data containers with tenant-scoped queries on every read path" |
| `assessment.html:194` | "99.9% uptime SLA target" | Remove here (it is a feature list, so it reads as a commitment), or move under the "Operational Targets" heading where line 501 already sits |
| `assessment.html:501` | "99.9% uptime SLA target" | Keep — it is already under "Operational Targets". Consider "Design target: 99.9% — no production SLA is offered today." |
| `assessment.html:300,529` | "Unlimited multi-payer support" / "Unlimited multi-payer scale" | "Multi-payer by design; validated to 1M claims in a single local run" |
| `assessment.html:330,342` | "Manual faxing/phone calls: 100% elimination" / "Manual data entry errors: 100% elimination" | "…: eliminated for transactions the platform handles end-to-end" |
| `insights.html:575` | "Unlimited tenant support with complete logical isolation." | "Multi-tenant by design, with per-tenant containers and tenant-scoped queries." |

Note on `99.9% uptime SLA`: the copy already says **"target"**, which is softer
than the August audit implied. The fix is mostly placement, not deletion.

### 4.2 The flagship benchmark number disagrees across four documents

Still unreconciled since August. This is the worst possible place for
inconsistency, because it is the proof point everything else rests on:

| Document | Says the current best result is |
| --- | --- |
| `README.md` | Full 1M run, episode 15, 129,981/130,000, 0 platform failures |
| `docs/benchmarks/README.md` | Tops out at the **100K** run |
| `docs/POSITIONING.md:288` | Episode **16**, 155.89 claims/sec, 129,980/130,000 |
| `docs/roadmap/README.md:44` | "Full one-million-claim benchmark" — listed under **Stretch Goals** |

**Pick one canonical result, update all four, and move the achieved 1M milestone
out of "Stretch Goals."** Roughly an hour of work; disproportionate credibility
payoff.

### 4.3 Two smaller consistency gaps

- **README architecture diagram overclaims X12.** `README.md:78` lists
  `276/277, 278` as EDI inputs. There is no X12 parser for 276, 277, 278 or 835 in
  `src` — the 278 capability is delivered via **FHIR PAS**, not X12. Suggested:
  `"X12 / EDI Inputs\n837, 834, 270/271"`, with 276/277/278/835 named as roadmap.
- **Test-project count is wrong.** `docs/guides/FEATURES.md:18` claims "6,933
  automated tests across **44** test projects." The repo has **55** test projects.
  The test *count* is CI-generated and trustworthy; the project count is stale.

---

## 5. Standing findings carried forward (unchanged from August)

These were verified as still accurate and are **not** regressions — they are known
architecture, and they need *documenting* more than fixing:

- **28 of 35 services wire no authentication.** The authenticated 7 are the
  external edge (FHIR, SMART-auth, authorization, attachment, trading-partner,
  idcard, reference-data), which is the right place for it. The internal core —
  claims, member, eligibility, coverage, payment — assumes an authenticating
  gateway in front.
- **Tenant identity falls back to a client-supplied `X-Tenant-ID` header**, and
  `TenantMiddleware.RequireTenantId` still defaults to `false`
  (`TenantMiddleware.cs:140`), so a missing tenant silently resolves to
  `default-tenant`. No service in the repo sets it to `true`.

This is a **defensible** microservice pattern, but it is currently neither
documented as a trust boundary nor enforced in code. The single highest-value
security deliverable before a technical deep-dive is a one-page trust-boundary /
threat model plus `RequireTenantId = true` in production config. "One tenant could
read another tenant's PHI" is the worst sentence to have to answer live, and the
answer is much better than the current documentation implies.

- **89 `NotImplementedException`** (down from 121). The large majority remain in
  clearly-labelled legacy CAPS migration adapters (Facets/QNXT/HealthEdge), which
  is honest scaffolding — acceptable **provided** no sales copy implies working
  vendor connectors.
- `appsettings.Development.json` is tracked in 37 places and the portal copy
  carries a literal Mongo credential. Local-dev defaults in
  `docker-compose.development.yml` use `${MONGO_PASSWORD:-securepassword123}`,
  which is a legitimate pattern and was left alone.
- PR #1141 (portal/API security audit) and PR #1104 (the August DD audit) are both
  still open and unmerged.

---

## 6. Recommended sequence from here

**Before the first investor technical conversation:**

1. **Merge this branch and confirm CI goes green on `main`.** A green default
   branch is the cheapest credibility signal available, and it is currently the
   loudest negative one.
2. Reconcile the benchmark number across the four documents (§4.2).
3. Decide on the marketing copy (§4.1) — the table gives drop-in replacements.
4. Correct the README X12 diagram and the test-project count (§4.3).
5. Close out or merge PR #1104 and #1141 so the data room has no stale open audits.

**Before a technical/security deep-dive:**

6. Adopt `Directory.Packages.props` (§3) so the `NU1605` class of break cannot recur.
7. Publish the trust-boundary / threat model; set `RequireTenantId = true` for
   production (§5).
8. Capture a clean-checkout `docker compose up` + `curl /health/live` transcript
   and a green full-CI run link for the data room. **This is now possible for an
   outside reviewer for the first time**, because of the registry fix in §3.

---

*Findings-only sections: §1, §2, §4, §5. Changes made in this branch are limited to
those itemised in §3. A clean .NET build was not executed in this environment; CI
is the gate.*
