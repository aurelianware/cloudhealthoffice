# v6.0.0

**Release date:** October 28, 2026
**Previous release:** [v5.0.0](./RELEASE_NOTES_v5.0.0.md) (October 6, 2026)

This release covers the 340 pull requests merged since v5.0.0 (#1222 to
#1580). It is a major version because the Cosmos DB SDK persistence path is
gone, a public endpoint was removed, and several stores changed how they write
data. Read **Breaking changes** and **Upgrading from 5.x** before you deploy.

Most of the work is product depth: claims that pay correctly on the money
path, inpatient stays priced from CMS's own rules, plan configuration, member
service, and CMS-0057-F data served from CHO's own services rather than
mocks. As with v5.0.0, these notes say what the code does and what has
evidence behind it, and say so where something is validated only on the GKE
demo or locally.

**This release does not complete the CMS-0057-F January 2027 API
requirements.** What is still to come is listed under
[Not yet done for CMS-0057-F](#not-yet-done-for-cms-0057-f).

---

## Highlights

### Claims pay correctly, through one adjudicator

- The 837 to 835 money path is covered by a golden-file test (#1260). Plan-paid
  is paid, never billed (#1240); deductibles, family limits and ACA preventive
  apply as written (#1237, #1245); COB reaches tertiary payers (#1278).
- claims-service's pipeline is the only outcome writer. An outage pends a claim
  instead of denying it, and every denial carries a CARC (#1335 to #1339).
- Intake: duplicate detection (#1236), WEDI SNIP 1 to 5 with 999 (#1272), TA1
  (#1287), timely filing (#1448), replacement and void linking (#1446).
- Money rounds to the cent half away from zero everywhere
  (`MoneyRounding.ToCents`); see `docs/architecture/money-rounding-impact.md`.

### Inpatient pricing from CMS's rules

- A native MS-DRG grouper that agrees with CMS's own on every corpus row
  (ADR 024, #1478, #1492, #1498).
- IPPS payment with the hospital-level adjustments, the hospital-specific
  portion and pass-through costs, checked against CMS's own pricer (#1499,
  #1502 to #1510).
- One pricing engine for adjudication and the Pricing API, and one contract
  model with a per-tenant cutover flag (ADR 016, #1268, #1479).

### CMS-0057-F: data from CHO's own services

- Patient, Coverage, Encounter, Claim and prior authorizations served over FHIR
  from CHO's services (#1420, #1476, #1480); Payer-to-Payer from CHO services
  (#1429); a CARIN Blue Button EOB for Patient Access (#1526).
- Prior authorization: lawful 14-day decision extensions with the member
  notice, every denial reason per line, appeal outcomes in the reporting
  ledger, and the list of items and services that require PA (#1568, #1575
  to #1577).
- The public Provider Directory (#1578) and the Patient Access usage figures
  CMS asks plans to report (#1579).
- Provider Access bulk `$export`, **off by default** (#1580).
- Inferno kits run against CHO in CI, and CRD decides from the member's own
  record (#1509, #1511, #1522).

### Plan configuration, enrollment and member service

- Settings that cascade from tenant to carrier, program, plan and sponsor, with
  overrides, effective dating and two-person approval where it matters (#1507,
  #1515, #1519, #1521, #1523, #1525).
- 834s of any size on one intake path, exceptions as work items, orphaned
  dependents applied once their subscriber arrives (#1482, #1493, #1497,
  #1500).
- contact-service and the Service screen (#1506, #1552), utilization
  management (#1477), appeals State rule packs (#1404), correspondence (#1470),
  the document catalog on GCS or S3 (#1436, #1439), work management with Get
  Next (#1345).

### Security and platform

- An access log on every read of a claim, member, provider, authorization,
  payment or appeal record (#1365, #1378, #1379, #1389).
- An architecture test that fails if any MongoDB query is not tenant scoped
  (#1452).
- Cloud KMS token signing and Kubernetes service identity (#1458).
- The Remediation series (#1391 to #1399): integer-cent accumulators, a
  transactional outbox for claim events, resilient service-to-service calls,
  revision-checked MongoDB writes, and MongoDB as the only persistence path.

The full list, itemized, is in the [changelog](../../CHANGELOG.md).

---

## Breaking changes

| Change | Who is affected | What to do |
|---|---|---|
| **MongoDB is the only document store** (ADR 017, #1398). A service fails at start-up if `Database:Provider` is not `MongoDb`, if `CosmosDb:ConnectionString` or `CosmosDb:Endpoint` is set without `MongoDb:ConnectionString`, or if `MongoDb:ConnectionString` is missing. This now applies in Development and Testing too. | Anyone who set `Database:Provider=CosmosDb`, or who relied on idcard, reference-data, fhir, authorization, rfai, consent, personal-representative or attachment starting with no database | [Moving off Cosmos DB](#1-moving-off-cosmos-db) |
| **Azure DocumentDB needs `retrywrites=false`** in the connection string. | Azure deployments | Add it to every `MongoDb:ConnectionString` |
| **`POST /api/v1/adjudication/adjudicate` returns 404** (benefit-plan-service, #1399). The MCC validator's `--synchronous-diagnostic` mode and `SERVICEBUS_ONLY=false` are gone. | Callers of the dry-run endpoint | Use `POST /api/v1/adjudication/estimate` |
| **Cosmos-only operations are gone:** `POST /api/v1/admin/claims/cosmos-migration/run`, `GET …/status`, `src/tools/migration-wizard`, the Cosmos DB for NoSQL Bicep module. | Operators still running the 5.1b partition-key migration | Finish or abandon it on 5.x first |
| **`BatchEligibility:CosmosDb` is now `BatchEligibility:MongoDb`.** | eligibility-service batch jobs | Rename the section |
| **Redis accumulators store integer cents** (#1393). Old and new pods misread each other's values. | Every deployment | Scale benefit-plan-service to zero before rolling it ([step 4](#4-redis-accumulators)) |
| **PCP assignment uses multi-document transactions** (#1547, #1549). | MongoDB running as a standalone server | Run a replica set, or a managed service with transactions |
| **Authorization numbers are unique per tenant** (#1406). Start-up refuses existing duplicates. | Tenants with duplicate authorization numbers from earlier PAS races | `docs/operations/AUTHORIZATION-NUMBER-UNIQUENESS.md` |
| **Error bodies are RFC 7807 problem documents** with a `traceId` in attachment, eligibility, member, provider, premium-billing, payment, benefit-plan, capitation, trading-partner, fhir, idcard, enrollment-import and terminology services. Exception text is no longer returned. | Clients that parse `{ error }` or `{ message }` from a 500 | Read `title`, `detail` and `traceId` |
| **`Portal:Search:RequireCriteria` is removed.** Record search always needs criteria. | Demo tenants that browsed lists | None; demos use curated personas and work lists |
| **Development hosts refuse to start on Kubernetes, App Service or Container Apps** unless `CHO_ALLOW_DEV_ON_DEPLOYED_HOST=true`. | Deployments running `ASPNETCORE_ENVIRONMENT=Development` | Run Production hosts, or set the override knowingly |

### Behaviour changes that change what a claim pays

These need no configuration, but a plan should expect different results:

- **Rounding:** half a cent rounds away from zero, not to even. An amount changes
  by one cent only when it is exactly half a cent and the cent below is even.
  `docs/architecture/money-rounding-impact.md` lists every calculation.
- **Deductibles apply on non-HDHP plans** where `DeductibleApplies=true`. Before,
  these claims were overpaid by the unmet deductible.
- **Timely filing denies CO-29 by default** (`Adjudication:TimelyFiling`, 365
  days unless configured). Existing tenants route the `TIMELY` pend to their
  fallback queue.
- **Eligibility fails closed** on an unknown or unreachable member (#1445), and
  no longer reports an upstream failure as no coverage (#1486).
- **Unmapped procedure codes deny with CARC 204**, not CARC 18.
- **Provider Access shares unless the member opts out** (ADR 021, #1431).

---

## Upgrading from 5.x

Do these in order, in a staging environment first.

### 1. Moving off Cosmos DB

**If you already run MongoDB** (the default since v5.0.0, `Database:Provider=MongoDb`):

- There is no data to move.
- Remove `CosmosDb:ConnectionString` and `CosmosDb:Endpoint` from your
  configuration.
- Keep any `CosmosDb:*` keys that name collections, such as
  `CosmosDb:ContainerName`. The Mongo path still reads them as collection-name
  overrides. ADR 017 lists every one.
- Make sure every service has `MongoDb:ConnectionString`. idcard,
  reference-data, fhir, authorization, rfai, consent, personal-representative
  and attachment used to start without one and no longer do.

**If you run on Cosmos DB for NoSQL (SQL API):**

- **Stay on 5.x until your data is in MongoDB.** 6.0 cannot read it.
- **CHO ships no migration tool.** The Cosmos and Mongo paths never stored
  identical documents: partition keys, ids and enum spelling differed. Treat the
  move as a data migration project, and validate it against the 5.x Mongo path
  before upgrading.
- In Azure, the target is Azure DocumentDB (MongoDB vCore). `main.bicep`
  provisions it with `enableDocumentDb`.
- Incremental Bicep deployments do not delete your existing SQL API account.
  Delete it yourself once you have checked the move.

**In Azure DocumentDB, also:**

- Add `retrywrites=false` to the connection string.
- Build any new unique index on a large collection in a maintenance window.
  Index builds block writes on that collection.
- Expect in-memory sorts and a 10-minute cursor lifetime.
- Read the compatibility table in ADR 017. EphemeralMongo does not exercise these
  limits, so run a smoke test against a real cluster before production.

### 2. Rename configuration

- `BatchEligibility:CosmosDb` → `BatchEligibility:MongoDb`.
- Remove `Portal:Search:RequireCriteria`.
- `Database:Provider`, if set, must be `MongoDb`.

### 3. MongoDB topology

- PCP assignment needs multi-document transactions, so MongoDB must run as a
  replica set. See `docs/operations/PCP-ATOMIC-CAPACITY.md`.
- Before starting authorization-service, check for duplicate authorization
  numbers. See `docs/operations/AUTHORIZATION-NUMBER-UNIQUENESS.md`.

### 4. Redis accumulators

1. Scale benefit-plan-service to zero.
2. Deploy 6.0.
3. Scale it back up.

Nothing is converted. Each hash is rebuilt from claims-service the first time it
is read. See `docs/operations/ACCUMULATOR-CENTS-CUTOVER.md`, including the
known gap for Production execution mode.

### 5. AR cash postings applied by 5.x

Applying a cash posting now credits AR balances. A posting applied by 5.x was
never credited. After you deploy, applying or voiding one of those postings
returns 409 until finance reconciles it. Follow
`docs/operations/AR-LEGACY-POSTING-RECONCILIATION.md`.

**Never run the reconciliation tool before you deploy.**

### 6. Callers and clients

- Move callers of `POST /api/v1/adjudication/adjudicate` to
  `POST /api/v1/adjudication/estimate`.
- Update clients that parse error bodies from the 13 services listed above.

### 7. Token signing (Google Cloud)

A Production token-service needs exactly one signing key:
`TokenSigning:KeyVaultKeyId` or `TokenSigning:CloudKmsKeyVersion`, which is new.
See `docs/security/portal-token-service.md`.

---

## Where this release has been validated

- **Google Cloud (GKE):** the demo environment is deployed on every merge to
  `main`, and the Million Claim Challenge runs against it.
- **Azure:** automatic deploys from `main` are paused
  (`AZURE_DEPLOYMENTS_ENABLED`) until the deployment checklist from #1217 is
  done. That checklist covers workload identity, token-service, FieldProtection
  key rings, the bank-account encryption migration, Stripe, NACHA per tenant,
  smart-auth certificates and the SFTP password. **6.0 has not been deployed
  to Azure.**
- **Azure DocumentDB:** the behaviours in ADR 017 have not been tested against a
  real cluster.

---

## Off by default

These ship in 6.0 but do nothing until a deployment turns them on:

| Feature | Switch |
|---|---|
| Provider Access bulk `$export` (#1580) | `BulkExport:Enabled`; GCP `enable_bulk_export` |
| Public Provider Directory (#1578) | A plan listed in `PublicProviderDirectory:Directories` |
| NACHA bank transmission (#1283) | `BankTransmission:Enabled` and the tenant's `paymentControls.nachaTransmission.enabled` |
| GL journal posting (#1286, #1491) | `GlEvents:DispatchEnabled` |
| Rated premium billing (#1288) | `PremiumBilling:RatedBilling:Tenants:{tenant}:Enabled` |
| Managed FHIR store publishing (#1469) | `ManagedFhirStore:Provider` (GCP only) |
| DRG grouping in adjudication (#1517) | Plan setting `pricing.inpatient.groupDrg` |
| Contract cutover to the canonical model (#1479) | Per-tenant cutover flag (ADR 016) |
| SAM.gov exclusion screening on GCP (#1564) | `SAM_API_KEY`; `SAM_SCREENING_ENABLED=false` turns it off |

---

## Not yet done for CMS-0057-F

The Patient Access, Provider Access and Payer-to-Payer API requirements take
effect on January 1, 2027. 6.0 does not meet all of them. These are planned for
6.1 (late November) and 6.2 (mid-December):

- **SMART Backend Services authentication** (`private_key_jwt` and JWKS, G-42).
  It is advertised but not supported. Until it ships, an outside provider's bulk
  client cannot authenticate.
- **Plan-Net 1.2.0 conformance for the Provider Directory** (G-36). The
  directory is public, but it has not been conformance-checked. It does not yet
  serve Location, HealthcareService, Endpoint or OrganizationAffiliation.
- **The rest of bulk `$export`** (G-34):
  - A provider's panel can come only from configured panels. Claims-history
    attribution cannot list one yet, so a plan that relies on it alone exports
    nobody.
  - Payer-to-Payer history is not yet sent over bulk.
  - `Patient/$export` is not built, and system-level `$export` answers 501.
  - No Bulk Data conformance kit has run against the export.
- **No X12 278 bridge.** CHO relies on HHS enforcement discretion for
  FHIR-based prior authorization without a 278.

A plan must also supply some things itself:

- the `PA-DECISION-EXTENSION` letter template;
- its entry in `PublicProviderDirectory:Directories`;
- its counsel's reading of "more than once" for the Patient Access usage
  figures.

`docs/epa-gap-matrix.md` gives the status of every requirement. The
California, Texas, Oregon and Illinois Medicaid assessments in this release all
conclude that readiness is not yet verified. They are planning documents, not
claims of readiness.

---

**Full changelog:** [CHANGELOG.md](../../CHANGELOG.md) ·
[v5.0.0...v6.0.0](https://github.com/aurelianware/cloudhealthoffice/compare/v5.0.0...v6.0.0)
