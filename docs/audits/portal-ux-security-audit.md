# Portal UX, workflow, and security audit

**Date:** 2026-09-04
**Scope:** Blazor Server operations portal (`src/portal/CloudHealthOffice.Portal`) and HTTP APIs under `src/services`
**Method:** Static source scan plus targeted code review. Not a live pentest, traffic capture, or HIPAA certification.

Machine inventories (regenerate after adding pages or controllers):

- [Generated portal route inventory](generated/portal-route-inventory.md)
- [Generated HTTP endpoint inventory](generated/api-endpoint-inventory.md)

```bash
python3 scripts/audit/inventory-portal-and-apis.py --write-docs
dotnet test src/portal/CloudHealthOffice.Portal.Tests/CloudHealthOffice.Portal.Tests.csproj --filter FullyQualifiedName~PortalRouteAuthInventoryTests
```

`PortalRouteAuthInventoryTests` fails if a new `@page` route is added without `[Authorize]` or `[AllowAnonymous]`, if a new anonymous route is not added to the documented allow-list, or if a previously undeclared gap is fixed without shrinking `KnownUndeclaredRoutes`.

---

## Findings at a glance

| ID | Severity | Finding |
| --- | --- | --- |
| P0-1 | P0 | Portal has **no fallback authorization policy**. Pages without `[Authorize]` or `[AllowAnonymous]` are reachable without sign-in. |
| P0-2 | P0 | Most HTTP APIs declare **no `[Authorize]`**. Shared infrastructure does **not** call `UseAuthentication()`, and tenant middleware trusts `X-Tenant-ID` when there is no JWT tenant claim. |
| P1-1 | P1 | `/demo/*` wrappers are `[AllowAnonymous]` and embed production operational pages (`Claims`, `Members`, …). Nested `[Authorize]` is not enforced by the router. |
| P1-2 | P1 | `PermissionGate` is a **render** gate. It does not stop the page `OnInitializedAsync` from calling backend APIs. |
| P1-3 | P1 | Several `[Authorize]` pages have no `PermissionGate` (or an empty one). Any signed-in user can open them. |
| P2-1 | P2 | Nav is a single tree for every authenticated user. Role labels exist only inside `PermissionGate`, not in the menu. |
| P2-2 | P2 | Portal PHI access is not written to a dedicated audit log. Backend services vary. |
| P2-3 | P2 | Placeholder operational routes (`/claims/submit`, `/providers/verification`) sit in the nav without auth attributes. |

P0 means “fix before treating the portal or APIs as an internet-facing production surface.” Network isolation and ingress auth can mitigate P0-2 in a private cluster; they do not fix P0-1 on a public portal.

---

## 1. Portal as it exists

The portal is a Blazor Server app. `AuthorizeRouteView` in `App.razor` sends unauthenticated users to Entra sign-in (or `/local-demo/sign-in` when `Authentication:Mode` is `LocalDemo` in Development). That only applies to components the router selected that have `[Authorize]`.

### 1.1 Personas implied by the UI

These are labels in `PermissionGate` / `UserContext`, not separately shipped products.

| Persona | Typical permission | Primary routes |
| --- | --- | --- |
| Claims examiner / supervisor | `claims:read`, `claims:work` | `/claims`, `/claims/{ClaimId}`, `/work-queues`, `/edi-transactions`, `/mass-adjudication-runs` |
| Member services | `members:read`, `eligibility:check` | `/members`, `/eligibility` |
| Enrollment specialist | `enrollment:read` | `/enrollment-ops` |
| UM coordinator | `authorizations:read`, `appeals:read` | `/authorizations`, `/appeals` |
| Provider relations | `providers:read` | `/providers` |
| Finance | `finance:read`, `billing:read`, `payments:read`, `contracts:read` | `/finance/*`, `/premium-billing`, `/payment-runs`, `/capitation/*` |
| Compliance | `compliance:read`, `reports:compliance` | `/compliance/pa-rules`, `/reports` |
| Tenant admin | `settings:manage`, `users:manage` | `/settings`, `/settings/users` |
| Platform admin | `platform:tenants`, `platform:inquiries` | `/platform/tenants`, `/platform/inquiries` |
| Anonymous / marketing | none | `/welcome`, `/signup`, `/demo`, `/apis`, `/pricing`, `/docs`, `/legal` |

`UserContextService` in LocalDemo mode grants TenantAdmin, ClaimsSupervisor, MemberServices, ProviderRelations, and Finance in one principal. That is appropriate for a laptop demo, not for a shared environment.

### 1.2 Workflows the portal can actually run

Implemented operational loops (page exists, calls a service, is not a “coming soon” stub):

- Claims search, claim detail, work queues, mass adjudication runs, EDI transaction history
- Eligibility check, authorizations, appeals
- Members, providers, enrollment operations
- Benefit plans, sponsors, trading partners, reference data, terminology crosswalk
- AR, premium billing, provider contracts, payment runs, capitation
- Workflows console, EDI operations, reports, PA rule explorer
- Tenant settings and user management; platform tenant and inquiry admin

Stubs or marketing-only:

- `/claims/submit` — copy only; points at X12 837 / FHIR
- `/providers/verification` — “Coming soon”
- `/demo` — hardcoded stats; child routes reuse live pages
- `/pricing`, `/docs`, `/legal`, `/contact-sales`, `/request-access`, `/welcome`, `/signup`

### 1.3 Navigation vs authorization

`MainLayout.razor` shows one Operations / Members / Plan / Finance / Compliance / Settings / Platform tree to every authenticated user. `PermissionGate` hides page **content**, not nav items. A claims examiner still sees Finance and Platform links and then an access-denied alert (if the page has a gate).

---

## 2. Authentication

| Mode | When | Mechanism |
| --- | --- | --- |
| Entra (default) | `Authentication:Mode` unset or `Entra` | Microsoft Identity Web OIDC; cookie `.CloudHealthOffice.Auth` (`Secure`, `SameSite=None`, `HttpOnly`) |
| LocalDemo | Development **and** `Authentication:Mode=LocalDemo` | Cookie `.CloudHealthOffice.LocalDemoAuth`; `GET /local-demo/sign-in` issues an 8-hour persistent cookie |

LocalDemo sign-in is anonymous by design and only registered when both Development and `LocalDemo` are set. Redirects are normalized to same-host paths.

`Program.cs` explicitly does **not** set a fallback authorization policy so public marketing pages work. Consequence: omission of `[Authorize]` means anonymous access, not “inherit authenticated.”

---

## 3. Authorization gaps (P0-1, P1-1, P1-2, P1-3)

### 3.1 Routes with neither attribute

From the generated portal inventory. `/` (`Index.razor`) is a router: unauthenticated users go to `/welcome`. The rest are reachable without sign-in.

| Route | Intended audience | Recommended attribute |
| --- | --- | --- |
| `/claims/submit` | Operations (stub) | `[Authorize]` |
| `/providers/verification` | Operations (stub) | `[Authorize]` |
| `/docs` | Public | `[AllowAnonymous]` |
| `/pricing` | Public | `[AllowAnonymous]` |
| `/contact-sales` | Public | `[AllowAnonymous]` |
| `/legal` | Public | `[AllowAnonymous]` |
| `/request-access` | Public | `[AllowAnonymous]` |
| `/Error/AdminConsentRequired` | Auth-flow error | `[AllowAnonymous]` |

These eight routes are the `KnownUndeclaredRoutes` list in `PortalRouteAuthInventoryTests`. Shrink that list only by adding the attribute — do not add new undeclared routes.

### 3.2 Documented anonymous allow-list

Keep this set small. Review any addition here.

`/welcome`, `/apis`, `/fhir-apis`, `/api-docs`, `/demo`, `/demo/claims`, `/demo/claims/{ClaimId}`, `/demo/members`, `/demo/eligibility`, `/demo/authorizations`, `/signup`, `/login`, `/signin`, `/error`, `/quickstarts/local-claims`

### 3.3 Demo wrappers (P1-1)

`DemoClaims.razor` is `[AllowAnonymous]` and renders `<Claims />`. `AuthorizeRouteView` only consults the **routed** page, so `Claims`’s `[Authorize]` does not run. `PermissionGate` on `Claims` fails closed for anonymous users (access-denied alert). Authenticated users hitting `/demo/claims` see **their tenant’s live claims**, not a synthetic demo dataset.

Same pattern: `/demo/members`, `/demo/eligibility`, `/demo/authorizations`, `/demo/claims/{ClaimId}`.

Fix: dedicated demo pages bound to a demo tenant and read-only APIs, or drop the wrappers and keep `/demo` as a marketing page with static figures.

### 3.4 PermissionGate is not an authorization filter (P1-2)

`PermissionGate` hides `ChildContent` when the user lacks a permission. Page `@code` still runs. Example: `Claims.razor` wraps markup in the gate but `OnInitializedAsync` can still call `IClaimsService` when `RunId` is on the query string.

`[Authorize]` on the page still requires a signed-in user. The missing control is **permission** at the data-fetch boundary, not authentication.

### 3.5 Authenticated but ungated PHI pages (P1-3)

Any signed-in user (including a marketing-trial account) can open:

- `/claims/{ClaimId}` — no `PermissionGate`
- `/dashboard` — `[Authorize]` only
- `/benefit-plans` — `<PermissionGate>` with **no** `Permission` (any user with at least one role)
- `/correspondence`, `/sponsors`, `/trading-partners`, `/workflows`, `/edi-operations`, `/reference-data`, `/pricing-api`

`/claims/{ClaimId}` is the highest-risk of these: claim detail is PHI and is not permission-gated.

---

## 4. API exposure (P0-2)

The scanner found **230 mutation endpoints** (POST/PUT/PATCH/DELETE) with neither `[Authorize]` nor `[AllowAnonymous]`. ASP.NET Core does not deny those by default unless a fallback policy or `[Authorize]` is present.

Services that **do** attribute most of their surface: `attachment-service`, `authorization-service`, `trading-partner-service` (partial), some `fhir-service` and `idcard-service` actions.

Services with **zero** `[Authorize]` on scanned controllers include claims, members, eligibility, appeals, AR, capitation, benefit plans, providers, payments, premium billing, tenant, coverage, consent, and others. See the generated API inventory for the full table.

Shared pipeline (`UseChoInfrastructure`):

- Does **not** call `UseAuthentication()`, so JWT tenant claims are empty unless the service wires auth itself
- `TenantMiddleware` then accepts `X-Tenant-ID` or `X-Dev-Tenant-ID`
- `RequireTenantId` defaults to **false**; missing tenant becomes `default-tenant`

The portal client always sends `X-Tenant-ID` via `TenantHttpMessageHandler` / `MainLayout`. That is isolation between portal users only if APIs are unreachable from elsewhere. A caller who can hit claims-service can pick a tenant id.

Mitigations that may already exist in a given deployment (not visible as `[Authorize]` in source): private cluster network policy, ingress auth, API keys (Pricing API admin secret), environment-specific gates. Treat them as compensating controls, not as API authorization.

---

## 5. Tenant isolation

Portal:

- Tenant id comes from Entra `tid` / `extension_TenantId` plus `ITenantService` subscription lookup
- `MainLayout` writes `X-Tenant-ID` on `HttpClient`
- Platform admins can switch / impersonate tenants; the UI shows `[Impersonating]`
- Index auto-provisions a subscription in Development when Entra tenant is unknown

APIs:

- Header-based tenant is the common path
- Header is attacker-controlled if the listener is reachable without a trusted identity
- Default-tenant fallback can mix data if a service stores rows under `default-tenant`

Recommended end state: authenticate every external request, take tenant from the token, ignore client `X-Tenant-ID` in production, set `RequireTenantId = true`.

---

## 6. PHI handling

| Surface | Notes |
| --- | --- |
| Operational pages | Member names, member ids, claim numbers, clinical/auth data rendered in tables and dialogs. Expected for a payer ops console; must be behind auth + permission. |
| Demo routes | Labelled “sample data” on `/demo`; child routes are live pages. |
| LocalDemo user | Email-shaped identifiers were previously an issue; current local demo uses `local-demo-user` / `cho_local_demo`. |
| Error UI | `App.razor` error boundary does not dump exception text to the client. `DetailedErrors` is enabled for server logs. |
| Public pages | Marketing/docs/pricing should stay free of member/claim payloads. |

Do not put real member, claim, or customer data in screenshots, fixtures, or this audit’s generated tables. Inventories list routes and attributes only.

---

## 7. Auditability

| Control | Status |
| --- | --- |
| Portal page-view / PHI-read audit | Not implemented as a dedicated store. Circuit diagnostics log lifecycle, not “who viewed claim X.” |
| Backend mutation audit | Varies by service (for example claim version events, AI examination audit). Not uniform. |
| Auth events | Entra and LocalDemo sign-in are cookie-based; no portal-side audit export. |
| Tenant impersonation | UI flag only; no immutable impersonation log found in the portal. |

For HIPAA Security Rule access-control and audit-control discussions, this portal currently provides authentication and some RBAC UI, not a complete audit trail of PHI reads.

---

## 8. Recommended implementation order

### Wave 1 — close anonymous portal holes (P0-1)

1. Add `[Authorize]` to `/claims/submit` and `/providers/verification`.
2. Add `[AllowAnonymous]` to `/docs`, `/pricing`, `/contact-sales`, `/legal`, `/request-access`, `/Error/AdminConsentRequired`.
3. Confirm `PortalRouteAuthInventoryTests` is green and `KnownUndeclaredRoutes` is empty.
4. Add a portal fallback policy of `RequireAuthenticatedUser` **after** every public page is explicitly `[AllowAnonymous]`.

### Wave 2 — demo and permission gates (P1)

1. Stop embedding production pages under `/demo/*`, or bind those routes to a locked demo tenant and read-only API.
2. Put `PermissionGate` (or a filter that runs **before** data load) on `/claims/{ClaimId}`, `/dashboard`, `/correspondence`, `/workflows`, and other ungated PHI pages.
3. Fail closed in page `OnInitializedAsync` when `IUserContextService.HasPermission` is false.
4. Hide nav groups the user cannot use.

### Wave 3 — API authorization and tenant (P0-2)

1. Call `UseAuthentication()` in every service that should not be anonymous.
2. Set a fallback deny policy; mark health/swagger `[AllowAnonymous]`.
3. Resolve tenant from the token in production; do not trust `X-Tenant-ID` unless the caller is already authenticated as that tenant.
4. Set `RequireTenantId = true` outside local/dev.

### Wave 4 — audit trail (P2)

1. Record portal PHI reads (member, claim, auth, appeal) with actor, tenant, resource, timestamp.
2. Record tenant impersonation start/end.
3. Retain per the existing security-doc retention targets.

---

## 9. What this audit does not cover

- Runtime verification (no browser pass, no packet capture)
- Kubernetes NetworkPolicy / ingress annotations in deployed clusters
- The marketing site under `src/site` (separate from the Blazor portal)
- Third-party SOC 2 / HIPAA certification (see `docs/security/` for that track)
- Engine-only projects under `src/engines` except where they host HTTP (fhir-service scan includes engines if controllers live under `src/services`)
