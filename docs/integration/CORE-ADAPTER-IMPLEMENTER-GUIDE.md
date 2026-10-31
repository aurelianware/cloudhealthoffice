# Core adapter implementer guide

**Audience:** the engineer building a connector between CHO and a health plan's
core administration system (QNXT, Facets, HealthEdge HealthRules Payer or a
plan-built system).
**Status:** written October 31, 2026 against `main` at 2c2342b. Re-check the
interfaces named here before relying on a method list.

The [core adapter QRE](../features/questionnaires/qre-core-admin-adapter-questions.md)
tells you what to find out from the plan. The
[onboarding checklist](../implementation/CORE-CONNECTOR-ONBOARDING.md) and
[core data contract](CORE-DATA-CONTRACT.md) say what to agree and deliver.
This guide covers the code: what each seam must do, which CHO features depend
on it, and how to prove a connector behaves. [ADR 027](../adr/027-core-adapter-engagement-boundary.md)
explains why vendor connectors are built per engagement.

## 1. Two kinds of integration

A plan that keeps its core while it adopts CHO touches CHO in two different ways.
They are easy to confuse, and they cost very different amounts of work.

| | Live adapters | Hydration into CHO |
| --- | --- | --- |
| What it is | A class behind a domain interface (`IClaimAdapter`, `IProviderAdapter`, ...) that answers a CHO request by calling the core | Feeds that load the core's data into CHO's own stores (members, coverage, providers, claims) and keep them in step |
| Chosen by | Tenant configuration `configuration.<domain>Platform.platform` (for example `qnxt`) | The engagement's sync jobs. Nothing per request |
| What reads through it | CHO's internal REST APIs and the portal: claim lookup and member claim search, provider lookup and roster, benefit plan views, eligibility inquiry, ID card issue, authorization create and status | Every CMS-0057-F FHIR API (see below) |

**The CMS-0057-F APIs do not call the live adapters.** They read CHO's
repositories directly:

| FHIR surface | Code | Reads |
| --- | --- | --- |
| ExplanationOfBenefit, Claim (Patient Access, Provider Access, Payer-to-Payer) | `claims-service/Controllers/FhirExplanationOfBenefitController.cs`, `FhirClaimController.cs` | `IClaimRepository` |
| Practitioner, PractitionerRole, Organization, Plan-Net export (Provider Directory) | `provider-service/Controllers/Fhir*Controller.cs`, `PlanNetDirectoryExportController.cs` | `IProviderRepository`, `IOrganizationRepository` |
| InsurancePlan | `benefit-plan-service/Controllers/FhirInsurancePlanController.cs` | `IBenefitPlanRepository` |
| Patient, Coverage, Encounter (through fhir-service) | `fhir-service/Services/ChoNative/ChoNativeFhirDataAdapter.cs` | member-service, coverage-service and encounter-service over HTTP |
| Prior authorization (PAS, Prior Authorization API) | authorization-service | `AuthorizationBackendSelector`: the CHO backend in Replace mode, `IAuthorizationBackend` for the core in Augment mode |

So a plan that wants CMS-0057-F compliance over QNXT needs hydration first.
Implementing `QnxtClaimAdapter` would not put a single EOB on the Patient Access
API. Prior authorization is the one CMS-0057-F path where a live seam
(`IAuthorizationBackend`) matters.

## 2. What to build, in order

| Tier | Goal | Work | Seams |
| --- | --- | --- | --- |
| 1. Foothold | CMS-0057-F APIs over the existing core | Initial load and change feed for members and coverage (834 through enrollment-import-service), providers, organizations and networks (provider-service APIs), benefit plans, and adjudicated claim history (**no load path exists yet**, see section 7). Agree who answers prior authorization. | Hydration. `IAuthorizationBackend` only if PA stays authoritative in the core |
| 2. Live reads | Staff and portal see the core's current data, not yesterday's load | Read methods of the live adapters | `IClaimAdapter` reads, `IProviderAdapter`, `IOrganizationAdapter`, `IBenefitPlanAdapter`, `IEligibilityAdapter` |
| 3. Writes and Augment | CHO writes to the core, or runs in shadow beside it | `SubmitClaimAsync`, authorization create and status writes, a legacy-result feed for comparison ([operating modes](../architecture/OPERATING-MODE.md)) | Agreed per engagement in the SOW |

Most plans start and stay at tier 1 for the compliance date. Price and scope
tier 2 and 3 separately.

## 3. Rules every live adapter follows

**Selection.** Each service registers its adapters as `IEnumerable<T>` and a
factory picks one by comparing the tenant's configured platform with the
adapter's `Platform` (or `BackendKey`) property, case-insensitively. Use a
lowercase key such as `qnxt`. The tenant's `platformSettings` dictionary
arrives on every request as `PlatformSettings`. Secrets never go in it: put a
secret name in `keyVaultSecretName` and resolve it through the secret provider.

**Tenant.** Every request carries `TenantId`. Return only that tenant's
records, and stamp `TenantId` on every result. A vendor connector usually maps
one tenant to one core environment through settings.

**Not found.** Return a response whose payload is `null` (or an empty list).
Do not throw for a record that does not exist.

**Exceptions map to HTTP statuses** in
`Infrastructure/Middleware/ExceptionHandlingMiddleware.cs`, so pick them on
purpose:

| Throw | Caller sees | Use for |
| --- | --- | --- |
| `ArgumentException` | 400 | The caller's request is missing a required identifier. Never for a bad upstream response |
| `NotImplementedException` | 501 | A method this engagement has not implemented |
| `TimeoutException` | 504 | The core did not answer in time, or is in a published outage window |
| anything else | 500 | Unexpected upstream failure |

**Never fake it.** An unimplemented or failing method must not return
synthetic data, an empty success, or CHO's own data. The stub guard tests
(section 5) enforce this for the templates.

**Raw responses.** `RawResponse` exists on most response types for
troubleshooting. It is stored and logged, so leave it null unless the engagement
agrees what it may hold. It must never hold credentials or tokens.

**Cancellation and limits.** Honor the `CancellationToken`, pass it to every
outbound call, and bound retries. Respect the rate limits recorded in the QRE.

## 4. The seams, method by method

Paths are under `src/services/`. "Reference" is what the CHO adapter does today.

### Claims: `claims-service/Adapters/IClaimAdapter.cs`
Tenant key `configuration.claimsPlatform`. Callers: `ClaimsV1Controller`,
`ClaimSubmissionService`, `ClaimAdjudicationOrchestrator`.

| Method | Required input | Must return | Reference |
| --- | --- | --- | --- |
| `GetClaimAsync` | `ClaimId`, or `ClaimVersionId` (+ optional `AsOf`) | The claim, or `Claim == null`. With `ClaimVersionId`, the latest version in effect at `AsOf` | `ArgumentException` when neither id is given |
| `GetClaimByNumberAsync` | `ClaimNumber` | The current version for the payer claim number, or null | `ArgumentException` without a number |
| `GetClaimVersionAsync` | `ClaimVersionId` and `VersionId` | That version, or null | |
| `ListClaimVersionsAsync` | `ClaimVersionId`, `PageSize`, `ContinuationToken` | Versions of the chain, newest first, with a continuation token | Map the core's adjustment and reversal links to one chain |
| `SearchClaimsAsync` | Optional member, NPI, dates, status, LOB, `Page`, `PageSize` | At most `PageSize` claims matching every filter given | |
| `SearchClaimsForMemberAsync` | `MemberId` plus filters | That member's claims and, if known, `TotalCount` | `ArgumentException` without a member |
| `SubmitClaimAsync` | `Claim` | The stored claim | Tier 3. Adjudication writes stay off the adapter |

### Eligibility: `eligibility-service/Adapters/IEligibilityAdapter.cs`
Tenant key `configuration.eligibilityPlatform`. Callers: `EligibilityService`,
`BatchEligibilityService`.

| Method | Required input | Must return |
| --- | --- | --- |
| `VerifyEligibilityAsync` | Subscriber (and dependent) identity, `ServiceDate`, `ServiceTypeCode`, provider NPI | `IsEligible`, `StatusCode` (`1` active, `6` inactive), coverage dates, plan and group, benefits, deductible and out-of-pocket accumulators, other insurance. No coverage on the date is `IsEligible = false` with a reason, not an exception |

There is no QNXT, Facets or HealthEdge eligibility template yet. The Availity
and Change Healthcare adapters are clearinghouse templates, not core
connectors. Add `QnxtEligibilityAdapter` when an engagement needs live
eligibility.

### Benefit plans: `benefit-plan-service/Adapters/IBenefitPlanAdapter.cs`
Tenant key `configuration.benefitPlanPlatform`.

| Method | Required input | Must return |
| --- | --- | --- |
| `GetPlanAsync` | `PlanId` | The current published plan, or null |
| `GetPlanVersionAsync` | `PlanId`, `VersionId` | That version, or null. `ArgumentException` without `VersionId` |
| `GetMemberBenefitViewAsync` | `PlanId`, optional `ServiceDate` (defaults to today) | The plan's benefits as they apply on that date, or null |

### Providers and networks: `provider-service/Adapters/IProviderAdapter.cs`, `IOrganizationAdapter.cs`
Tenant key `configuration.providerPlatform`. Callers: `ProvidersController`,
`NetworksController`.

| Method | Required input | Must return |
| --- | --- | --- |
| `GetProviderAsync` | `ProviderId`, optional `VersionId` | The current (or named) version, or null. `ArgumentException` without an id |
| `GetProviderByNpiAsync` | `Npi` | The provider, or null. `ArgumentException` without an NPI |
| `SearchProvidersAsync` | Name, specialty, location, plan, LOB, type, accepting new patients, paging | At most `PageSize` matching providers |
| `GetNetworkRosterAsync` | Network or plan scope, paging | Providers participating in that scope |
| `GetNetworkAsync` | `NetworkId` | Not implemented in the reference adapter either. Leave it for later |
| `IOrganizationAdapter.GetOrganizationAsync`, `GetByParentAsync`, `ListAsync` | Organization id or parent | Organizations, or null / empty |

### Authorizations: `authorization-service/Backends/IAuthorizationBackend.cs`
Selected by `Cms0057:Authorization:OperatingMode` and `AugmentBackend`, not by
the tenant platform lookup. A core backend sets `IsAuthoritative = false`.

| Method | Must do |
| --- | --- |
| `CreateAsync` | Create the request in the core's UM system and return it with the core's number |
| `GetByNumberAsync` | Return the authorization by number, or null |
| `UpdateStatusAsync` | Apply a status change and decision details. Keep the history append-only |
| `RecordAutomatedDecisionAsync` | Record a trusted automated decision, or refuse if the core does not accept one |

### ID cards: `idcard-service/Adapters/IIdCardAdapter.cs`
`QnxtIdCardAdapter` is already a working Augment pattern: it issues through
CHO, then mirrors the request to a QNXT queue that a nightly reconciliation
job backstops. Copy that shape for any other fire-and-forget mirror.

## 5. Conformance test kit

Each read seam has an abstract xUnit suite. A connector proves itself by
subclassing it and pointing the fixture at known records in the payer's test
environment. The CHO reference adapter runs the same suite in this repository.

| Suite | Location | Reference run |
| --- | --- | --- |
| `ClaimAdapterConformanceSuite` | `tests/CloudHealthOffice.ClaimsService.Tests/Adapters/Conformance/` | `ChoClaimAdapterConformanceTests` |
| `ProviderAdapterConformanceSuite` | `tests/CloudHealthOffice.ProviderService.Tests/Adapters/Conformance/` | `ChoProviderAdapterConformanceTests` |

```csharp
public sealed class QnxtClaimAdapterConformanceTests : ClaimAdapterConformanceSuite
{
    protected override Task<ClaimAdapterConformanceFixture> ArrangeAsync() =>
        Task.FromResult(new ClaimAdapterConformanceFixture(
            Adapter: BuildConnectorAgainstSandbox(),
            TenantId: "plan-test",
            ExpectedPlatform: "qnxt",
            KnownClaimId: "...", KnownClaimNumber: "...", KnownClaimVersionId: "...",
            KnownMemberId: "...", MissingClaimId: "...", MissingClaimNumber: "..."));
}
```

The suites check the platform key, tenant stamping, found and not-found
behavior, `ArgumentException` on missing identifiers, version ordering and page
size. They do not check field mappings. Those belong in the engagement's
mapping tests and reconciliation evidence ([core data contract](CORE-DATA-CONTRACT.md), section 7).

**Stub guards.** `VendorConnectorStubAssertions`
(`src/services/shared/CloudHealthOffice.Infrastructure.Tests/Adapters/`) invokes
every method of a template and fails if any method does anything other than
throw `NotImplementedException`. The claims, provider, organization, benefit
plan and QNXT authorization templates are covered. When an engagement
implements a method, pass its name as `implementedMethods` and cover it with
the conformance suite.

## 6. QNXT notes

Everything here needs to be confirmed against the plan's licensed QNXT
specification. Where it disagrees with this page, the specification wins.

**QXI authentication, as Mark described it on October 31, 2026 (not yet checked
against a specification):**

1. The connector calls the QNXT STS (security token service) with the QNXT
   username and password in a Base64-encoded header.
2. The STS returns a token.
3. Each QXI call sends that token as `Authorization: Bearer <token>`.
4. Each QXI call also carries the QNXT environment ID. The STS request does not.

Suggested shape for the connector:

- Settings: `qnxt:stsUrl`, `qnxt:baseUrl` and `qnxt:environmentId` in
  `platformSettings`, one set per environment. The service account's username
  and password are a secret named by `keyVaultSecretName`.
- A `DelegatingHandler` that gets a token, caches it per tenant and environment,
  refreshes it before it expires, and adds the bearer token and environment ID
  to each call.
- On a 401 from QXI, refresh the token once and retry once. If the STS rejects
  the credentials, fail and alert. Never loop, because repeated failures can
  lock the service account.
- Never log the authentication header, the token or the decoded credentials.

**Where the code lives.** Keep a QNXT connector built from licensed
specifications in a private package, not this public repository, until the
plan and Cognizant terms allow publishing it. The templates and suites here
stay vendor-neutral.

## 7. Known gaps

Found while writing this guide. Each needs a decision before the first
engagement relies on it.

1. **No way to load claims the core adjudicated.** The CMS-0057-F EOB APIs read
   `IClaimRepository`, but adjudication results are only written by CHO's own
   adjudication pipeline (`PersistenceStage` through
   `UpdateAdjudicationProjectionAsync`). A Foothold plan needs an import that
   accepts claims with the core's adjudication results (allowed, paid, member
   responsibility, CARC/RARC, adjustment links), probably from the core's
   claim extract or 835s. This is the biggest gap for tier 1.
2. **Platform lookup falls back to CHO.** `TenantPlatformLookup` uses `cho`
   when tenant-service is unreachable or answers 5xx, and every adapter factory
   (claims, eligibility, benefit plan, provider, organization, ID card) uses
   `cho` for a platform key it has no adapter for. For a QNXT tenant that means
   a tenant-service outage or a mistyped key quietly serves CHO's own data. A
   tenant set to `qnxt` for eligibility gets CHO's answer today, because no
   QNXT eligibility adapter is registered. ADR 027 says connectors must not fall back silently. Failing
   the request (or caching the last known platform) for tenants that name a
   vendor platform would match the ADR.
3. **No core eligibility template.** See section 4.
4. **Provider roster ignores `NetworkId`.** `ChoProviderAdapter.GetNetworkRosterAsync`
   filters by plan and line of business only, and `GetNetworkAsync` is not
   implemented. A vendor connector should not copy that limitation.
