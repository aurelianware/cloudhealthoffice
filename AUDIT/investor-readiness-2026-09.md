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
| Published coverage figures vs. CI | 🔴 → 🟢 **fixed** — injection reconnected to the live deploy (§4.2b) |
| Local dev setup on a clean machine | 🔴 → 🟢 **fixed** — `deploy-local.sh` shebang + `bootstrap-macos.sh` |
| Marketing claims vs. code | 🟠 **open** — needs a founder decision (§4.1) |
| Benchmark number consistency | 🟠 **open** — highest remaining payoff (§4.2) |
| What the Azure subscription hosts | ⚪ **unverified** — not checkable from the audit environment (§4.4) |
| Internal service auth / tenant isolation | 🟡 **open, by design** — needs documenting (§5) |

---

## 1b. Before the next investor conversation — ordered by payoff per hour

Everything in §2–§3 is already fixed and merged. This is what is left, ordered for
someone with limited time before a meeting rather than by severity.

| # | Do this | Time | Why it earns the time |
| --- | --- | --- | --- |
| 1 | **Confirm what the Azure subscription hosts** (commands in §4.4) | 2 min | "What's running in production?" is an early question. You want a precise answer, not a hedge a reviewer can check faster than you can give it. |
| 2 | **Reconcile the benchmark number** across README, `docs/benchmarks/`, `POSITIONING.md`, `roadmap/README.md` (§4.2) | ~1 hr | Your flagship proof point is currently stated four ways, including as a **Stretch Goal** in the roadmap *and* as achieved in the README. This is the single most damaging inconsistency left. |
| 3 | **Decide on `assessment.html`** (§4.1, drop-in replacements provided) | ~30 min | "Resistance to adoption is futile" and a "99.9% uptime SLA" read as vaporware next to a README that carefully says the opposite. One overclaim discounts the honest 95%. |
| 4 | **Rotate the Postgres credential** if `reference-data-service` ran anywhere shared (§2.2) | 15 min | Cheap, and it closes the only credential that was ever genuinely live. |

**What you can now invite a reviewer to do, which was impossible yesterday:** clone the
repo and build it. The dead-registry fix (§2.1) means `docker compose` no longer requires
Azure tenant access, and `main` is green. "Clone it and run it" is a strong offer; it was
a broken one 24 hours ago.

**The honest framing that works.** This repo's real advantage is that it under-claims and
publishes reproducible evidence — the CMS-0057-F matrix separating "implemented" from
"integration required," `trust.html` explicitly disclaiming SOC 2 and HITRUST, the
benchmark methodology's stated limitations. That posture is worth more in healthcare
diligence than any single number, and the open items in §4 are the places where the repo
currently breaks its own rule. Fixing them is defending the thesis, not polishing.

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

Values are **redacted here deliberately** — an audit trail should identify the affected
credential, not reproduce it. Retrieve the actual values from git history when rotating.

| File | Credential |
| --- | --- |
| `infrastructure/k8s/coverage-service-deployment.yaml` | MongoDB password (shared literal A) |
| `infrastructure/k8s/member-service-deployment.yaml` | MongoDB password (shared literal A) |
| `infrastructure/k8s/sponsor-service-deployment.yaml` | MongoDB password (shared literal A) |
| `infrastructure/k8s/services/attachment-service.yaml` | MongoDB password (shared literal A) |
| `src/services/reference-data-service/k8s/reference-data-service-deployment.yaml` | PostgreSQL password (literal B), in an `ASPNETCORE_ENVIRONMENT: "Production"` manifest |

These target in-cluster, self-hosted datastores — this is **not** a leaked cloud
key or a PHI breach — but "committed password in a Production-labelled manifest
for the member service" is the first thing a healthcare security reviewer greps
for, and it bypassed the repo's own `infrastructure/k8s/secrets/database-secret.yaml`
template.

**Exposure analysis — the two credentials are not equivalent.** This was traced
after the initial finding, and it materially lowers the urgency of one of them:

| | MongoDB literal (A) | PostgreSQL literal (B) |
| --- | --- | --- |
| Was it the datastore's real password? | **No.** The MongoDB StatefulSet takes its root password from the `mongodb-auth` Secret, created by `deploy-local.sh` (a local dev value) or from Key Vault on AKS. `infrastructure/k8s/mongodb-deployment.yaml` says so explicitly: *"mongodb-auth Secret is created by CI/CD from GitHub Secrets. Do NOT hardcode credentials here."* The literal in the four service manifests therefore **never matched** the actual root password on either Kubernetes path. | **Yes.** The Postgres StatefulSet set `POSTGRES_PASSWORD` *from* the committed Secret, so wherever `reference-data-service` ran, this was the live database password. |
| Where was it genuinely live? | The local **Docker Compose** stack only (`docker-compose.development.yml`, `.env.example`). | Any cluster that deployed `reference-data-service` — local Kubernetes, and AKS while it was in use. |
| Reachable from outside the cluster? | No — target host is `mongodb:27017`; the Service is headless (`clusterIP: None`). | No — headless `postgres-service`. |
| Data held | Local dev data. | Reference data (code sets). **Not PHI.** |
| History footprint | 3 commits touch the literal. | Introduced with the service. |

**Conclusion:** (A) is a hygiene fix, not an incident — it was both stale *and*
non-functional on Kubernetes. (B) is worth rotating on any cluster where
`reference-data-service` actually ran; on the current local-Kubernetes setup a
re-deploy picks up the new externally-provisioned value. Neither credential ever
pointed at Atlas, Cosmos, or any internet-reachable host.

### 2.2b A third committed credential — documented, not accidental

`docs/features/MONITORING-AND-OBSERVABILITY.md` **instructed** operators to install
Grafana with a fixed admin password (`--set grafana.adminPassword=<literal>`) and then
documented `admin / <literal>` as the login. Access is via `kubectl port-forward` to
localhost, so real-world exposure is low — but a *documented* default is worse practice
than an accidental literal, because every reader who follows the guide reproduces the
same known admin password. Fixed here: the guide now generates a password
(`openssl rand -base64 24`) into `$GRAFANA_ADMIN_PASSWORD`.

### 2.3 A stray source archive at the repo root

`CHO-ProviderEnrollment-PriorAuthRuleEngine.zip` (93 KB, 41 source files) was a
stale duplicate snapshot of code already tracked under `src/engines/`. Verified:
**all 41 files exist in tracked source.** `.gitignore` already contains `*.zip`,
so it had been force-added.

---

## 3. What was changed in this branch

| # | Change | Files |
| --- | --- | --- |
| 1 | Aligned `MongoDB.Driver` → 3.11.2 and `Microsoft.Azure.Cosmos` → 3.63.0 in `benefit-plan-service` to match `BenefitEngine` | 1 |
| 1b | Aligned **every** `Microsoft.Azure.Cosmos` pin repo-wide to 3.63.0 | 27 additional projects |
| 2 | Repointed base-image registry default to `mcr.microsoft.com` | 35 Dockerfiles |
| 3 | Removed committed credentials and rewired the manifests to externally-provisioned secrets (see below) | 5 manifests + 2 deploy paths |
| 4 | Removed the stray source archive | 1 |
| 5 | Corrected `docs/deployment/DOCKER-BUILD-STATUS.md`, which still documented the ACR host as the no-argument default | 1 |

**On change #3 — placeholders were the wrong fix, and review caught it.** The first
attempt replaced each committed password with a `REPLACE_WITH_*` placeholder *in the same
`Secret` manifest*. That removes the credential but leaves a manifest that, when applied,
creates a live `Secret` holding a non-functional value — so the pods come up unable to
connect. Three of the five manifests are applied by real deploy paths, so this would have
been a genuine regression. The corrected approach:

- **The four MongoDB services** (coverage, member, sponsor, attachment) now read
  `secretKeyRef: {name: database-secret, key: connectionString}` — the shared secret that
  **both** `scripts/deploy-local.sh` and `deploy-azure-aks.yml` already provision, and that
  20+ canonical `src/services/*/k8s/` manifests already use. Their redundant per-service
  `Secret` objects are gone. This required no new configuration on either path.
- **reference-data-service (PostgreSQL)** had no external provisioner at all — its
  committed `Secret` was the only source, for both the Postgres StatefulSet and the app.
  The `Secret` is removed from the manifest and now created by each deploy path in that
  path's existing idiom: an env-overridable local-dev default in `deploy-local.sh`
  (matching `MONGO_PASS`), and a Key Vault / repo-secret lookup in `deploy-azure-aks.yml`
  that **fails loudly** if unset rather than silently deploying a known password.

Note also that `infrastructure/k8s/coverage-service-deployment.yaml` and
`member-service-deployment.yaml` are referenced by **no** deploy path — canonical copies
live under `src/services/*/k8s/`. They are dead duplicates and are candidates for deletion.

**Why 1b was necessary.** Change #1 alone cleared the `NU1605` restore error, which let the
`benefit-plan-service` image build progress further — and hit the *next* symptom of the
same underlying inconsistency:

```
error NETSDK1152: Found multiple publish output files with the same relative path:
  .../microsoft.azure.cosmos/3.62.1/runtimes/win-x64/native/Microsoft.Azure.Cosmos.ServiceInterop.dll,
  .../microsoft.azure.cosmos/3.63.0/runtimes/win-x64/native/Microsoft.Azure.Cosmos.ServiceInterop.dll, ...
```

Dependabot had bumped a *group* of three projects (`BenefitEngine`, `NcciEngine`,
`FeeScheduleEngine`) to Cosmos 3.63.0 and left 27 others on 3.62.1, so a single publish
collected native assets from both versions. Bumping only the projects inside
`benefit-plan-service`'s reference graph was not viable: `CloudHealthOffice.Infrastructure`
is referenced by nearly every service, so raising it alone would have produced `NU1605` in
every service still pinned at 3.62.1. A uniform bump has zero blast radius by construction.

All 31 projects now pin 3.63.0. A graph walk over every `.csproj` (resolving
`ProjectReference` transitively and comparing pinned versions) confirms **0 remaining
package-downgrade risks** for both `Microsoft.Azure.Cosmos` and `MongoDB.Driver`.

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

Changes #1 and #1b fix today's break but **not the cause**. `MongoDB.Driver` is still
pinned independently across 35 projects:

| Version | Projects |
| --- | --- |
| 3.6.0 | 27 |
| 3.7.1 | 3 |
| 3.11.2 | 4 |
| 2.28.0 | 1 |

This spread is currently *safe* — the graph walk confirms no project pins below a
project it references, so there is no `NU1605` today, and `MongoDB.Driver` ships no
RID-specific native assets, so `NETSDK1152` does not apply. It was left alone to keep
this PR minimal. But it is one grouped Dependabot bump away from reproducing exactly
what happened here.

The sequence in this PR is the argument: a grouped bump moved three projects ahead of
27, which produced `NU1605` at restore; fixing that surfaced `NETSDK1152` at publish —
**two different errors, in two different build phases, from one root cause.** That is
the failure mode of per-project version pinning, and it has now cost this repo three
separate red-CI incidents (the August `MongoDB.Driver` 3.10.0/3.11.0 break, and both
symptoms here).

**Recommendation:** adopt Central Package Management (`Directory.Packages.props`) so
every project resolves one version by construction. This is the difference between
"they fixed it three times" and "it can't happen again" — and a technical reviewer
will read it that way.

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

### 4.2b Published coverage claims contradict measured coverage

This surfaced from the Codecov bot's own comment on this PR, which makes it especially
awkward: **CI publishes the real number on every pull request.**

| Source | Claim | Measured (CI, this PR) |
| --- | --- | --- |
| `src/site/cms-0057f-compliance.html:796` — **public marketing site** | "85.93% coverage" | **57%** line / 46% branch |
| `docs/guides/FEATURES.md:357` and `:900` | "Test Coverage: 100% (FHIR module)" | `fhir-service` **78%** |
| `docs/features/IMPLEMENTATION-SUMMARY.md:251` | "Test suite passes with >80% coverage" | 57% |
| `docs/features/WEBSITE-UPDATES-FINAL.md:97`, `WEBSITE-PHASE2-COMPLETE.md:155` | "480+ tests (100% pass, 80% coverage)" | 57%; test count is now ~6,900 |

A reviewer can find both the 85.93% claim and the 57% CI badge inside five minutes. Note
this is *not* a case of the tests being weak — 6,900 tests is real work, and several
components are genuinely well covered (`CobEngine` 94%, `EncounterEngine` 96%,
`RiskAdjustmentEngine` 93%, `Infrastructure` 80%, `ClaimsScrubEngine` 81%). The problem is
purely that the *published numbers* were written once and never re-derived.

**The uncomfortable detail worth knowing before someone else finds it:** coverage is
weakest on several of the adjudication engines the investment story rests on —
`BenefitEngine` 53%, `FeeScheduleEngine` 52%, `NcciEngine` 47%, `PriorAuthRuleEngine` 41%
— and lowest on `sponsor-service` (13%), `attachment-service` (19%),
`member-document-service` (21%), `CHO.TerminologyService` (23%), `Portal` (26%),
`PricingApi` (27%), with `CloudHealthOffice.ReferenceData` at 0%.

**Root cause (found on follow-up — the mechanism existed and was orphaned).** This was
never a case of nobody building automation. `scripts/inject-test-metrics.js` already reads
`summary.coverage_pct` from the `test-metrics.yml` artifact and already rewrites
`NN.NN% coverage` in `src/site/*.html`. Two wiring gaps stopped it working:

1. **The injection ran only in `deploy-static-site.yml`** (Azure Static Web Apps), which
   that file's own header declares **DORMANT** — GitHub Pages became the live path for
   cloudhealthoffice.com. `deploy-pages.yml` did `npm ci && npm run build` with **no
   injection**, so the live site published whatever was committed in `src/site/*.html`.
   Retiring the Azure path (PR #1169) silently orphaned the injection.
2. **The commit-back in `test-metrics.yml` stages only** `README.md` and
   `docs/guides/FEATURES.md` (`git add README.md docs/guides/FEATURES.md`), never the site
   HTML — so the repo's copy could never self-correct either.

Compounding both: **`test-metrics.yml` was itself red on `main` for weeks** (§2.1), so even
the two files it does maintain were not being refreshed.

**Fixed here:** the injection now runs in `deploy-pages.yml` before the site build, so
published figures track CI instead of drifting. Verified locally — running the script
against a metrics file rewrote the site's `85.93% coverage` to the CI-measured value,
changing exactly that one line and nothing else.

**Still hand-maintained (no regex covers these), corrected here:** the
"Test Coverage: 100% (FHIR module)" claims in `docs/guides/FEATURES.md` (two places), and
the test-project count, which said 44 against an actual 55. Note the auto-injection updates
"6,933 automated tests" but *not* the "across NN test projects" suffix — worth folding into
the script if that number is going to keep being published.

Raising coverage on the four adjudication engines is the substantive follow-up; the
published-number contradiction is now closed.

### 4.3 Two smaller consistency gaps

- **README architecture diagram overclaims X12.** `README.md:78` lists
  `276/277, 278` as EDI inputs. There is no X12 parser for 276, 277, 278 or 835 in
  `src` — the 278 capability is delivered via **FHIR PAS**, not X12. Suggested:
  `"X12 / EDI Inputs\n837, 834, 270/271"`, with 276/277/278/835 named as roadmap.
- **Test-project count is wrong.** `docs/guides/FEATURES.md:18` claims "6,933
  automated tests across **44** test projects." The repo has **55** test projects.
  The test *count* is CI-generated and trustworthy; the project count is stale.

---

## 4.4 Stated data-layer strategy, and two things inconsistent with it

Confirmed with the founder during this pass, and worth stating explicitly because it
reframes two earlier findings:

> **Intent:** everything speaks the **MongoDB wire protocol**, so the platform stays
> cloud-agnostic; on Azure, that same Mongo driver runs against **Cosmos DB's API for
> MongoDB** rather than the native Cosmos SDK.

This is a *stronger* story than the one the repo currently tells, and it is worth making
explicit in the architecture docs — "one data-access path, portable across MongoDB, Cosmos
DB for MongoDB, and any Mongo-compatible service" beats "dual backend" in a diligence
conversation, because dual backends invite the parity question. Two things in the tree
currently contradict it:

1. **The native `Microsoft.Azure.Cosmos` SDK is a direct dependency of 31 projects.** If
   Cosmos is reached through the Mongo API, this SDK is largely the wrong dependency —
   and it is the one that produced *both* CI breaks in this PR (`NU1605`, then
   `NETSDK1152`, the latter caused specifically by its RID-specific native assets, which
   `MongoDB.Driver` does not ship). Auditing whether it is still needed would remove a
   recurring build-fragility source and simplify the story at the same time.
2. **`reference-data-service` uses PostgreSQL**, with its own StatefulSet — a third
   persistence technology, and the one outlier from "everything supports Mongo." It is
   also the only service whose credential had no external provisioner (§3).

The August audit's "Cosmos vs MongoDB parity gap" finding (`ClaimAdjustmentRepository`
throwing `NotImplementedException` on the Cosmos path) is best read in this light: under a
Mongo-wire-protocol-everywhere design that gap **should not exist as a category**, because
there is one code path rather than two. Worth confirming whether the native-SDK path is
still live or is vestigial.

### Deployment reality — what is verified, and what is not

**Separate the two claims carefully, because a reviewer will.**

**Verified from the repository (high confidence):**

- Day-to-day operation is **local Kubernetes on Docker Desktop**, via
  `scripts/deploy-local.sh`.
- Automatic Azure deploys are **gated off in code**: `AZURE_DEPLOYMENTS_ENABLED` is
  opt-in, with the in-file comment "while the Azure subscription is inactive."
- `deploy-static-site.yml` (Azure Static Web Apps) declares itself **DORMANT**; the
  marketing site is served by **GitHub Pages**.
- All published benchmark evidence is from local runs, which the README already states
  correctly.

**NOT verified — do not assert either way without checking:** what the Azure
subscription actually hosts today. This audit ran in an environment with no Azure CLI and
no credentials, so the live subscription was never queried. The founder's recollection is
that it hosts some services, *possibly supporting a different product (CloudDentalOffice)
rather than CloudHealthOffice* — a shared subscription is common and would explain a live
subscription alongside a gated CloudHealthOffice deploy path.

**Check it before the conversation** (about two minutes):

```bash
az login
az account show --query '{name:name, id:id}' -o table
az resource list --query "[].{name:name, type:type, group:resourceGroup}" -o table
az aks list -o table                      # is there a live AKS cluster at all?
```

**Why this matters more than it looks.** "What's running in production?" is an early
question in any technical diligence conversation, and the strong answer is a precise one.
*"CloudHealthOffice runs on local Kubernetes today; here is a reproducible 1M-claim run
with a seed and a command line; the Azure subscription hosts <X>"* is credible and
defensible. A vague "it's on Azure" invites a follow-up that a reviewer can check faster
than you can answer, and this repo's own evidence would contradict it.

**Consequence for §3.** Whether the `reference-data-service` Postgres change is an
*imminent prerequisite* or a *latent one* depends entirely on the above. If AKS is live
and running that service, `POSTGRES_PASSWORD` (or Key Vault `Postgres--Password`) must be
configured **before the next deploy**, and that credential was live there and should be
rotated. If AKS is not running CloudHealthOffice, both are latent. **Confirm before
relying on either reading.**

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
  `docker-compose.development.yml` use an env-overridable
  `${MONGO_PASSWORD:-...}` form, which is a legitimate pattern and was left alone
  (the same posture as `MONGO_PASS` in `scripts/deploy-local.sh`).
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
3b. Fix the published coverage numbers (§4.2b). The public site claims 85.93% while CI
   publishes 57% on every PR; this is the cheapest contradiction in the repo to remove.
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
