# CloudHealthOffice Portal — UX, Workflow, and Security Audit

**Status:** Inventory & analysis (no broad implementation changes)
**Scope of this document:** the operator-facing **Blazor Server portal**
(`src/portal/CloudHealthOffice.Portal`) and the **35 backend microservices**
(`src/services/*`) it consumes.
**Audit date:** 2026-08-31
**Method:** static source review + a repeatable endpoint-inventory script
(`scripts/audits/inventory-endpoints.py`).

This is a blueprint for turning findings into an ordered series of small,
reviewable implementation PRs (see [§16 Roadmap](#16-implementation-roadmap)).
It intentionally does **not** change production behavior. The only code added is
documentation and a read-only inventory script.

> **Relationship to existing security docs.** `docs/security/` already contains a
> strong *infrastructure/HIPAA* narrative (private endpoints, HSM-backed keys,
> managed identity, encryption-at-rest, PHI telemetry scrubbing) and rates the
> posture 8.3/10 with "no critical issues." Those infrastructure controls are
> real and are this platform's primary line of defense. This audit looks at a
> different layer — **application and service authorization, portal UX, and payer
> workflows** — and finds that the app/service layer leans almost entirely on
> network isolation, with little in-app enforcement. The two views are
> complementary, not contradictory: today's compensating control (services are
> ClusterIP-only, reachable through the portal) is exactly what keeps the
> app-layer gaps from being internet-exploitable. The recommendations here are
> about adding defense-in-depth so a single network misconfiguration, SSRF, or
> compromised pod is not a total-compromise event.

---

## Table of contents

1. [Portal inventory](#1-portal-inventory)
2. [Payer platform / persona audit](#2-payer-platform--persona-audit)
3. [End-to-end workflow audit](#3-end-to-end-workflow-audit)
4. [Navigation & information architecture](#4-navigation--information-architecture)
5. [Authentication audit](#5-authentication-audit)
6. [Authorization & role security audit](#6-authorization--role-security-audit)
7. [Proposed permission model](#7-proposed-permission-model)
8. [API endpoint audit](#8-api-endpoint-audit)
9. [OAuth / service-to-service security](#9-oauth--service-to-service-security)
10. [Tenant isolation audit](#10-tenant-isolation-audit)
11. [PHI exposure audit](#11-phi-exposure-audit)
12. [Enterprise auditability](#12-enterprise-auditability)
13. [Dashboard / landing experience](#13-dashboard--landing-experience)
14. [UX quality audit](#14-ux-quality-audit)
15. [Prioritized gap analysis](#15-prioritized-gap-analysis)
16. [Implementation roadmap](#16-implementation-roadmap)
17. [Validation & what could not be evaluated](#17-validation--what-could-not-be-evaluated)

Supporting artifacts:

- [`api-security-inventory-generated.md`](api-security-inventory-generated.md) —
  full machine-generated endpoint table (622 endpoints).
- [`scripts/audits/inventory-endpoints.py`](../../scripts/audits/inventory-endpoints.py) —
  regenerates the inventory (`python3 scripts/audits/inventory-endpoints.py`).

---

## 1. Portal inventory

The portal is an **ASP.NET Core 8 Blazor Server** app using **MudBlazor**
("The Sentinel" obsidian/neon dark theme). It renders ~50 routed pages backed by
~30 typed HTTP service clients. Navigation is a single global `MudNavMenu` in
`Shared/MainLayout.razor`.

**Authentication classification used below**
- **Anon** — page is reachable without sign-in (`[AllowAnonymous]` *or* no
  `[Authorize]` — see [§5](#5-authentication-audit); there is no fallback
  authorization policy, so "no attribute" == anonymous).
- **AuthN** — bare `[Authorize]`: any authenticated user. **No portal page uses
  role or policy authorization.**
- **UI-admin** — bare `[Authorize]` on the page, but the *nav link* is hidden
  unless `_isTenantAdmin`/`_isPlatformAdmin`. The page itself is **not** role-gated
  (see [FIND-AUTHZ-02](#15-prioritized-gap-analysis)).

### 1.1 Authenticated operator pages

| Route | Page/Component | Area | Persona(s) | Purpose | AuthN | Authorization | Tenant-aware | UX status | Recommendation |
|---|---|---|---|---|---|---|---|---|---|
| `/dashboard` | `Dashboard.razor` | Operations | All | Landing metrics + work-queue summary | AuthN | `PermissionGate` (any role) | Yes (header) | REDESIGN | Make role-aware (see §13) |
| `/claims` | `Claims.razor` | Claims | Examiner, Supervisor | Search/list claims | AuthN | `PermissionGate claims:read` (UI only) | Yes | REDESIGN | Work-queue-oriented; server authz |
| `/claims/{id}` | `ClaimDetailsNew.razor` | Claims | Examiner, Supervisor | Claim detail, adjudication, actions | AuthN | UI gate only | Yes | KEEP_WITH_MINOR_CHANGES | Add authority checks on actions |
| `/claims/submit` | `ClaimsSubmit.razor` | Claims | Examiner, EDI | Manual claim entry | Anon* | none | Yes | KEEP_WITH_MINOR_CHANGES | Confirm intended anon; add authz |
| `/mass-adjudication-runs` | `MassAdjudicationRuns.razor` | Claims | Supervisor | Batch re-adjudication | AuthN | UI gate | Yes | KEEP_WITH_MINOR_CHANGES | High-authority op — gate `claims:adjudicate` |
| `/work-queues` | `WorkQueues.razor` | Operations | Examiner, Supervisor | Task queues | AuthN | UI gate | Yes | REDESIGN | Central to examiner workflow — invest |
| `/edi-transactions` | `EdiTransactions.razor` | Operations | EDI Ops | 837/835/277 transactions | AuthN | UI gate | Yes | KEEP_WITH_MINOR_CHANGES | Move under Integrations |
| `/eligibility` | `Eligibility.razor` | Members | Member Services | Eligibility check (270/271) | AuthN | `PermissionGate eligibility:check` | Yes | KEEP | — |
| `/authorizations` | `Authorizations.razor` | UM | UM Reviewer | Prior-auth lookup/review | AuthN | `PermissionGate authorizations:read` | Yes | KEEP_WITH_MINOR_CHANGES | Add decision workflow |
| `/appeals` | `Appeals.razor` | UM | UM Reviewer | Appeals queue | AuthN | `PermissionGate appeals:read` | Yes | KEEP | — |
| `/members` | `Members.razor` | Members | Member Services | Member search/detail | AuthN | `PermissionGate members:read` | Yes | KEEP_WITH_MINOR_CHANGES | Add eligibility/claims context |
| `/providers` | `Providers.razor` | Providers | Provider Ops | Provider search/detail | AuthN | `PermissionGate providers:read` | Yes | KEEP_WITH_MINOR_CHANGES | Link to contracts/claims |
| `/providers/verification` | `ProviderVerification.razor` | Providers | Provider Ops | Credentialing/verification | AuthN | UI gate + `providers:verification.refresh` | Yes | KEEP | — |
| `/enrollment-ops` | `EnrollmentOperations.razor` | Members | Enrollment | 834 enrollment operations | AuthN | `PermissionGate enrollment:*` | Yes | KEEP_WITH_MINOR_CHANGES | — |
| `/benefit-plans` | `BenefitPlans.razor` | Config | Config Analyst | Plan/benefit configuration | AuthN | `PermissionGate benefits:*` | Yes | KEEP_WITH_MINOR_CHANGES | Gate write ops |
| `/sponsors` | `Sponsors.razor` | Config | Config Analyst | Employer/group sponsors | AuthN | UI gate | Yes | KEEP | — |
| `/trading-partners` | `TradingPartners.razor` | Config/Integr. | EDI Ops | EDI trading partners | AuthN | UI gate | Yes | MERGE | Move under Integrations |
| `/reference-data` | `ReferenceData.razor` | Config | Config Analyst | Code sets / reference data | AuthN | `PermissionGate reference-data` | Yes | KEEP_WITH_MINOR_CHANGES | Gate writes |
| `/terminology/crosswalk` | `TerminologyCrosswalk.razor` | Config | Config Analyst | Code crosswalks | AuthN | UI gate | Yes | KEEP | — |
| `/pricing-api` | `PricingApiDashboard.razor` | Config | Config Analyst | Pricing API config/usage | AuthN | UI gate | Yes | KEEP | — |
| `/compliance/pa-rules` | `Compliance/PaRuleExplorer.razor` | Compliance | Compliance, UM | TMPPM PA rule explorer | AuthN | UI gate | Yes | KEEP | Well-built; model page |
| `/finance/ar/accounts` | `ArGlAccounts.razor` | Finance | Finance | GL accounts | AuthN | UI gate | Yes | KEEP | — |
| `/finance/ar/balances` | `ArBalances.razor` | Finance | Finance | AR balances | AuthN | UI gate | Yes | KEEP | — |
| `/finance/ar/cash-posting` | `ArCashPosting.razor` | Finance | Finance | Cash posting | AuthN | UI gate | Yes | KEEP | — |
| `/finance/ar/adjustments` | `ArAdjustments.razor` | Finance | Finance | AR adjustments | AuthN | UI gate | Yes | KEEP_WITH_MINOR_CHANGES | Gate as authority op |
| `/finance/ar/aging` | `ArAging.razor` | Finance | Finance | Aging report | AuthN | UI gate | Yes | KEEP | — |
| `/finance/ar/batch-rules` | `ArBatchRules.razor` | Finance | Finance | AR batch rules | AuthN | UI gate | Yes | KEEP | — |
| `/premium-billing` | `PremiumBilling.razor` | Finance | Finance | Premium billing | AuthN | UI gate | Yes | KEEP | — |
| `/finance/contracts` | `ProviderContracts.razor` | Finance | Provider Ops, Finance | Provider contracts | AuthN | UI gate | Yes | KEEP | — |
| `/finance/contracts/statements` | `CapitationStatements.razor` | Finance | Finance | Capitation statements | AuthN | `PermissionGate` | Yes | KEEP | — |
| `/payment-runs` | `PaymentRuns.razor` | Finance | Finance | FFS payment runs | AuthN | `PermissionGate payments:*` | Yes | KEEP_WITH_MINOR_CHANGES | Gate run/approve separately |
| `/capitation/rate-config` | `CapitationContracts.razor` | Finance | Finance | Capitation rate config | AuthN | `PermissionGate` | Yes | KEEP | — |
| `/capitation/runs` | `CapitationRuns.razor` | Finance | Finance | Capitation runs | AuthN | `PermissionGate` | Yes | KEEP | — |
| `/workflows` | `Workflows.razor` | Monitoring | Ops Admin | Workflow monitoring | AuthN | UI gate | Yes | KEEP_WITH_MINOR_CHANGES | — |
| `/edi-operations` | `EdiOperations.razor` | Monitoring | EDI Ops | EDI operational health | AuthN | UI gate | Yes | MERGE | Consolidate with EDI Transactions |
| `/reports` | `Reports.razor` | Monitoring | Supervisor, Exec | Reporting | AuthN | `PermissionGate reports:*` | Yes | REDESIGN | Role-scoped reporting |
| `/correspondence` | `Correspondence.razor` | Admin | Member Svc, UM | Member/provider correspondence | AuthN | `PermissionGate correspondence` | Yes | KEEP | — |
| `/settings/users` | `UserManagement.razor` | Admin | Tenant Admin | Manage users & roles | UI-admin | `PermissionGate users:manage` (UI) | Yes | KEEP_WITH_MINOR_CHANGES | **Add server-side role check** |
| `/settings` | `Settings.razor` | Admin | Tenant Admin | Tenant settings | UI-admin | UI gate | Yes | KEEP_WITH_MINOR_CHANGES | **Add server-side role check** |
| `/platform/tenants` | `PlatformTenants.razor` | Platform | Platform Admin | Cross-tenant admin | UI-admin | `PermissionGate platform:tenants` (UI) | Cross-tenant | KEEP_WITH_MINOR_CHANGES | **Add server-side role check** |
| `/platform/inquiries` `/admin/inquiries` | `AdminInquiries.razor` | Platform | Platform Admin | Sales inquiries | UI-admin | `PermissionGate platform:inquiries` (UI) | Cross-tenant | KEEP | **Add server-side role check** |

\* `/claims/submit` has **no** `[Authorize]` attribute at all, so with no
fallback policy it is reachable **anonymously** — a claim-submission screen with
no sign-in requirement. Confirm intent; it should require `claims.submit`.

### 1.2 Public / marketing / auth pages

| Route | Page | Purpose | AuthN | Recommendation |
|---|---|---|---|---|
| `/` | `Index.razor` | Router: redirects to `/dashboard` or `/welcome`/`/signup` | Anon | KEEP |
| `/welcome` | `Welcome.razor` | Marketing landing | Anon | KEEP |
| `/docs` | `Docs.razor` | Documentation | Anon | KEEP |
| `/apis` `/fhir-apis` `/api-docs` | `Apis.razor` | FHIR API catalog | Anon | KEEP (merge 3 routes → 1) |
| `/pricing` | `Pricing.razor` | Pricing | Anon | KEEP |
| `/legal` | `Legal.razor` | Legal | Anon | KEEP |
| `/contact-sales` | `ContactSales.razor` | Lead capture | Anon | KEEP |
| `/request-access` | `RequestAccess.razor` | Access request | Anon | KEEP |
| `/signup` | `Signup.razor` | Self-service signup + Stripe | Anon | KEEP |
| `/demo` + `/demo/*` | `Demo.razor`, `DemoWrappers/*` | Sandboxed demo of claims/members/etc. | Anon | KEEP (isolate from real data) |
| `/quickstarts/local-claims` | `LocalClaimsQuickstart.razor` | Local quickstart | Anon | REVIEW — is this business UX? |
| `/login` `/signin` | `SignInRedirect.razor` | Sign-in redirect | Anon | KEEP |
| `/error` | `AppError.razor` | Error page | Anon | KEEP |
| `/Error/AdminConsentRequired` | `Error/AdminConsentRequired.razor` | Entra admin-consent error | Anon | KEEP |

**Observations**
- **Route duplication:** `/apis`,`/fhir-apis`,`/api-docs` all map to one page;
  `/platform/inquiries` and `/admin/inquiries` both map to `AdminInquiries`.
- **Demo surface leaks into product IA:** `Demo.razor` + `DemoWrappers/*` and
  `LocalClaimsQuickstart` are developer/sales artifacts mixed into the same
  routing table as production pages.
- **No breadcrumbs** anywhere; deep pages (`/finance/ar/*`, `/capitation/*`)
  rely solely on the drawer for context.

---

## 2. Payer platform / persona audit

The system already *defines* payer roles in
`Services/UserContextService.cs` (`GetPermissionsForRole`) — a good foundation.
The gap is that these roles only shape **UI rendering**, never server authority,
and no page or endpoint is scoped to a persona.

Roles defined today: `ClaimsExaminer`, `ClaimsSupervisor`, `MemberServices`,
`EnrollmentSpecialist`, `UMCoordinator`, `ProviderRelations`, `Finance`,
`ComplianceOfficer`, `ComplianceViewer`, `TenantAdmin`, `PlatformAdmin`.

### Persona matrix

| Persona | Primary tasks | Existing coverage | Missing capabilities | Recommended landing |
|---|---|---|---|---|
| **Claims Examiner** | Work pended claims, inspect edits/adjudication, request reprocess | **Partial** — `/claims`, `/claims/{id}`, `/work-queues` exist | Assigned-work queue, structured pend-reason actions, "why did this process this way" summary surfaced by default | Claims work queue |
| **Claims Supervisor / Manager** | Monitor queues, assign work, approve overrides, run mass adjudication | **Weak** — `/mass-adjudication-runs`, override endpoints exist but no metrics/authority controls | Queue SLA/aging metrics, assignment UI, override-approval authority, dollar thresholds | Claims operations dashboard |
| **Member Services Rep** | Look up member, eligibility, benefits, claim status | **Good** — `/members`, `/eligibility`, `/correspondence` | 360° member view (eligibility + accumulators + claims + auths on one screen), call-context | Member 360 search |
| **Provider Ops / Network Mgmt** | Provider search, network status, contracts, credentialing | **Good** — `/providers`, `/providers/verification`, `/finance/contracts` | Contract↔claim linkage, fee-schedule editing UX | Provider workspace |
| **Benefit / Config Analyst** | Configure plans, benefits, cost shares, code sets | **Good** — `/benefit-plans`, `/reference-data`, `/terminology/crosswalk` | Config validation & diff/preview, change approval, effective-dating UX | Configuration workspace |
| **UM / Auth Reviewer** | Look up/review auths, appeals, RFAI, decisions | **Partial** — `/authorizations`, `/appeals` exist | Decision (approve/deny) workflow, claim↔auth linking, letter generation | UM review queue |
| **Finance / Payment Ops** | AR, cash posting, payment runs, capitation, premium billing | **Strong** — full `/finance/*` suite | Separation of run vs. approve authority, GL reconciliation dashboard | Finance operations dashboard |
| **Compliance / Audit** | Read-only oversight, audit trails, PA-rule compliance | **Partial** — `/compliance/pa-rules`, `ComplianceOfficer` role (`*:read`) | An **audit trail viewer** (no cross-cutting audit store exists — see §12) | Compliance dashboard |
| **Integration / EDI Ops** | Monitor 837/835/277/834, trading partners, failures | **Partial** — `/edi-transactions`, `/edi-operations`, `/trading-partners` | Unified "Integrations" home, failure triage, reprocessing | Integrations control center |
| **System Administrator** | Manage users/roles, tenant settings | **Partial** — `/settings/users`, `/settings` | Server-enforced admin authority (today UI-only), audit of admin actions | Administration home |
| **Executive / Ops Leadership** | Portfolio KPIs, throughput, financials | **Missing** — `/reports` is generic | Executive operations center: throughput, auto-adjudication rate, financial exposure, SLA | Executive operations center |

**Pages a persona should *not* normally see** (currently visible to every
authenticated user because there is no server-side role scoping):
Finance sees claims-override endpoints; Member Services can reach benefit-plan
configuration; every examiner can open `/settings` and `/platform/*` by typing
the URL (nav is hidden but the page loads — see §6).

---

## 3. End-to-end workflow audit

### 3.1 Claims

**Entry points:** `/dashboard` → work-queue summary → `/claims` (search) or
`/work-queues` → `/claims/{id}`.

`ClaimsController` (`src/services/claims-service`) is feature-rich:
submit, search, `{id}` detail, **`{id}/audit-timeline`**,
**`{id}/adjudication-detail`** (transparency data), status/pend/adjudication
updates, remittance posting, void, `work-queue/{claimId}/assign|override|resolve`,
accumulator totals, 277CA generation. This is genuinely capable adjudication
plumbing.

Can a claims examiner understand…

| Question | Today | Gap |
|---|---|---|
| **1. Why a claim processed the way it did** | Partially — `adjudication-detail` transparency data + `audit-timeline` exist | Not surfaced as the *default* claim-detail experience; requires knowing to look |
| **2. What requires attention** | Weak — work-queue summary exists but not assignment-aware | No "my assigned work," no pend-reason grouping, no SLA/aging |
| **3. What action they can take** | Partial — actions exist (pend/override/resolve/void) | Actions not organized by role authority; unsafe ops (void/override) sit beside safe ones |
| **4. What happens after the action** | Weak | No confirmation of downstream effects (re-adjudication, remittance, member impact) |
| **5. Is the action auditable** | **Partial/No** | Claim *version events* exist per-claim, but there is **no actor identity** on writes (no auth), so "who did this" is unanswerable (see §12) |

**Dead ends / friction:** override and void are single POST/DELETE calls with no
authority gate and no reason-code enforcement at the service; reprocessing is
via `mass-adjudication-runs` (batch) with no single-claim reprocess affordance
tied to the examiner's queue.

### 3.2 Members

`member-service` is the most PHI-sensitive service (36 endpoints). Member,
identifiers (SSN/MBI/Medicaid — **encrypted at rest**, good), family
relationships, alerts, notes, coverage. Portal `/members` + `MemberDetailsDialog`
give demographics; eligibility is a *separate* page (`/eligibility`).

**Gap:** no consolidated **Member 360** (demographics + eligibility + coverage +
accumulators + claims history + authorizations + PCP on one screen). A Member
Services rep on a call context-switches across `/members`, `/eligibility`,
`/claims`, `/authorizations` with no linking.

### 3.3 Providers

`provider-service` (53 endpoints) + `provider-verification-service` +
`provider-contracts-service`. Search, demographics, network status,
credentialing, contracts, fee schedules. Reasonable coverage. **Gap:** no
cross-links provider → their claims, provider → contract → fee schedule in one
flow.

### 3.4 Benefit / product configuration

`benefit-plan-service` (47 endpoints) + `reference-data-service` (22) +
terminology. Plans, benefits, cost shares, exclusions, network tiers, code sets.
`BenefitPlanValidationService` exists in the portal. **Gap:** no config
**diff/preview/effective-dating** UX and no change-approval workflow — benefit and
fee-schedule changes are the highest-blast-radius edits in a payer system and are
currently one unauthenticated PUT away (see §6/§12).

### 3.5 Authorizations

`authorization-service` (has `[Authorize]` + `TenantMiddleware`). Portal
`/authorizations` provides lookup; `SubmitAuthorizationDialog` + detail dialog
exist. **Gap:** the approve/deny **decision workflow** and claim↔auth linking are
thin. `UMCoordinator` has `authorizations:decide` in the permission map but no UI
routes it.

### 3.6 Operations / integrations

`/edi-transactions`, `/edi-operations`, `/workflows`, `/trading-partners`,
`enrollment-import-service`, `encounter-service`, `fhir-service`. Capability
exists but is scattered across "Operations," "Configuration," and "Monitoring"
nav groups. **Gap:** no single Integrations control center; no unified
failure/exception triage; batch/EDI reprocessing is not discoverable.

---

## 4. Navigation & information architecture

Current top-level drawer groups (`Shared/MainLayout.razor`):
**Operations, Members & Providers, Configuration, Finance, Monitoring,
Compliance, Admin**, plus a **Platform** group for platform admins.

**Findings**
- **Organized by concept, mostly good** — the payer-domain grouping is a real
  strength and a better starting point than most portals.
- **Duplicate/《misfiled》 items:** `EDI Transactions` (Operations) vs
  `EDI Operations` (Monitoring) vs `Trading Partners` (Configuration) — EDI is
  split across three groups. `Provider Verification` sits under Members &
  Providers but relates to credentialing/contracts.
- **Technical terminology exposed:** "Mass Adjudication," "Trading Partners,"
  "Reference Data," "PA Rule Explorer," "TMPPM" — reasonable for payer staff but
  worth a glossary/tooltips.
- **No breadcrumbs, no context links.** Claims↔Member↔Provider↔Plan↔Auth↔Payment
  are not linked; each is an island reached from the drawer.
- **Nav is not role-aware** beyond the `_isTenantAdmin`/`_isPlatformAdmin`
  toggles — a Finance user sees the full Claims/UM/Config tree.
- **Deeply nested Finance** (up to 4 levels: Finance → Payments → FFS/Capitation →
  link) buries frequently used pages.

### Recommended future navigation (derived from what exists)

```
Operations        Dashboard, Work Queues, Claims, Mass Adjudication
Claims            Search, Work Queue, Adjustments/Reprocessing
Members           Member 360, Eligibility, Enrollment, Correspondence
Providers         Providers, Verification/Credentialing, Contracts
Authorizations    Auth Review Queue, Appeals, RFAI
Benefits & Products  Plans, Reference Data, Terminology, Pricing API
Payments          FFS Payment Runs, Capitation, Premium Billing, AR (GL/Balances/Cash/Adjustments/Aging)
Integrations      EDI Transactions, EDI Operations, Trading Partners, Enrollment Import, FHIR
Reports           Role-scoped reporting & exports
Compliance        PA Rules, Audit Trail (new)
Administration    Users & Roles, Settings   (role-gated, server-enforced)
Platform          Tenants, Inquiries        (platform-admin only, server-enforced)
```

This is a *proposal*, not a change — the only trivially safe cleanups worth doing
early are collapsing the duplicate `/apis` and `/inquiries` routes and moving EDI
into one place.

---

## 5. Authentication audit

| Aspect | Portal | Backend services |
|---|---|---|
| IdP / token | **Microsoft Entra ID** (OIDC, `AddMicrosoftIdentityWebApp`) | **None on 28/35 services**; 7 register JWT/OIDC |
| Cookie auth | Yes — `.CloudHealthOffice.Auth`, `Secure`/`HttpOnly`, `SameSite=None` | n/a |
| Dev bypass | **`LocalDemo` cookie mode** — issues a `TenantAdmin`+`ClaimsSupervisor` cookie with no credentials | `X-Dev-Tenant-ID` header accepted by tenant middleware |
| Token → downstream | **Not forwarded.** Portal → services carry only `X-Tenant-ID`; **no bearer token** | Services trust the header |
| Service-to-service | Plain HTTP, no auth headers | Same |
| Anonymous exceptions | `/health`, marketing pages, `/signup`, `/demo/*` | `/health`,`/ready`,`/live`,`/swagger` passthrough |

**Key findings**

- **FIND-AUTHN-01 (portal→service token gap).** The portal authenticates the
  human with Entra but calls all backend services with **no propagated
  identity** — only a tenant header (`Services/TenantHttpMessageHandler.cs`).
  Services therefore cannot know *which user* is acting, which is the root cause
  of the auditability gap (§12) and the authorization gap (§6).
- **FIND-AUTHN-02 (`LocalDemo` bypass).** `Program.cs` mints a fully-privileged
  cookie (`TenantAdmin`, `ClaimsSupervisor`) at `/local-demo/sign-in` with no
  credential check. It is correctly guarded by
  `builder.Environment.IsDevelopment() && Authentication:Mode == "LocalDemo"`, so
  it cannot activate in Production as written — but it is a
  never-enable-in-prod switch that must be covered by a config-safety test.
- **FIND-AUTHN-03 (services largely unauthenticated).** 28 of 35 services never
  call `AddAuthentication`. The 7 that do (attachment, authorization, fhir,
  idcard, reference-data, smart-auth, trading-partner) are the external/partner
  and FHIR-facing ones — the intended pattern exists but is applied
  inconsistently.
- **FIND-AUTHN-04 (`UseAuthorization` without `UseAuthentication`).** The shared
  `UseChoInfrastructure` calls `UseAuthorization()` but deliberately not
  `UseAuthentication()`. On services with no auth handler, any `[Authorize]`
  present would fail closed (401) — but since 110/124 controllers have no
  attribute, the practical effect is open endpoints.

---

## 6. Authorization & role security audit

**Current model:** authorization is **UI-rendering trust only**. There is:
- **No** role/policy `[Authorize]` on any portal page (`grep "Authorize("` → 0).
- **No** `[Authorize]` on 110/124 backend controllers; **0** role/policy checks
  in the data-mutating services (claims, member, provider, benefit-plan, payment,
  ar, eligibility, tenant).
- A portal-side `PermissionGate` component + `UserContextService.HasPermission`
  that expands roles → permissions **in the Blazor circuit**. This controls what
  renders, not what the server allows.

### The fail-open RBAC problem (most important authz finding)

`Services/UserContextService.cs` grants **`TenantAdmin` (which expands to
`*:*`)** in *three* fallback paths:

1. Tenant context unavailable (missing `tid` claim / MongoDB down) → `TenantAdmin`.
2. `Services:TenantService` unconfigured or the user lookup throws → `TenantAdmin`.
3. A provisioned user whose `Roles` list is empty → defaults to `TenantAdmin`.

Because `PermissionMatches` treats `*:*` as matching **everything including
`platform:admin`**, a fallback user also passes `_isPlatformAdmin` and sees the
cross-tenant Platform menu. **Authorization fails *open* to full admin.**

### Authorization gap table

| Area | Operation | Current protection | Risk | Recommended permission |
|---|---|---|---|---|
| Claims | View claim | Header tenant only; UI `claims:read` | HIGH | `claims.read` (server) |
| Claims | Pend / update adjudication | none (server) | HIGH | `claims.adjudicate` |
| Claims | **Override** (`work-queue/{id}/override`) | none | **CRITICAL** | `claims.override` + dollar authority |
| Claims | **Void** (`/{id}/void`, `DELETE /{id}`) | none | **CRITICAL** | `claims.void` |
| Claims | Mass adjudication run | none | **CRITICAL** | `claims.adjudicate` (supervisor) |
| Members | Read member + PHI | header tenant only | HIGH | `members.read` |
| Members | Create/update, identifiers (SSN) | none | HIGH | `members.update` |
| Benefits | Modify plan / benefit / cost share | none | **CRITICAL** | `benefits.configure` + approval |
| Providers | Modify provider / contract / fee schedule | none | HIGH | `providers.manage` / `contracts.manage` |
| Payments | Create/approve payment run | none | **CRITICAL** | `payments.run` vs `payments.approve` (SoD) |
| AR | Post cash / adjustments | none | HIGH | `payments.manage` |
| **Admin** | **Create user / assign roles** (`tenant-service`) | **none** (server); UI-hidden | **CRITICAL** | `users.manage` (server) |
| **Admin** | **Define role permissions** (`/api/v1/roles`) | **none** | **CRITICAL** | `roles.manage` (server) |
| Platform | Cross-tenant admin | UI-hidden only | **CRITICAL** | `platform.admin` (server) |

**FIND-AUTHZ-01 — Privileged action plane is unauthenticated.**
`tenant-service` exposes `POST /api/v1/tenants/{tenantId}/users` (create user
with arbitrary roles) and `POST/PUT/DELETE /api/v1/roles` (define
role→permission mappings) with **no auth, CORS `AllowAll`, tenant id taken from
the URL path**. Any caller with network reach can mint a `TenantAdmin` in any
tenant. Mitigated *today only* by ClusterIP network isolation.

**FIND-AUTHZ-02 — UI-only admin gating.** `/settings/users`, `/settings`,
`/platform/tenants`, `/platform/inquiries` hide their nav links behind
`_isTenantAdmin`/`_isPlatformAdmin` but the pages carry only bare `[Authorize]`.
Any authenticated user can navigate directly and load them; the underlying
tenant-service calls then succeed because the service does not check either.

**FIND-AUTHZ-03 — Fail-open RBAC.** (above) Three fallbacks grant `*:*`.

---

## 7. Proposed permission model

Adopt a **capability/permission-based RBAC** (roles are bundles of permissions),
enforced **server-side**, with the portal continuing to use the same permissions
for rendering. Keep the existing role names; normalize permission naming to
`domain.action` (the code currently uses `domain:action` — pick one; the roadmap
assumes `.`).

### Core permissions (justified by existing functionality)

```
claims.read  claims.search  claims.submit  claims.adjudicate
claims.reprocess  claims.adjust  claims.override  claims.void
members.read  members.search  members.update
providers.read  providers.manage  contracts.manage
benefits.read  benefits.configure
authorizations.read  authorizations.review  authorizations.approve
payments.read  payments.run  payments.approve
reference-data.read  reference-data.manage
integrations.read  integrations.manage
reports.read  audit.read
users.manage  roles.manage  settings.manage
platform.admin  platform.tenants  platform.inquiries
```

### Persona → permission mapping (starting point)

| Role | Permissions |
|---|---|
| ClaimsExaminer | claims.read/search/adjudicate, claims.reprocess (request), members.read, providers.read, reference-data.read |
| ClaimsSupervisor | + claims.override, claims.void, claims.adjust, payments.read, reports.read |
| MemberServices | members.read/search, eligibility.read, coverage.read, claims.read, authorizations.read |
| EnrollmentSpecialist | members.read/update, enrollment.*, coverage.* |
| UMCoordinator | authorizations.read/review/approve, appeals.*, members.read, claims.read |
| ProviderRelations | providers.read/manage, contracts.manage |
| Finance | payments.read/run, payments.approve (or split), billing.*, reports.read |
| ComplianceOfficer | *.read, audit.read, reports.read |
| ComplianceViewer | authorizations.read, audit.read |
| TenantAdmin | users.manage, roles.manage, settings.manage + all business reads |
| PlatformAdmin | platform.* + TenantAdmin |

### Keep ABAC-ready (do not over-engineer now)

Design permission checks to accept an optional **context object** (`tenantId`,
`lineOfBusiness`, `department`, `dollarAmount`, `planId`) so future restrictions
(dollar authority on overrides, LOB scoping, plan/product access) slot in without
reworking the model. Ship RBAC first; leave the seam.

---

## 8. API endpoint audit

Full generated inventory:
[`docs/audits/api-security-inventory-generated.md`](api-security-inventory-generated.md)
(re-run `python3 scripts/audits/inventory-endpoints.py`).

**Headline numbers (static scan of 124 controllers):**

| Metric | Value |
|---|---|
| Total endpoints | **622** |
| Protected by `[Authorize]` (net of `AllowAnonymous`) | **51 (8%)** |
| Unprotected at app layer | **571 (92%)** |
| Mutating (POST/PUT/DELETE/PATCH) | **292** |
| Explicit `[AllowAnonymous]` | 4 |

**Services with app-layer authorization** (all or most endpoints):
attachment-service, authorization-service, fhir-service, idcard-service,
reference-data-service, smart-auth-service, trading-partner-service.

**Services with *zero* app-layer authorization** (representative, all
data-sensitive): claims (43), member (36), provider (53), benefit-plan (47),
payment (24), ar (29), eligibility (35), capitation (30), premium-billing (26),
**tenant (35 — includes user/role management)**, appeals (19), coverage (15).

**Highest-risk endpoint classes (unauthenticated + mutating + sensitive):**

| Class | Example | Risk |
|---|---|---|
| Claim financial actions | `POST /api/claims/{id}/void`, `.../override`, `.../remittance` | CRITICAL |
| Benefit/fee configuration writes | `PUT /api/v1/benefit-plans/...`, fee schedules | CRITICAL |
| User/role administration | `POST /api/v1/tenants/{t}/users`, `POST /api/v1/roles` | CRITICAL |
| Payment runs | `POST /api/.../payment-runs` | CRITICAL |
| PHI reads | `GET /api/v1/members/...`, identifiers | HIGH |
| Admin/migration | `AdminMigrationController` (claims-service) | HIGH |

**Cross-cutting API findings**
- **Default CORS = `AllowAll`** (`AllowAnyOrigin/Method/Header`) on every service
  using the shared infra default — combined with no auth, any origin can call any
  reachable service.
- **Swagger is dev-only** (good) but the tenant middleware passthrough includes
  `/swagger` unconditionally.
- No consistent request-size, rate-limit, or input-validation middleware observed
  at the shared layer.

---

## 9. OAuth / service-to-service security

**Endpoint categorization**

| Category | Examples | Recommended auth |
|---|---|---|
| Human interactive portal | Portal → all services | User bearer token (OBO) *or* signed gateway assertion carrying user + tenant |
| External partner API | `fhir-service` (CRD/DTR/PAS), `smart-auth-service`, `trading-partner-service` | **OAuth2 client-credentials + scopes** (already partly present) |
| Internal service-to-service | claims→provider, claims→benefit-plan, portal→tenant | mTLS or client-credentials with a service identity |
| Public API | marketing, `/health` | anonymous (intended) |
| Operational/admin | `AdminMigrationController`, seed endpoints | Admin scope, never anonymous |

**Findings**
- The FHIR/SMART surface (`smart-auth-service`, `fhir-service`) is the one place
  OAuth is taken seriously — it should become the **reference pattern**.
- Internal calls carry **no identity at all** (not even a service token). Recommend
  OAuth2 client-credentials with per-service scopes (e.g. `claims.read`,
  `eligibility.read`, `providers.read`) or mTLS, so an internal caller is
  authenticated and least-privileged rather than implicitly trusted.
- Recommended scopes for external/M2M: `claims.read`, `claims.submit`,
  `eligibility.read`, `providers.read`, `reference-data.read`.

---

## 10. Tenant isolation audit

**How tenant identity is established (today):**
`TenantMiddleware.ExtractTenantId` prefers a JWT claim
(`tenant_id`/`extension_TenantId`/`GroupSid`) **but** falls back to the
**`X-Tenant-ID` (or `X-Dev-Tenant-ID`) request header**, and with
`RequireTenantId = false` (the default everywhere except tests) a *missing*
tenant resolves to `DefaultTenantId = "default-tenant"`.

Because 28/35 services have no authentication, `context.User` is unauthenticated,
so the JWT branch never fires — **tenant identity is taken from a
client-supplied header** on essentially all data services. The portal is the only
thing setting that header, from the user's resolved subscription
(`MainLayout` → `HttpClient.DefaultRequestHeaders["X-Tenant-ID"]`).

**FIND-TENANT-01 (CRITICAL by classification).** Tenant isolation is enforced by
an **unauthenticated, spoofable header**. Any caller that can reach a service can
set `X-Tenant-ID: <any-tenant>` and read or write that tenant's data. The task's
rule — *treat possible cross-tenant access as CRITICAL* — applies. The **only**
compensating control is that services are ClusterIP-only and the portal is the
sole ingress; there is no defense-in-depth if that boundary is crossed (SSRF via
a portal service client, a compromised pod, an accidental ingress/LoadBalancer,
or a future API gateway).

**Downstream propagation:** claims-service pins tenant onto a scoped
`IAdjudicationTenantContext` and re-sends `X-Tenant-ID` to provider/benefit/member
services from background Service Bus consumers — so a spoofed or wrong tenant
propagates coherently through the pipeline (isolation is *consistent*, just not
*trusted*). DB filtering, cache keys, and background jobs all key off this same
untrusted value.

**Recommendation:** make tenant identity come from a **validated token claim**;
set `RequireTenantId = true`; treat the header as a dev-only affordance gated to
Development; and cross-check the token tenant against any header/path tenant,
rejecting mismatches.

---

## 11. PHI exposure audit

**Existing controls worth crediting:**
- SSN/MBI/Medicaid identifiers are **encrypted at rest** via
  `IIdentifierEncryptor` and matched by fingerprint (`member-service`).
- A **`PhiScrubbingSpanProcessor`** scrubs PHI from OpenTelemetry spans.
- Log sanitizers exist in some services (`personal-representative-service`).

**Gaps / recommendations (documentation only — do not strip data now):**
- **Access, not encryption, is the exposure.** Every member/claim read endpoint
  is unauthenticated at the app layer (§6/§8); PHI protection currently relies on
  network isolation + at-rest encryption, not authorization.
- **Over-return:** list/table endpoints return full member/claim DTOs; portal
  tables should request masked/projection DTOs (mask SSN/DOB except last-4) and
  services should offer a `summary` projection.
- **PHI in identifiers via URL/path:** some member identifier lookups accept
  identifier values in the route; ensure PHI is never placed in query strings or
  logs (fingerprint-only lookups already help).
- **Persona-scoping:** Finance/EDI personas can currently read full member PHI;
  scope PHI reads to `members.read`.
- Recommend a lightweight **PHI-in-response test** and a `PhiScrubbing` log audit
  as follow-ups.

---

## 12. Enterprise auditability

**Can CHO answer these today?**

| Question | Answer | Evidence |
|---|---|---|
| Who performed the action? | **No** | Services have no authenticated user; writes carry no actor identity |
| Which tenant? | Partial | From the (untrusted) header |
| What resource changed? | Partial | Per-domain only |
| What operation? | Partial | Claim **version events** capture claim lifecycle; `AiExaminationAudit` captures AI examiner steps |
| When? | Yes | Timestamps on version events |
| What changed (prior→new value)? | Partial | Claim version chain gives before/after for claims; not general |
| Correlation / request id? | Inconsistent | Present in a few services (personal-rep), absent in most |
| Allowed via which permission/policy? | **No** | No authorization is evaluated, so nothing to record |

**FIND-AUDIT-01.** There is **no cross-cutting audit-event model or store**. The
best coverage is claims-specific (version-event stream + `audit-timeline`
endpoint) and AI-examination audit. The high-risk operations that most need an
enterprise audit trail — **benefit/fee-schedule changes, provider changes,
authorization decisions, user/role changes, integration-config changes** — have
**no actor-attributed audit** because the acting user is unknown at the service.

**Recommendation.** Introduce a shared `AuditEvent` (`actor`, `tenantId`,
`resourceType`, `resourceId`, `operation`, `before`, `after`, `permissionUsed`,
`correlationId`, `timestamp`) written by a shared middleware/decorator once user
identity is propagated (depends on FIND-AUTHN-01). Standardize a
`X-Correlation-ID` across all services.

---

## 13. Dashboard / landing experience

`Dashboard.razor` is a **single generic dashboard** for all roles: a welcome bar
(shows `PrimaryRoleDisplayName`), service-health/metrics tiles, and a work-queue
summary, wrapped in a bare `<PermissionGate>` (any role passes). `Index.razor`
routes authenticated users here, unauthenticated to `/welcome`.

It does **not** reliably answer the operational questions: *what needs my
attention, what is assigned to me, what is failing, what is pending, are
interfaces healthy, are claims processing normally, what do I do next.* It shows
tenant-wide metrics, not role-relevant work.

**Recommendation:** evolve toward a **role-aware Operations Center** — the same
shell, role-selected widgets. Proposed dashboards (design only, not built here):

- **Claims Examiner** — My assigned queue, pended-by-reason, aging/SLA, "start next."
- **Claims Supervisor** — Queue depth & aging by team, override-approval inbox,
  auto-adjudication rate, mass-run status.
- **Configuration Analyst** — Pending config changes, validation failures,
  effective-dated changes going live, recent edits.
- **Operations Administrator** — Interface/EDI health, failed batches, service
  health, user-access requests.
- **Executive / Ops Leadership** — Throughput, auto-adjudication %, financial
  exposure (pended $, paid $), SLA attainment, tenant KPIs.

---

## 14. UX quality audit

The portal is visually consistent (one MudBlazor theme, "The Sentinel"). Patterns
worth standardizing into shared components; inconsistencies to resolve:

| Dimension | Observation | Recommendation |
|---|---|---|
| Page headers | Ad-hoc per page | Shared `PageHeader` (title, breadcrumbs, primary action) |
| Tables | Repeated `MudTable` setups, varying columns/paging | Shared `DataTable` with consistent paging/empty/loading |
| Search/filter | Inconsistent placement & debounce | Shared `SearchBar` pattern |
| Loading states | Mostly `MudProgressLinear`, not universal | Standard skeleton/spinner convention |
| Empty states | Frequently missing | Shared `EmptyState` |
| Error/success | Mix of `MudAlert` + `Snackbar` | Convention: inline for context, snackbar for transient |
| Destructive confirms | Some dialogs (Deny/Reversal/Terminate) exist; void/override are one-click | Require confirm + reason for all destructive/authority ops |
| Identifiers | Claim/member/provider id formatting varies | Shared formatters (already `BenefitCostShareFormatter` for money) |
| Currency/date | `BenefitCostShareFormatter` exists; dates ad-hoc (`DateTime.Now.ToString`) | Centralize date/currency; use tenant timezone |
| Breadcrumbs | **Absent** | Add globally |
| Accessibility | Dark-only neon theme; contrast/focus not verified | Audit contrast, focus order, ARIA on dialogs |
| Responsive | `MaxWidth.ExtraExtraLarge` desktop-first | Verify tablet breakpoints for floor staff |

**Diagnostics exposed as business UX:** `/quickstarts/local-claims`, the
`/demo/*` wrappers, and `Apis.razor` developer catalog are mixed into the product
routing table — segregate demo/dev surfaces from operator IA.

---

## 15. Prioritized gap analysis

Complexity: **S** ≤ ~2 days · **M** ~1 week · **L** multi-week.

### P0 — Must fix before external/customer use

| ID | Area | Finding | Evidence | Risk/impact | Recommended solution | Cx |
|---|---|---|---|---|---|---|
| P0-1 | AuthZ | Privileged user/role admin plane unauthenticated | `tenant-service` `UsersController`/`RolesController` (no `[Authorize]`), CORS AllowAll | Anyone with network reach mints TenantAdmin in any tenant | AuthN on tenant-service + `users.manage`/`roles.manage` policy; tighten CORS | M |
| P0-2 | Tenant | Tenant isolation via spoofable `X-Tenant-ID` header | `TenantMiddleware.ExtractTenantId`; portal sends header only | Cross-tenant read/write if boundary crossed | Derive tenant from validated token; `RequireTenantId=true`; header dev-only | L |
| P0-3 | AuthZ | Fail-open RBAC → `*:*` | `UserContextService` 3 fallbacks → `TenantAdmin` | Any auth glitch = full admin incl. platform | Fail closed: no-role = no-access; never default to admin | S |
| P0-4 | AuthZ | Claim override/void/mass-adjudication unauthenticated | `ClaimsController` `void`/`override`/`mass-adjudication`; no `[Authorize]` | Unauthorized financial action, no actor record | Server permissions `claims.override/void/adjudicate` + reason + audit | M |
| P0-5 | AuthZ | Config/payment writes unauthenticated | benefit-plan (47), payment (24), ar (29) — 0 protected | Unauthorized benefit/fee/payment changes | Server permissions + approval on high-blast-radius writes | L |
| P0-6 | AuthZ | UI-only admin gating | `/settings/users`,`/platform/*` bare `[Authorize]` | Direct-URL access to admin pages | Add role/policy authorization to pages *and* services | S |

### P1 — Enterprise readiness

| ID | Area | Finding | Evidence | Risk | Solution | Cx |
|---|---|---|---|---|---|---|
| P1-1 | AuthN | No user identity propagated portal→services | `TenantHttpMessageHandler` (tenant header only) | Root cause of authz + audit gaps | Forward user token (OBO) or signed gateway assertion | L |
| P1-2 | Model | No enforced permission model | §6/§7 | No least-privilege | Implement server RBAC (§7); reuse in portal | L |
| P1-3 | Audit | No cross-cutting audit trail | §12 | Cannot answer who/what/when for key ops | Shared `AuditEvent` + correlation id | M |
| P1-4 | API | Default CORS AllowAll | `ServiceCollectionExtensions` | Cross-origin abuse | Per-service origin allowlist | S |
| P1-5 | AuthN | Consistent service auth | 28/35 no auth | Inconsistent posture | Standardize auth middleware across services | M |
| P1-6 | Nav | Role-aware navigation | `MainLayout` static tree | Over-exposed IA | Filter nav by permissions | M |
| P1-7 | Config | `LocalDemo` prod-safety test | `Program.cs` | Bypass if misconfigured | Startup guard + test asserting off in Prod | S |

### P2 — Workflow & UX

| ID | Area | Finding | Solution | Cx |
|---|---|---|---|---|
| P2-1 | Claims | No assignment-aware work queue | Build examiner work queue + single-claim reprocess | L |
| P2-2 | Dashboard | Generic, not role-aware | Role-aware Operations Center (§13) | L |
| P2-3 | Members | No Member 360 | Consolidated member view w/ context links | M |
| P2-4 | UM | Thin auth decision workflow | Approve/deny + claim↔auth linking | M |
| P2-5 | Nav/IA | EDI split across 3 groups; dup routes | Consolidate Integrations; collapse dup routes | S |

### P3 — Polish

| ID | Area | Finding | Solution | Cx |
|---|---|---|---|---|
| P3-1 | UX | Missing empty/loading/breadcrumb patterns | Shared components (§14) | M |
| P3-2 | UX | Date/identifier formatting inconsistency | Central formatters | S |
| P3-3 | IA | Demo/dev pages in product IA | Segregate demo surfaces | S |
| P3-4 | A11y | Contrast/focus/ARIA unverified | Accessibility pass | M |

---

## 16. Implementation roadmap

Sequenced foundation-first: **prove the identity/authz seam, fail closed, then
enforce, then redesign UX.** ~14 PRs.

**PR 1 — Fail closed in the portal RBAC**
Goal: remove fail-open `*:*` fallbacks. Files: `Services/UserContextService.cs`,
`Shared/PermissionGate.razor`. Changes: no-role/unresolved → *no access* + a
clear "access pending" screen; never default to `TenantAdmin`. Deps: none.
Accept: unresolved user cannot reach any gated page; tests cover all 3 old
fallbacks. Risk: could lock out mis-provisioned users → ship with an explicit,
audited bootstrap path.

**PR 2 — `LocalDemo` prod-safety guard + test**
Goal: make the dev bypass un-shippable to Prod. Files: `Program.cs`, portal tests.
Changes: startup assertion that `LocalDemo` requires Development; test asserts it
is inert in Production. Deps: none. Accept: test fails if bypass reachable in
Prod. Risk: minimal.

**PR 3 — Tighten CORS + `RequireTenantId` for internal services**
Goal: remove AllowAll; require a tenant. Files: `ServiceCollectionExtensions.cs`,
per-service config. Changes: default to a configured origin allowlist;
`RequireTenantId=true` for services only reached via the portal. Deps: none
(header still supplies tenant for now). Accept: missing tenant → 401; cross-origin
blocked. Risk: M — validate every portal client sends the header (it does).

**PR 4 — Identity propagation seam (portal → services)**
Goal: services learn the acting user. Files: `TenantHttpMessageHandler`, shared
infra auth. Changes: forward a validated assertion (OBO token or signed
gateway header incl. user id + tenant + roles). Deps: PR 3. Accept: a service can
read authenticated user + tenant from the request. Risk: L — core architectural
change; land behind a flag.

**PR 5 — Shared authorization primitives**
Goal: reusable server RBAC. Files: shared infra (policies, `RequirePermission`
attribute/filter), permission constants. Changes: implement the §7 model;
tenant from validated claim. Deps: PR 4. Accept: an annotated test endpoint
enforces a permission. Risk: M.

**PR 6 — Protect the admin plane (tenant-service)** *(closes P0-1)*
Goal: authn+authz on user/role/tenant admin. Files: `tenant-service`. Changes:
`AddAuthentication`, `users.manage`/`roles.manage`/`platform.*` policies; verify
path tenant == token tenant. Deps: PR 5. Accept: unauthenticated/for-other-tenant
calls 401/403. Risk: M.

**PR 7 — Protect claims authority actions** *(closes P0-4)*
Goal: gate override/void/adjust/mass-adjudication. Files: `claims-service`,
portal claim pages. Changes: `claims.override/void/adjust/adjudicate` +
mandatory reason. Deps: PR 5. Accept: examiner cannot override without permission;
supervisor can. Risk: M.

**PR 8 — Protect config & payment writes** *(closes P0-5)*
Goal: gate benefit/fee/provider/payment mutations. Files: benefit-plan, provider,
provider-contracts, payment, ar. Changes: `benefits.configure`,
`providers.manage`, `payments.run` vs `payments.approve` (SoD). Deps: PR 5.
Accept: reads open to role, writes gated. Risk: L.

**PR 9 — Enforce page-level authorization in portal** *(closes P0-6)*
Goal: pages match server policy. Files: portal pages, `PermissionGate`/attributes.
Changes: role/policy on admin & platform pages; align every `PermissionGate` with
the server permission. Deps: PR 5. Accept: direct-URL to `/settings/users` as
non-admin denied. Risk: S.

**PR 10 — Enterprise audit trail** *(closes P1-3)*
Goal: actor-attributed audit for high-risk ops. Files: shared infra
(`AuditEvent`, middleware), claims/benefit/provider/auth/tenant. Changes: write
before/after + permission + correlation id. Deps: PR 4. Accept: override, benefit
change, role change each produce a queryable audit event. Risk: M.

**PR 11 — Standardize service auth + correlation id** *(closes P1-5)*
Goal: consistent posture across all 35 services. Deps: PR 4/5. Accept: inventory
script shows every data service authenticated. Risk: M.

**PR 12 — Role-aware navigation** *(closes P1-6)*
Goal: drawer reflects permissions; consolidate Integrations; collapse duplicate
routes. Files: `MainLayout`. Deps: PR 5. Accept: Finance user sees no UM/Config
tree. Risk: S.

**PR 13 — Claims work queue + role-aware dashboard** *(P2-1/P2-2)*
Goal: assignment-aware queue and role-selected dashboard widgets. Deps: PR 7/9.
Risk: L.

**PR 14 — Shared UX component library** *(P3)*
Goal: `PageHeader`, `DataTable`, `EmptyState`, breadcrumbs, central formatters.
Deps: none (parallelizable). Risk: M.

> Sequence rationale: PRs 1–3 are safe, high-value hardening that need no
> architecture change. PR 4 is the linchpin — authorization (6–9), audit (10),
> and role-aware UX (12–13) all depend on the service layer knowing *who* is
> acting. UX redesign (13–14) comes after the security floor is in place.

---

## 17. Validation & what could not be evaluated

**This PR is additive** — it adds `docs/audits/*` and a read-only Python script.
No production `.cs`, `.razor`, config, or infrastructure file is modified, so
existing behavior is unchanged.

**Ran:**
- `python3 scripts/audits/inventory-endpoints.py` — succeeds; generated
  `api-security-inventory-generated.md` (622 endpoints). `--summary` and `--json`
  modes verified.
- Internal doc links checked (relative paths to the script and generated file).

**Could not run in this environment:**
- **`.NET` build / test suite** — the .NET SDK is not available in the audit
  environment (`dotnet` not on PATH), so the solution was not compiled and unit/
  integration tests were not executed. No .NET sources were changed, so build
  status is unaffected by this PR, but a maintainer should confirm CI stays green.
- **Runtime behavior** — findings are from static source review, not a running
  cluster; the network-isolation compensating control (services ClusterIP-only)
  is asserted from k8s manifests, not observed live.

**Method / caveats:**
- The endpoint scanner is a regex static scan (not Roslyn). It resolves
  `[controller]` route tokens and combines class+action routes, and flags
  controller- and action-level `[Authorize]`/`[AllowAnonymous]`. It may
  miss endpoints defined via minimal-API `MapGet`/`MapPost` (e.g. `/health`,
  the `LocalDemo` routes in portal `Program.cs`) and cannot evaluate custom
  authorization filters or middleware-level checks. Treat its counts as a close
  lower bound on the exposure, not an exact spec.
- Persona/permission mappings are proposals derived from existing role
  definitions in `UserContextService.cs`; validate with real payer operations
  staff before implementation.
