# Changelog

All notable changes to Cloud Health Office will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Hosted mode for `scripts/deploy-gke.sh` (`HOSTED=true`) and a manual, approval-gated `deploy-gcp-hosted` workflow. Disks are encrypted with the hosted disks key. Kafka gets a TLS-only listener with SCRAM-SHA-512 sign-in. MongoDB and Redis serve TLS from an internal CA, and Redis gets a password and an append-only file. Database passwords are kept in Secret Manager (`infrastructure/gcp/hosted-secrets.tf`, its own CMEK key). The plan's tenant and administrators are seeded instead of the demo tenant. Cloudflare, demo tenants and the AI examiner without a BAA are refused. In hosted mode `render_manifests.py` gives every Kafka client the SASL credentials and sets `SSL_CERT_FILE` with the CA bundle. `CPCDS_DROP_BUCKET` and `CPCDS_DROP_SERVICE_ACCOUNT` now reach the renderer. See `docs/deploy/GCP-HOSTED-HIPAA.md`, "Deploy".
- `render_manifests.py --hosted` for the hosted HIPAA environment. It renders Production hosts with synthetic data refused, keeps the FHIR data classified as PHI, and drops the demo seeding. Plan tenants must be named (never `demo`), and every Redis connection string is read from `redis-secret`. `--access-provider google-iap` signs the portal in through Google Cloud IAP. `--ca-bundle-configmap` gives every container a CA bundle that also trusts the in-cluster Kafka CA.
- Hosted HIPAA profile, operations (`infrastructure/gcp/hosted-operations.tf`): a daily Backup for GKE plan covering every namespace, its secrets and its volume data. It is encrypted with its own customer-managed key, kept 35 days and delete-locked for the first 7. Also security alert policies on the audit logs (IAM policy, encryption key, audit log routing and organization policy changes, and failed backups), emailed to `hosted_alert_emails`. Off unless `hosted = true`.

- Hosted HIPAA profile for the Google Cloud Terraform (`hosted = true`, `infrastructure/gcp/hosted.tf`), for a separate project under a Google Cloud BAA: customer-managed Cloud KMS keys (90-day rotation) for Kubernetes secrets, node disks, every bucket and Artifact Registry; a private-node regional cluster on its own VPC with Cloud NAT, Shielded nodes, a least-privilege node account and a DNS control plane endpoint; project organization policies (US-only locations, no service account keys, no external VM addresses); and data access audit logs for every service, kept six years. Off by default, so the demo is unchanged. A new `terraform-gcp` workflow formats and validates the module. See `docs/deploy/GCP-HOSTED-HIPAA.md`.

### Fixed

- The plan settings change consumer authenticated to Kafka with SASL PLAIN while every other client uses SCRAM-SHA-512. It now uses SCRAM-SHA-512 too.

## [6.2.0] - 2026-10-31

### Added

- CPCDS trial kit for plans that host CHO themselves (`docs/integration/CPCDS-TRIAL-KIT.md`): `scripts/cpcds/trial.sh check|load <folder>` dry-runs or loads a folder of CPCDS member, coverage and claim extracts and UM authorization files (CSV or X12 278) through the import APIs in the drop's order, prints what was created, refused and unmapped, and keeps each report; `trial.sh validate` runs the Inferno deployment kits. A compose overlay (`deploy/local/cpcds-trial/`) runs the same flow on a laptop with the sample files and the daily drop on.
- Fee schedule CSV import (#1694). `POST /api/v1/fee-schedules/import/csv` (benefit-plan-service, `settings:manage`) loads a state Medicaid fee schedule or a plan's rate sheet into the `FeeSchedules` store that claims adjudication prices from, either as a plan's default schedule (`planId`) or as a standalone schedule a contract term names. Dry run by default; column aliases, row filters, duplicate-code policy and provenance are documented in docs/integration/FEE-SCHEDULE-IMPORTER.md.
- Shadow adjudication of claims a core already adjudicated (#1668): `POST/GET /api/v1/claims/shadow-adjudication/runs` (claims-service, `claims:import`) re-adjudicates imported claims in CHO's pipeline without persisting, publishing or writing accumulators, and records a line-level comparison with the core's result (match, outcome, denial code, allowed/paid/member-liability variance, or CHO undecided with the missing input). Off unless `Adjudication:Shadow:Enabled`, per tenant in `Adjudication:Shadow:Tenants`. See `docs/integration/SHADOW-ADJUDICATION.md`.
- QHP benefit plan import (#1671): `POST /api/v1/plans/import/qhp-puf` (benefit-plan-service) reads the CMS Exchange QHP public use files (Plan Attributes, Benefits and Cost Sharing). A dry run by default; a real run creates Draft plans only and skips plan ids that already exist. Not yet run on the PY2027 files. See `docs/integration/QHP-PUF-BENEFIT-PLAN-IMPORTER.md`.
- State Medicaid and CHIP benefit plan templates (#1672, #1673, #1678): 25 templates in `schemas/benefit-plan-templates/` for Texas (STAR, STAR+PLUS, STAR Kids, STAR Health, CHIP), California (Medi-Cal), Florida (SMMC MMA and LTC, Healthy Kids), Arizona (AHCCCS, ALTCS, KidsCare) and New York (Medicaid managed care, HARP, MLTC, Child Health Plus). `GET /api/v1/plans/templates` lists them and `POST /api/v1/plans/templates/{id}/drafts` creates a Draft plan from one. Each value is marked `sourced` (with its citation) or `verify`; most covered-service values are still `verify`, so a template is a starting point to check against the plan's state contract. See `docs/integration/STATE-PROGRAM-BENEFIT-TEMPLATES.md`.
- Website insight "Medicaid CMS-0057-F blueprint, built and tested" (#1689).
- The CapabilityStatement declares the profiles CHO serves (G-36): Patient (US Core, C4BB-Patient), Coverage (C4BB-Coverage), ExplanationOfBenefit (the CARIN BB inpatient, outpatient, professional and oral profiles, alongside PDex prior authorization), and Practitioner and Organization (US Core, C4BB). These are the profiles the projections put in `meta.profile`.
- Portal **Imports & EDI** console (was EDI Transactions): new **Daily drop** tab (freshness and reconciliation report, totals by import type, each drop run with its files, step counts and errors, a download of each archived file as received, and **Load now**) and **Setup** tab (drop settings, and the hosted SFTP login with its SSH keys). The CPCDS drop API now sends its states as names (`"Failed"`, `"Stale"`, `"Authorizations"`) rather than numbers, and drop settings take the line of business by name or number.
- The CPCDS file drop archives every file it loads, as received, in document-service (`CpcdsDrop:Archive:Enabled`, on in the GCP render): new document type `SourceExtract` (CSV, plain text or X12 only, its own size cap `Documents:MaxSourceExtractBytes`, default 1 GiB), channel `SFTP`, linked to the run as `ImportRun:{runId}` (readable with `claims:import`), ten-year retention and legal hold. The run records each file's `documentId`; an archive failure is reported but never blocks the load.
- Authorization import from X12 278: `POST /api/v1/authorizations/import/x12-278` loads 005010X217 responses (paired with their requests by trace number for received dates and requested quantities) as read-only authorizations keyed by the certification number; re-sent responses in a new envelope are unchanged. Authorization files (`auth` or `278` in the name) now load from the CPCDS daily drop after members and coverage, routed to the 278 or CSV import by content.
- Implementation guide `docs/integration/LOADING-A-PLANS-DATA.md`: loading a plan's data into CHO in dependency order (benefit plans, plan-code mappings, providers and network, members and coverage, claims, authorizations), the daily drop, Inferno validation and a go-live checklist.
- Authorization import: `POST /api/v1/authorizations/import/csv` (authorization-service, `authorizations:import`) loads prior authorizations a plan's UM platform or core decided, so Patient Access, Provider Access, Payer-to-Payer and the Prior Authorization API serve them. Keyed by the source's authorization number, idempotent on reload, never overwrites an authorization CHO owns, and read-only in CHO (decision, cancel and extension endpoints answer 409; the SLA watchdog and expiry worker skip it). Responses show `imported` and `importedFrom`. See `docs/integration/AUTHORIZATION-IMPORT.md`.
- Hosted CPCDS SFTP logins: `GET/PUT/DELETE /api/v1/tenants/{tenantId}/cpcds-sftp` (tenant-service, `settings:manage`) creates a plan's SFTPGo login from the SSH public keys it registers (ed25519, ECDSA, RSA 3072+), scoped to its `{tenantId}/` prefix of the drop bucket with `/inbound` writable and `/processed`, `/failed` read only. No passwords. Off unless `CpcdsSftp:Enabled`.
- Hosted CPCDS drop on GCP: `infrastructure/gcp/cpcds-drops.tf` (`enable_cpcds_drops`) creates a private drop bucket (unversioned, objects deleted after 30 days, optional CMEK), Workload Identity accounts for claims-service and SFTPGo with object access to that bucket only, and a static SFTP address. `deploy/gcp/sftpgo/` runs SFTPGo on GKE with Cloud Storage as each plan's filesystem: SSH-key-only sign-in, port 22 limited to the plans' source ranges, the REST API reachable only from tenant-service, web UIs off. `render_manifests.py --cpcds-drop-bucket/--cpcds-drop-service-account` points claims-service's drop at the bucket. See `docs/integration/CPCDS-SFTP-DROP-GCP.md`.
- The CPCDS file drop can read a Google Cloud Storage bucket (`CpcdsDrop:Store = Gcs`, `CpcdsDrop:Gcs:Bucket`) instead of a mounted folder, with the same `{tenant}/inbound`, `processing`, `processed` and `failed` layout. Each file still goes to exactly one run when several pods poll: a run copies the file into its run prefix and deletes the inbound object only if it is still the generation it listed. A file rewritten after it was listed waits for the next poll. Credentials are the pod's Google identity. This is the hosted drop on GCP, where the SFTP server writes into the bucket.
- A tenant can turn its CPCDS file drop on without a redeploy: `PUT /api/v1/claims/import/cpcds/drops/settings` (claims-service, `claims:import`) saves the tenant's drop settings (on or off, source system, line of business, column map, shadow after import, expected cadence and grace in hours) and `GET` returns them with where they came from and whether files dropped now would load. Saved settings replace the tenant's `CpcdsDrop:Tenants` entry, including to turn it off; the worker reads the tenant list each poll; `CpcdsDrop:Enabled` remains the deployment's switch. This is what the portal setup page and onboarding call after a plan signs up.
- Validate a deployment with Inferno: `./scripts/interop/run.sh deployment` runs the pinned US Core, CARIN BB and Plan-Net kits against a CHO that is already running (a plan's own installation or a hosted tenant) and judges the data it holds, such as claims loaded with the CPCDS importer. Only the kit starts and nothing is seeded; the deployment's FHIR base, the plan's patient ids, an access token the deployment issued and the public directory URL come from `CHO_INTEROP_TARGET_*` environment variables, and the token never reaches the evidence. Groups and known kit issues match the nightly run. Bulk Data and PDex are refused against a deployment until the kit can be registered as a Backend Services client there. See `docs/integration/VALIDATE-A-DEPLOYMENT.md`.
- CPCDS claims link to CHO coverage: an imported claim's EOB `insurance.coverage` now references the CHO Coverage the member held on the first date of service for the claim's benefit line, looked up in coverage-service, instead of the core's coverage id, which CHO's FHIR server could not resolve. Without one, the EOB names the plan by display and the claim result carries a warning; reloading the file after coverage is loaded links it. The core's id is kept as `ExternalSource.SourceCoverageId`.
- CPCDS file drop for claims another system adjudicated: `POST /api/v1/claims/import/cpcds` (claims-service, permission `claims:import`) loads a CARIN CPCDS claim extract from the plan's core (QNXT, Facets, HealthRules, any system that writes CPCDS) into the claim store, so Patient Access and Provider Access serve those claims as CARIN BB ExplanationOfBenefits through the same projections as claims CHO adjudicated. Professional, institutional (inpatient and outpatient) and oral claims are mapped; pharmacy claims are refused until a pharmacy projection exists. Comma, pipe or tab delimited files; headers match ignoring case and punctuation, and a column map covers files that name columns differently. Reloads are idempotent: EOB ids are deterministic per tenant and source claim, an unchanged claim writes nothing, and a changed claim gets a new version with the old one superseded. A claim is refused whole rather than loaded in part, and one refused claim does not stop the file. The response reports claims created, updated, unchanged and refused, with submitted and paid amounts in the file against paid amounts loaded. A member crosswalk seam (`ICoreMemberCrosswalk`, pass-through by default) maps the core's member ids. See `docs/integration/CPCDS-FILE-DROP-IMPORTER.md`.
- CPCDS freshness and reconciliation report: `GET /api/v1/claims/import/cpcds/drops/report` (`claims:import`) says whether a core-fed tenant's data is current (`Fresh`, `Stale` past the tenant's `ExpectedEvery` plus grace, `Failing` after 3 failed runs, `NeverLoaded`, `NotConfigured`), with the newest claim extract's time and age, and over a window of days the claims created, updated, unchanged and refused, paid amount in the files against loaded, members and providers loaded, unmapped plan codes, and a plain-language list of what needs attention.
- CPCDS daily file drop: claims-service watches `{CpcdsDrop:Root}/{tenantId}/inbound` for each configured tenant and loads what has stopped changing, in dependency order: members and coverage (enrollment-import-service), then for each claim file the providers it names (provider-service) and its claims. Files are recognized by name, moved atomically into a per-run folder (safe with several replicas) and then to `processed/` or `failed/`. Each run is recorded with its files, per-step counts and errors (`GET /api/v1/claims/import/cpcds/drops`, `claims:import`); `POST .../drops/run` loads now. After each claim batch loads, tenants with `ShadowAfterImport` get a shadow adjudication run on it. Works on any storage the pod can mount (SFTP volume, Azure Blob, GCS, persistent volume). Off unless `CpcdsDrop:Enabled`.
- Providers named on CPCDS claims: `POST /api/v1/providers/import/cpcds-claims` (provider-service, `providers:write`) reads the distinct NPIs in a CPCDS claim file's billing, performing, attending, referring and site-of-service columns and creates each one CHO does not have as a reference-only provider described from NPPES, so the imported EOBs' `Practitioner/{npi}` and `Organization/{npi}` references resolve. New `Provider.ReferenceOnly`: such providers are served by the FHIR API but never published in the public Plan-Net Provider Directory. NPIs NPPES does not know, failed lookups and non-NPIs are reported, not created; `dryRun=true` writes nothing.
- CPCDS members and coverage: `POST /api/v1/enrollment/import/cpcds` (enrollment-import-service, permission `enrollment:process`) loads a CPCDS member file and, optionally, its coverage file. Both are turned into the 834 enrollment shape and processed by the existing 834 import, so members, sponsors and coverage are created and updated as from an 834. Every member, dependants included, is filed under the core's own member id, so the claims file's member ids resolve without a crosswalk. Coverage needs the tenant's plan-code mapping; every call returns the plan-code gap report, and `dryRun=true` writes nothing. The CSV reader is shared from `CloudHealthOffice.Infrastructure`.
- A requesting payer can find a matched member's Patient by the identifier `$member-match` returned (PDex 2.0 requirement 35). Under `system/PayerToPayer.read`, `GET Patient?identifier=` with that one parameter is admitted, and the searchset holds a Patient only when the consent naming the caller's registered payer, recorded by member match (ADR 022), is in force for that member. Anyone else is left out, so no consent and no such member look the same. Every other Patient search, a Patient read and every other resource are still refused for that scope.
- `Patient/$member-match` accepts the HRex/PDex request form. A FHIR `Parameters` body (`MemberPatient`, `CoverageToMatch`, optional `CoverageToLink`, `Consent`; `hrex-parameters-member-match-in`) is read exactly as one `MemberBundle` of `Group/$bulk-member-match` is, and a match is answered with the HRex `MemberIdentifier` Parameters (v2-0203 `MB`, system `urn:cho:member-id`, which `Patient?identifier=` finds). A Consent that names the member as HRex does, by a local reference to the `MemberPatient` parameter (`Patient/1`), is judged against that Patient, in this operation and in each `MemberBundle` of `Group/$bulk-member-match`; before, such a consent named no one CHO could resolve and was refused. The consent check, payer binding and refusals are otherwise unchanged: no consent is 422, no match or more than one is 422, a request without `MemberPatient` or `CoverageToMatch` is 400. Before this, only CHO's own JSON shape was understood, so another payer's conforming request was refused as having insufficient criteria. CHO's JSON form still works and still answers with a Bundle. The CapabilityStatement now declares `member-match` on Patient with the HRex canonical (G-36).
- Nightly conformance: the Inferno PDex payer-server kit (`davinci-pdex-test-kit` v0.13.2) judges CHO's HRex `Patient/$member-match` as `INFERNO-PDEX-SERVER-001`: the member-match group and the no-match case. The kit authenticates as a requesting payer (SMART Backend Services, `system/PayerToPayer.read`, registered payer) and sends HRex requests built from the seed with a consent naming its payer. The interop profile binds Payer-to-Payer to CHO's own services. The result is published with the other kits, with an `inferno-pdex` badge on ci-evidence.
- DTR questionnaire packages with CQL prepopulation. `POST /fhir/r4/Questionnaire/$questionnaire-package` follows DTR 2.2.0: it takes the member's `coverage` and any of `order`, `questionnaire` (canonical, optionally `|version`), `context` and `changedsince`, and answers with `dtr-qpackage-output-parameters` holding one `DTR-QPackageBundle` per questionnaire: the Questionnaire first, a draft in-progress QuestionnaireResponse (subject, `qr-coverage`, `qr-context` for the orders, `intendedUse` withpa, every reference pointing at the caller's own resources), every Library the form names through `cqf-library` and recursively depends on (CQL and ELM), and every ValueSet those reference, expanded. Orders pick forms by the codes a questionnaire declares as its `focus` use context. A requested questionnaire that does not exist is a warning in `outcome`, not a failure; a coverage without an id or beneficiary is 400. Questionnaires now resolve by canonical (`url`, `url|version`), by id, or by this server's `{base}/Questionnaire/{id}`, so a DTR client can submit a completed QuestionnaireResponse naming the canonical it was given; forms stored without a canonical are published under the server's base at their stored version. The CapabilityStatement declares the operation with the DTR canonical and `dtr-std-questionnaire` on Questionnaire (G-54).
- The MRI prior authorization form (`https://cloudhealthoffice.com/fhir/Questionnaire/pa-imaging-mri|1.0.0`) is DTR content in `src/services/fhir-service/DtrContent/`: a DTR standard questionnaire for 11 MRI CPT codes whose radiculopathy, conservative treatment and symptom onset answers prepopulate from the EHR through the `ChoImagingMriPrepopulation` CQL library (`text/cql-identifier` expressions, each with its ELM), with two value sets (radiculopathy diagnoses, physical therapy procedures). Answers are evidence or nothing: a missing record never prepopulates "No". CHO does not run the CQL; the provider's DTR client does (Q-6). `scripts/dtr/compile-cql.sh` compiles the CQL with the HL7 translator and records its hash, and a test fails if the CQL changes without a recompile (G-55, first form). Shipped default-library forms now upgrade an older stored copy when the build ships a higher `meta.versionId`, so existing deployments get the new MRI form.

- CRD names the DTR form and DTR resolves CRD's assertion. A documentation-required `coverage-information` now carries a `questionnaire` canonical for each active DTR form whose `focus` use context lists the ordered code (CRD resp-38; the MRI form for its 11 CPT codes). CHO keeps every coverage assertion it returns (Mongo `crd_coverage_assertions`, 90 days by default, `Cms0057:Crd:AssertionRetentionDays`), so `$questionnaire-package` with only `coverage` and `context` (the `coverage-assertion-id`, DTR spec-107) returns the forms CRD named. A context that is unknown, expired, issued for another Coverage or for an answer that needed no documentation is 422 (spec-130, spec-108); one whose forms are missing says how else to send documentation (oper-8). A CRD launch card can carry `appContext` (questionnaire canonicals, `context`, `order`, `coverage`) for a DTR app the tenant configures in `Cms0057:Dtr:DtrLaunchUrl` (G-44, G-56).

### Changed

- **CRD documentation cards no longer link to a CHO DTR app.** CHO ships none (decision Q-6), and the old card's smart link went to `https://cloudhealthoffice.com/dtr/launch`, which served nothing. Without `Cms0057:Dtr:DtrLaunchUrl` the card is now an instructions card (no link, no suggestion) and the provider completes the form in the EHR's own DTR client; with it, the card launches that app. `DtrLaunchUrl` no longer has a default.
- **`$questionnaire-package` refuses a `context` it cannot resolve** with 422, even when the request also carries orders (spec-130). Before, an unknown context beside an order was ignored with an information note.
- **`$questionnaire-package` input and output changed (DTR 2.2.0).** It now requires a `coverage` parameter and answers with Parameters (`packagebundle`, `outcome`) instead of a bare Bundle, and a questionnaire that is not found is a warning in a 200 rather than a 404. Callers that sent only `questionnaire` get a 400 naming the missing coverage.

### Fixed

- **Payer-to-Payer outbound consent follows HRex `hrex-consent`.** When CHO is the new payer, the Consent it sends now names the prior payer with the profile's source role (`provenance-participant-type#performer`, was v3 `PERF`), names the member as performer, and points `sourceReference` at the registry consent that authorized the exchange. CHO's own inbound check is unchanged.

## [6.1.1] - 2026-10-31

"Spartacus," patch.

Fixes a CARIN BB regression in the published v6.1.0 tag. That tag was placed
on #1654 rather than on the commit the Inferno gate tested, and #1654 broke
`Coverage?_include=Coverage:payor`. This release adds #1655, which fixes it,
plus website and documentation updates (#1652 to #1657). No configuration
changes. Release notes: `docs/releases/RELEASE_NOTES_v6.1.1.md`.

### Fixed

- CARIN BB `Coverage?_include=Coverage:payor` answers again. Once the interop Coverage named the plan's Organization as its payor (`Fhir:Coverage:PayorReference`), the search was refused with a 400 because fhir-service could not include a referenced payor. A payor that references the plan's own Organization (`Fhir:PlanOrganization`) is now included in the searchset (`search.mode` include); any other referenced payor is still refused.
- CARIN BB's Organization tests and most must-support checks have data to judge. The plan's own Organization is served as a C4BB-Organization at `Organization/{Fhir:PlanOrganization:Id}` (payer id, NAIC code, address; read, `_id` and `_lastUpdated` search), and the EOB insurer (claims-service `Fhir:Insurer:Reference`) and Coverage payor (`Fhir:Coverage:PayorReference`) reference it. A claim keeps its billing provider's 837 entity type (NM102, `billingProviderEntityType`), so the EOB's provider is `Organization/{npi}` or `Practitioner/{npi}` when the 837 said which; provider-service's provider Organizations declare C4BB-Organization 2.0.0 with a US address, and its Organization search takes `_id` and `_lastUpdated`. Coverage carries the member id as its MB identifier and, when recorded, the group and plan names (new optional `groupName`/`planName` on coverage create). Patient adds the member id as CARIN BB's unique member id (`um`) and states `deceasedBoolean` false for a living member; member create accepts `race` and `ethnicity`. The interop seed and compose wire all of this.

### Documentation

- Website: the Inferno results page, badges and article show the excused known kit issues and match the all-green run (#1657); the architecture page is refreshed for v6.0 (#1656); new answers on pended versus pending prior authorizations (#1652).

## [6.1.0] - 2026-10-31

"Spartacus."

A minor version with no breaking changes: SMART Backend Services
authentication in both directions, the public Provider Directory as Plan-Net
1.2.0, and the rest of Bulk Data `$export` (still off by default). Covers the
pull requests merged between v6.0.0 and this release (#1583 to #1651).
Release notes: `docs/releases/RELEASE_NOTES_v6.1.0.md`.

### Added

- CAQH CORE real-time claim status. With `CoreConnectivity:Enabled`, claims-service answers CORE envelopes at `/core/claimstatus`: a 276 (005010X212) in, a 277 back with one status loop per matching claim (TRN echoed, STC, payer claim control number, patient control number, service dates). Claims are looked up by REF*1K, REF*EJ, or the subscriber, provider and dates, and are only returned when they belong to the subscriber the 276 names. Not found is A4:35; a lookup that fails or runs past the response budget is E1:0.
- CAQH CORE real-time eligibility. With `CoreConnectivity:Enabled`, eligibility-service answers CORE Connectivity vC2.2.0 envelopes at `/core/eligibility` (SOAP 1.2 with WSDL at `?wsdl`, or MIME multipart): a 270 in, its 271 back in the same call, a TA1 for a rejected interchange, or a 999 for a 270 that cannot be parsed. Submitters authenticate with a client certificate, a username and password (PBKDF2 hashes in `CoreConnectivity:Submitters`), or a CHO OAuth token; the tenant comes from the submitter registration. A 270 CHO cannot answer within `CoreConnectivity:ResponseBudget` (15 s) gets a 271 with AAA 42. Each exchange is measured (`cho.core.realtime.duration`) and logged without payload content. `POST api/eligibility/270` now shares the same pipeline. See `docs/architecture/caqh-core-connectivity.md`.
- Claims intake accepts dental claims (837D, 005010X224A2). The 837 parser reads SV3 service lines (CDT procedure, charge, place of service, oral cavity, prosthesis code, quantity, diagnosis pointers) and the TOO tooth and surface segments; claim lines now keep `teeth`, `oralCavityDesignations` and `prosthesisCrownInlayCode`, and dental lines reach the scrub engine as CDT (`AD`) with their teeth. SNIP validation recognises the 837D version and checks SV3, TOO, CLM02 balancing against SV302, SV311 pointers and CDT format; diagnoses are optional on 837D as the implementation guide allows. Before this, a dental file was rejected at GS08 and, if parsed, lost every service line.
- Website and README: Inferno conformance results page (`/evidence/inferno-conformance`) rendered from a new `inferno-conformance-public-evidence.json` snapshot, and the article "Which Inferno Test Kit Checks Which CMS-0057-F API". The DaVinci Interop workflow's new `publish-inferno` job projects the nightly kit results (`scripts/interop/publish-inferno-evidence.mjs`) to the ci-evidence branch, and the site deploys overlay it. It also publishes one shields.io badge per kit to `ci-evidence/badges/inferno-*.json`, shown in the README. Pages that said no Inferno kit had run now link to the results.
- CAQH CORE system availability. `CoreConnectivity:Availability` holds the weekly regular downtime, planned windows (flagged when announced less than a week ahead) and the record of unscheduled and emergency outages. `GET /core/eligibility/availability` publishes the schedule, the downtime in force, the next 30 days of windows and this week's and last week's availability against the 86% rule, without credentials. During a published window a 270 gets an immediate 271 with AAA 42. Each host logs a warning at startup for a schedule that cannot meet the rule.

- **The public Provider Directory links practitioners, affiliations and
  endpoints the way Plan-Net expects.** A practitioner's role names the
  hospital of their hospital affiliation as the organization it is practised
  at, with that hospital's HealthcareService and Location. Each
  OrganizationAffiliation names the organization that manages its network as
  its primary organization, and the payer Organization lists its Endpoints.
  All three are Plan-Net must-support elements CHO did not publish, and the
  Inferno Plan-Net kit's forward-chain searches (`PractitionerRole?service.service-category=`,
  `OrganizationAffiliation?primary-organization.name=` and others) had nothing to
  follow. A link to a hospital the directory does not publish is dropped.

- **The GKE demo can publish public Provider Directories (G-36).** The
  `PUBLIC_PROVIDER_DIRECTORY` repository variable (or environment variable for
  `scripts/deploy-gke.sh`) is a JSON object keyed by directory id, each entry
  naming its tenant and the plan's own name and address; the deploy turns it
  into fhir-service's `PublicProviderDirectory:*` settings. A missing address
  field, an unknown setting or a directory id that is not a URL segment fails
  the render. Unset, nothing is published, as before. Until now only the
  interop compose profile could publish a directory.

- **The nightly Bulk Data run now judges the SMART Backend Services token
  endpoint.** `INFERNO-BULK-SERVER-001` adds the kit's SMART Backend Services
  group (discovery STU2 and six authorization tests) to the Group and Patient
  export groups. Its token-endpoint TLS test reports omit through the kit's
  own `INFERNO_DISABLE_TLS_TEST` switch, because the interop network is plain
  HTTP; that test passed on hosted Inferno against the GKE demo (G-60).

- **The GKE demo can be put in front of hosted Inferno (G-60).** With
  `CONFORMANCE_FHIR_HOST` and `CONFORMANCE_AUTH_HOST` set, the demo deploy makes
  smart-auth-service's issuer and fhir-service's base URL those public tunnel
  hostnames, has fhir-service trust that issuer as an explicit `ExternalIssuer`
  entry, and has smart-auth-service believe `X-Forwarded-Proto` from the
  cluster's pod range (cloudflared delivers HTTPS as plain HTTP). Off by default.
  The Cloudflare Access, tunnel and TLS steps and the Inferno run are in
  `docs/deploy/GCP-CONFORMANCE-TESTING.md`.
- **CMS-0057-F prior authorization: refused appeal outcomes can be re-queued.**
  An appeal outcome the authorization's reporting ledger refused (no evidenced
  adverse determination, a time outside the ledger, the authorization not
  found) or that the retry sweep gave up on used to stay off the reporting
  record for good. `GET api/service-cases/reporting/needs-attention` lists them
  for the tenant; once the cause is fixed, an `appeals:admin` (or
  `platform:admin`) operator re-queues one with
  `POST api/service-cases/{id}/reporting/requeue`, which sends it again at once,
  or every refused outcome in the tenant with
  `POST api/service-cases/reporting/requeue`, which the sweep delivers on its
  next pass. Each re-queue resets the attempt budget and keeps the refusal it
  cleared, who cleared it and when on the case. The portal case screen shows
  administrators a "Re-queue for reporting" button on a refused outcome.

- **Bulk export `_typeFilter` and `patient` (G-34).** Group and Patient
  `$export` apply `_typeFilter` queries to each resource as it is written,
  using the R4 search parameter's own FHIRPath (token, string and date
  parameters, `_id`, `_lastUpdated`, and CARIN BB's EOB `type` and
  `service-date`); anything else is refused at kick-off. A POST kick-off reads
  its parameters from a FHIR Parameters body, which is where Bulk Data 2.0
  puts `patient`: it narrows the export to the named members and never widens
  it, and a named patient the export cannot include is reported in the
  manifest's `error` array. Before this, `_typeFilter` on these endpoints was
  silently ignored.
- **Nightly Inferno Bulk Data run (G-60).** `INFERNO-BULK-SERVER-001` runs the
  bulk-data-test-kit (`v0.13.2`) Bulk Data 2.0.0 Group and Patient export
  groups against the seeded `interop-cho` profile, so export files are
  downloaded and their resources validated every night. The kit authenticates
  with SMART Backend Services as a provider-bound backend client the harness
  registers through smart-auth-service's admin API, using the public keys the
  kit publishes. Fetch it with `./scripts/interop/fetch-inferno.sh bulk-data`
  and run it with `./scripts/interop/run.sh inferno bulk-data`. See
  `docs/interop/davinci.md`.
- **Outbound bulk Payer-to-Payer (G-35).** CHO, as the new payer, can ask a
  prior payer for many members at once over the PDex 2.1 bulk operations:
  `POST /fhir/r4/PayerToPayer/$bulk-initiate` records a job, and a background
  worker runs `Group/$bulk-member-match`, then `Group/{id}/$davinci-data-export`
  on the matched Group, downloads the NDJSON, sorts it by member and ingests
  each member's package as a single exchange does. Each member is checked as
  `$initiate` checks one, and their opt-in is asked again before the export and
  before ingestion. Calls carry CHO's SMART backend token for the payer (G-42).
  Status and file URLs from the payer are followed only on its directory origin
  (or a listed file host, without the token). Off by default
  (`Cms0057:PayerToPayerOutbound:Bulk:Enabled`); jobs are kept in MongoDB
  (`p2p_outbound_bulk_jobs`). See `docs/architecture/payer-to-payer.md`.
- **The public Provider Directory is Da Vinci PDex Plan-Net 1.2.0 (G-36).**
  `/fhir/r4/directory/{directory}/` now serves all nine Plan-Net profiles:
  networks, organizations (including the plan's own payer Organization),
  Practitioner, PractitionerRole, Location, HealthcareService,
  OrganizationAffiliation, InsurancePlan and Endpoint. It answers every SHALL
  search parameter, chain, `_has` reverse chain, `_include` and `_revinclude`
  of the Plan-Net CapabilityStatement, which `/metadata` instantiates, plus
  vread and ETags. provider-service projects the directory
  (`/fhir/plannet-export/*`, read by fhir-service only); fhir-service holds each
  published directory as an in-memory snapshot, rebuilt in the background after
  `PublicProviderDirectory:RefreshMinutes` (60). New settings:
  `PublicProviderDirectory:Payers:<directory id>:*` (the plan's own
  Organization; Plan-Net requires its address), `RefreshMinutes` and
  `MaxResources`. See `docs/architecture/public-provider-directory.md`.
- **The Inferno Plan-Net kit runs nightly against the public directory**
  (`INFERNO-PLANNET-SERVER-001`, kit `v0.13.4`, generated from Plan-Net 1.1.0),
  with no token. The `interop-cho` profile gains benefit-plan-service and its
  Redis, and the seeder writes a network, two providers and a plan for it.
- **SMART Backend Services client authentication (`private_key_jwt`, G-42).**
  A tenant administrator can register a backend client with its public keys,
  as a JWK Set (`jwks`) or a `jwksUri`, instead of receiving a client secret.
  The client then gets `client_credentials` tokens by presenting a signed JWT
  as the SMART Backend Services profile defines it (RS384 or ES384, `aud` =
  the token endpoint, single-use `jti`, `exp` at most five minutes out).
  `PUT /api/admin/smart/clients/{clientId}/keys` rotates the keys. This lets
  outside providers' bulk `$export` clients authenticate. Secret clients are
  unchanged. See `docs/security/smart-auth-tenancy.md`.
- **A backend client can act for a provider (G-42).** A tenant administrator
  sets `providerId` on a backend client registration (or later with
  `PUT /api/admin/smart/clients/{clientId}/provider`). Its tokens carry
  `provider_id`, and fhir-service treats the client as that provider for
  Provider Access: attribution on single reads, and bulk
  `Group/{providerId}/$export`. fhir-service believes `provider_id` only from
  the issuer marked `IssuesChoClientRegistrations` and only on a system-scope
  token. The token's `sub` is still the client id.
- **CHO authenticates to other payers with SMART Backend Services (G-42).**
  For Payer-to-Payer as the new payer, fhir-service now gets an access token
  from the other payer's token endpoint with a `private_key_jwt` assertion it
  signs itself (RS384 or ES384), caches it until a minute before it expires,
  and retries once with a new token when the peer answers 401. Configure CHO's
  keys under `SmartBackendClients:SigningKeys` (from the secret store; a bad
  key stops startup) and one registration per payer under
  `SmartBackendClients:Clients:<key>` (`ClientId`, `TokenEndpoint`, `Scope`),
  then set that key as `CredentialKey` on the payer's Payer-to-Payer directory
  entry. `GET /fhir/r4/.well-known/smart-backend-jwks.json` publishes the
  public keys for the peer's `jwks_uri`; standby keys allow rotation. Payers
  with no `CredentialKey` behave as before (no token). Other outbound clients
  can use the same `ISmartBackendTokenClient`. See
  `docs/security/smart-auth-tenancy.md`.
- **Bulk Provider Access exports members attributed from claims history
  (G-34).** A provider's `Group/{providerId}/$export` now includes the members
  it billed or rendered for inside the attribution lookback, not only those on
  configured panels. claims-service lists them at
  `GET api/provider-attribution/members` for fhir-service's service identity
  only; if claims history cannot be read, the export fails instead of writing a
  short panel. Each member is still checked for attribution and opt-out before
  anything is written.
- **Payer-to-Payer bulk transfer (G-35).** With `BulkExport:Enabled`, a
  requesting payer's registered backend client can export, in one Bulk Data
  job at `PayerToPayer/$export`, every member whose consent naming that payer is
  in force. Each member gets what `$member-data-export` would send it: consent
  checked again, the five-year claims window, prior authorizations and the
  same redaction. The job needs `system/PayerToPayer.read`, the same grant as
  the single-member export. Only the client that started the job, while still
  registered to that payer, can poll or download it.
- **`Patient/$export` (G-34).** With `BulkExport:Enabled`, a provider can
  start a bulk export at `Patient/$export`. It exports the same attributed panel
  as `Group/{providerId}/$export`. It needs `system/Patient.read` (or a
  wildcard) and a backend token.
- **PDex bulk member match (G-35).** With `BulkExport:Enabled`, a requesting
  payer can match many members at once with `POST Group/$bulk-member-match`
  (PDex 2.1 Parameters, one `MemberBundle` per member, up to
  `BulkExport:MaxMembersPerMatch`, 500 by default). Each member is matched as
  `$member-match` matches one and sorted into the PDex Groups: matched, not
  matched, and matched but consent-constrained. With `Prefer: respond-async`
  the answer is at a status URL. The payer can read its Groups at
  `GET Group/{id}` for `BulkExport:MatchGroupLifetime` (30 days by default),
  and exports the matched one with
  `Group/{id}/$davinci-data-export?exportType=hl7.fhir.us.davinci-pdex#payertopayer`,
  which exports each member as `PayerToPayer/$export` does. `_until` and
  `_typeFilter` are refused. All of it needs `system/PayerToPayer.read` and a
  client registered to the payer.

### Changed

- Every bulk export kick-off (`$export`, `Patient/$export`,
  `Group/{id}/$export`) now accepts GET, the Bulk Data IG's kick-off method, as
  well as POST.
- The public directory's Location, HealthcareService, Endpoint and
  OrganizationAffiliation are new; its Practitioner, PractitionerRole and
  Organization now declare only the Plan-Net profiles, and PractitionerRole
  carries Plan-Net codes (ProviderRoleCS, specialties from the Plan-Net value
  set) instead of the CHO tier and secondary specialties. The authenticated
  `/fhir/r4/` routes are unchanged.
- **`.well-known/smart-configuration`** now advertises RS384/ES384 (and the
  256/512 variants) for `private_key_jwt`, the `client-confidential-asymmetric`
  capability, and the `refresh_token` grant (D-25).

### Fixed

- FHIR Practitioner search honours `_lastUpdated` (date, dateTime or instant at its own precision, with the FHIR prefixes). It was ignored, so a search for one practitioner's last update returned every practitioner, which failed the CARIN BB kit's Practitioner `_lastUpdated` test. A value that is not a FHIR date is a 400.
- CARIN BB Coverage and Practitioner. fhir-service's Coverage now carries `subscriberId`, `subscriber` and `relationship` (subscriber-relationship, mapped from the member's INS02) read from member-service, and declares C4BB-Coverage at 2.0.0 only when those are present. member-service's FHIR Patient search returns each member's relationship code. provider-service's Practitioner types its NPI identifier (v2-0203 `NPI`), declares C4BB-Practitioner at 2.0.0, and answers `_id` searches by NPI. The interop seed adds the claims' rendering practitioner, so the EOB's careTeam reference resolves; the public directory lists it with no role.
- 271 content follows the CAQH CORE eligibility data content rule. The 270 parser reads every `EQ` service type (repeats and several segments), `TRN` trace numbers and the BHT reference; the 271 echoes them as `TRN*2` and BHT03. A generic `EQ*30` inquiry is answered with the CORE service types the plan covers. Deductible and out-of-pocket amounts move to EB07 with time period 23 (or 22), met amounts use 24 and remaining amounts 29, always sent. Co-payment, co-insurance (EB08 as a decimal) and visit limits go under each service type's X12 code, mapped from the plan's category; a label is never sent in EB03. Network "N" now stays N. Inactive coverage no longer carries `AAA*N**42*Y`, which meant "try again later".

- **The Inferno job frees disk space before building images.** Run
  41159022696 ran out of disk while building the CHO service images, so no kit
  started. The job now removes preinstalled toolchains it doesn't use (Android,
  GHC, Swift, CodeQL) and prunes Docker before the build, and logs `df` before
  and after.
- **DaVinci Interop judges an Inferno run from its tests.** The harness runs a
  suite's groups one at a time in one session, and Inferno's row for their
  parent group carries the failures of every group run before it. In run
  41133414552 the CARIN BB scenario failed only on that row (`c4bb_v200-Group02`),
  carrying the EOB group's excused validation tests. Each group's failures are
  still counted in its own run.
- **The Plan-Net forward-chain searches are known kit issues.** The kit
  (v0.13.4) chains on the first resource it collected with the chained element,
  usually the payer, whether or not anything references it. The 25 forward-chain
  tests that still fail after #1636 and #1639 are listed in
  `infernoKnownKitIssues` with that reason (see `docs/interop/davinci.md`).
- **DaVinci Interop runs the CARIN BB FHIR API group as one Inferno run.**
  The Coverage, Organization and Practitioner groups read the references the
  EOB searches save in Inferno's scratch, which lasts one run, so run as
  separate groups they skipped with nothing to read, even in the kit's order.
  The scenario now runs their parent group (`c4bb_v200-Group02`), which also
  runs the RelatedPerson group (it skips: CHO publishes no RelatedPerson).
- **The interop stack has 15 minutes to start.** `docker compose up --build`
  for the first scenario builds every CHO and kit image, and took over the old
  5-minute limit in run 41133414552, so the Bulk Data scenario never ran.

- **DaVinci Interop prints the validator's log in the job log.** When the
  inferno-validator sidecar answers with an error, every kit reports "Unable
  to connect to validator" without the reason. The Inferno job now prints the
  last 200 lines of the validator log the harness saved with the evidence.

- **A merge to main no longer cancels a dispatched DaVinci Interop run.** The
  workflow's concurrency group was per branch, so a push to main (which never
  runs the Inferno kits) cancelled the dispatched or nightly run that does.
  The group is now per branch and trigger.

- **DaVinci Interop runs the CARIN BB Coverage group after the EOB group.**
  The kit's Coverage group reads the Coverage ids its EOB searches saved from
  `insurance.coverage`. The scenario listed Coverage first, so it skipped
  with "No Coverage resources appear to be available" every run. The groups
  now run in the kit's own order.

- **`_lastUpdated` with a fraction of a second matches only that fraction.**
  A FHIR search date with a time part matched the whole second it named, so
  `_lastUpdated` set to one resource's `meta.lastUpdated` (which carries
  fractional seconds) also returned resources updated earlier or later in
  the same second. The CARIN BB kit's `_lastUpdated` ExplanationOfBenefit
  search failed on that. An instant now matches the precision it is written
  to: a minute, a second, or its fractional digits. This applies to every
  date search parameter the CHO-native FHIR facade translates.

- **DaVinci Interop: known kit issues stop counting against CHO.** A
  conformance scenario can name kit tests that no correct server can pass
  (`infernoKnownKitIssues` in `interop/scenarios.json`, each with a reason).
  Their failures are recorded as findings but don't fail the run. They are
  CARIN BB's five EOB subtype validation tests (inpatient, outpatient, oral,
  pharmacy and professional):
  the pinned kit validates every EOB against each subtype profile, so a
  member with mixed claim types fails them all. See "Known kit issues" in
  `docs/interop/davinci.md`.
- **Public directory `_id` search accepts a reference.** `_id=Practitioner/123`
  (relative, absolute or versioned) now finds Practitioner 123, and a
  reference to another type finds nothing. The Inferno Plan-Net kit sends
  the reference it read off a resource as the `_id` of its `_revinclude`
  tests, so all ten of them failed with "no resources appear to be
  available".
- **The SMART token response names the patient it is bound to.** A
  patient launch's token response now carries the `patient` (and
  `encounter`) launch context SMART App Launch requires; it was only in the
  access token's claims. The DaVinci Interop CARIN BB suite now runs as the
  seeded member's own patient app, through enrolment, the Development
  login, PKCE and consent, because Patient Access answers searches without
  a `patient` parameter only for a token bound to one member.
- **ExplanationOfBenefit search applies the CARIN BB search parameters.**
  `type`, `identifier`, `service-date`, `service-start-date`,
  `billable-period-start`, `_lastUpdated`, `created` and `status` used to be
  forwarded to claims-service and ignored, so a filtered search returned
  every EOB the patient had. fhir-service now parses them into structured
  filters that claims-service applies in the store, answers a search
  nothing CHO holds could match (a pharmacy or vision type) with an empty
  bundle, and refuses a parameter it cannot apply with a 400 instead of
  ignoring it. CARIN BB's `_include` values are accepted and advertised.
  They add nothing to the bundle: the referenced resources are read from
  the same base.

- **CARIN BB conformance: EOB profile version, payee, and Coverage
  `_include`.** A Patient Access ExplanationOfBenefit now declares its CARIN
  BB profile twice in `meta.profile`: as before, and with `|2.0.0`, because
  CARIN BB 2.0.0 makes a versioned canonical an error-level requirement and
  the Inferno kit checks 2.0.0. It also carries a `payee`: the billing
  provider, or the pay-to plan on a subrogation demand. A Coverage search
  accepts `_include=Coverage:payor` (advertised in the CapabilityStatement);
  CHO's payor is a display-only reference, so nothing is added, and a search
  whose payor is a resource reference is refused rather than answered
  without it. The interop seed now gives every claim its Coverage id, so the
  kit has Coverage resources to test, and an attending physician on the
  inpatient claim, so its EOB has a care team.

- **CARIN BB conformance: CapabilityStatement and line codes.** fhir-service's
  CapabilityStatement now instantiates CARIN BB's server CapabilityStatement
  (`.../carin-bb/CapabilityStatement/c4bb`), which the CARIN BB kit checks
  for. A CARIN ExplanationOfBenefit line whose procedure CHO holds only under
  its local system, such as an inpatient room-and-board line with a revenue
  code and no procedure, now also carries `data-absent-reason#not-applicable`
  in `productOrService`. CARIN BB's required value sets hold that code for
  exactly this case, and the local coding stays beside it.

- **The interop seed has a second member for Bulk Data.** The Bulk Data kit
  fails an export with fewer than two Patients, and the seed had one.
  `additionalMembers` in `interop/cho-seed.json` adds a member with coverage
  and a claim from the same billing provider, so both members are in the
  panel the kit exports. US Core and CARIN BB still judge the first member.
  The directory's network now has an address (Plan-Net's Organization tests
  validate it as a plannet-Organization, which requires one), and the plan
  document's Endpoint uses a `.invalid` host instead of `example.org`, which
  the validator refuses.

- **US Core searches the Inferno kit makes no longer answer 400.** Patient
  and Encounter searches accept `_revinclude=Provenance:target`, which US Core
  requires. CHO keeps no Provenance, so the result is the same as the search
  without it. Any other `_revinclude` is still refused. Encounter adds
  `identifier` (the submitter's control number) and `class`. encounter-service
  matches `class` through the place of service it is derived from, and a code
  the projection never emits matches nothing. The CapabilityStatement declares
  all of these.

- **FHIR codings now pass terminology validation.** ICD-10-CM codes are
  dotted when projected to FHIR (`J189` becomes `J18.9`) on ExplanationOfBenefit,
  Claim, Encounter, the prior-authorization EOB and CarePlan; X12 keeps the
  undotted form. Patient and Practitioner languages carry the language's name
  as the display (`English`, not `en`), or no display for a tag CHO doesn't
  know. Practitioner and PractitionerRole NUCC codings use NUCC's display
  (`Internal Medicine Physician`) and keep the plan's specialty label in text.
  The Patient's member id is typed `MB` (CARIN BB's `identifier:memberid`
  slice) and `meta.profile` adds `C4BB-Patient|2.0.0`. Inferno's US Core,
  CARIN BB, Plan-Net and Bulk Data kits all reported these.

- **Inferno findings now say why.** A failing test's finding carries the
  error messages it logged (the validator's issues behind a profile failure)
  and, for a request CHO refused, CHO's response body, so a CHO gap can be
  fixed from the run rather than reproduced by hand first.

- **The interop stack now runs authorization-service and seeds an
  Encounter.** Bulk Data `$export` jobs read prior-authorization
  ExplanationOfBenefit from authorization-service, which the interop profile
  never started, so every export ended in error and the kit's status polls got
  500. The US Core kit found no Encounter to judge because the seed wrote
  none; it now writes one through encounter-service and checks fhir-service
  serves it. A scenario that does not pass now keeps the containers' logs in
  its evidence.

- **fhir-service answers FHIR POST search.** `POST [type]/_search` with a
  form-encoded body (FHIR R4 http.html#search, required by US Core 3.1.1)
  matched no endpoint and answered 403, so Inferno's US Core kit failed
  Patient `_id` search. It is now served as the equivalent GET search,
  rewritten before routing and authentication, so scopes, patient binding and
  `[FhirAccess]` apply to it exactly as to the GET. Parameters in the URL and
  the body both apply.

- **Bulk Data export groups now run with the token the kit obtained.** The
  SMART Backend Services group stores the access token CHO issued it in the
  session's `smart_auth_info`, but the harness sent its original
  `smart_auth_info` (client id and token URL, no token) with each later group,
  and Inferno saved that over the token. Every Group and Patient kick-off went
  out unauthenticated and got 401 (run 40956429968). Later groups now take
  their `auth_info` inputs from the session, as Inferno's UI pre-fills them.
  The US Core token also gains `encounters:read`; without it every Encounter
  search answered 403 to the harness's own token.

- **The nightly Inferno run no longer reports harness faults as CHO failures.**
  Three things in run 40937514337 were the harness, not CHO. Every CARIN BB
  validation errored with "Unable to connect to validator": the C4BB suites
  name their IGs by file, and the validator never had the files, so it
  answered 500. A one-shot service now copies the kit's IG packages into the
  validator before the kit starts. The US Core and Plan-Net TLS tests failed
  on the plain-HTTP interop network; every kit now runs with the kits' own
  `INFERNO_DISABLE_TLS_TEST` switch, as Bulk Data already did, so they report
  omit. Bulk Data never ran because the seed reached fhir-service before it
  listened (ECONNRESET); the seed now waits for every CHO service it calls to
  answer `/health/live`.

- **Inferno answered 500 to every session the harness opened.** The harness
  sent its JSON with `Transfer-Encoding: chunked`; Puma buffers a chunked body
  to a Tempfile, and Inferno's API calls `request.body.string` on it ("undefined
  method `string' for an instance of Tempfile"). Requests to a kit now carry a
  Content-Length.

- **GKE demo SMART hosts now work after a deploy, and pass hosted Inferno.**
  smart-auth-service on GKE answered every discovery and token request with
  500 ("The X509 certificate could not be added to the store") because its
  container user had no writable `HOME` for OpenIddict's development
  certificates; it now gets `HOME=/tmp`. fhir-service and smart-auth-service
  restart when the conformance settings change, even with an unchanged image
  tag. With both fixed, the hosted Inferno Bulk Data kit's SMART Backend
  Services group passed against the demo on 2026-10-29 (G-60). The runbook
  adds Cloudflare bot protection, which challenged the kit.

- **Inferno kits never booted in the nightly run.** Every Inferno kit app and
  worker died on its first query with `no such table: headers`, because
  nothing ran `inferno migrate` on the kit's fresh SQLite volume (upstream's
  `setup.sh` does it once by hand). The `inferno` job is allowed to fail, so
  the nightly runs from 2026-10-24 to 2026-10-28 showed green with no US Core,
  CARIN BB, Plan-Net or Bulk Data result. Each kit profile now has a one-shot
  `<kit>-migrate` service that the app and worker wait for.
- **Interop seed could not create Coverage.** coverage-service has written
  coverage inside a Mongo transaction since the PCP roster fence, and the
  interop profile's standalone `mongo:7` cannot run one, so every seed stopped
  at `503 PCP_PANEL_UNAVAILABLE` before a suite ran. `interop-mongo` is now a
  single-node replica set, initiated by its healthcheck.
- **Interop seed rejected the hospital.** The seeded organization's
  `primarySpecialty` ("General Acute Care Hospital", 27 characters) exceeded
  provider-service's 20-character limit, so the seed stopped before the
  directory was complete. It is now "Hospital"; the Plan-Net specialty display
  still comes from taxonomy `282N00000X`.
- **Inferno runs refused after seeding.** With seeding fixed, three more
  problems stopped the kits before a single test ran. smart-auth-service in
  the interop profile could not save its development signing certificate (its
  container user has no home directory), so every token request answered 500;
  it now gets `HOME=/tmp`. The harness wrote inputs with a PUT that Inferno's
  API does not have (405); inputs now travel only with the run request, as
  Inferno expects. And Inferno's API, unlike its UI, does not pre-fill a
  required input's default, so Plan-Net refused to start without
  `no_param_search`, `max_instances` and `max_pages`; the harness now sends the
  kit's own defaults for required inputs a scenario leaves unset and records
  which. A kit's refusal now carries its response body into the failure
  message.

- **smart-auth-service answers over plain HTTP when its issuer is HTTP.** An
  http issuer is only accepted in Development (the interop network), but
  OpenIddict still refused every request there for not being HTTPS (ID2083),
  so no token could be issued. The HTTPS requirement is now dropped for an
  http issuer only; an https issuer keeps it.

- **Fixes from the first Inferno Bulk Data run** (bulk-data-test-kit
  `v0.13.2`, Bulk Data 2.0.0 suite, run by hand against a local CHO; G-60).
  The SMART Backend Services and server groups now pass, and Group and
  Patient kick-offs are accepted. Five defects were found and fixed:
  - smart-auth-service's token response carries `scope`. SMART requires it
    even when it equals the request; OpenIddict left it out.
  - In `Demo` trust, fhir-service trusts the bundled smart-auth-service's
    issuer as smart-auth-service writes it (`Uri.AbsoluteUri`). With the
    same `SmartAuth:Issuer` on both services, a host-only issuer such as
    `https://auth.example.com` arrived as `https://auth.example.com/` and
    every token was refused. The match is still exact, and explicit
    `TrustedIssuers` are taken as written.
  - SMART discovery no longer advertises `//connect/token` when the issuer
    ends in a slash. A Backend Services client signs its assertion for the
    advertised URL, which then failed the `aud` check.
  - The CapabilityStatement declares `export` on `Patient` and `Group`
    (`patient-export`, `group-export`) when `BulkExport:Enabled` is set. It no
    longer claims the system-level `$export`, which answers 501.
  - A cancelled export's status URL answers 404 with an OperationOutcome, as
    Bulk Data 2.0 requires (it was 410). A failed export's status answers 500
    with an OperationOutcome instead of a plain JSON error.

- **Bulk exports read CHO's own services in the export's tenant.** The bulk
  worker had no request to take a tenant from, so with the `ChoNative` binding
  its reads of member, coverage and claims data carried no tenant and no
  service token. Each job now runs in a context that names only its tenant.

## [6.0.0] - 2026-10-28

"Paths of Glory."

A major version: the Cosmos DB SDK persistence path is gone and a public
endpoint was removed. Covers the 340 pull requests merged between v5.0.0 and
this release (#1222 to #1580). Release notes, with upgrade steps for 5.x:
`docs/releases/RELEASE_NOTES_v6.0.0.md`.

### Breaking changes (summary)

Each is itemized under Removed or Changed below; the upgrade steps are in the
release notes.

- **MongoDB is the only document store (ADR 017, #1398).** Services refuse to
  start with `Database:Provider` set to anything but `MongoDb`, with
  `CosmosDb:ConnectionString` or `CosmosDb:Endpoint` and no
  `MongoDb:ConnectionString`, or with no `MongoDb:ConnectionString` at all, in
  every environment. There is no migration tool from Cosmos DB SQL API data.
- **Azure DocumentDB connection strings must include `retrywrites=false`.**
- **benefit-plan-service `POST /api/v1/adjudication/adjudicate` returns 404**
  (#1399). Use `POST /api/v1/adjudication/estimate`.
- **The claims Cosmos partition-key migration endpoints are gone**
  (`/api/v1/admin/claims/cosmos-migration/*`), as is `src/tools/migration-wizard`.
- **`BatchEligibility:CosmosDb` is now `BatchEligibility:MongoDb`.**
- **`Portal:Search:RequireCriteria` is gone**; record search always needs
  criteria (#1463).
- **Redis accumulators store integer cents (#1393).** benefit-plan-service must
  be scaled to zero before the new version starts
  (`docs/operations/ACCUMULATOR-CENTS-CUTOVER.md`).
- **PCP assignment writes use multi-document transactions (#1547, #1549)**, so
  MongoDB must run as a replica set (or a managed service that supports
  transactions) (`docs/operations/PCP-ATOMIC-CAPACITY.md`).
- **Error bodies from 13 services are RFC 7807 problem documents** with a
  `traceId`, not `{ error | message }` objects.
- **Behaviour changes on the claim path that change what is paid:** money
  rounds half away from zero; deductibles apply on non-HDHP plans; claims past
  the timely filing limit deny CO-29 by default (#1448); eligibility fails
  closed on unknown or unreachable members (#1445); unmapped procedure codes
  deny CARC 204 instead of 18.

### Not yet done for CMS-0057-F (1 January 2027)

This release does not complete the January 2027 API requirements. Still to
come, planned for 6.1 and 6.2:

- **SMART Backend Services client authentication** (`private_key_jwt` / JWKS,
  G-42). Outside providers' bulk clients and payer-to-payer callers cannot
  authenticate with it yet.
- **Plan-Net 1.2.0 conformance for the public Provider Directory** (G-36). The
  directory is public, but it has not been conformance-checked and serves no
  Location, HealthcareService, Endpoint or OrganizationAffiliation.
- **The rest of bulk `$export` (G-34).** The Provider Access Group export ships
  **off** behind `BulkExport:Enabled`. Still missing: a claims-history panel
  listing (a plan relying on claims attribution alone exports nobody),
  Payer-to-Payer history over bulk, `Patient/$export`, and a Bulk Data
  conformance run. System-level `$export` answers 501.
- **No X12 278 bridge.** CHO relies on HHS enforcement discretion for FHIR-only
  prior authorization.

The plan-side items (the `PA-DECISION-EXTENSION` letter template, listing a
plan in `PublicProviderDirectory:Directories`, and counsel's reading of the
"more than once" usage figure) are the plan's to supply. See
`docs/epa-gap-matrix.md` for the status of every requirement.

### Also in this release (not itemized below)

- **Claims money path.** Plan-paid not billed (#1240); deductibles, family
  limits and ACA preventive (#1237, #1245); COB to tertiary payers (#1266,
  #1278, #1279); 835 compliance and balancing (#1249, #1254, #1258, #1263,
  #1275, #1381); golden 837 to 835 test (#1260).
- **One adjudicator.** Side doors removed, outages pend instead of deny, every
  denial carries a CARC, Pay/Deny kept workable until the payment cycle
  (#1335 to #1339, #1344); edit-level dispositions and override policy on
  examiner approval (#1360 to #1362).
- **Claim intake.** Exact and suspect duplicate detection (#1236), WEDI SNIP 1
  to 5 with 999 (#1272, #1281), TA1 (#1287), 837I institutional model (#1252),
  837 replacement and void linking by REF*F8 (#1446), timely filing (#1448).
- **Pricing.** One pricing engine for adjudication and the Pricing API, and one
  contract model with backfill, dual read and a per-tenant cutover flag (ADR
  016, #1268, #1479); NCCI PTP and MUE (#1267, #1363); MPFS multiple procedure
  reduction (#1234, #1248).
- **Inpatient.** A native MS-DRG grouper at parity with CMS's on every corpus
  row (ADR 024, #1478, #1492, #1498), IPPS payment with the hospital-level
  adjustments, hospital-specific portion and pass-throughs, checked against
  CMS's own pricer (#1499, #1502, #1503, #1505, #1508, #1510). Grouping in
  adjudication is off unless `pricing.inpatient.groupDrg` is set (#1517).
- **CMS-0057-F and FHIR.** ePA waves for tenant isolation, SLA watchdog,
  persisted PAS decisions and RFAI clocks (#1294, #1297, #1300, #1406 to #1409);
  Patient, Coverage, Encounter, Claim and prior authorizations served from
  CHO's own services (#1420, #1476, #1480); Payer-to-Payer from CHO services
  (ADR 020, #1429); Provider Access opt-out model (ADR 021, #1431) and
  attribution from real treatment (#1504); CARIN Blue Button EOB (#1526);
  Inferno kits run against CHO in CI and CRD decides from the member's record
  (#1509, #1511, #1522); PA reporting evidence and annual drafts (#1542,
  #1550).
- **C-CDA reader** for problems, allergies and medications, and the clinical
  summary a UM reviewer reads (#1532, #1533, #1539).
- **Plan configuration.** Cascading settings with overrides, carriers and
  programs, sponsor policies, contract terms and per-sponsor billing (#1507,
  #1515, #1519, #1521, #1523, #1525, #1529, #1531, #1534, #1540).
- **Enrollment and EDI operations.** EDI file records and one intake path for
  834 and 837 at any size (#1482, #1494, #1497); 834 exceptions as work items
  and orphaned dependents (#1481, #1483, #1493, #1500); 834 parsed per the
  X220A1 loops (#1239).
- **Payments and finance** (each off by default): NACHA CCD+ files and
  exactly-once bank transmission (#1269, #1283); a double-entry GL journal
  (#1286) and ACH settlements posted to it (#1491); premium rating engine,
  rated invoices, 820 and lockbox cash, BAI2 reconciliation (#1270, #1271,
  #1288, #1289). AR legacy cash postings need the reconciliation runbook
  (#1276, `docs/operations/AR-LEGACY-POSTING-RECONCILIATION.md`).
- **Member service, UM and appeals.** contact-service and the Service screen,
  follow-ups and case intake recovery (#1506, #1552, #1553, #1559, #1563);
  utilization management with clinical criteria and concurrent review (#1477);
  appeal deadlines, extensions, external review and State rule packs (#1243,
  #1247, #1265, #1404, #1512); correspondence step 1 (#1470).
- **Documents.** GCS and S3 document store providers, the document catalog,
  attachments and member documents in it, 275 matching by TRN02, scanning
  and retry (#1436, #1439, #1440, #1442, #1443, #1554, #1558).
- **Work management** with routing, queues and Get Next (#1345, #1353, #1358).
- **Portal.** Record pages for claims, members, providers, authorizations,
  appeals and payments, global search and a regrouped nav (#1368 to #1377);
  Insights for claims, authorizations and appeals (#1386 to #1390); calls to
  backend routes that do not exist fixed or hidden (#1473, #1474).
- **Security and audit.** An access log on every read of claim, member,
  provider, authorization, payment and appeal records (#1365, #1378, #1379,
  #1385, #1389); tenant scoping enforced on every MongoDB query by an
  architecture test (#1452); Cloud KMS token signing and Kubernetes service
  identity (#1458); evidence-first security challenge campaigns (#1426, #1430,
  #1434, #1435, #1438, #1441); real OIG LEIE and SAM.gov exclusion screening
  (#1242, #1564); the `@azure/msal-node` advisory fixed by an override
  (#1567).
- **Google Cloud.** GKE demo environment deployed on every merge to main, with
  Cloudflare Access sign-in (#1305 to #1308, #1317, #1346, #1459); production
  reference architecture (#1456); managed FHIR store publishing to BigQuery,
  off by default (#1469).
- **Smaller fixes worth knowing.** Claims are priced in the async pipeline
  instead of being allowed at billed charges (#1235); date-of-service
  eligibility is decided by coverage span (#1238); accumulators commit on final
  adjudication with lock-safe approvals (#1285); appeals publish through a
  transactional outbox (#1277); a failed PA lookup pends the claim (#1299);
  authorization checks are bound to the individual member (#1419); PAS binds
  its caller to the token (#1530) and Payer-to-Payer consent to the requesting
  payer (ADR 022, #1433); denied authorizations are left out of P2P exports
  (#1485); DTR state is durable across pods (#1414); Medicaid is excluded from
  the Texas gold-card rule (#1546).
- **Also new.** The CMS-0057-F compliance console with decision clocks and
  public metrics (#1352); the extension model and plan edit service (ADR 019,
  #1424); append-only claim notes (#1355); a member coverage view, with
  Eligibility as a 270/271 simulator (#1341); appeals encryption through GCP
  Secret Manager (#1562); a demo persona seeder (#1466); QuestPDF replaced by
  SkiaSharp for licensing (#1465).
- **CI.** Security and coverage gates are blocking, and CI no longer commits to
  main (#1449); dependency PRs are no longer auto-merged on failing checks
  (#1416).
- **Remediation series** (#1391 to #1399): time-zone independent baseline,
  security fixes, integer-cent accumulators, the claim event outbox, resilient
  service calls, MongoDB write concurrency and the Cosmos removal.
- **Docs and site.** Medicaid readiness assessments for California, Texas,
  Oregon and Illinois (#1541, #1543 to #1545, #1548), which conclude readiness
  is not yet verified; unsupported compliance claims removed (#1460, #1461);
  marketing repositioning and new website (#1411, #1450); Azure resource
  names, subscription and tenant ids scrubbed (#1571, #1573).

### Added

- **CMS-0057-F Provider Access: bulk `$export`.** Off by default
  (`BulkExport:Enabled`); until then kick-off answers 501 as before. When on,
  a provider exports its own attributed panel as
  `POST fhir/r4/Group/{its provider id}/$export` (`_type`, `_since`), and every
  member is authorized again (attribution, then opt-out) before anything is
  written. A background worker writes NDJSON per resource type to a dedicated
  bucket (`BulkExport:DocumentStore:*`, never the documents bucket), and the
  caller who started the export downloads the files through
  `GET fhir/r4/$export-file/{job}/{file}` with its own token for 24 hours.
  Files are deleted after two days, or at once on `DELETE $export-status`.
  System-level `$export` still answers 501. GCP: `enable_bulk_export` in
  `infrastructure/gcp` and the `BULK_EXPORT_BUCKET` /
  `FHIR_SERVICE_GOOGLE_ACCOUNT` deploy variables.

- **CMS-0057-F Patient Access API usage figures.** fhir-service records each
  day a member's app successfully reads that member's data through the Patient
  Access API (tenant, patient id, OAuth client id and UTC day, no resource
  content) in a new `api_disclosures` collection. `GET
  api/v1/interoperability/usage?year=2026` (`compliance:read`) returns the two
  figures a plan files each year: unique patients whose data went to an app,
  and those whose data went more than once (more than one app-day). The report
  says whether the year is complete and from which day the ledger covers it,
  since nothing before this release was recorded. Plans still submit the
  figures themselves (HPMS, their state, or MPMS).

- **Public Provider Directory (CMS-9115-F).** fhir-service serves a plan's
  Practitioner, PractitionerRole, Organization and InsurancePlan resources at
  `/fhir/r4/directory/{directory}/` with no authentication, as CMS requires.
  A plan publishes its directory by being listed in
  `PublicProviderDirectory:Directories` (directory id to tenant); nothing is
  published by default. Any credential on the request is ignored, the plan's
  data is read as fhir-service for that tenant only, and responses drop
  CHO-internal extensions (such as the provider integrity score), tax ids and
  inactive providers. Requests are rate limited per client address
  (`PublicProviderDirectory:PermitPerMinute`, default 300). The authenticated
  directory routes are unchanged.

- **CMS-0057-F prior authorization: extension screen and member extension
  notice.** The authorization record's Review tab shows the decision deadline
  in force and, for an open Medicare Advantage or Medicaid request, lets a
  reviewer with `authorizations:decide` take the one lawful 14-day extension
  with a ground and the reason given to the member. The reviewer then sends
  the member's written extension notice (letter type `PA-DECISION-EXTENSION`,
  addressed from the member record) through correspondence-service. The
  letter is recorded on the extension
  (`POST api/authorizations/{id}/decision-extension/member-notice`). Extended
  requests with no recorded notice are counted in the CMS-0057-F metrics
  (`extensionNoticesOwed`). See docs/compliance/CMS-0057-F-EXTENSION-NOTICE.md.

- **CMS-0057-F prior authorization: list of items and services that require
  prior authorization.** benefit-plan-service builds the list for a state,
  line of business and program from the same rule set PA requests are
  evaluated against (`GET api/v1/prior-auth-rules/required-services`, JSON,
  or `format=csv` to post as a file). Each entry names its codes or code
  ranges and when PA applies (always or at listed places of service, with
  listed diagnoses, or above a quantity limit). Drug (J) codes are left out
  and reported separately, as are exemptions and any rule type the list
  cannot describe, so a reviewer sees everything that shapes it before
  publishing.

- **CMS-0057-F prior authorization: appeal outcomes reach the reporting
  ledger.** Recording a decision on a member's pre-service authorization
  appeal (`POST api/service-cases/{id}/decision`) now requires a structured
  outcome (`Upheld`, `Overturned`, `PartiallyOverturned`; refused for a
  grievance). appeals-service writes it to the authorization's reporting
  ledger as an `AppealDecision` fact (Denied, Approved or Modified), as the
  service for the case's tenant, keyed by the case so a retry is never
  counted twice. A fact that cannot be delivered right away is retried by a
  background sweep with backoff; one the ledger refuses is flagged on the
  case. The portal case screen asks for the outcome and shows whether it is
  on the reporting record, so "approved after appeal" counts these appeals.

- **CMS-0057-F prior authorization: lawful extensions, every denial reason,
  and the extension and appeal metrics.** A reviewer can take the one lawful
  extension of an open Medicare Advantage or Medicaid request's decision
  timeframe (`POST api/authorizations/{id}/decision-extension`, up to 14
  calendar days, before the deadline, with a stated ground and reason). The
  SLA watchdog, the console and "decided within timeframe" use the extended
  deadline, and the extension is written to the reporting ledger so the
  annual draft can report "extended, then approved". Denial-reason insights
  and the recent-decision list now carry every denied or reduced line's
  reason, not only the first, and line reason codes accept the same 50
  characters as the request-level code. The operational CMS-0057-F metrics
  add extended-then-approved and approved-after-appeal from ledger evidence.

- **Plan configuration: settings that cascade, with overrides (step 1).**
  tenant-service gains a configuration module: a setting catalog defined in
  code, values by level (tenant, line of business, sponsor) with effective
  dates, locks, legal floors, version history, a change log, two-person
  approval, and a resolve API that explains where every value came from.
  Services read settings through a cached client that falls back to the
  defaults if tenant-service is unreachable. New permissions `config:*` and
  roles AccountManager, Underwriter, ConfigurationAnalyst and
  ConfigurationManager. The orphan-dependent grace period moves into the
  catalog; enrollment-import's interim settings store and
  `enrollment:configure` are retired. See
  `docs/architecture/plan-configuration.md` §12.1.

- **Orphaned dependents wait for their subscriber, then apply themselves.**
  A dependent rejected because its subscriber is not on file is held for a
  grace period, resolved through new enrollment settings by line of business,
  benefit plan and sponsor (defaults: commercial 7 days, Marketplace and
  Medicaid 0). When the subscriber arrives in any later import, the dependent is
  reprocessed automatically. When the grace period ends, a sweep retries it once
  more and otherwise opens it to the Matching queue with its history. See
  `docs/architecture/enrollment-operations.md` §9.3.

- **Raw 834s of any size, on one intake path.** Every raw 834 — uploaded or
  from SFTP, small or a 1 GB state audit file — is streamed to the raw-file
  store (the configured document store, or GridFS), its envelope checked
  from a stream (TA1, duplicate ISA13), and imported by a background run of
  units (a subscriber with its dependents, or a standalone dependent). The
  upload answers 200 with the usual result when the run finishes within a
  few seconds, otherwise 202 with `GET api/v1/enrollment/import/runs/{id}`.
  Runs resume after a restart without applying a unit twice, and finish
  exactly once. Uploads up to 1 GiB; the file is never held in memory. See
  `docs/architecture/edi-operations.md` §11.

- **834 enrollment exceptions are worked as work items.** Every rejected 834
  row opens an `EnrollmentException` holding the change as received, its reason
  and errors, and its file. Enrollment specialists can reprocess it (optionally
  corrected), match a dependent to its subscriber, hold it, send it back to the
  sponsor or close it with no action, each recorded with who and why.
  work-management-service reconciles workable exceptions into `Enrollment834`
  items routed to Retry, Matching, Retro, Data and Other queues by reason. See
  `docs/architecture/enrollment-operations.md` §9.2.

- **Finalized claims publish to the plan's managed FHIR store, streaming to
  BigQuery and Vertex AI (ADR 023).** With `ManagedFhirStore:Provider` set to
  `GcpHealthcare`, fhir-service consumes `claims.finalized.v1` and writes each
  claim's ExplanationOfBenefit (the claims-service projection, at its version
  chain id) into a Google Cloud Healthcare API FHIR store. The store streams
  every write to BigQuery as SQL-on-FHIR tables, and BigQuery ML can call
  Vertex AI models over them. CHO stays the system of record; publishing is
  off by default, idempotent, limited to one tenant per store, and fails
  closed when a named provider is incompletely configured.
  `infrastructure/gcp/fhir-store.tf` provisions the store, BigQuery streaming,
  Pub/Sub notifications, an export bucket and the Vertex AI connection behind
  `enable_managed_fhir_store` / `enable_vertex_ai`, and the GKE deploy wires
  fhir-service to it through Workload Identity. `claims.finalized.v1` gains an
  additive `ClaimVersionId`. See
  `docs/architecture/managed-fhir-store-analytics.md`.

- **Demo persona seeder (`tools/DemoPersonaSeeder`).** Seeds a demo tenant
  with named synthetic personas through the services' own APIs, and writes a
  demo script with the IDs to type. The cast:
  - the Delgado family on an employer PPO: PT visits, a COB pend, and a
    denied MRI with its appeal;
  - a Medicaid member in concurrent inpatient review;
  - a Medicare Advantage member with an expedited auth due;
  - an orthopedic group with approved claims;
  - an employer group's premium invoice;
  - two members with similar names.

  Demos look records up the way real users do, now that record search always
  needs criteria. The seeder refuses to run unless the tenant ID is repeated
  with `--confirm-synthetic`, and re-runs find what earlier runs created.

### Changed

- **Record search in the portal always needs criteria, in every tenant.**
  Claims, Members, Providers and Authorizations no longer list records when
  the page opens in a demo tenant, and the `Portal:Search:RequireCriteria`
  setting is gone (its `NonDemoTenants` default and `Never` mode let a tenant
  marked as demo browse freely). A privacy control with an off switch is one
  wrong flag away from exposing real members, so there is no switch.
  Drill-downs from Insights and batch-run links still open their lists,
  because they arrive with their own filters. Demos will use work-list roles
  and curated personas instead (`docs/architecture/portal-search.md` §3.5).

- **The portal now matches the marketing site's Enterprise look by default,
  with Sentinel kept as a dark option and per-tenant branding.** Every portal
  color now comes from tokens in `wwwroot/css/theme-tokens.css`. The new
  default, Enterprise, uses the website palette (white and slate surfaces,
  navy app bar, teal accent, IBM Plex Sans), and its text colors meet WCAG AA.
  Sentinel, the original obsidian and neon cyan look, renders as before; users
  switch between the two in the account menu, and the choice is kept in the
  `cho-theme` cookie. `Portal:DefaultTheme` sets a deployment's default.
  About 1,200 hardcoded colors across 57 pages now resolve through tokens;
  translucent ones go through `legacy-color-bridge.css`, which gives Sentinel
  the original value and Enterprise a value suited to how the color is used.
  A tenant can set its own app bar name, logo and accent color under
  `TenantBranding:{tenantId}`. Accents below AA contrast on white are ignored,
  and status colors (paid, pended, denied) cannot be overridden. A test now
  fails if a page hardcodes a Sentinel color again.

- **Money is rounded to the cent half away from zero everywhere
  (`MoneyRounding.ToCents`).** Several calculations used `Math.Round(x, 2)`,
  whose default is banker's rounding (half to even). They now round a half
  cent away from zero. A result changes only when the unrounded amount is
  exactly half a cent and the cent below it is even, and then by one cent
  (up for a positive amount): $10.125 was $10.12 and is now $10.13; $10.135
  was and stays $10.14. The calculations that change:
  - **Benefit engine coinsurance** (`BenefitCalculationEngine`), in all three
    copay modes: remaining allowed × coinsurance %. 15% of $100.30 = $15.045:
    was $15.04, now $15.05, so plan paid falls by one cent.
  - **Fee schedule allowed amounts** (`RateResolutionService`):
    - the final line amount (rate × units after modifier adjustments);
    - a DRG case rate (base rate × weight): $6,543.23 × 1.5 = $9,814.845, was
      $9,814.84, now $9,814.85;
    - a percent-of-Medicare line (Medicare rate × multiplier);
    - an RVU amount (total RVU × conversion factor): 1.00 × 10.125 was $10.12,
      now $10.13;
    - Medicaid amounts (inline RVU, percent of the Medicare MPFS rate, and the
      stored-rate fallback): 70% of $50.35 = $35.245, was $35.24, now $35.25;
    - the multiple-procedure reduction (50% of a rank-2+ line): 50% of
      $100.01, was $50.00, now $50.01;
    - billed charges used for lesser-of and per-stay allocation (only a
      billed amount with sub-cent precision can change).
  - **Seeded Pricing API rates** (`FeeScheduleLoaderService`): RBRVS
    facility and non-facility rates (total RVU × conversion factor) and DRG
    rates (weight × base rate). (1.00 + 0.25) × 10.10 = $12.625, was $12.62,
    now $12.63. Only rates seeded after this change are affected.
  - **Capitation** (`CapitationRunService`): risk-adjusted PMPM (base PMPM ×
    risk score), gross amount (× proration) and withhold (× withhold %).
    $50.15 × 1.5 = $75.225, was $75.22, now $75.23; a 10% withhold of $100.25
    was $10.02, now $10.03.
  - **MPIP-enhanced allowed amount** (`MpipAdjudicationEnhancer`): allowed ×
    MPIP multiplier. $15.00 × 1.063 = $15.945, was $15.94, now $15.95.
    claims-service registers the enhancer but nothing calls it today, so no
    paid amount changes through it yet.
  - **Legacy premium billing lines** (`PremiumBillingService`): subscriber
    premium, employer contribution and total. $100.125 was $100.12, now
    $100.13; $50.005 was $50.00, now $50.01.
  - **The Million Claim Challenge answer key** (`ExpectedMoney` in the
    benchmark generator) uses the same rule for expected allowed, coinsurance,
    paid and member liability.

  Sites that already rounded half away from zero now call the helper with no
  change: accumulator cents, examiner edit dispositions, the EOB unit price,
  the 820 and lockbox parsers, `PremiumRatingEngine.RoundMoney`, and the
  whole-cent checks in ar-service and `RatedInvoiceGenerator`. Amounts already
  stored are not recalculated. The figures that change for the MCC benchmark,
  and the sign-off this needs, are in
  `docs/architecture/money-rounding-impact.md`.

- **Every .NET project builds with `TreatWarningsAsErrors`** (root
  `Directory.Build.props`, imported by `src/Directory.Build.props`). No
  warning codes are exempted.

- **The portal's System.Security.Cryptography.Xml override is 9.0.21**, the
  first 9.0.x release outside GHSA-7hqp-7c5m-4vq6, GHSA-c399-9853-6jmj and
  GHSA-r955-8q5j-3h83.

- **Services refuse to start against a missing or Cosmos database.**
  - `Database:Provider` set to anything but `MongoDb` fails at start-up.
  - `CosmosDb:ConnectionString` or `CosmosDb:Endpoint` without
    `MongoDb:ConnectionString` fails at start-up.
  - Every service now requires `MongoDb:ConnectionString` in every
    environment, Development and Testing included. Before this, some services
    fell back to Cosmos, started with no database, or ran on in-memory stores:
    idcard, reference-data, fhir, authorization, rfai, consent,
    personal-representative and attachment. Their in-memory stores remain
    only as test doubles.

- **eligibility-service batch storage is configured under
  `BatchEligibility:MongoDb`** instead of `BatchEligibility:CosmosDb`.

- **Some `CosmosDb:*` keys are still read**, as Mongo collection-name
  overrides; they are listed in ADR 017.

- **The Azure DocumentDB connection string must include `retrywrites=false`.**

- **Redis accumulators store integer cents.** `RedisAccumulatorService`
  wrote amounts with `HINCRBYFLOAT` and read them back as
  `double` → `decimal`. 10,000 × $0.10 did not sum to $1,000.00, and a
  deductible filled exactly to its limit could read as not met. Every hash
  value is now an integer number of hundredths (`HINCRBY`, integer Lua
  arithmetic, cents in the per-claim journal and clamp results). An amount
  with sub-cent precision is rejected, not rounded.

- **Hashes written before integer cents are never read as cents.** New
  hashes carry `__v=2`. A hash without it is a cache miss: its amounts and
  journals are dropped, its fences are kept, and it is rebuilt from
  claims-service. **Deploying needs benefit-plan-service scaled to zero
  first**, because old and new pods misread each other's values. See
  `docs/operations/ACCUMULATOR-CENTS-CUTOVER.md`.

- **Accumulator audit rows are written before the operation returns.**
  These are the audit writes on commit, increment and reversal. They were
  fire-and-forget `Task.Run`s, so a write could be lost on an error or at
  shutdown, and they used a scoped writer after its request scope had
  ended. A failed audit write now fails the operation with
  `AccumulatorAuditException`. The caller retries it (commits come from
  the claims-service accumulator outbox), and the retry writes the row. For
  increments, the audit row is written first.

- **CI runs the .NET unit tests a second time with `TZ=America/Phoenix`**
  (`test-dotnet.yml` matrix). The UTC leg keeps the `.NET Unit Tests` check
  name, artifact and coverage.

- **Benefit engine: deductibles now apply on non-HDHP plans.** The benefit-plan
  projection (`ChoBenefitPlanProvider`) emitted only copay/coinsurance rules, so
  the engine never took the deductible unless the plan was an HDHP — claims were
  overpaid by the unmet deductible. Benefits with `DeductibleApplies=true` now
  project an explicit deductible rule for both network tiers.

- **Benefit engine: unset family limits no longer zero out cost share.** A plan
  with no family deductible or family OOP max seeded zero-limit family
  accumulators that read as "met", so members owed nothing. Unset (null) limits
  now mean "no limit"; an explicit $0 deductible still means no deductible; a
  zero OOP max is treated as unset and logged. Embedded remaining amounts are
  now bounded by the closer of the individual and family limits. Aggregate plans
  with no family limit use the member's individual deductible / OOP max as the
  pool instead of dropping it.

- **ACA preventive services carry no in-network cost share** (§2713 /
  45 CFR 147.130) regardless of the `DeductibleApplies` default, and HDHP plans
  exempt them from deductible-first (IRS Notice 2004-23).

- **Coinsurance stored on the percent scale (e.g. `20`) is normalized** to the
  engine's fraction, matching how the FHIR projector reads it.

- **Benefit engine: unmapped procedure codes deny with CARC 204** ("not covered
  under the patient's current benefit plan") instead of CARC 18 ("exact
  duplicate claim/service"), on both the per-line and DRG paths. Payment
  estimates still report these as `NO_BENEFIT_MAPPING` / `needs_review`.

### Removed

- **The native Cosmos DB SDK persistence path (ADR 017).** MongoDB, through
  the MongoDB driver, is the only document store; in Azure it is Azure
  DocumentDB (MongoDB vCore). Removed from every service and engine:
  - the startup switch between Mongo and Cosmos, the Cosmos repositories,
    `CosmosSystemTextJsonSerializer` copies and Cosmos-only noop classes;
  - shared Infrastructure's Cosmos client registration, Cosmos health check
    and `ChoDatabaseProvider.CosmosDb`;
  - the Cosmos emulator tests, their fixture and the CI emulator job. Each
    behaviour they covered has a Mongo test, ported where none existed;
  - the claims 5.1b Cosmos partition-key migration, including
    `POST /api/v1/admin/claims/cosmos-migration/run` and `GET …/status`
    (they only worked in Cosmos mode), its settings, metrics and runbook;
  - the Cosmos DB for NoSQL Bicep module and its Logic App connector.
    `main.bicep` provisions Azure DocumentDB through `enableDocumentDb`
    instead. Incremental deployments do not delete an existing SQL-API
    account.

  - `src/tools/migration-wizard`, which exported TriZetto data straight into
    Cosmos SQL containers that no service read (no tenant ids).

  Nothing references `Microsoft.Azure.Cosmos` any more; the package is gone
  from `Directory.Packages.props`.

- **API removal: benefit-plan-service `POST /api/v1/adjudication/adjudicate`.**
  The deprecated synchronous dry run ran its own engine chain and its
  answers diverged from the claims-service pipeline, the only adjudicator
  (docs/architecture/adjudicate-endpoint-decision.md, Option B). Callers
  now get 404. Removed with it: the MCC validator's `--synchronous-diagnostic`
  mode (it now fails with a clear error) and the scripts' `SERVICEBUS_ONLY=false`
  path. The validator's `--pend-diagnostics` NCCI sample, which selected on
  the endpoint's `NCCI_MUE_EDIT_FAILURE` code, now selects on the pend codes
  the pipeline's `NcciEditsStage` persists (`NCCI`, `MUE`). Also removed:
  `ITerminologyCrosswalkClient`, used only by this endpoint; the
  `cho.claims.processing.duration` metric (`ChoMetrics.ClaimProcessingLatency`)
  and the Grafana `cho-adjudication` panels fed only by it. Use
  `POST /api/v1/adjudication/estimate` for what-if pricing. No longer runs
  anywhere on the claim path: Legacy-Only / Augment routing of a claim, the
  plan terminology crosswalk before pricing, and in-process prior-auth rule
  evaluation for a claim.

### Fixed

- Update MessagePack to 2.5.303 for GHSA-qhrr-8q5h-9q3h, preserving vulnerability warnings as build errors; repair NBomber load-harness compilation.

- Guard authorization lifecycle transitions, audit cancellation through the backend, and expire ended approvals with tenant/revision-fenced history updates.

- Persist receipt-based authorization decision deadlines and prevent RFAI document arrival from restarting SLA or turnaround clocks.

- Persist reviewer and automated authorization outcomes per service line, require coded explanations for denied/reduced units, and project committed decisions through PAS and claims validation.

- **Authorization numbers are unique per tenant (D-39).** MongoDB indexes
  prevent concurrent PAS inserts from creating ambiguous records. A losing
  insert returns the existing decision only for an identical request hash;
  another payload gets 409. Startup refuses legacy duplicates without changing
  them; see `docs/operations/AUTHORIZATION-NUMBER-UNIQUENESS.md`. The tenant/status
  index also closes D-37. The ePA matrix now records real metrics as PARTIAL,
  backed by an acceptance test of the production reporting calculator.

- **837 amounts parse with the invariant culture.** CLM02 and the SV1/SV2
  charge and units used the thread culture, so on a host with ',' as the
  decimal mark 180.50 parsed as 18050 (de-DE) or 0 (fr-FR).

- **Capitation contracts: New Contract opened an editor with no contract**
  and failed on render. It now starts a new contract.

- **Capitation statements "Approve All"** reported every Generated statement
  as approved even when some approvals were refused; it now names the refused
  ones. The provider dialog no longer swallows save failures.

- **fhir-service DTR and payer-to-payer stores use the host's database
  name** when `MongoDb:DatabaseName` is unset, instead of a name differing
  only in case (which MongoDB refuses).

- **No sync-over-async on request and message paths** (Kafka consume loops,
  batch eligibility index creation, `Task.WhenAll` followed by `.Result`).

- **smart-auth-service no longer builds a second service container** for
  OpenIddict's MongoDB stores; they use the registered database.

- **Concurrent writes no longer silently overwrite each other (MongoDB).**
  Repositories that read a document and replaced it whole now condition the
  replace on a `Revision` that every write increments. A write based on a
  stale read gets **409** instead of erasing a concurrent change.

  This covers:
  - claims: claim, claim adjustment;
  - payment: payment, payment run, reversal run;
  - premium-billing: billing run, EFT draft;
  - capitation: run, disbursement, statement;
  - authorization, RFAI case, coverage, member;
  - AR: cash posting, adjustment;
  - ID card, tenant user;
  - benefit plan, provider, organization (publish/activate needs a Draft, and
    two publishes of one version cannot both succeed);
  - capitation contract, provider contract, fee schedule, encounter, risk
    score, attachment, sponsor, family relationship, tenant, remittance batch;
  - the shared claim lifecycle store (transmissions, remittance and inbound
    attachment receipts).

  Eligibility batch jobs and the FHIR clinical backfill ledger now use
  targeted updates of the fields each writer owns instead of a revision.

  Some lost updates this prevents:
  - two executes of one billing or capitation run both running;
  - a stale write undoing a coverage termination;
  - a card scan undoing a revocation;
  - a login restoring a revoked role;
  - a Stripe or NACHA outcome undoing a cancel;
  - a stale 277CA or gateway outcome moving a transmission status backwards,
    or a stale outbox dispatch reverting a posted 835 so it could post twice;
  - two dispatches putting one encounter into two 837 batches;
  - a Stripe webhook and an admin edit of a tenant overwriting each other.

  Every service now answers `ConcurrencyConflictException` with 409. Stripe
  webhooks and the claim lifecycle background writers re-read and re-apply on
  a conflict. The low-risk sites still open, and the known residual gaps, are
  listed in
  `docs/architecture/mongo-write-concurrency.md`.

- **Benefit-plan plan-year boundaries no longer shift with the host time
  zone.** `PlanYearStart` and `PlanYearEnd` were stored as local times, so on
  a host east of UTC a January 1 start fell in the previous year. They now use
  the UTC calendar-date serializer, like the accumulator plan-year fields.

- **The provider-enrollment Mongo cache can hold more than one record.** Every
  upsert wrote `ObjectId.Empty` as `_id`, so the second (NPI, state) record
  failed with a duplicate-key error.

- **A claim is no longer sent twice while its first send is in flight.**
  The Stedi and Mock gateways treated a `Transmitting` transmission like a
  failed one, so a second submit of the same idempotency key (or the loser of
  the create race) sent the 837 again. A transmission now records
  `TransmittingSinceUtc`; while that is under 10 minutes old, a second submit
  is answered as a replay with `AcceptedForProcessing: false`. After that the
  sender is taken to have crashed and the claim may be resent. A retry of a
  failed send claims it with a conditional save first, so of two concurrent
  retries only one sends; the other gets the replay instead of a 409.

- **An AR adjustment can no longer be counted twice on its balance.** Posting
  and reversing saved the balance entry, then the adjustment, and the entry
  had a fresh GUID. A retry after a failed adjustment save added a second
  entry. Entries now carry a fixed id per adjustment (`adj-post:{id}`,
  `adj-reverse:{id}`), and a retry that finds it only saves the adjustment.

- **The claim lifecycle store's MongoDB indexes are created.** Its
  `EnsureIndexesAsync` declared a unique index on `_id`, which MongoDB
  rejects. That failed host start-up and left the unique tenant +
  idempotency-key index uncreated.

- **Service-to-service calls are resilient.** Every factory-built
  `HttpClient` gets these defaults:
  - a total timeout and a per-attempt timeout;
  - retries with jitter, for idempotent requests only (a POST is never
    retried without an idempotency key or an explicit opt-in);
  - a circuit breaker per client.

  The settings live in `ChoServiceClientResilience` (Infrastructure). An
  open circuit surfaces as a 503 `HttpRequestException`, so the claims
  pipeline's "pend, don't deny" behaviour applies unchanged. See
  `docs/architecture/service-client-resilience.md`.

- **Whether a claim pend is an outage is a typed flag, not reason text.**
  `ClaimAdjudicationStageResult.CheckUnavailable`, set by each stage, replaces
  substring matching of pend reasons, so rewording a message can no longer
  make an outage pend overridable by an examiner.

- **claims-examiner-service no longer drops claims.pended.v1 messages.**
  - A claims-service transport failure, or a 5xx on the write-back, is
    retried with backoff.
  - After the last attempt the message is dead-lettered to
    `claims.pended.v1.dlq`.
  - The offset only moves past a message that was processed or
    dead-lettered.
  - A crashed consume loop stops the host instead of exiting silently.
  - The Anthropic client retries 429 and 529.
  - An Anthropic outage that outlasts those retries no longer escalates the
    claim to a person for good. That covers 429, 529, an open circuit and
    a timeout. The orchestrator throws `ExaminerTransientException` and
    writes nothing, and the consumer retries the message, then
    dead-letters it. Other Anthropic failures still write `EscalateToHuman`.
  - Model dispositions outside the known set escalate to a human.

- **A client's resilience timeouts follow its own `HttpClient.Timeout`.**
  The shared pipeline had given every client a 10 s attempt and a 30 s total
  timeout. That cut Stedi (30 s), FHIR Payer-to-Payer (up to 300 s), the
  Anthropic model call (60 s) and other longer calls at 10 s. A client's own
  `HttpClient.Timeout` also still capped all of its attempts together.
  - The client's configured timeout is now its attempt timeout.
  - The total covers every attempt and its back-off.
  - `HttpClient.Timeout` is raised just past the total.
  - The Anthropic clients set their timeout at registration, not in their
    constructors.

  The inventory is in `docs/architecture/service-client-resilience.md`.

- **A failing call is retried by one layer, not two.** Clients that retry
  on their own now opt out of the shared pipeline (`WithoutChoResilience`).
  Before, a failing Stedi call made up to 9 attempts and NPPES or SAM.gov
  up to 12. The clients that opt out:
  - Stedi, which keeps `StediHttpSender`'s loop. The loop reports retry
    counts and never retries auth or validation errors. The attachment
    upload, a one-shot stream, is no longer retried at all.
  - NPPES, which keeps `AddStandardResilienceHandler`.
  - The SAM.gov exclusions API, which keeps the screener's own loop.
  - The exclusion dataset download.

- **eligibility-service uses its configured HttpClient.** A duplicate
  `AddScoped` registration had replaced the typed client and its 5 s timeout.

- **`claims.finalized.v1` is no longer lost when Kafka is down or the
  process dies.** claims-service published the event right after the
  finalize write and logged and dropped any failure, so accumulator-service
  could silently miss a Paid or Voided claim. The event is now stored on the
  claim (`PendingClaimEvents`) in the same write as the status change.
  - It is published inline and retried with backoff by
    `ClaimEventOutboxDispatcher` (`Claims:EventOutbox`) until Kafka
    acknowledges it.
  - A claim's events are published in order.
  - Delivery is at least once with a stable `eventId`. accumulator-service
    and work-management-service already deduplicate.

  See `docs/architecture/claim-event-outbox.md`.

- **The claim-event outbox refuses tenant-scoped databases.** Its dispatcher
  scans only the base claims database, so with `MongoDb:UseTenantScoping` a
  tenant's pending events were never retried. claims-service now fails at
  start-up in that configuration (Kafka configured, dispatcher enabled)
  instead of running without retries. Tenant-scoped deployments are not
  supported by the outbox yet; `Claims:EventOutbox:Enabled=false` starts
  without the dispatcher.

- **The claim-event outbox age query no longer scans every claim.**
  `OldestClaimEventAsync` filtered on `PendingClaimEvents.0` and sorted on
  `PendingClaimEvents.CreatedAtMs`, neither indexed, so every dispatcher
  poll scanned the whole Claims collection. A sparse multikey index
  `claim_event_outbox_created` is now created at start-up and the filter is
  `$exists` on the indexed path. On MongoDB the query reads one claim. Azure
  DocumentDB sorts in memory (ADR 017), over the pending claims only; this
  is not verified on a DocumentDB cluster.

- **Shared Kafka producer.** `CloudHealthOffice.Events.Kafka.ChoKafkaProducer`
  has idempotence on, `acks=all` and a bounded delivery timeout.
  claims-service now publishes all its claim events through it. Six other
  services still build their own producer; they are listed in the outbox
  doc.

- **Accumulator plan-year dates no longer shift with the host time zone.**
  The MongoDB driver stored an `Unspecified`-kind plan-year boundary such as
  2025-01-01 as local time converted to UTC: 07:00Z on a UTC-7 host and
  2024-12-31T22:00Z on a UTC+2 host. On the UTC+2 host the read-back start
  then built the previous plan year's snapshot id. Plan-year fields on
  accumulator snapshots, events and processed-claim lease targets now use
  `UtcCalendarDateSerializer` (Infrastructure), which stores the wall-clock
  date as UTC. On a UTC host it writes exactly what the default serializer
  wrote, so documents from UTC pods read back unchanged. Documents written by
  a non-UTC host are not rewritten; they still hold the shifted value.

- **Eligibility's default service date is today in UTC** from the injected
  `TimeProvider`, not the host's `DateTime.Today`. This covers quick check,
  benefit details, accumulation and the auth-requirement lookups.

### Security

- **Member and subscriber ids are masked in log lines** (`***6789`) through
  the shared `LogSanitizer`, which also replaces control characters in
  logged values. Claim, tenant and provider ids are not masked.

- **Attachment uploads are validated before anything is stored.**
  `POST api/attachments` built the blob path from client-supplied parent ids
  and the file extension, and the Content-Type from `DocumentFormat`. Now:
  - Parent ids must be plain ids (letters, digits, `-`, `_`), and every path
    segment is also sanitized.
  - Only PDF, TIFF, JPEG and PNG are accepted. DocumentFormat, file
    extension and file signature must agree, and stored and served
    Content-Types come from that allowlist.
  - Size is capped by `Attachments:Upload:MaxFileSizeBytes`, 64 MB by
    default (the outbound gateway's limit).
  - The parent claim, authorization or appeal must exist for the caller's
    tenant, read from the service that owns it. If that check cannot be
    made, the upload is refused with 502.

- **Member last-name search is an escaped, case-insensitive prefix match**
  of at most 100 characters. Before, the caller's text was run as a
  MongoDB `$regex`, so `.*` matched every member of the tenant.

- **834 enrollment events no longer store SSN or date of birth.** The raw
  snapshot on `EnrollmentEvent.RawSegment` replaces NM109 and DMG02 with
  `[REDACTED]` for the subscriber and every dependent. Names and addresses
  remain. Events stored before this change are not rewritten.

- **Development hosts refuse to start on a deployed platform**
  (Kubernetes, App Service or Container Apps) unless
  `CHO_ALLOW_DEV_ON_DEPLOYED_HOST=true`, and then warn on stderr. The portal
  now runs the same check. The GKE demo renderer and
  `scripts/deploy-local.sh` set the override.

- **Unexpected errors no longer return exception text.** The affected
  services are attachment, eligibility, member, provider, premium-billing,
  payment, benefit-plan, capitation, trading-partner, fhir, idcard,
  enrollment-import and terminology, plus the migration-wizard UI. They now
  return an RFC 7807 problem with a `traceId` (new `ChoProblemResults`
  helper), or record a fixed message on run, batch and row results, and log
  the exception server-side. Messages from the services' own validation and
  state exceptions are unchanged. Converted error bodies are now problem
  documents rather than `{ error | message }` objects. For example, the
  provider credentialing 503 no longer carries
  `error: "credentialing_publish_failed"`. The portal still shows raw
  exception text in about 200 places; that is not yet changed.

- **Secret scanning covers service `appsettings*.json` files.** The
  path-based exemption in `.gitleaks.toml` is removed, and
  `verify-gitleaks-rules.sh` fails if it returns.

- **Claim transmissions are read within the caller's tenant.** This covers
  claim-status and attachment submission by transmission id, remittance
  posting and acknowledgment replay. Another tenant's transmission id is
  answered "not found", and attachment submission now requires a tenant.

- **Stedi attachment submission resolves the payer within the caller's
  tenant.** Before, it read the transmission by id across tenants and
  resolved the payer under the stored record's tenant, so another tenant's
  transmission id returned a different error than an unknown one.

## [5.0.0] - 2026-10-06

First tagged release since v4.0.0; also covers 4.1.0–4.4.0 below, which were
recorded here but never tagged. Release notes:
`docs/releases/RELEASE_NOTES_v5.0.0.md`.

### Also in this release (not itemized below)

- **CHO token auth and tenant isolation across all backend services**, payment
  controls, NACHA transmission controls, SMART production sign-in; Argo
  claims-adjudication workflow, event source and SLA watchdog removed (#1217).
  Tenant now comes from the token only — this supersedes the SEC-01 header
  fallback below; `X-Dev-Tenant-ID` is no longer honored.
- **Da Vinci external interoperability harness** against digest-pinned HL7
  reference implementations: PAS submit, CRD, and DTR `$questionnaire-package`
  (#1159, #1160, #1161).
- **Secret scanning fixed** — gitleaks ran with zero detectors (#1184).
- **HashiCorp Vault secret provider** (#1182).
- **MongoDB as the default data provider**; services routed through shared
  infrastructure; clean-cluster startup defects fixed (#1174, #1177, #1178, #1188).
- **Prospective adjudication (payment estimate) API** (#1091).
- **Canonical reference data service**; ICD-10 display catalog (#914–#920, #1094–#1097).
- **Benefit plan authoring** — version-safe rules, exclusions, networks;
  plan validation; provider network verification (#1076–#1081).
- **Pended-claim examiner workflow**, claim audit timeline, evaluator journeys
  (#1069–#1071, #1082–#1085).
- **Inbound X12 834 and 837 parsers**, 834→837 smoke test, EDI Transactions
  console (#1002–#1018).
- **Million Claim Challenge** validation harness, scaled to one million claims;
  scale fixes for Redis eviction, MongoDB cache, CPU contention; 837
  adjudication over Azure Service Bus (#799–#1047).
- **Provider eligibility API for Cloud Dental Office** (#1204).
- Runbooks: production readiness, DR, observability incidents, 837 operations
  (#1072–#1075); full local Kubernetes deployment (#790, #791).

### Added — SEC-01: production SMART on FHIR / OAuth trust

Cloud Health Office can now be deployed behind an externally managed OAuth/OIDC/
SMART authorization server without source changes or insecure issuer shortcuts.

- **Explicit identity mode.** `SmartAuth:Mode` is `Demo` or `ExternalIssuer`,
  stated rather than inferred. A Demo deployment on a non-development host
  **fails startup**, so production cannot silently fall back to the bundled
  issuer.
- **Multi-issuer trust registry.** The issuer is resolved from the token's exact
  `iss` first and then supplies its own keys, audiences, algorithms, claim
  mapping and permitted tenants — so one trusted IdP's material never authorizes
  another's token.
- **Validated OIDC discovery and JWKS.** The document's `issuer` must match
  exactly; fetch targets are confined to configured origins (SSRF boundary),
  with loopback, link-local and RFC1918 addresses barred outside development.
- **Key rotation.** Unknown `kid` triggers a rate-limited, single-flighted
  refresh; cached keys survive an IdP outage within a bounded staleness window
  and fail closed outside it.
- **Algorithm policy.** Asymmetric only — no `alg=none`, and no HMAC (a
  symmetric verifier accepts a token signed with the issuer's public key).
- **Caller identity model.** One resolution per request into
  `AuthenticatedCaller`; provider NPI is established only from a claim a trusted
  issuer was configured to assert.
- **Readiness.** A `smart-identity-trust` health check reports operational trust
  state without exposing keys, discovery payloads or IdP error text.

#### Fixed

- **Tenant could be named by an unauthenticated header.** `X-Tenant-ID` was
  consulted whenever a token carried no tenant claim, so any authenticated
  caller whose issuer did not map a tenant could select any tenant. The header
  may now fill a vacuum but never contradict a token — a mismatch is a 403.
  `X-Dev-Tenant-ID` is honoured on development hosts only.
- **CDex submitter binding strengthened.** Where a trusted issuer asserts the
  caller's NPI, `$submit-attachment` compares the *token's* identity against the
  provider the request was addressed to, closing the substitution a public-NPI
  corroborating key cannot detect. Deployments without a provider claim are
  unchanged.

Docs: `docs/architecture/smart-oauth-trust.md`,
`docs/architecture/idp-integration-contract.md`.


### USCDI clinical resources served through Patient and Provider Access (PAT-02)

Cloud Health Office could receive a prior payer's Condition, Observation,
Procedure and the rest — the Payer-to-Payer pipeline validated, counted, named
and archived them — but it had nowhere to put them and no way to serve them.
They were classified `Unsupported`, because claiming to ingest a resource type
with no read path behind it would have been a false claim. That gap is now
closed at its root.

**Twelve types, one table.** `AllergyIntolerance`, `CarePlan`, `CareTeam`,
`Condition`, `Device`, `DiagnosticReport`, `Goal`, `Immunization`,
`MedicationDispense`, `MedicationRequest`, `Observation`, `Procedure` — the
USCDI clinical data classes this repository already documents as CMS-0057-F
obligations, minus the ones CHO discharges elsewhere (Patient demographics,
Coverage, CARIN EOB, DocumentReference clinical notes, Provenance as metadata).
`ClinicalResourceInventory` is the single source of truth, and the FHIR routes,
the SMART scope layer, the Provider Access authorization filter, the
Payer-to-Payer import classification and the CapabilityStatement all read it.
That is not tidiness: a clinical type reachable through SMART but missing from
the governed set would be readable by any provider with a scope, attributed or
not, consented or not. Structural tests pin each consumer to the table, and a
test checks every entry against the Firely search-parameter registry so the
table cannot claim a parameter FHIR R4 does not have.

**The import store was promoted, not copied.** The rows Patient and Provider
Access read are the rows a Payer-to-Payer exchange committed — one object in DI
behind two interfaces. There is no projection to fall behind, no dual write to
reconcile, and no second place a resource could be stale in. Clinical data
therefore stays out of CHO's authoritative member, enrollment, coverage and
claim stores *by construction*: a prior payer's Condition cannot be read as a
CHO-owned record because it does not live where CHO-owned records live. Reads
return the version from the most recently **committed** exchange, so a staged or
failed ingestion is invisible and never displaces a committed one.

**Identity is derived, not allocated.** A resource is served under the
deterministic import identity — SHA-256 of tenant + member + source payer +
resource type + source id. A replay updates in place instead of appearing at a
new URL; two payers' identically-numbered records are two resources; another
tenant's id resolves to nothing; and the URL leaks no member, payer or clinical
detail. Serving the payer's own `Observation/123` would have collided on all
three counts.

**Imported `subject` is data, never authorization authority.** Tenant and member
come from the exchange context CHO drove, so a package whose Observation names
another member is filed under the member CHO resolved — and *served* that way
too, with the subject element rewritten to the trusted binding on the way out.

**Both authorization boundaries, unchanged in shape.** Patient Access requires a
patient-context token whose binding is now enforced against **both**
member-naming search parameters: `patient` *and* `subject`. Checking only the
first would have left the second as an unguarded way to ask for someone else's
record, and `subject` is exactly what the clinical types added. Provider Access
keeps all four CONSENT-01 controls — authentication, SMART scope, attribution,
active ProviderAccess-purpose consent — because the clinical types are in the
same globally registered filter's governed set and there is exactly one clinical
controller. A Payer-to-Payer consent still opens nothing here.

**Knowing an id is not authority to read it.** Every store call is keyed on the
tenant *and* the authorized member, so another member's resource is never
selected rather than filtered out afterwards. A provider reading by id must name
the member, because Provider Access authorizes a member and not an id. "Not
yours", "not there" and "another tenant's" return one identical 404 — telling
them apart is what enumeration needs — with the category kept in a PHI-free
audit line carrying ids, an outcome and a count, and no field a value, diagnosis,
medication name or narrative could live in.

**Ingestion gains a payload gate and keeps its honesty.** Clinical resources
pass a type / source-id / size / nesting check before they become readable PHI,
using the parser this service already owns rather than a second one. A refusal is
per resource, counted and named by reason — one oversized Observation does not
cost the member the rest of their history — and the package stays archived
verbatim. Types still outside the inventory are still named, counted and
archived, never dropped.

**Existing history becomes visible without re-running an exchange.** Clinical
data CHO already holds from exchanges committed before this change lives only in
the archived package. `ClinicalBackfillService` re-projects it under exactly the
identities a real import would have produced: deterministic, replay-safe,
tenant-safe (binding from the ledger entry, never the Bundle), committed-only,
non-destructive, gated by the same validator, disabled by default with a dry-run
mode. Asking a prior payer again for data CHO already has would be impossible
once that relationship has ended.

**Stated, not glossed.** No US Core profile is claimed: no `meta.profile`, no
`supportedProfile`. CHO serves these as valid FHIR R4 and does not re-shape a
prior payer's content to satisfy US Core invariants, so a profile URL would be a
label rather than conformance. Search is read plus `_id`, `patient` and
`subject` where R4 defines it — no `category`, `code`, `status` or date search.
No CHO component authors native clinical data yet, so every served resource is
imported; the source axis exists so native data will coexist rather than
overwrite, but it is an unexercised seam.

`meta.source` names the originating payer and the source resource id,
`meta.versionId` is a content hash, so imported data is never indistinguishable
from CHO-authored data.

PAT-02 moves **PARTIAL → PASSABLE**, and the CMS-0057-F evidence generator
computes CHO Replace at **20 PASSABLE / 1 PARTIAL / 0 GAP** from the manifest —
nothing is hard-coded. The one remaining PARTIAL is **SEC-01**, where the
identity provider is configured per engagement.

Design: [docs/architecture/clinical-fhir.md](docs/architecture/clinical-fhir.md).

### CDex additional-information round trip on a pended prior authorization (PAS-07)

A prior authorization pended for additional information stopped at an A4 status.
Cloud Health Office could say *that* more information was needed and could not
say *what*, could not hand the request to the provider, and had no way to take
the answer back. That round trip now exists, end to end, on one durable record.

**Which CDex interaction.** CHO is the payer, so the exchange it needs is the
solicited one: the **CDex Task Attachment Request** profile for the request half
(`GET fhir/r4/Task/{id}`, or by `identifier` / `focus=Claim/{authNumber}`) and
the **`$submit-attachment`** operation for the response half
(`POST fhir/r4/$submit-attachment`). CDex's *Task Data Request* profile — a payer
querying a provider's clinical record — is a different transaction and is
deliberately not what a pended prior authorization uses.

**One record, extended rather than duplicated.** rfai-service already held
`RfaiCase`, so that is the additional-information aggregate: no second store, and
no second lifecycle. The FHIR `Task` is a projection computed on read, and
authorization-service keeps only the handle (`Authorization.RFAIReference`). The
correlation chain is tenant → authorization → request → response → stored
artifact.

**An A4 status alone does not raise a request.** `PendedAuthorizationRfaiCoordinator`
raises one only when the review decision is **A4 *and* the decision names what
documentation is wanted**. A pend that asks the provider for nothing has not asked
them anything, and manufacturing a request from it would put a question to the
provider that no reviewer posed. A1/A2/A3 and a plain InReview state raise nothing
either — all asserted.

**Structured, not prose.** Each requested item carries the X12 PWK code *and* the
LOINC code (the same request has to be expressible on both wires), the service
line it is about, diagnosis context (on a CHO-owned `Task.input` type, since CDex
has no code for it and typing a diagnosis as `attachment-code` would make it read
as a document being requested), and whether it is required; the case carries
a due date and a coded reason. Free text supplements the codes and never replaces
them, and a request with no items is refused at creation.

**Idempotency is in the primary key, not in a read-then-write.** The case's
document id is derived from tenant + authorization + a digest of the decision, so
two workers racing on one A4 event address the same document and exactly one
conditional insert wins (Cosmos 409 / Mongo duplicate key); the loser reads back
the winner's case. A redelivered event replays onto the request the first delivery
created **whatever status it has since reached** — otherwise a redelivery after
the cycle closed would open a second one. At most one cycle is open per
authorization, and a later cycle is a new record with the next sequence, so
earlier request/response evidence is never overwritten.

**A submission is bound to its request, not to an authorization number.** Tenant
(from the authenticated context, never the payload), the payer-issued tracking id,
the authorization named in `AttachTo`, and — where the request records one — the
submitting provider's NPI must all agree. Knowing an authorization number attaches
nothing. Submission identity is content-derived (tenant + request + tracking id +
SHA-256 of the bytes), so a retry records nothing twice while a materially
different document is appended as an *additional* response rather than
overwriting the first.

**Payload policy is enforced before anything is stored, all-or-nothing per call:**
a content-type allow-list (PDF, images, plain/RTF text, XML, C-CDA, FHIR JSON),
20 MB per attachment, 50 MB and 10 attachments per call, 25 artifacts per request
enforced by the aggregate itself, caller-supplied `Attachment.url` refused rather
than fetched, and titles kept as sanitised metadata. Storage keys are derived
entirely from server-side values through the platform's existing
`IClaimAttachmentContentStore`; the case record keeps a pointer and a hash, never
content, and no clinical payload reaches a log or an audit event.

**Receiving documents returns the authorization to review — never approves it.**
The resume-review announcement is raised only on the transition into
`DocsReceived`, so a replay cannot restart the decision clock twice; a partial
delivery records the arrival but leaves the status pended; and a decided
authorization is not reopened by documents arriving late. An acceptance test
asserts explicitly that documentation submission does not imply approval.
`Claim/$inquire` follows the lifecycle — `pended-additional-information` with the
X12 A4 reviewAction before, plain `pending` after — so it is never left
permanently reporting A4 once the data is in.

**A security hole this surfaced and closed — read scopes no longer authorize
writes.** `SmartScopeEnforcementMiddleware` derived the required scope from the
resource-type path segment and always demanded `.read`. Two consequences: a
system-level operation path — `/fhir/r4/$submit-attachment` — names no resource
type and fell through the "unknown path" branch **entirely unenforced**; and
every write reachable under `/fhir/r4`, `POST Claim/$submit` included, was
authorized by a **read** scope.

A request is now resolved into the interaction it actually is — which resource's
scope governs it, whether it reads or writes, and which scope contexts may invoke
it — from the path *and* the method. FHIR operations are classified explicitly,
because an operation's HTTP method says nothing about its effect (`$inquire` and
`$member-match` are POSTs that read); plain REST follows the method. Nothing
falls through: an operation nobody classified is still governed by a scope named
for the operation, with the access its method implies. `$submit-attachment`
additionally requires a `user/` or `system/` context — a patient-context token is
not an acceptable caller for a provider/payer transaction.

**Behaviour change:** `Claim/$submit`, `$cho-appeal-submit`,
`PayerToPayer/$initiate` and DTR questionnaire authoring now require a **write**
scope where a read scope previously sufficed. smart-auth-service therefore issues
write scopes (`user`/`system` × `*`, `Claim`, `Task`, `Questionnaire`,
`QuestionnaireResponse`) and `.well-known/smart-configuration` advertises them —
without that the CDex response half would have been ungrantable, since CHO issued
no write scope at all. There is deliberately **no patient-context write scope**. rfai-service's legacy `by-auth/{tenantId}/…` route now honours a path
tenant only when it matches the authenticated one, instead of selecting on it.

**Anti-enumeration.** Unknown tracking id, other tenant, other authorization and
other provider all return one identical 404, with the distinguishing category kept
in a PHI-free audit line. Request-shape defects are described plainly as 400
because they say nothing about what exists, payload defects as 422, and a fully
correlated but closed request as 409 — the caller has already proven it is theirs.

**Deliberately not Provider Access consent.** That gate governs a provider reading
a member's clinical record; this is a payer/provider transaction about the
submitter's own prior-authorization request. The separation introduced with the
shared consent registry is preserved rather than borrowed from.

**Known limitations, named rather than left absent.** The submitter is bound by the
tracking id and the corroborating provider NPI rather than by the caller's own
identity, because this repository has no token-subject-to-NPI mapping (the same
documented limitation as `$inquire`). The payer makes the request available for
retrieval rather than pushing a Task to a provider endpoint — there is no provider
FHIR endpoint registry here. fhir-service registers the **in-process** attachment
content store by default and says so at startup, so a deployment must bind a
durable implementation before submitted documentation survives a restart. Malware
scanning is a registered seam with **no scanner behind it**; unscanned content is
recorded with scan status `Unknown`, never `Safe`. Expiry is derived from the due
date rather than swept. And there is no outbox: a failure between recording the
decision and raising the request leaves the authorization pended with
`RFAIIssued = false` — the recoverable state, because the retry carries the same
correlation key and cannot duplicate.

**One existing test changed, with cause.** `RfaiDocsReceivedConsumerTests`
asserted that a *partial* delivery wrote nothing at all. Documents reaching the
payer is a fact about the authorization whether or not the request is complete, so
the first arrival is now stamped on `RFAIResponseDate`; the assertion that matters
— the status stays pended and the decision clock stays stopped — is unchanged and
sharpened. `rfai-service` and a new `RfaiService.Tests` were also added to the
solution, which did not previously build them.

Design: [docs/architecture/cdex-additional-information.md](docs/architecture/cdex-additional-information.md).

Acceptance 317 passed (+63), rfai-service 37 (new), fhir-service 458, SmartAuth 59,
authorization-service 26 + 38, evidence 32; all 0 failed. PAS-07 PARTIAL →
**PASSABLE** on 41 supporting tests, so CHO Replace is **19 PASSABLE / 2 PARTIAL /
0 GAP** (generator-computed). Remaining PARTIAL: PAT-02, SEC-01.

### Durable prior-authorization data retention (PAT-03)

Prior-authorization state was persisted and queryable but had no retention
lifecycle: nothing said how long a record must be kept, nothing expired one, and
nothing could prove a record had not been deleted early. That is now an explicit,
testable rule with a production sweeper applying it.

**The rule, in one place.** `IPriorAuthorizationRetentionPolicy` is pure and
deterministic — it reads an authorization and a clock and answers three
questions. `RetentionUntil = last status change + retention period`, and a record
is purgeable only when it is **both** operationally terminal **and** past that
boundary.

**The period is floored, not merely configured.** CMS-0057-F states a *minimum* —
retain for at least one year after the last status change — not a maximum. One
year is enforced as a floor configuration cannot go under, and the default is six
years, matching the HIPAA posture already used for member documents. Defaulting a
destructive job to the bare regulatory minimum would quietly make prior-auth the
shortest-retained regulated data in the platform.

**The anchor is a lifecycle fact, never a read.** `StatusHistory.ChangedAt`
(max), falling back to `ReviewedDate` then `SubmittedDate` — deliberately not
`LastUpdatedDate`, which every write touches, so an unrelated edit cannot move
the boundary. **An inquiry cannot extend a record's regulatory retention**, and a
test drives five reads then asserts the purge still happens exactly when
originally due. A record with no establishable anchor is kept, not purged.

**Open authorizations are never purged**, however old their timestamps: a pended
decision may still be waiting on information no matter how long it has waited.
The open/terminal split was previously spelled out separately in the SLA watchdog,
the RFAI consumer and both repositories; it is now defined once as
`AuthorizationStatus.IsOpen()`/`IsTerminal()` and is total over the enum.

**The sweeper is modelled on `IntegrityProjectionWorker`, deliberately not on
this service's `SlaWatchdogService`** — which captures a scoped repository for
the process lifetime and calls a tenant-less repository method from a background
thread, so it throws on every tick. `PriorAuthorizationRetentionWorker` takes a
scope per tenant from `IServiceScopeFactory`, passes the tenant explicitly to
every repository call (no ambient `HttpContext`), bounds work per tenant per
sweep, observes cancellation between every record, is disabled by default, and
supports a dry run.

**Purge is a conditional delete**, so the check and the delete are one operation:
Mongo filters on id + tenant + expected status in a single `DeleteOne`; Cosmos
re-reads inside the purge and carries that read's ETag into `DeleteItemAsync` as
`IfMatchEtag`. A record that reopens between being listed and being purged
survives — the predicate no longer matches. Repeated sweeps are no-ops, an
already-purged record is not an error, and a failed batch simply retries.

**Deletion is hard, and coherent.** The `Authorization` document embeds its
diagnoses, requested service lines, attachment metadata and status history, so
removing it removes them; the PAS `ClaimResponse` is projected on read rather
than stored, so no FHIR artifact survives. Nothing is kept behind a soft-delete
flag while claiming to be purged. Two dependencies are named as out of scope
rather than half-handled: blobs behind `ClinicalAttachment.FileUrl`, and
`rfai-service` `RfaiCase` records, whose repository has no delete path at all.

**Tenant-safe throughout.** New repository methods (`ListTenantIdsAsync`,
`FindRetentionCandidatesAsync`, `PurgeIfStillEligibleAsync`) take the tenant as
an argument and a `CancellationToken` — the first on this interface to do either.
A purge naming the wrong tenant refuses outright.

**Audit is aggregate.** Sweeps report counts; per-record purge lines carry
tenant, opaque authorization id, policy version, retention boundary and status —
never a member, payload, narrative or credential, with CR/LF stripped (CWE-117).
`cho.authorization.retention.outcomes.total` is dimensioned by outcome, dry-run
and tenant.

**The freshness half of PAT-03 needed no job.** *"Update PA data within 1 business
day"* bounds how stale a copy may be; CHO keeps no copy. PA state is projected
from the authoritative record at read time, so the interval between a status
change and its visibility is zero. That is structural rather than incidental
because the read seam exposes a single lookup and no write, so no cached or
replicated projection can exist behind it to drift — asserted by test. The
absence of a freshness job is the design, not a gap.

Acceptance: **PAT-03 moves PARTIAL → PASSABLE**, so CHO Replace declares
18 PASSABLE / 3 PARTIAL / 0 GAP — computed by the evidence generator, not
hard-coded. Remaining PARTIAL: PAS-07, PAT-02, SEC-01. PAT-02 and PAS-07 are
untouched by this work. Zero GAPs is still not complete CMS-0057-F compliance.

Documentation: a Data retention section in
`docs/architecture/prior-authorization.md`; updated
`docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md`.

### Da Vinci PAS `Claim/$inquire` — prior-authorization status through the FHIR surface

Prior-authorization state was persisted and retrievable, but only through
authorization-service's own REST surface. The standards-facing FHIR operation did
not exist, which is what kept PAS-04 PARTIAL. `POST fhir/r4/Claim/$inquire` now
serves it.

**One record, projected — not a second model.** The inquiry reads the same
authorization record `$submit` writes and the rest of the platform updates, over
the read endpoint authorization-service already exposes. There is no
inquiry-specific store and no second status field. `PriorAuthorizationRecord` is
a deliberately narrow read projection: the fields an inquiry does not need —
patient name, date of birth, clinical attachments, reviewer, notes — have no
property to land in, so they cannot leak into a response or a log.

**Standards shape, not a CHO wire format.** The request is a PAS Bundle carrying
a `Claim` with `use = preauthorization`, whose identifier (or
`insurance.preAuthRef`) names the authorization. The response is a Bundle
carrying a `ClaimResponse` on the Da Vinci PAS profile, built by the same
`PasResponseBuilder` that serves `$submit`.

**The status mapping is deterministic and total** over CHO's authorization
states: Submitted/InReview → queued `pending`; Pended/A4 → queued
`pended-additional-information` with the X12 306 reviewAction; Approved/A1 →
complete `approved`; Modified/A2 → partial `modified`; Denied/A3 → complete
`denied` with the coded reason; Expired → complete `expired`; Cancelled →
cancelled. `outcome` carries the coarse answer and `disposition` the specific
one, so a caller can distinguish pending from pended-for-information from
approved from denied. An unrecognised status reads as still in progress rather
than as an approval CHO cannot vouch for.

**Read-only by contract.** `IPriorAuthorizationStore` exposes one lookup method
and no write method at all — asserted structurally, so an inquiry cannot create a
record, move a status, restart a decision clock, or trigger a payer submission
however often it is repeated. Status comes from the stored adjudication record,
so an inquiry never becomes an outbound X12 transaction.

**Freshness.** Every inquiry reads live committed state, so a status changed
after submission is the status returned; tests drive pended → approved and assert
the second inquiry reflects it.

**Lookup cannot be guessed into.** An authorization number alone is never
sufficient — a corroborating member or provider key must accompany it and match
the record, and a supplied key that does *not* match refuses even when another
does. Tenant comes from the authenticated context and is re-checked on the record
itself, so it holds even if header propagation is lost.

**Anti-enumeration, without swallowing honest errors.** Request-shape defects —
no authorization identifier, no corroborating key — return `400` naming what is
missing, because they say nothing about what exists; telling a caller who forgot
an identifier that their authorization "does not exist" would be both wrong and
unhelpful. Every refusal about a *record* — unknown, wrong tenant, not the
caller's — returns one identical `404` `OperationOutcome`, and a structural test
pins that classification so a newly added outcome cannot quietly become
distinguishable. The category is kept in a PHI-free audit line carrying tenant,
caller, authorization number, outcome and status only.

**Thin controller.** The action routes and maps to HTTP; lifting the lookup keys
out of the inquiry Claim belongs to the service, since which element carries
which key is a property of the PAS request shape rather than of HTTP.

**Authorization controls.** Authentication (`[Authorize]`), the SMART
`*/Claim.read` scope, and tenant from context — the same controls `$submit` has.
Deliberately **not** routed through the Provider Access consent gate: that gate
governs a provider reading a member's clinical record, whereas PAS is a
system-to-system transaction between the submitter and the payer about the
submitter's own request. The corroborating key, not a member consent, is what
binds an inquiry to its authorization.

**Making status inquirable required fixing the write side.** `preAuthRef` was set
only on approvals, and a pended submission persisted an authorization number that
was never returned to the caller — so the outcome that most needs following up
was un-inquirable. Approved, denied and pended responses now all carry the
number that was persisted. The denial code and reason, the approved period, and
the service lines from the submitted Claim are now persisted too, so an inquiry
can answer *why* and *for what* rather than just "denied". The authorization HTTP
client now propagates the tenant header — without it authorization-service falls
back to its default partition, and reads and writes would have crossed tenants.

**CapabilityStatement.** `Claim` now advertises both `submit` and `inquire` with
their Da Vinci PAS `OperationDefinition` canonicals, pinned by test to the routes
actually served. `$submit` had never been advertised.

Acceptance: **PAS-04 moves PARTIAL → PASSABLE**, so CHO Replace declares
17 PASSABLE / 4 PARTIAL / 0 GAP — computed by the evidence generator, not
hard-coded. **PAS-07 stays PARTIAL**: `$inquire` *reports* a pended-for-
information decision, which CHO already knows from the A4 review decision, but it
neither requests documentation nor accepts it — that round-trip is what CDex is,
and it is not implemented. Remaining PARTIAL: PAS-07, PAT-02, PAT-03, SEC-01.
PAS-01/02/03/05/06/08 are unchanged and green. Zero GAPs is still not complete
CMS-0057-F compliance.

New documentation: `docs/architecture/prior-authorization.md`. Updated:
`docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md`, `docs/diligence/ADAPTER-STATUS.md`.

### Enforce Provider Access through the shared consent registry

Payer-to-Payer already ran on a purpose-scoped consent decision; Provider Access
did not. A provider-shaped token (`user/…` or `system/…`) that passed the SMART
scope check could read **any** member's record — the acceptance suite even
asserted that as expected behaviour. Provider Access now composes four
independent, mandatory controls, and the consent one runs on the same registry
and the same policy as Payer-to-Payer.

**Four controls, none implying another.** Authentication and SMART scope stay
where they are (middleware — token validation is not moved or re-implemented).
On top of them `ProviderAccessAuthorizationService` adds provider/member
attribution and an active `ConsentPurposeOfUse.ProviderAccess` consent. A correct
scope implies neither attribution nor consent; attribution does not imply
consent; consent does not imply attribution; and a Payer-to-Payer consent
authorizes nothing here. The composed decision fails closed — any one refusal
denies, and so do a missing tenant, a missing member context, an unidentified
caller, and an unreadable registry.

**One registry, one policy, now shared by construction.** A new
`IConsentEvaluator` owns the fail-closed registry read and delegates every
lifecycle and purpose rule to the existing pure `ConsentAuthorizationPolicy`.
Payer-to-Payer's gate was refactored onto it rather than keeping its own copy, so
the two capabilities cannot drift: they differ only in the purpose they ask for.
`IConsentSource` generalises the registry seam that #1152 introduced — the same
`HttpConsentRegistryConsentSource` against `consent-service` serves both, and no
second consent store exists.

**Enforced at one shared boundary, before any PHI is read.**
`ProviderAccessAuthorizationFilter` is a **global** MVC action filter, not a
per-controller attribute, so a new member-scoped controller is governed the
moment it exists. A filter rather than middleware because Provider Access needs
the tenant and `TenantMiddleware` runs *after* `SmartScopeEnforcementMiddleware`;
a filter runs after the whole pipeline yet still before any action body. It
governs every member-scoped resource the SMART layer serves — `Patient`,
`Coverage`, `ExplanationOfBenefit`, `Encounter`, `Claim`, `Task`,
`Communication`, `DocumentReference`, `ClaimResponse` — and a structural test
pins that inventory to the SMART layer's own list so a resource cannot escape one
by being forgotten in the other. FHIR operations are deliberately excluded:
`$member-match` and `$member-data-export` have their own Payer-to-Payer
authorization, and an operation name is not a member id.

**Provider Access is a caller shape, not a route name.** A `user/`- or
`system/`-scoped token is a provider reading someone else's record. A
patient-scoped token is Patient Access — the member reading their own data —
which a Provider Access consent does not govern and is not required for. The
distinction comes from the token, so it cannot be lost by adding an endpoint.

**Member context is required, not guessed.** `Patient/{id}` names the member;
otherwise it comes from `?patient=` or the SMART binding. A resource id is never
resolved to a member, because resolving it means reading the resource being
authorized. No member context denies — which is why a provider-shaped search
across the whole membership is now refused rather than returning every member.

**Refusals cannot be used to enumerate.** "Not attributed", "no consent" and "no
such member" return one identical `403` FHIR `OperationOutcome`; a test asserts
the bodies are byte-identical. The structured category lives in the audit record
instead. Decisions are audited with PHI-free identifiers only — tenant, member
id, caller id, resource type, consent id, category, instant — never
demographics, clinical payloads, consent narrative, or credentials, with CR/LF
stripped from ids (CWE-117).

**Attribution, stated honestly.** The repository had **no** attribution code at
all: PROV-02's "attribution enforcement" test asserted a dictionary miss on an
unknown id, and the capability text describing Provider Access as "governed by
attribution plus SMART scopes" was aspirational. Attribution is now a real,
enforced control backed by a configured panel catalog
(`Cms0057:ProviderAttribution`) that fails closed on an empty catalog — but no
live roster feed from a payer source system is wired up, and nothing claims one
is. That remains engagement integration behind `IProviderAttributionSource`.

Two existing tests asserted the hole and were rewritten rather than deleted:
`EobSearch_UserToken_CanSearchAnyPatient` (a provider token reading any patient)
and `PatientSearch_SystemToken_NoPatientBinding` (a backend token listing the
whole membership) now assert the refusals, alongside new tests for the authorized
paths.

Acceptance: **CONSENT-01 moves PARTIAL → PASSABLE**, so CHO Replace declares
16 PASSABLE / 5 PARTIAL / 0 GAP — computed by the evidence generator from the
manifest, not hard-coded. Payer-to-Payer behaviour is unchanged and its suite
re-runs green: a Provider Access consent still authorizes no exchange. Remaining
PARTIAL: PAS-04, PAS-07, PAT-02, PAT-03, SEC-01. Zero GAPs is still not complete
CMS-0057-F compliance; this is implementation evidence, not certification, and
the QNXT/external-core column is unchanged.

New documentation: `docs/architecture/provider-access.md`. Updated:
`docs/architecture/consent.md`, `docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md`,
`docs/diligence/ADAPTER-STATUS.md`.

### Dedicated Payer-to-Payer consent lifecycle and enforcement

Payer-to-Payer authorization was a generic Active consent: any active record for
the member let an exchange proceed. It is now a first-class, purpose-scoped
decision on the same registry.

**A purpose axis, not a new consent type.** `ConsentPurposeOfUse`
(`Unspecified` / `PayerToPayerExchange` / `ProviderAccess`) says what a consent
authorizes the plan to *do*, orthogonal to `ConsentType`, which says what
regulatory instrument the record *is*. A §164.508 authorization for
Payer-to-Payer and one for Provider Access are the same instrument and different
permissions, and a sensitive-category authorization can be purpose-scoped too.
The axis follows FHIR `Consent.provision.purpose` (HL7 v3 PurposeOfUse), so the
record projects onto FHIR Consent when that projection lands.

**One registry, one policy.** `consent-service` remains the authoritative store —
no second consent collection was added. The new
`CloudHealthOffice.Consent.Contracts` project carries a PHI-free
`ConsentAuthorizationSnapshot` (tenant, member, id, purpose, status, period,
version — no narrative fields, and no field to put them in) and
`ConsentAuthorizationPolicy.Evaluate`, a pure function that is the single place
any purpose-scoped authorization is decided. A snapshot must match the tenant
**and** the member, carry the requested purpose, be `Active`, and be in force at
the evaluation instant — the effective period is applied by the policy rather
than trusted from the stored status, so a record persisted as `Active` past its
`ExpiresAt` still denies. Ties resolve deterministically (latest-expiring,
unbounded first, then highest version), so two evaluations of the same registry
state name the same consent.

**Refusals say which refusal.** `NoConsentOnRecord`, `NoConsentForPurpose`,
`NotActivated`, `Revoked`, `Expired`, `NotYetEffective` — reported
most-specific-first, because "they revoked it", "it lapsed", and "they never
granted this purpose" are different operational facts.

**Enforced server-side, identically in both directions.** Inbound
(`PayerToPayerExchangeService`) evaluates before any member data is assembled.
Outbound (`PayerToPayerOutboundService`) evaluates twice: before the remote
`$member-match`, so an unauthorized member's identity never leaves CHO at all,
and **again immediately before the export**, so a revocation landing while the
match is in flight stops the data request. Both call one
`IPayerToPayerConsentGate` over one policy against one registry, so responding
cannot drift more permissive than initiating. No Payer-to-Payer request type has
a consent field in either direction — an acceptance test asserts that by
reflection over all four request types, so neither a peer payer nor an internal
caller can self-attest.

**Provider Access separation is structural.** A member with an Active
`ProviderAccess` consent and nothing else is denied for Payer-to-Payer with
`NoConsentForPurpose`. The purposes are compared as data inside the policy;
nothing about the calling controller or route participates.

**Exchanges record what authorized them.** `AuthorizingConsentId`,
`ConsentDecisionReason`, and `ConsentEvaluatedAtUtc` are written on the outbound
exchange, and the consent id and reason on both audit entries. A retry clears
them so it re-asks rather than reusing an earlier answer.

**Fail-closed at every edge.** Blank tenant or member, an unreadable or
unreachable registry, a source that throws, an empty catalog, and `Unspecified`
purpose all deny. Consent-lookup failures log a category only, never registry
detail.

New consent-service surface: `PurposeOfUse` on `Consent` (optional on create) and
`GET api/v1/members/{memberId}/consents/authorization-snapshots?purposeOfUse=`,
a PHI-free projection so another service can authorize without reading consent
narrative. `fhir-service` reads it through `HttpConsentRegistryConsentSource`
when `Services:ConsentServiceUrl` is configured; the configuration-backed source
remains the Demo/test fallback in the *same* shape (purpose, status, period), so
Demo exercises the real policy instead of a boolean allow-list.

**Migration is explicit and fails closed.** Records written before `PurposeOfUse`
existed deserialize to `Unspecified` and authorize nothing purpose-specific.
There is no backfill, no inference from `ConsentType`, and no "treat Active as
P2P" fallback. The practical consequence is deliberate: **a deployment upgrading
to this code authorizes zero Payer-to-Payer exchanges until members' consents are
recorded with `PurposeOfUse = PayerToPayerExchange`.** Unchanged: `ConsentType`,
the state machine and its transitions, the encrypted narrative fields, and every
existing endpoint's contract (the consent event payload gains a `purposeOfUse`
field).

Acceptance: **P2P-03 moves PARTIAL → PASSABLE**, so CHO Replace declares
15 PASSABLE / 6 PARTIAL / 0 GAP — computed by the evidence generator from the
manifest, not hard-coded. **CONSENT-01 stays PARTIAL** with a new GAP test naming
the reason: the registry can now express a Provider Access purpose, but the
Provider Access *read path* does not consult it (attribution plus SMART scopes
govern that path), so CONSENT-01 does not ride on the Payer-to-Payer work. The
QNXT/external-core column is unchanged — P2P-03 augment stays **GAP**. Zero GAPs
is still not complete CMS-0057-F compliance; this is implementation evidence, not
certification or attestation.

New documentation: `docs/architecture/consent.md`. Updated:
`docs/architecture/payer-to-payer.md` (Consent section, order of operations),
`docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md`.

### Durably ingest inbound Payer-to-Payer data into the member record

The outbound Payer-to-Payer exchange previously stopped at a validated Bundle:
CHO retrieved another payer's member-scoped package, checked it, stamped
provenance, audited it — and kept nothing. A successful exchange now produces a
durable, tenant-safe, member-scoped, provenance-preserving CHO record.

New fhir-service code: `PayerToPayerPackageIngestionService` (application
service) receives an ALREADY VALIDATED package from `PayerToPayerOutboundService`
and never contacts a payer itself — the orchestration and transport added for
P2P-02 are unchanged, and no second Payer-to-Payer wire format exists. It
classifies each resource, normalizes intra-package references, stages every row
under a deterministic import key, and commits.

**Imported data is kept apart from CHO-authoritative data.**
`IPayerToPayerImportRepository` (MongoDB when `MongoDb:ConnectionString` is set,
in-process otherwise — the same fallback `DtrService` uses) is a separate store
from CHO's member, enrollment, claim, and provider records. Source ownership is
structural rather than conventional: an imported row cannot be read as a CHO-owned
record, so a remote `Patient` never replaces CHO's member identity and a prior
payer's `Coverage` never touches current enrollment. Both are stored as
reference-only administrative context, keeping the peer's resource id while filed
under CHO's own member.

**Supported types are the ones CHO actually serves**, not the CMS wish list:
`ExplanationOfBenefit`, `Claim`, `ClaimResponse`, `Encounter`, and
`DocumentReference` are ingested as member history; `Patient`, `Coverage`,
`Organization`, `Practitioner`, `PractitionerRole`, and `Provenance` are stored as
reference-only. Everything else — `Condition`, `Observation`, and the rest of the
USCDI clinical set — is **named and counted on the exchange and preserved in an
archived copy of the package**, never silently dropped and never claimed as
ingested.

**Replay-safe by construction.** The import key is a hash of
tenant + local member + source payer + resource type + source resource id, joined
with a separator that cannot occur in an identifier. Replaying a package lands on
the same rows instead of doubling a member's history; the same source id from a
*different* payer is a different key, so two payers' records are never merged; a
content hash tells "same again" from "changed"; and each exchange's own
`Provenance` stamp stays its own record so it remains clear which exchange
delivered what.

**Atomic enough to be safe.** Rows are versioned by exchange — identified by
(tenant, exchange, import key) — and reads return the version from the most
recently committed exchange. Staging writes only that exchange's own rows and
committing is a single-document ledger write, so a failed ingestion both adds
nothing visible AND takes nothing away: it cannot overwrite or hide the version an
earlier exchange committed, and an updated resource supersedes the older one only
once the exchange carrying it commits. A retry re-stages the same deterministic
keys and commits; an exchange abandoned in a non-terminal state is taken over
once stale, so a process that dies mid-ingestion cannot strand a coverage
transition. The
exchange gained `DataReceived` and `Ingesting` states plus structured ingestion
fields (status, failure category, persisted / duplicate / administrative /
unsupported counts with the unsupported types named, and start/finish timestamps);
`Completed` is now reachable **only** after the commit lands, so a package that
was retrieved but not stored is never reported as success.

**References** are rewritten only when they resolve to another resource in the
same package — relative, absolute, and versioned forms all resolve to CHO's
imported identity, while references to resources the peer did not send, contained
(`#…`) references, and `urn:uuid` forms are left exactly as they arrived. CHO does
not invent links the source payer never asserted, and an absolute URL does not
survive as a live pointer at the peer.

Tenant, member, and source payer on every stored row come from the validated
exchange context, never from the peer's Bundle: a package whose resources name
another tenant or member changes nothing about where its data is filed. Logs and
audit carry ids, categories, and counts only — no Bundle bodies, demographics,
clinical payloads, or endpoint URLs. Real tests
(`PayerToPayerIngestionTests`, `[Trait("Backend","Replace")]`, 15 scenarios, plus
`PayerToPayerImportPolicyTests` / `PayerToPayerReferenceNormalizerTests`, 36
cases, plus `PayerToPayerOutboundControllerTests`) drive the production path:
durable persistence with correct binding, provenance retention, administrative
ownership, unsupported-type handling, replay and cross-payer non-merging, staging
and commit failure, a failed later exchange not hiding committed history, retry
and stale-exchange takeover, tenant and member safety, reference resolution, and
per-failure HTTP mapping.

**No acceptance scenario status changed.** P2P-02 was already PASSABLE and its
rationale is updated; **P2P-03 stays PARTIAL** (no dedicated Payer-to-Payer
`ConsentType`), **PAT-02 stays PARTIAL** (USCDI clinical types are archived, not
served), and **PAT-03 stays PARTIAL** (no retention job). CHO Replace remains
14 PASSABLE / 7 PARTIAL / 0 GAP, which is not full CMS-0057-F compliance,
completeness, or certification. Imported data is durable but **not yet projected
into CHO's FHIR read APIs**; payer onboarding (SMART Backend Services / UDAP,
mTLS) remains deployment integration; and QNXT/external-core Payer-to-Payer
integration remains **GAP**. New architecture documentation:
`docs/architecture/payer-to-payer.md`.


### Implement CHO-native outbound Payer-to-Payer exchange (P2P-02)

Closed CMS-0057-F acceptance gap **P2P-02** (Payer-to-Payer outbound initiation)
as real Cloud Health Office Replace-mode capability: on an authorized coverage
transition, CHO — the member's new payer — initiates the exchange against the
member's prior payer, rather than only answering other payers' requests. It
orchestrates the existing P2P primitives instead of duplicating them (P2P-01
respond semantics for the data request, P2P-04 coverage selection for the local
prior-coverage context) and adds no second wire format.

New fhir-service code: `PayerToPayerOutboundService` (application service) drives
the workflow fail-closed and in order — tenant scope, member + prior-payer
coverage context from CHO-owned data, target-payer endpoint resolution,
server-side opt-in, remote `Patient/$member-match`, member-data export, response
validation, provenance, audit, exchange state. A thin
`PayerToPayerOutboundController` (`POST fhir/r4/PayerToPayer/$initiate`, under
the SMART-enforced surface, tenant from the authenticated context) only routes;
no outbound logic lives in it.

**Endpoint resolution is the SSRF boundary.** `IPayerToPayerEndpointResolver`
resolves a *payer id* — never a caller-supplied URL — against a tenant-scoped
configuration directory (`Cms0057:PayerToPayerOutbound`), and fails closed: an
unknown payer, a duplicate entry, a non-absolute or non-HTTPS base URL (plain
HTTP only under an explicit development flag, with a warning), or a URL carrying
user info, a query, or a fragment resolves to nothing. The outbound request and
its DTO carry no URL/endpoint field at all. `HttpPayerToPayerRemoteClient` uses a
named `HttpClient` with redirects disabled (a peer cannot bounce CHO onto another
host), a response-size cap, unchanged TLS validation, and no logging of payloads,
demographics, credentials, or endpoint URLs — log lines identify a peer by its
opaque directory key.

**Authorization is server-side and enforced before anything leaves CHO.** The
existing `IPayerToPayerConsentGate` decides the member's opt-in; there is no
caller-supplied consent field, and an unauthorized member's identity is never
disclosed to a remote payer (not even in a member-match). The remote match sends
only what the operation needs — the member's identifier with that payer (from
CHO's own coverage record) plus family name and birth date; no SSN, address,
phone, or email. Export is requested **only** after the peer resolves exactly one
member, and the returned FHIR Bundle is parsed and checked for member consistency
(single matched Patient, no foreign `Patient/…` reference) before acceptance;
anything unparseable, empty, or inconsistent is rejected whole. Accepted packages
are stamped with a `Provenance` naming the source payer, so another payer's data
is never mistaken for CHO-originated.

Outcomes are structured, not free text (`TargetPayerNotConfigured`,
`NotAuthorized`, `LocalCoverageAmbiguous`, `MemberNoMatch`, `MemberAmbiguous`,
`RemoteUnauthorized`, `RemoteUnavailable`, `InvalidRemoteResponse`) and are
recorded on a `PayerToPayerOutboundExchange` with an idempotency key
(tenant | member | target payer | transition), so a repeated initiation replays
one exchange and a retry after a failure resumes it. Audit carries tenant,
member, target payer, endpoint key, exchange id, outcome, and resource count —
no demographics, payload, URL, or credential. Real acceptance tests
(`PayerToPayerOutboundTests`, `[Trait("Backend","Replace")]`, 24 scenarios) drive
the production orchestration with only the far side of the wire faked, asserting
call ordering and request content, missing consent, unconfigured/non-HTTPS payer,
no-match, ambiguous match, remote auth/transport failure, malformed and
cross-member packages, cross-tenant refusal, overlapping local coverage, and
idempotent retry. `HttpPayerToPayerRemoteClientTests` pins the transport seam's
own contract: peer status → structured outcome (422 is the anti-enumeration
no-match signal; a 404 is a route/configuration error, not a member no-match),
calls only to the resolved endpoint URIs, no fabricated Authorization header, and
an oversized or empty body refused rather than buffered.

**Scope, stated plainly.** P2P-02 CHO Replace moves GAP → **PASSABLE** and CHO
Replace now declares 14 PASSABLE / 7 PARTIAL / 0 GAP — which is not full
CMS-0057-F compliance, completeness, or certification. **P2P-03 remains
PARTIAL**: opt-in is still a generic Active consent with no dedicated
Payer-to-Payer `ConsentType`. Received packages are retrieved, validated, and
audited but **not ingested** into the CHO member record; exchange state lives in
an in-process store; and connecting to any named payer needs that payer's
onboarding — a directory entry plus transport credentials (SMART Backend
Services / UDAP client registration, mTLS) behind
`IPayerToPayerCredentialProvider`, which supplies none by default rather than
fabricating one. QNXT/external-core P2P integration (including outbound
initiation from a QNXT-backed deployment) remains **GAP**.


### Implement Cloud Health Office-native Payer-to-Payer member match (P2P-04)

Closed CMS-0057-F acceptance gap **P2P-04** (Payer-to-Payer member-match /
concurrent coverage) with the FHIR `Patient/$member-match` operation as real
Cloud Health Office Replace-mode capability — cross-payer identity resolution
over CHO-owned data, distinct from the P2P-01 known-member respond. New
fhir-service code: `PayerToPayerMemberMatchService` (application service) resolves
the transitioning member within the tenant from normalized identity attributes
and returns the relevant **member + coverage context**; a thin
`PayerToPayerMemberMatchController` (`POST fhir/r4/Patient/$member-match`, under
the SMART-enforced surface, tenant from the authenticated context) only routes.
Matching is **deterministic and fail-safe** (`MemberMatchPolicy`): a positive
assertion needs a strong identifier (member/subscriber id or SSN) or the
family-name + birth-date pair, and any contradicting attribute — wrong DOB, wrong
member id, different sex — fails the candidate closed. Zero candidates → no match,
more than one → ambiguous, cross-tenant → never visible, and a weak single
attribute is refused before any search (anti-enumeration). `MemberIdentityNormalizer`
makes equivalent formatting (casing, whitespace, accents, phone/ZIP punctuation,
identifier hyphens) compare equal without merging distinct people.
`PayerToPayerCoverageSelector` picks the relevant **concurrent/prior/current**
coverage by requested payer/subscriber context and effective date; genuinely
overlapping coverages without a discriminator return an ambiguity rather than a
guess. The match reuses the same CHO member/coverage store via a new
`IChoMemberDirectory` on `MockPatientAccessDataProvider` (no duplicate store; the
Patient Access contract is unchanged), and its resolved member id feeds the P2P-01
export path directly (proven by an acceptance test). Member-match is identity only
and does **not** gate on or introduce consent. Real acceptance tests
(`MemberMatchTests`, `[Trait("Backend","Replace")]`) exercise the production
service/policy/selector/normalizer/source: exact strong-id and demographic
matches, prior-payer subscriber id, no-match, ambiguous identity narrowed by given
name/gender, conflicting id/DOB/given name, cross-tenant, insufficient criteria,
normalization, concurrent-coverage selection (prior/current/overlapping), and the
P2P-01 hand-off. The prior P2P-04 GAP-assertion test is replaced by this behavioral
coverage; the adapter-status report's PayerToPayer source now names `$member-match`
(mode stays Demo). The scenario manifest moves P2P-04 `replace` `GAP → PASSABLE`.
**P2P-02** (outbound initiation, stays GAP), **P2P-03** (dedicated P2P ConsentType,
stays PARTIAL), and **QNXT Augment** are unchanged and independently truthful. The
CI evidence pipeline derives the new result automatically — CHO Replace counts move
`PASSABLE 12 / PARTIAL 7 / GAP 2` → `PASSABLE 13 / PARTIAL 7 / GAP 1` with no
evidence-tooling change and no manual edit of generated evidence. Synthetic data
only; no PHI or payer configuration. Probabilistic matching, member enumeration,
and production P2P transport security (mTLS/UDAP) remain engagement work.

### Implement CHO-native Payer-to-Payer member data export (P2P-01)

Closed CMS-0057-F acceptance gap **P2P-01** (Payer-to-Payer inbound respond) with
the first production-shaped Payer-to-Payer vertical slice, Cloud Health Office
acting as the authoritative prior payer in Replace mode. New fhir-service
`PayerToPayer` domain: `PayerToPayerExchangeService` (application service) resolves
the transitioning member via a **tenant-scoped, deterministic** resolver
(`PayerToPayerMemberResolver` over `PatientAccessPayerToPayerMemberSource`, which
reuses the existing `IPatientAccessDataProvider` — no duplicate store), enforces
the member's opt-in **authorization** (fail-closed), and assembles a member-scoped
FHIR export with `PayerToPayerExportBuilder` reusing the existing CARIN/US Core
`PatientAccessMapper` (Patient + Coverage + ExplanationOfBenefit). A
`PayerToPayerExportPolicy` applies the locked 5-year date-of-service lookback (the
remittance/cost-sharing/drug exclusions are represented as predicates, gated on
data-model markers that do not exist yet). A thin `PayerToPayerController`
(`POST fhir/r4/PayerToPayer/$member-data-export`, under the SMART-enforced FHIR
surface, tenant taken from the authenticated context) routes to the service; all
logic lives in the service, not the controller. Member matching is safe:
insufficient criteria, no candidate, more than one candidate, a demographic
mismatch, a cross-tenant request, or a missing opt-in each fail explicitly and
never return another member's or another tenant's data. Every exchange yields an
audit entry. Real acceptance tests (`PayerToPayerExportTests`,
`[Trait("Backend","Replace")]`) exercise the production service/resolver/source/
builder/mapper: happy path, wrong member, no/ambiguous match, tenant boundary,
missing consent, empty-but-valid member, and the 5-year lookback. The prior P2P-01
GAP-assertion test is replaced by this behavioral coverage; the adapter-status
report moves PayerToPayer `OutOfScope → Demo` (inbound respond). The scenario
manifest moves P2P-01 `replace` `GAP → PASSABLE`. **P2P-02** (outbound initiation),
**P2P-03** (dedicated P2P ConsentType — stays PARTIAL), **P2P-04** (`$member-match`
/ concurrent coverage), and **QNXT Augment** are unchanged and independently
truthful. The CI evidence pipeline derives the new result automatically — CHO
Replace counts move `PASSABLE 11 / GAP 3` → `PASSABLE 12 / GAP 2` with no
evidence-tooling change and no manual edit of generated evidence. Synthetic data
only; no PHI or payer configuration. Production P2P transport security (mTLS/UDAP)
remains engagement work.

### Enforce drug exclusions in the CMS-0057-F prior-auth workflow

Closed CMS-0057-F acceptance gap **PAS-08** by implementing benefit drug/service
exclusion as real Cloud Health Office **Replace-mode** product capability, in the
authorization/benefit decision path (not a FHIR-controller or test-only check). New
`authorization-service` benefit-exclusion domain — `BenefitExclusion` model, a
configuration-driven, tenant-scoped `IBenefitExclusionCatalog` (no hard-coded
codes), a `DrugServiceCodeNormalizer` (NDC/RxNorm/HCPCS/CPT/service-type),
a pure `DrugExclusionEvaluator`, and an `AuthorizationExclusionService`.
`ChoAuthorizationBackend.CreateAsync` now consults it before the ordinary path: a
request for a drug/service the member's applicable plan excludes (or the pharmacy
service type, out of the CMS-0057-F medical scope) is recorded as a coded denial
(278 A3, structured `DenialReasonCode`) and persisted in the authoritative CHO
record with an auditable status history — never auto-approved by a generic rule.
A non-excluded request is unaffected. `RequestedService` gained an optional
`ProductOrServiceSystem` so a drug identity can be normalized. Real acceptance
tests (`DrugExclusionTests`, `[Trait("Backend","Replace")]`) exercise the
production backend + catalog + evaluator over a repository fixture — excluded drug,
non-excluded comparator, no-catalog, coverage scoping, code normalization, unknown
code, multiple exclusions, pharmacy service type, and PAS denied-response mapping
via `PasResponseBuilder.BuildDeniedResponse`. The prior PAS-08 GAP-assertion test is
replaced by this behavioral coverage. The scenario manifest moves PAS-08 `replace`
`GAP → PASSABLE` (rationale updated); QNXT Augment stays `N/A` (no external-core
drug-exclusion integration is claimed). The CI evidence pipeline derives the new
result automatically — CHO Replace counts move `PASSABLE 10 / GAP 4` →
`PASSABLE 11 / GAP 3` with no change to the evidence tooling and no manual edit of
generated evidence. Synthetic data only; no PHI, formulary, or payer configuration.

### Harden and publish CMS-0057-F acceptance evidence

Made the CMS-0057-F evidence pipeline fresh, traceable, and externally
understandable without exposing CI internals. The evidence workflow now also runs
when runtime/domain code that can affect CMS-0057-F behavior changes (the FHIR,
authorization, member, provider, claims, benefit-plan, consent, and smart-auth
services, plus the operating-mode and prior-auth-rule engines), not only when the
acceptance suite or evidence tooling changes. A new allow-list projection in
`tools/Cms0057Evidence` (`--public-output`) emits a sanitized
`cms0057-public-evidence.json` — built field by field, so it carries only schema
version, evidence status, commit SHA/short/URL, timestamp, synthetic
classification, framework, FHIR version, scenario count, a test-execution summary,
independent Replace (product) and per-backend Augment (integration) declared-status
counts, a per-scenario declared-status matrix, and disclaimers — never test names,
rationales, run identity, PHI, secrets, tenant data, or QNXT field mappings. The
projector refuses to publish a run with any failed test. Declared capability status
stays separate from execution: a passing GAP-assertion test remains **GAP** in the
public snapshot, never a pass. The workflow now splits into an `evidence` job
(PR + main; validates and uploads artifacts only) and a main-only `publish` job
(narrow `contents: write`) that commits the sanitized snapshot to the site tree; the
acceptance-scenarios page renders it under **Latest published evidence** with the
tested source revision and generation date, Replace shown as product capability and
each external core as separate integration capability. Public reporting avoids any
CMS-certification or universal-production-readiness claim, and known gaps stay
visible. New projector unit tests cover allow-list sanitization, GAP-stays-GAP,
independent Replace/Augment counts, deterministic ordering, fail-safe on missing or
failed input, and unknown future backends. No runtime service behavior changed.

### Versioned CMS-0057-F acceptance evidence in CI

Turned the CMS-0057-F acceptance suite into auditable, reproducible evidence
tied to a source revision. `tests/Cms0057Acceptance.Tests/scenarios.json`
(`schemaVersion: 1`) is now the machine-readable source of truth for scenario
status (`PASSABLE | PARTIAL | GAP | N/A`), scored on two independent axes —
Cloud Health Office **Replace** (product capability) and external-core
**Augment** (integration capability). New `ScenarioManifestTests` reconcile the
suite's `[Trait]`s against the manifest (unknown/duplicate ids, invalid statuses,
a scenario silently losing all its tests, or a PASSABLE-for-a-backend scenario
backed only by GAP-assertion tests all fail the build). New generator
`tools/Cms0057Evidence` reads the manifest, the acceptance TRX, and the suite's
traits and emits deterministic `cms0057-evidence.json`/`.md`/`.html` bound to the
full tested commit SHA, keeping **declared capability status** separate from
**test execution status** (a passing GAP-assertion test confirms the gap and is
never promoted to PASSABLE). New `CMS-0057-F Acceptance Evidence` workflow runs
the suite, generates the evidence, writes a job summary, and uploads the
`cms0057-acceptance-evidence-<sha>` artifact; it fails on test failure or
manifest/test drift. Evidence contains only synthetic identifiers, repository
metadata, and test results — no PHI, tenant data, secrets, or QNXT field
mappings. Generator has its own unit tests (`tools/Cms0057Evidence.Tests`). No
runtime service behavior changed.

### Cloud Health Office as the native CMS-0057-F authorization backend

Made Cloud Health Office the authoritative (Replace-mode) backend for the
prior-authorization CMS-0057-F vertical slice, distinct from external-core
(Augment-mode) integration. New authorization-service backend seam:
`Backends/IAuthorizationBackend` selected by operating mode via
`AuthorizationBackendSelector` (`Cms0057:Authorization:OperatingMode`,
default Replace). `ChoAuthorizationBackend` (Replace) is the CHO-native system
of record — a thin application layer over the existing `IAuthorizationRepository`
(Cosmos/Mongo) that persists submission, retrieval, status/decision lifecycle,
stable id, and an append-only `Authorization.StatusHistory`.
`QnxtAuthorizationBackend` (Augment) is a documented stub (throws; no fake SOAP)
selected only when configured — never a silent fallback to CHO; selection fails
loudly if the configured external backend is unregistered. Replaces PR #1143's
flat `IAuthorizationAdapter`. `AuthorizationsController` routes create through
the selected backend and exposes `GET /api/authorizations/backend-status`
(active mode/backend, no sensitive config). Reuses the OperatingMode engine's
`EngineOperatingMode`. The FHIR/PAS layer is unchanged and depends on no
vendor-specific abstraction.

The acceptance suite now distinguishes **product capability** (CHO Replace) from
**integration capability** (QNXT Augment): PAS-03 is product PASSABLE on
`ChoAuthorizationBackend` (exercised via an in-memory repository *fixture* so the
real production backend, not an acceptance-only path, is proven) and integration
GAP on QNXT Augment. Scenarios carry `[Trait("Backend","Replace"|"Augment")]`.
METRICS-01 product moved PARTIAL → PASSABLE (metrics derive from the persisted
CHO record). Inventory and the public acceptance page now score the two
dimensions separately and clarify Demo (synthetic) vs Replace (CHO authoritative)
vs Augment (external core). No runtime behavior change to shipped services beyond
the additive backend routing (Replace is the default and preserves prior
behavior).

### CMS-0057-F acceptance scenario suite

Executable acceptance harness (`tests/Cms0057Acceptance.Tests/`, in
`cloudhealthoffice-main.sln`) proving the CMS-0057-F scenario set against the
real C# services in Demo/Cho mode. Scenarios are tagged
`[Trait("Scenario","…")]` (PAS-01..08, PROV-01..03, P2P-01..04, PAT-01..03,
SEC-01, CONSENT-01, METRICS-01), each with a happy path and, for prior auth, a
negative path. GAP scenarios are tests that assert the still-unimplemented seam
(the QNXT source-system adapters, the out-of-scope Payer-to-Payer surface, the
absent drug-exclusion and retention paths) rather than papering over stubs.
Added `IAuthorizationAdapter` + `QnxtAuthorizationAdapter` documented stub in
authorization-service (mirrors the existing `Qnxt*Adapter` pattern) so the
PAS-03 QNXT create-auth seam is explicit and testable; it throws
`NotImplementedException` and is not wired into DI. Honest PASSABLE / PARTIAL /
GAP inventory and traceability table at
`docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md`; public definition-of-done
guide at `src/site/insights/cms-0057-f/acceptance-scenarios.html`. No change to
runtime behavior of shipped services.

### Layer 1 commercial packet (first-customer motion)

Founding-partner CMS-0057-F Compliance Accelerator offer ($90k / 6–8 weeks), CISO diligence binder (BAA template, security one-pager, adapter-status table, data-handling rules, 25-name target list), and a labeled synthetic demo tenant. `fhir-service` now exposes `GET /fhir/r4/adapter-status` and stamps `X-CHO-Adapter-Mode` / `X-CHO-Data-Class` / `X-CHO-Adapter-Label` on every response so mock adapters cannot look live.

### 835 remittance payment posting

`IRemittancePoster` posts a stored, matched 835 (`AvailableForPosting`) onto
claim financials and member benefit accumulators and marks the receipt
`Posted`. Source of the ERA is the remittance store — this does not invent
835s, change 277CA or 276/277, or reconcile EFT. Tenant comes from the
matched transmission. Duplicate posts replay. Failed claim or accumulator
writes abort without marking `Posted`. Gateway-only claims (no domain
claim) skip the claim sink. Accumulators use 835 PR deductible/copay/
coinsurance deltas with AdjustmentId `835|{remittanceId}|{claimId}`, not
`claims.finalized.v1`. Development: `POST /api/dev/gateway/remittance/{receiptId}/post`.

### Claim intelligence API

Vendor-neutral `IClaimIntelligenceComposer` composes 837 submission, 277CA
acknowledgment, 276/277 claim status, 275 attachments, and 835 remittance
into a tenant-scoped read model. Lifecycle status is derived without letting
one transaction overwrite another (277CA accepted is not paid; 276/277 paid
does not invent an 835). Financial and attachment summaries are
informational. Timeline event ids are stable, so duplicate deliveries do not
duplicate history. `GET /api/claims/{claimId}/intelligence`. The view is not
the system of record and does not post payment.

### 835 ERA remittance ingestion through Stedi

Vendor-neutral `IRemittanceGateway.RetrieveRemittanceAsync` with canonical
`GatewayRemittance`. Stedi transport is the 835 ERA Report
`GET https://healthcare.us.stedi.com/2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/835`
after webhook or poll discovery. `IRemittanceProcessor` matches claims
deterministically (payer claim control number, then patient control number),
persists receipts, and emits identifier-only events. It does not post
payment, change 277CA, or overwrite 276/277 status. Development:
`POST /api/dev/gateway/remittance`.

Contract-tested against Stedi's documented 835 API; live ERA retrieve pending
production/test capability.

### 276/277 claim status inquiry through Stedi

Vendor-neutral `IClaimStatusGateway.CheckClaimStatusAsync` with canonical
`ClaimStatusRequest` / `ClaimStatusResponse`. Callers pass `ClaimId` or
`TransmissionId`; the coordinator derives payer, provider, subscriber, dates,
and control numbers from the original 837 snapshot and from a matched 277CA
payer claim control number when present. Stedi transport is Real-Time Claim
Status JSON `POST https://healthcare.us.stedi.com/2024-04-01/change/medicalnetwork/claimstatus/v2`.
276/277 status is a separate dimension from 277CA acknowledgment,
adjudication, and 835 payment. HTTP 200 with no matching claim is a business
`NoRecordFound`, not a transport failure. Mock returns deterministic
statuses for tests. Development:
`POST /api/dev/gateway/claims/{transmissionId}/status`.

Stedi test keys are not supported for this endpoint. Contract-tested against
the documented API; live inquiry pending production/test capability.

### Payer-side inbound 275 claim attachment receiver

Vendor-neutral `IClaimAttachmentReceiver` so Cloud Health Office can receive
a 275-equivalent attachment as the payer. Distinct from outbound
`IClaimAttachmentGateway`. Canonical `InboundClaimAttachment`, deterministic
claim/service-line matching, SHA-256 content store, durable receipts with
outbox, quarantine for unmatched attachments. Development:
`POST /api/dev/payer/claims/{claimId}/attachments`.

Stedi inbound payer-side 275 is **adapter-ready / pending Stedi payer
connectivity**, not implemented. Raw X12 275 ingress is deferred. Receipt
does not adjudicate or pay the claim.

### 275 claim attachment submission through Stedi

Vendor-neutral `IClaimAttachmentGateway.SubmitAttachmentAsync` with canonical
`ClaimAttachmentSubmissionRequest`. Bytes live in `IClaimAttachmentContentStore`
(existing `IDocumentStore` / Azure Blob when configured) as a content
reference plus SHA-256 — never on the claim aggregate. Attachments associate
deterministically to an existing 837 transmission (optional service line).
Stedi transport is Create Claim Attachment JSON
`POST https://claims.us.stedi.com/2025-03-07/claim-attachments/file` plus PUT
to the pre-signed URL. Unsolicited 275 only. MIME/size validated before
send. Attachment lifecycle is independent of 837 / 277CA / adjudication /
payment. Idempotency is
tenant+transmission+attachment+checksum+type+line+version.

Synchronous gateway acceptance is not payer review or claim payment. Live
275 is not claimed for sandbox accounts. Development:
`POST /api/dev/gateway/claims/{transmissionId}/attachments`.

### 277CA acknowledgment production hardening

Durable Mongo persistence for transmissions, 277CA acknowledgments, outbox,
and poll cursors. Non-Development hosts fail closed unless Mongo is
configured. `TryCreateAsync` is unique-index atomic. Outbox publication is
retried by a hosted dispatcher. Transmission state transitions are guarded
so malformed/duplicate events cannot rewind or overwrite a completed 277CA
outcome. Same-key 837 submit after 277CA is a replay.

### 277CA claim acknowledgment lifecycle through Stedi

Vendor-neutral `GatewayClaimAcknowledgment` plus `IClaimAcknowledgmentGateway`
retrieve and `IClaimAcknowledgmentProcessor`. Stedi discovers 277CAs via
`transaction.processed.v2` webhooks or Poll Transactions
(`core.us.stedi.com/2023-08-01`) and retrieves JSON from
`GET /2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/277`.
Acknowledgments match deterministically to `#1111` transmission records.
Tenant comes from the matched transmission. 277CA accepted/rejected stays
separate from adjudication and payment. Duplicate webhooks are idempotent.

Stedi does not HMAC-sign claim-response webhooks; CHO authenticates the
configured credential-set header. Live 277CA testing is not claimed for
sandbox accounts.

Development: `POST /api/dev/gateway/claims/{transmissionId}/277ca`.
Production webhook: `POST /api/integrations/stedi/claim-responses`.

### Outbound 837 claim submission through Stedi

`IClaimSubmissionGateway.SubmitClaimAsync` is a real capability. Mock and
Stedi implement 837P / 837I / 837D against Stedi's documented JSON APIs
(`professionalclaims/v3`, `institutionalclaims/v1`, `dental-claims`).
Canonical `GatewayClaimSubmissionRequest` stays vendor-neutral. Payer
readiness reuses `IPayerReferenceService`. Durable
`IClaimTransmissionStore` records transmission state separately from
adjudication/payment. Idempotency is tenant+claim+version+type+frequency.

Synchronous gateway acceptance is not 277CA, adjudication, or payment.
Live 837 calls are not claimed for sandbox accounts (Stedi test claims
require a production-account test key). Development:
`POST /api/dev/gateway/claims`.

### Payer-side eligibility responder (inbound 270/271)

Vendor-neutral `IEligibilityResponder` so Cloud Health Office can act as the
payer/information source for an inbound eligibility inquiry. Canonical
`PayerEligibilityInquiry` / `PayerEligibilityResponse`, exact-match member
and dependent resolution, read-only coverage / benefit / accumulator access,
and a Development-only `POST /api/dev/payer/eligibility` ingress.

Stedi does not currently document a self-service inbound 270 payer-hosting
API. The Stedi inbound adapter is **adapter-ready / pending Stedi payer-side
connectivity**, not implemented. Existing outbound `IEligibilityGateway` /
Stedi eligibility is unchanged.

### Stedi dependent eligibility (270/271)

Canonical `GatewayEligibilityPerson` subscriber/patient model. Dependent
inquiries emit Stedi `dependents[]`; subscriber-only requests do not. Opt-in
live sandbox smoke covers the documented UHC 87726 John/Jane Doe Active
Coverage path.

### Stedi payer reference directory

Canonical, vendor-neutral payer identity for Cloud Health Office. Stedi List
Payers JSON (`GET https://payers.us.stedi.com/2024-04-01/payers`) synchronizes
into `IPayerReferenceService`. `StediHealthcareGateway` resolves eligibility
payers through that service; `PayerMap`/`TenantPayerMap` are deprecated
fallbacks. Arbitrary payer ids are no longer passed through to Stedi.

---

## [4.4.0] - May 2026

### Claims Phase 1 — Closure

Closes the Cloud Health Office Claims-domain Phase 1 effort spanning 14 capabilities (5.1a–5.12b). Phase 1 delivers a full claim lifecycle end-to-end: submit → adjudicate (7-stage pipeline: Scrubbing 100 / Network 200 / BenefitCalculation 300 / NCCI 400 / CoB 500 / AI Examination 600 / Persistence 999) → pay (operator-initiated batched 835) → adjust (re-adjudication via predecessor chain) → reverse (operator-initiated batched negative 835).

**No new functionality.** This release is documentation-driven: capability matrix, end-to-end narrative, architectural-pattern index, Phase 2 backlog catalog, CMS-0057-F readiness assessment, canonical V1 API surface reference, and a portfolio module-status register.

#### Capabilities closed (PRs)

- **5.1a** ([#725](https://github.com/aurelianware/cloudhealthoffice/pull/725)) — Claim Identity & Versioning (versioning fields + Mongo event chain)
- **5.1b** ([#743](https://github.com/aurelianware/cloudhealthoffice/pull/743)) — Cosmos partition-key migration to `/tenantId`
- **5.2** ([#728](https://github.com/aurelianware/cloudhealthoffice/pull/728)) — Adapter pattern foundation
- **5.3** ([#729](https://github.com/aurelianware/cloudhealthoffice/pull/729)) — Claim Submission API (canonical V1 surface)
- **5.4** ([#734](https://github.com/aurelianware/cloudhealthoffice/pull/734)) — Pre-adjudication scrubbing + claims-scrubbing-service decommission
- **5.5** ([#731](https://github.com/aurelianware/cloudhealthoffice/pull/731), [#732](https://github.com/aurelianware/cloudhealthoffice/pull/732)) — Adjudication pipeline foundation
- **5.6** ([#733](https://github.com/aurelianware/cloudhealthoffice/pull/733)) — Network & credentialing enforcement
- **5.7** ([#736](https://github.com/aurelianware/cloudhealthoffice/pull/736)) — NCCI / MUE edits enforcement + projection bypass extension
- **5.8** ([#737](https://github.com/aurelianware/cloudhealthoffice/pull/737)) — Coordination of Benefits + Phase 2 hook stub
- **5.9** ([#738](https://github.com/aurelianware/cloudhealthoffice/pull/738)) — AI-Backed Examination pipeline stage
- **5.10** ([#740](https://github.com/aurelianware/cloudhealthoffice/pull/740)) — Operator-initiated batched 835 remittance + cross-service finalize
- **5.11** ([#739](https://github.com/aurelianware/cloudhealthoffice/pull/739)) — FHIR ExplanationOfBenefit projection
- **5.12a** ([#741](https://github.com/aurelianware/cloudhealthoffice/pull/741)) — Adjustment Workflow chain + re-adjudication
- **5.12b** ([#742](https://github.com/aurelianware/cloudhealthoffice/pull/742)) — ReversalRun batched 835 reversal + lifecycle wiring

#### Documentation surfaces shipped (this release)

- `docs/architecture/claims-phase-1-closer.md` — Phase 1 closer narrative (capability matrix, end-to-end lifecycle, 14-pattern architectural index, diligence-readiness checklist)
- `docs/roadmap/claims-phase-2-backlog.md` — Phase 2 backlog (48 items across 10 categories: inbound EDI, FHIR completeness, CMS-0057-F, COB priorEob, AI examiner, cross-service event-stream depth, operational, trading-partner transmission, reference-data workflows, infrastructure follow-ups)
- `docs/compliance/claims-cms-0057-f-readiness.md` — CMS-0057-F readiness posture (Phase 1 shipped vs Phase 2 required vs January 2027 mandate)
- `docs/api/claims-v1-surface.md` — canonical V1 API surface (8 controllers / 47 verbs across claims-service + payment-service customer-facing surfaces)
- `docs/status/MODULE-STATUS.md` — portfolio module-status register (initialized at Claims Phase 1 close; format mirrorable for future service-level closures)

#### Closer pattern established

Claims 5.13 establishes the **closer pattern** for service-level / domain-level Phase 1 / Phase 2 closures across Cloud Health Office. Future Provider Phase 2, BenefitPlan Phase 2, and other domain closures can mirror the structure: capability matrix → operational narrative → pattern index → phase boundary → diligence-readiness posture, with separate registries for backlog, compliance posture, and API surface.

#### No code changes

OpenAPI / Swagger surfaces continue to be served by both claims-service (via shared `AddChoInfrastructure`) and payment-service (via direct `AddSwaggerGen`) in development environments. payment-service Swagger pattern parity migration and XML-doc-driven Swagger surface enrichment are tracked as Phase 2 follow-ups.

#### Outstanding follow-ups (post-close)

- Legacy `Claims` Cosmos container deletion (~30-day retention window from 5.1b cutover; Bicep PR)
- Phase 2 sequencing per `docs/roadmap/claims-phase-2-backlog.md`. Primary near-term driver: CMS-0057-F unauthenticated patient access (January 2027 mandate).

## [4.3.0] - March 2026

### Capitation Service — PMPM Provider Payments

New microservice enabling per-member-per-month (PMPM) capitation payments from health plans to capitated providers. Structurally mirrors premium-billing-service (which collects premiums FROM sponsors) but pays TO providers.

**New Service: `capitation-service`**
- **CapitationContract** — provider agreements with 12-tier age-sex rate schedules, risk adjustment (HCC/RAF), quality withhold percentages, incentive pools, per-member and aggregate stop-loss thresholds
- **CapitationRunService** — monthly batch orchestration that fetches PCP panel rosters from coverage-service, risk scores from risk-adjustment-service, calculates proration for mid-month adds/terms, applies withholds, and generates provider payment statements
- **CapitationStatement** — provider-facing payment detail with member-level line items (base PMPM, risk score, adjusted PMPM, proration factor, gross/withhold/net), retroactive adjustments, and RecalculateTotals()
- **CapitationDisbursementService** — EFT payment lifecycle supporting NACHA ACH credits, Stripe Connect transfers, and paper checks, with ACH return handling (R01-R29 codes) and auto-retry logic
- **CapitationEraService** — X12 005010X221A1 835 ERA generation for capitation payments (CLP02=22, CLP06=CP, no SVC service lines, CAS CO-45 for withholds, PLB with WO/72/L6/FB adjustment codes)
- **NachaCreditFileService** — NACHA credit file generation (transaction codes 22/32 for checking/savings credits, SEC code CCD, service class 220, entry description CAPITATION)
- **StripeConnectService** — Stripe Transfer API integration for Connected Account payouts with webhook processing (transfer.created, transfer.reversed, payout.paid, payout.failed)
- Dual Cosmos DB / MongoDB repositories (8 files) with tenant isolation
- Kubernetes deployment manifest, Dockerfile, docker-compose entry (port 5012)

**Supporting Service Changes:**
- **coverage-service** — PcpNpi, PcpName, PcpAssignmentDate, PcpAssignmentMethod, PreviousPcpNpi fields on Coverage model; new PcpAssignmentMethod enum (AutoAssigned, MemberSelected, PlanDefault); `GET /api/v1/coverage/by-pcp/{npi}` endpoint for panel roster queries; compound indexes on (TenantId, PcpNpi, Status)
- **provider-service** — ProviderBankAccount model with EFT/Stripe Connect/check disbursement support, W-9/1099 compliance fields; DisbursementMethod, BankAccountType, TaxIdType enums; `GET/PUT /api/providers/npi/{npi}/bank-account` endpoints

**Portal — Capitation Management (3 new pages):**
- Capitation Contracts — data grid with contract#/provider/type/LOB/status/tiers/withhold, inline rate tier editor, activate/terminate actions
- Capitation Runs — create/execute runs with period selector, run list with provider count/member-months/net payable/duration, drill into statements
- Capitation Statements — filterable list, member-level breakdown with age/gender/PMPM/risk score/proration/withhold, approve/hold/void workflows, batch "Pay Approved" disbursement
- ICapitationService API client (16 methods), Capitation navigation group in sidebar

**Seed Data:**
- `seed-capitation.sh` — 3 demo contracts, 20 member PCP assignments, completed capitation run
- `seed-capitation-pcp-assignments.js` — mongosh script for Coverage.PcpNpi updates

**Tests: 176 new (163 unit + 13 smoke)**
- CapitationRunService, CapitationDisbursementService, CapitationEraService (28 X12 835 tests), NachaCreditFileService, StripeConnectService, all 4 controllers, TenantMiddleware, CosmosSerializer
- WebApplicationFactory smoke tests for full HTTP pipeline

### Metrics

| Metric                | Previous | Current  |
|-----------------------|----------|----------|
| Portal pages          | 47       | 50       |
| Microservices         | 23       | 24       |
| Service interfaces    | 20       | 21       |
| C# application lines  | ~74,800  | ~86,800  |
| Total code lines      | ~192,000 | ~204,000 |
| Automated tests       | 797      | 973      |

---

## [4.2.0] - March 2026

### Portal — Operations Depth

**New Pages:**
- Work Queues — claims examiner workflow with pend queue management by reason (NCCI, missing auth, provider not contracted, COB, medical review), priority tracking, and examiner assignment
- Appeals — search-first appeal tracking with regulatory deadline monitoring (MA 30-day standard, 72-hour expedited), appeal detail dialog with full lifecycle review
- Correspondence — outbound letter queue management (adverse determinations, EOBs, RFAIs, welcome letters) with RFAI response tracking and deadline monitoring
- Enrollment Operations — daily 834 file processing dashboard with transaction counts, adds/terms, and rejection detail

**Enhanced Pages:**
- Dashboard — added operational alerts (work queue count, pending RFAIs, appeals due), EDI transaction volume summary, and system health indicators
- Member Detail — added Accumulators tab with plan year deductible and OOP max progress bars, service-specific accumulator tracking, and recent claim activity affecting accumulators
- Claims — consolidated ClaimsNew into primary Claims page with advanced search (Claim ID, Member ID, Provider, status, date range)
- Settings — added Operating Mode tab showing per-engine Augment/Replace configuration with mode descriptions

**Portal Architecture:**
- Navigation reorganized into 6 collapsible groups (Operations, Members & Providers, Configuration, Finance, Monitoring, Admin)
- All PHI pages changed from [AllowAnonymous] to [Authorize]
- Search-first pattern enforced on all pages displaying member, claim, or authorization data (HIPAA minimum necessary)
- 5 new service interfaces and implementations (WorkQueue, Appeals, Correspondence, EnrollmentOperations, OperatingMode)
- Dashboard metrics corrected (approval rate and claims trend math)

### Engine & Infrastructure
- ClaimsScrubEngine — C# port of TypeScript validation rules with 20+ rules across 6 categories, wired into AdjudicationController
- OperatingMode engine — per-engine, per-tenant Augment/Replace toggle with AugmentResult<T> and discrepancy logging
- Seed scripts corrected to lowercase database name (cloudhealthoffice)
- Tenant onboarding checklist (TENANT_ONBOARDING_CHECKLIST.md) with Azure AD multi-tenant admin consent flow documentation
- Parameterized seed-demo-data.js (1,345 lines) for any-tenant seeding
- Parameterized seed-tenant.js for tenant provisioning

### Documentation
- README updated with accurate platform metrics and new sections
- Architecture diagram (SVG, Sentinel theme) replacing ASCII art
- Adjudication pipeline diagram showing 8-stage processing with latency
- Operating mode diagram illustrating Augment/Replace architecture
- Channel Partners section for implementation firm distribution model
- Adoption path rewritten to reference Operating Mode by name
- Codebase Scale section with line-count breakdown by language

### Metrics

| Metric                | Previous | Current  |
|-----------------------|----------|----------|
| Portal pages          | 43       | 47       |
| Calculation engines   | 7        | 9        |
| Service interfaces    | 15       | 20       |
| Portal Razor lines    | 14,622   | 16,279   |
| C# application lines  | ~72,900  | ~74,800  |
| Total code lines      | ~160,000 | ~192,000 |
| Total lines (w/ docs) | ~240,000 | ~303,000 |
| Automated tests       | 1,018    | 1,295    |

---

## [4.1.0] - February 16, 2026

### 📚 FHIR API Prominence & Developer Experience

**Commercial positioning and developer discoverability improvements**

Based on comprehensive repository assessment, this release transforms FHIR APIs from buried technical features into prominently featured commercial products.

#### OpenAPI Specifications
- **Patient Access API**: Full OpenAPI 3.1 spec for CMS-9115-F Patient Access API (FHIR R4, US Core 3.1.1+, CARIN BB)
- **Claims Scrubbing API**: Commercial pre-validation API with ROI metrics (95%+ first-pass rates)
- **Provider Access API**: Fixed filename typo (provider-accerss-api.yaml → provider-access-api.yaml)
- **Interactive Documentation**: Swagger UI embedded viewers for all APIs

#### Quickstart Guides (3 new guides)
- **CMS-0057-F Compliance** (15 min): Deploy → Test → Verify compliance before Jan 1, 2027 deadline
- **Patient Access API** (30 min): OAuth setup → Authentication → Build member portal (JS/Python/C# examples)
- **Claims Scrubbing API** (20 min): EHR integration patterns, batch validation, ROI calculator

#### Portal Redesign
- **Homepage**: CMS-0057-F deadline banner (Jan 1, 2027 urgency messaging)
- **Featured Section**: FHIR APIs now 2x grid space in prime dashboard position
- **New Page**: Dedicated API documentation hub (`api-docs.html`) with interactive viewers
- **Navigation**: Added "FHIR APIs" to main menu
- **Commercial Card**: Claims Scrubbing positioned alongside core compliance APIs

#### Impact
- **Before**: No OpenAPI specs, no quickstarts, APIs buried in src/fhir/
- **After**: 3 OpenAPI specs, 3 quickstart guides, prominent portal presence, 15-min onboarding
- **Files Changed**: 16 files, 2,895 insertions
- **Time to First API Call**: Reduced from hours to < 30 minutes

#### Documentation
- [Full Release Notes](docs/releases/v4.1.0-FHIR-API-PROMINENCE.md)
- [OpenAPI Specifications](api/openapi/)
- [Quickstart Guides](api/quickstarts/)

---

## [4.0.0] - February 11, 2026

### 🔒 Zero-Vulnerability Security Hardening

**100% vulnerability elimination** from 86 high-severity issues to absolute zero.

#### Security Fixes
- **CVE-2024-43485**: Fixed System.Formats.Asn1 RCE (8.0.0 → 8.0.1)
- **CVE-2024-21907**: Fixed Newtonsoft.Json deserialization attack (10.0.2 → 13.0.3)
- **Directory.Build.props**: Global transitive dependency enforcement
- **59 package updates**: Azure.Identity, Azure.Core, Microsoft.Azure.Cosmos, MudBlazor, Stripe.net, Swashbuckle, and 50+ more

#### Multi-Tenant SaaS Isolation
- **TenantContextService**: Maps Azure AD tenant → CHO tenant via subscription lookup
- **TenantHttpMessageHandler**: Injects `X-Tenant-ID` header on all backend API calls
- **Portal Isolation**: Prevents cross-tenant data leakage (CRITICAL security fix)
- **Dynamic UI**: Shows actual tenant name with demo/production badges
- **Logout Functionality**: Proper Microsoft Identity sign-out flow

#### Cloud Portability Infrastructure (Feature Branch)
- **CloudHealthOffice.Infrastructure Package**: Cloud-agnostic `IDocumentStore<T>` interface
- **Azure Implementation**: `CosmosDocumentStore<T>` (current production)
- **DigitalOcean Implementation**: `MongoDocumentStore<T>` (65% cost savings)
- **Reference Implementation**: member-service compiles with multi-cloud support
- **GitHub Actions Workflow**: 3-click toggles for Azure/DigitalOcean deployment
- **Status**: Available in `feature/multi-cloud-infrastructure` branch for testing

#### SFTP Trading Partner Integration
- **New Tenant**: clouddentaloffice (dental claims EDI)
- **Endpoint**: LoadBalancer public IP, port 22 (pending DNS: sftp.cloudhealthoffice.com). *Address removed 2026-09-23: the dev subscription was deleted and the IP returned to Microsoft's pool, where it may since have been reassigned.*
- **Folder Structure**: /dental-claims/inbound/837/, /outbound/835/, /outbound/277/
- **Credentials**: Stored in Azure Key Vault

#### Infrastructure Updates
- **Azure Permissions**: Added Application Administrator & User Access Administrator roles
- **Deployment Gates**: Pre-approval checks in GitHub Actions
- **PII/PHI Scanner**: Configured to allow test data patterns
- **Logic Apps Migration**: Disabled deployment (moved to Argo workflows)

#### Package Highlights
- Azure.Identity: 1.12.1 → 1.13.1
- Azure.Core: 1.42.0 → 1.44.1
- Microsoft.Azure.Cosmos: 3.42.0 → 3.45.0
- MudBlazor: 7.20.0 → 8.4.0
- Stripe.net: 46.4.0 → 47.0.0
- Swashbuckle.AspNetCore: 6.5.0 → 10.1.2

#### Breaking Changes
- **Logic Apps Deployment**: Disabled in deploy.yml (use Argo workflows)
- **Multi-Tenant Headers**: Portal now sends `X-Tenant-ID` on all API calls (all services already compliant)

#### Known Issues
- DNS Configuration: sftp.cloudhealthoffice.com never pointed at the LoadBalancer IP
- Mock Data Fallback: Portal shows mock data when backend unavailable (configurable via `Portal.UseMockDataFallback`)
- Stripe.net Warning: NU1603 - Package 46.4.0 not found, resolved to 47.0.0 (non-breaking)

**Production Readiness**: ✅ Multi-Tenant Isolation | ✅ Security Hardening | ✅ HIPAA Controls | ✅ Zero Vulnerabilities

**Documentation**: [RELEASE-v4.0.0.md](RELEASE-v4.0.0.md), [MULTI-CLOUD-SETUP.md](MULTI-CLOUD-SETUP.md), [MULTI-CLOUD-DEPLOYMENT-GUIDE.md](MULTI-CLOUD-DEPLOYMENT-GUIDE.md)

---

## [3.0.0] - February 2026

### Dual-Market Healthcare Integration Platform

Cloud Health Office v3.0.0 is production-ready for both **health payers** (legacy system augmentation) and **healthcare providers** (practice management with direct EDI). This release delivers multi-cloud independence, CMS-0057-F compliance, and commercial launch readiness.

### Added

#### Multi-Cloud & Cloud Independence (December 2025)
- **Kubernetes/Helm Deployment**: Deploy Cloud Health Office to AKS, EKS, GKE, or any Kubernetes cluster
- **Argo Workflows Migration**: Cloud-native workflow orchestration replacing Azure Logic Apps
- **Apache Kafka Integration**: Cloud-agnostic messaging replacing Azure Service Bus
- **HashiCorp Vault Support**: Open-source secrets management as alternative to Azure Key Vault
- **Multi-Cloud Deployment Guide**: Comprehensive documentation for deploying across cloud providers

**Documentation**: [MULTI-CLOUD-DEPLOYMENT.md](./docs/MULTI-CLOUD-DEPLOYMENT.md), [ARGO-MIGRATION-GUIDE.md](./docs/ARGO-MIGRATION-GUIDE.md)

#### Argo Workflows for X12 EDI Processing (December 2025)
- **X12 275 Attachment Ingest Workflow**: Kubernetes-native SFTP polling and processing
- **X12 278 Authorization Request Workflow**: Cloud-agnostic prior auth handling
- **X12 277 RFAI Response Workflow**: Event-driven response generation via Kafka
- **X12 278 Replay Workflow**: Deterministic replay from Kafka offsets
- **Container Images**: X12 parser, encoder, SFTP fetcher, metadata extractor, Kafka publisher
- **Argo Events Configuration**: SFTP polling and Kafka event sources with sensors

**Documentation**: [ARGO-OPERATIONS.md](./docs/ARGO-OPERATIONS.md)

#### Azure Marketplace Readiness (December 2025)
- **Managed Application Plan**: ARM template deploying full Cloud Health Office stack
- **SaaS Plan with Meter-Based Billing**: Per-transaction pricing (837, 278, 275, FHIR API calls)
- **3-Tier Pricing**: Starter, Professional, Enterprise — [Contact sales](mailto:sales@cloudhealthoffice.com) for pricing
- **Partner Center Metadata**: Complete offer listing and marketing assets
- **Legal Documents**: Privacy policy, SLA (99.5%-99.95% uptime), support terms
- **Marketplace Icons**: Sentinel-branded SVG assets for all required sizes

**Documentation**: [marketplace/README.md](./marketplace/README.md)

#### Commercial Launch Materials (December 2025)
- **Sales Product Overview**: 2-page executive summary with competitive positioning
- **ROI Calculator**: TCO analysis and 5-year savings projections
- **Case Study Template**: Reusable template for pilot customer success stories
- **Financial Model**: 3-year projections with unit economics
- **Pitch Deck Content**: 15-slide framework for investor/customer presentations
- **Pilot Program**: 60-day structured pilot with success criteria
- **Sales Email Templates**: 5 targeted outreach templates
- **Marketing Landing Page Copy**: Conversion-optimized content

**Documentation**: sales materials (maintained in a private repository)

#### VC Fundraising Strategy (December 2025)
- **VC Target List**: 12+ prioritized healthcare and SaaS VCs with investment thesis fit
- **Investor One-Pager**: Single-page investment summary
- **Due Diligence Checklist**: Legal, financial, technical, commercial preparation
- **Strategic Partner Targets**: 50+ partners including Microsoft, SIs, technology vendors
- **Investor Meeting Script**: 30-minute pitch framework
- **Warm Intro Templates**: 4 introduction request templates
- **Alternative Funding**: Grants (SBIR), RBF, venture debt, strategic investors
- **PR Strategy**: Thought leadership, podcasts, conferences, LinkedIn

**Documentation**: fundraising materials (maintained in a private repository)

#### Microservices Architecture (December 2025)
- **Eligibility Service**: Azure Container Apps + Dapr with dual X12 270/271 and FHIR interface
- **ClaimRiskScorer Azure Function**: ML-powered fraud/abuse scoring (0-100) with PyTorch
- **Provider Directory API Logic App**: FHIR endpoints with NPPES NPI integration
- **Prior Auth API Logic App**: Da Vinci PAS CDex flow with 72-hour SLA tracking
- **Cosmos DB Integration**: PriorAuthorizations and ProviderDirectory containers

**Documentation**: [services/eligibility-service/README.md](./services/eligibility-service/)

#### CMS-0057-F Compliance Dashboard (December 2025)
- **Azure Monitor Workbook**: Real-time compliance metrics visualization
- **Patient Access API Tracking**: Enablement percentage with daily trends
- **Prior Auth SLA Monitoring**: 72-hour urgent and 7-day standard response tracking
- **Error Rate Analysis**: Transaction-level error tracking for 270/271, 278, 837
- **PHI Audit Trail**: Security operations monitoring via Application Insights

**Documentation**: [docs/AZURE-MONITOR-DASHBOARDS.md](./docs/AZURE-MONITOR-DASHBOARDS.md)

#### Migration Wizard (December 2025)
- **Blazor Web App**: `/tools/migration-wizard` for legacy system migration
- **Claims Backend SOAP Integration**: Paginated export via Open Access APIs
- **Cosmos DB Export**: Batch upsert for Members, ProviderDirectory, BenefitPlans
- **Mapping Report Generator**: 95%+ auto-match with field-level validation
- **One-Click API Cutover**: Routing key flip via Azure API Management
- **Azure Key Vault Integration**: Secure credential management

**Documentation**: [tools/migration-wizard/README.md](./tools/migration-wizard/)

#### 2026 Product Roadmap (December 2025)
- **Quarterly Milestones**: Q1-Q4 2026 with CMS compliance timeline
- **Microservice Releases**: eligibility-service v2.0, prior-auth-service v2.0, claims-service v1.0, remittance-service v1.0
- **Community Targets**: 500→7,500 GitHub stars, 15→150 contributors
- **OKRs**: Measurable success criteria for compliance, adoption, community, and AI

**Documentation**: [ROADMAP-2026.md](./ROADMAP-2026.md)

#### CMS-0057-F Whitepaper (December 2025)
- **Executive Whitepaper**: 7-page document for payer CIOs/CTOs
- **ROI Analysis**: 522% Year 1 ROI, 4.2-month payback period
- **TCO Comparison**: $16.7M legacy vs $2.6M Cloud Health Office (5-year)
- **Implementation Roadmap**: 12-16 week phased timeline
- **Mermaid Visualizations**: Gantt charts, TCO comparison, cost breakdown

**Documentation**: [docs/WHITEPAPER-CMS-0057-F-COMPLIANCE.md](./docs/WHITEPAPER-CMS-0057-F-COMPLIANCE.md)

#### Community Governance (December 2025)
- **CONTRIBUTING.md**: Enhanced with DCO and CLA instructions
- **CODE_OF_CONDUCT.md**: Contributor Covenant 2.1
- **GOVERNANCE.md**: Steering committee election process
- **Issue Templates**: Feature request and bug report YAML forms
- **PR Automation**: Auto-labeling and reviewer assignment workflows

#### Platform Improvements (December 2025)
- **Vendor-Agnostic Refactoring**: Removed 1,295 vendor-specific references across 185 files
- **Container Build Workflow Fix**: Corrected image tags for vulnerability scanning
- **patient_access_api Workflow Fix**: Added missing `kind` and `parameters` keys

### Changed

- Updated README.md with Kubernetes deployment badge and dual architecture options
- Updated ARCHITECTURE.md with deployment options section
- Updated ROADMAP.md to reflect multi-cloud strategy progress (40% complete)
- Helm charts updated with HashiCorp Vault integration settings

### Fixed

- Container build workflow image tag mismatch for Trivy scanner
- patient_access_api workflow.json missing required keys
- PHI compliance issues with HTTPS enforcement for Vault URLs

### Security

- Storage Account networkAcls defaultAction set to "Deny" for HIPAA compliance
- Key Vault networkAcls defaultAction set to "Deny" for HIPAA compliance
- Managed Identity exclusively used for Cosmos DB/Event Grid access (no keys)
- All 424 tests pass with zero security vulnerabilities

### PRs Merged (17 since v2.0.0)

| PR | Title | Category |
|----|-------|----------|
| [#116](https://github.com/aurelianware/cloudhealthoffice/pull/116) | Remove vendor-specific references | Platform |
| [#115](https://github.com/aurelianware/cloudhealthoffice/pull/115) | Add multi-cloud deployment documentation and HashiCorp Vault integration | Multi-Cloud |
| [#114](https://github.com/aurelianware/cloudhealthoffice/pull/114) | Fix image tag mismatch in container build workflow | CI/CD |
| [#113](https://github.com/aurelianware/cloudhealthoffice/pull/113) | Migrate X12 EDI processing to Argo Workflows and Kafka | Multi-Cloud |
| [#112](https://github.com/aurelianware/cloudhealthoffice/pull/112) | Add VC fundraising strategy and materials | Commercial |
| [#111](https://github.com/aurelianware/cloudhealthoffice/pull/111) | Add comprehensive commercial launch materials | Commercial |
| [#110](https://github.com/aurelianware/cloudhealthoffice/pull/110) | Enhance CMS-0057-F whitepaper with ROI analysis and visualizations | Documentation |
| [#109](https://github.com/aurelianware/cloudhealthoffice/pull/109) | Add CMS-0057-F compliance whitepaper for payer executives | Documentation |
| [#108](https://github.com/aurelianware/cloudhealthoffice/pull/108) | Add 2026 product roadmap with CMS compliance milestones | Roadmap |
| [#107](https://github.com/aurelianware/cloudhealthoffice/pull/107) | Add community governance files, issue templates, and PR automation | Governance |
| [#106](https://github.com/aurelianware/cloudhealthoffice/pull/106) | Add Blazor migration wizard for legacy platforms to Cloud Health Office | Tools |
| [#105](https://github.com/aurelianware/cloudhealthoffice/pull/105) | Add Azure Marketplace offer structure with managed app and SaaS plans | Marketplace |
| [#104](https://github.com/aurelianware/cloudhealthoffice/pull/104) | Add ClaimRiskScorer Azure Function for 837 fraud/abuse risk scoring | Microservices |
| [#103](https://github.com/aurelianware/cloudhealthoffice/pull/103) | Add eligibility-service with dual X12 270/271 and FHIR interface | Microservices |
| [#102](https://github.com/aurelianware/cloudhealthoffice/pull/102) | Add CMS-0057-F Compliance Dashboard workbook for Azure Monitor | Compliance |
| [#101](https://github.com/aurelianware/cloudhealthoffice/pull/101) | Fix patient_access_api workflow missing required keys | Bug Fix |
| [#100](https://github.com/aurelianware/cloudhealthoffice/pull/100) | Add ProviderDirectoryApi and PriorAuthApi Logic Apps with NPPES integration | Microservices |

---

## [2.0.0] - 2025-11-28

### FHIR Frontier Forge

Complete CMS-0057-F compliance with production-ready FHIR R4 APIs, delivered 18 months ahead of the January 1, 2027 deadline.

### Added

#### V2 Release Notes Infrastructure (November 2024)
- **Release Notes Portal**: New `site/release-notes.html` with delivered features, sandbox testing, and early adopter signup
- **Documentation Updates**: Enhanced CMS-0057-F compliance documentation with post-FHIR implementation status
- **Site Navigation**: Added release notes links across all platform landing pages
- **V2 Announcements**: Updated site/index.html with v2 banners and CMS-0057-F/FHIR API announcements

**Documentation**: [Release Notes](./site/release-notes.html), [CMS-0057-F Compliance](./docs/CMS-0057-F-COMPLIANCE.md)

#### Complete FHIR R4 API Coverage (November 2024)
- **X12 837 → FHIR Claim**: Professional, Institutional, and Dental claims with Da Vinci PDex profiles
- **X12 278 → FHIR ServiceRequest**: Prior authorization with Da Vinci PAS/CRD compliance
- **X12 835 → FHIR ExplanationOfBenefit**: Remittance advice with complete adjudication details
- **X12 275 → FHIR DocumentReference**: Clinical attachments and supporting documentation
- **CMS-0057-F Compliance Checker**: Automated validation of data classes and timeline requirements
- **Azure FHIR Validator**: Profile validation integration with Azure API for FHIR
- **US Core + Da Vinci IGs**: Full PDex, PAS, CRD, DTR implementation guide conformance
- **45 Comprehensive Tests**: All FHIR mappers validated with 100% pass rate
- **Zero External Dependencies**: Secure core mappers with no runtime vulnerabilities

**Compliance Status**: Ready for January 1, 2027 CMS-0057-F deadline  
**Documentation**: [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md), [CMS-0057-F-COMPLIANCE.md](./docs/CMS-0057-F-COMPLIANCE.md)

#### Provider Access API (November 2024)
- **Real-Time Patient Data Access**: FHIR R4 API for providers with patient authorization
- **SMART on FHIR Scopes**: `user/*.read`, `system/*.read` for provider/system access
- **NPI-Based Authorization**: Provider identity verification and access control
- **Consent Management**: Patient authorization tracking and revocation support

**Documentation**: [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md#provider-access-api)

#### Payer-to-Payer Data Exchange (November 2024)
- **Bulk FHIR Export**: `$export` operation for efficient data exchange
- **5-Year Historical Data**: Configurable retention via Azure Data Lake lifecycle policies
- **Enrollment-Triggered Transfers**: Automated data exchange on member transitions
- **USCDI v1/v2 Coverage**: Complete data class support for interoperability

**Documentation**: [CMS-0057-F-COMPLIANCE.md](./docs/CMS-0057-F-COMPLIANCE.md#payer-to-payer-api)

#### Config-to-Workflow Generator (November 2024)
- **Zero-Code Payer Onboarding System**: Transform JSON configuration into complete deployment artifacts
- **Interactive Configuration Wizard**: Guided setup experience completing in <5 minutes
- **TypeScript-Based Generator**: 700+ lines of automation code with comprehensive validation
- **30+ Handlebars Template Helpers**: String, array, conditional, JSON, math, date, type checking utilities
- **Workflow Templates**: Automatic generation of Logic App workflow.json files
- **Infrastructure Templates**: Bicep templates with parameters and deployment scripts
- **Documentation Generation**: Payer-specific DEPLOYMENT.md, CONFIGURATION.md, TESTING.md
- **Example Configurations**: Medicaid MCO and Regional Blues templates included
- **23-Test Comprehensive Suite**: All passing with 100% validation coverage
- **CLI Tool**: Command-line interface with generate, validate, template, list commands

**Documentation**: [CONFIG-TO-WORKFLOW-GENERATOR.md](./docs/CONFIG-TO-WORKFLOW-GENERATOR.md), [IMPLEMENTATION-SUMMARY.md](./IMPLEMENTATION-SUMMARY.md)

#### FHIR R4 Integration (November 2024)
- **X12 270 → FHIR R4 Mapping**: Transform eligibility inquiries to Patient & CoverageEligibilityRequest
- **CMS Patient Access API Compliance**: Ready for CMS-9115-F requirements (14 months ahead of roadmap)
- **US Core Implementation**: US Core Patient profile v3.1.1 compliant
- **Standards Support**: HIPAA X12 270 (005010X279A1), HL7 FHIR R4 (v4.0.1)
- **Zero External Dependencies**: Core mapper with no runtime vulnerabilities
- **19 Comprehensive Tests**: 100% pass rate, covers all mapping scenarios
- **Production-Ready Security**: Secure examples using native fetch and Azure Managed Identity
- **Service Type Mapping**: 100+ X12 service type codes supported
- **Subscriber & Dependent Support**: Complete demographics handling

**Documentation**: [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md), [FHIR-SECURITY-NOTES.md](./docs/FHIR-SECURITY-NOTES.md), [FHIR-IMPLEMENTATION-SUMMARY.md](./FHIR-IMPLEMENTATION-SUMMARY.md)

#### ValueAdds277 Enhanced Claim Status (November 2024)
- **60+ Enhanced Response Fields**: Comprehensive claim intelligence beyond basic status
- **Financial Fields (8)**: BILLED, ALLOWED, PAID, COPAY, COINSURANCE, DEDUCTIBLE, DISCOUNT, PATIENT_RESPONSIBILITY
- **Clinical Fields (4)**: Diagnosis codes, procedure codes, service dates, place of service
- **Demographics (4 objects)**: Patient, subscriber, billing provider, rendering provider details
- **Remittance Fields (4)**: Check/EFT details, payment date, trace numbers
- **Service Line Details**: 10+ fields per service line with configurable granularity
- **Integration Flags (6)**: Cross-module workflows for Appeals, Attachments, Corrections, Messaging, Chat, Remittance
- **Unified Configuration**: Complete valueAdds277 configuration in payer config schema
- **Premium Product Capability**: $10k/year additional revenue per payer
- **Provider ROI**: 7-21 minutes saved per claim lookup ($69,600/year for 1,000 lookups/month)

**Documentation**: [VALUEADDS277-IMPLEMENTATION-COMPLETE.md](./VALUEADDS277-IMPLEMENTATION-COMPLETE.md), [ECS-INTEGRATION.md](./docs/ECS-INTEGRATION.md)

#### Security Hardening (November 2024)
- **Premium Key Vault Infrastructure**: HSM-backed keys with FIPS 140-2 Level 2 compliance
- **Private Endpoints**: Complete network isolation for Storage, Service Bus, Key Vault
- **VNet Integration**: Logic Apps deployed in private virtual network
- **PHI Masking**: DCR-based transformation rules for Application Insights
- **Customer-Managed Keys**: Optional BYOK for regulatory requirements
- **Data Lifecycle Management**: 7-year retention with automated tier transitions (Hot → Cool → Archive)
- **Storage Cost Optimization**: 94% reduction ($463/mo → $29/mo) with lifecycle policies
- **HTTP Endpoint Authentication**: Azure AD Easy Auth for replay278 endpoint
- **Audit Logging**: 365-day retention with compliance queries
- **4 Bicep Modules**: keyvault.bicep, networking.bicep, private-endpoints.bicep, cmk.bicep (649 lines)
- **HIPAA Compliance**: 100% technical safeguards (§ 164.312) documented and implemented

**Security Score**: 9/10 (Target achieved)

**Documentation**: [SECURITY-HARDENING.md](./SECURITY-HARDENING.md), [HIPAA-COMPLIANCE-MATRIX.md](./docs/HIPAA-COMPLIANCE-MATRIX.md), [SECURITY-IMPLEMENTATION-SUMMARY.md](./SECURITY-IMPLEMENTATION-SUMMARY.md)

#### Gated Release Strategy (November 2024)
- **Pre-Approval Security Validation**: TruffleHog secret detection, PII/PHI scanning, artifact validation
- **UAT Approval Workflow**: 1-2 required approvers, triggers on `release/*` branches
- **PROD Approval Workflow**: 2-3 required approvers, manual dispatch from `main` only
- **Security Context for Approvers**: Scan results visible before approval decision
- **Automated Audit Logging**: Complete deployment history with compliance queries
- **Communication Strategy**: Stakeholder notification matrix with pre/post-deployment templates
- **Emergency Procedures**: Hotfix approval process with 30-minute SLA
- **Rollback Automation**: Automatic rollback-on-failure for UAT, documented procedures for PROD
- **Health Checks**: Post-deployment validation of Logic Apps, Storage, Service Bus, Application Insights
- **Metrics & Reporting**: Deployment success rate, approval times, rollback incidents

**Documentation**: [DEPLOYMENT-GATES-GUIDE.md](./DEPLOYMENT-GATES-GUIDE.md), [GATED-RELEASE-IMPLEMENTATION-SUMMARY.md](./GATED-RELEASE-IMPLEMENTATION-SUMMARY.md)

#### Onboarding Enhancements (November 2024)
- **Interactive Configuration Wizard**: Step-by-step guided configuration with validation (scripts/cli/interactive-wizard.ts)
- **Synthetic 837 Claim Generator**: PHI-safe test data for 837P and 837I claims (scripts/utils/generate-837-claims.ts)
- **Azure Deploy Button Template**: One-click sandbox deployment via azuredeploy.json
- **E2E Test Suite**: Comprehensive health checks with JSON reporting (scripts/test-e2e.ps1)
- **CI/CD PHI Validation**: 18 automated tests prevent PHI exposure (.github/workflows/phi-validation.yml)
- **Troubleshooting FAQ**: 60+ solutions across 9 categories (TROUBLESHOOTING-FAQ.md)
- **Documentation Suite**: QUICKSTART.md, enhanced ONBOARDING.md with 3 deployment options

**Onboarding Time Reduction**: 96% (2-4 hours → <5 minutes)
**Configuration Error Reduction**: 87.5% (40% error rate → <5%)
**Test Coverage Increase**: 41% (44 tests → 62 tests)

**Documentation**: [QUICKSTART.md](./QUICKSTART.md), [ONBOARDING.md](./ONBOARDING.md), [ONBOARDING-ENHANCEMENTS.md](./ONBOARDING-ENHANCEMENTS.md)

#### Sentinel Branding (November 2024)
- **Complete Visual Identity**: Sentinel logo with holographic/neon circuit veins aesthetic
- **Branding Guidelines**: Comprehensive standards document (BRANDING-GUIDELINES.md)
- **Absolute Black Design**: Primary color palette with neon cyan (#00ffff) and green (#00ff88)
- **Segoe UI Bold Typography**: Consistent font usage across all materials
- **Landing Page Transformation**: Complete redesign with Sentinel aesthetic
- **Repository-Wide Enforcement**: Updated all references and documentation

**Documentation**: [BRANDING-GUIDELINES.md](./docs/BRANDING-GUIDELINES.md), [BRANDING-IMPLEMENTATION-SUMMARY.md](./BRANDING-IMPLEMENTATION-SUMMARY.md)

### Changed

- Enhanced README.md with comprehensive features section and new capabilities
- Expanded QUICKSTART.md with post-v1.0.0 feature details
- Updated DEPLOYMENT.md with security hardening deployment section
- Enhanced DEPLOYMENT-SECRETS-SETUP.md with Key Vault migration procedures

### Fixed

- Null safety improvements in configuration validator
- JSON validation for all generated artifacts
- Workflow structure validation for Logic Apps Standard requirements

## [1.0.0] - 2025-11-21

### The Sentinel Has Awakened

This is the first production release of Cloud Health Office — the source-available, Azure-native, HIPAA-engineered platform that ends decades of payer EDI pain.

### Added

#### Core Platform
- **Multi-Tenant SaaS Architecture**: Configuration-driven platform supporting unlimited health plans
- **CLI Onboarding Wizard**: Complete deployment from worksheet to production in <45 minutes
- **Zero-Code Payer Onboarding**: Add new payers via JSON configuration without custom development
- **Backend-Agnostic Design**: Works with any claims system (core admin systems, custom platforms, modern cloud solutions)

#### EDI Transaction Processing
- **275 Attachments**: Clinical and administrative attachment processing with file validation
- **277 RFAI**: Request for Additional Information outbound workflow
- **278 Authorizations**: Prior authorization requests (inpatient, outpatient, referrals)
- **278 Authorization Inquiry (X215)**: Real-time status checks for existing authorizations
- **278 Replay Endpoint**: HTTP endpoint for deterministic 278 transaction replay
- **837 Claims**: Professional, Institutional, and Dental claims submission support
- **270/271 Eligibility**: Real-time eligibility verification with 6 search methods
- **276/277 Claim Status**: Claim status inquiries with date range filtering
- **Appeals Processing**: Appeals submission and tracking with 8 sub-statuses
- **ECS (Enhanced Claim Status)**: Advanced claim status with extended data and 4 query methods

#### Clearinghouse Integration
- **Clearinghouse Integration**: Native SFTP and API connectivity
- **Change Healthcare Support**: Ready for integration
- **Optum 360 Support**: Ready for integration
- **Inovalon Support**: Ready for integration
- **Direct Payer Endpoints**: Configuration-driven connectivity

#### Security & Compliance
- **Zero-Trust Architecture**: Private-endpoint-only, no public IPs
- **Azure Key Vault Premium**: HSM-backed keys (FIPS 140-2 Level 2)
- **Private Endpoints**: VNet integration for Storage, Service Bus, Key Vault
- **PHI Masking**: DCR-based redaction in Application Insights
- **HIPAA Compliance**: 100% technical safeguards addressed
- **Automated Secret Rotation**: API keys and credentials rotate automatically
- **7-Year Data Retention**: Automated lifecycle management with tier transitions
- **Audit Logging**: 365-day retention in Log Analytics

#### Infrastructure as Code
- **Complete Bicep Templates**: All Azure resources defined in source
- **Logic Apps Standard Workflows**: 15+ production-ready workflows
- **Modular Security Components**: Key Vault, networking, private endpoints, CMK
- **Multi-Environment Support**: DEV/UAT/PROD configurations
- **GitHub Actions Pipelines**: Automated deployment with approval gates

#### Developer Experience
- **Configuration Schema**: JSON Schema Draft-07 with 200+ validation rules
- **TypeScript Interfaces**: Type-safe configuration handling
- **OpenAPI Specifications**: Complete API documentation
- **Example Configurations**: Medicaid MCO and Regional Blues templates
- **Comprehensive Documentation**: 20+ detailed guides

#### claims backend Integration (First in Source-Available)
- **Real-Time Correlation APIs**: Link attachments to claims
- **Appeals Registration**: Direct integration with claims backend Appeals API
- **Authorization Processing**: Complete authorization lifecycle management
- **Eligibility Verification**: Member eligibility checks with retry logic
- **Retry Logic**: 4 retries @ 15-second intervals for API calls

#### Monitoring & Observability
- **Application Insights Integration**: Telemetry and distributed tracing
- **PHI-Safe Logging**: Automated masking of sensitive data
- **Custom Metrics**: Authorization decisions, claim status, appeal tracking
- **Health Checks**: Automated verification post-deployment
- **Dead-Letter Queues**: Failed message handling and replay

### Key Highlights

- **Onboarding time reduction**: 6–18 months (legacy) → <1 hour
- **Professional services cost elimination**: $500k–$2M → $0 (bring-your-own-subscription)
- **First production-grade claims backend REST correlation** in source-available healthcare IT
- **Complete source code transparency**: No black boxes, fully auditable
- **Azure Marketplace ready**: Prepared for Managed Application publishing

### Platform Specifications

- **Deployment Target**: Azure (Logic Apps Standard, Data Lake Gen2, Service Bus)
- **Runtime**: Logic Apps Standard (WS1+ SKU)
- **Storage**: Azure Data Lake Storage Gen2 with hierarchical namespace
- **Messaging**: Service Bus Standard tier with topics
- **Security**: Premium Key Vault with HSM-backed keys
- **Monitoring**: Application Insights with PHI masking
- **Language**: TypeScript (generator), Bicep (infrastructure), JSON (workflows)

### Documentation

Complete documentation suite includes:
- **CONTRIBUTING.md**: Development workflow and setup
- **ARCHITECTURE.md**: System architecture and data flows
- **DEPLOYMENT.md**: Step-by-step deployment procedures
- **SECURITY.md**: HIPAA compliance and security practices
- **TROUBLESHOOTING.md**: Common issues and solutions
- **BRANDING-GUIDELINES.md**: Sentinel brand identity standards

### Breaking Changes

N/A - First release

### Security

- All dependencies audited and up-to-date
- No known vulnerabilities in production dependencies
- HIPAA compliance validated for all PHI handling paths
- Security hardening guide included in SECURITY.md

### Known Limitations

- Azure-only deployment (AWS/GCP support planned for Q1 2025)
- Integration Account X12 schemas must be manually imported post-deployment
- API connections require manual authentication configuration
- Azure AD Easy Auth configuration required for replay endpoints

### Migration Guide

N/A - First release

### Contributors

Special thanks to all contributors who made this release possible.

### License

BSL 1.1 - see [LICENSE](LICENSE) file

---

## The Sentinel Has Awakened

The monolith has landed.  
Legacy EDI integration is now optional.

**Just emerged from the void.**

Star ★ the repo if you believe payers deserve better than 1990s technology in 2025.

---

[4.2.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v4.2.0
[4.1.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v4.1.0
[4.0.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v4.0.0
[3.0.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v3.0.0
[2.0.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v2.0.0
[1.0.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v1.0.0
