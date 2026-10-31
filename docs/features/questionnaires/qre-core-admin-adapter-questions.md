# QRE Core Administration Adapter Questionnaire (Questions Only)

This questionnaire collects what we need to connect Cloud Health Office (CHO) to a health plan's existing core administration platform (for example TriZetto QNXT, TriZetto Facets, or HealthEdge HealthRules Payer). Every plan runs a different core, a different version and its own configuration, so each legacy-core adapter is built per implementation. The plan's answers define how that adapter is built.

CHO is the reference backend implementation; QNXT, Facets and HealthEdge use separate connector implementations of the adapter contracts. See [ADR 027](../../adr/027-core-adapter-engagement-boundary.md) for the intentional scope and assessment classification.

The questions follow the format of the existing transaction QREs (278, 275). Answers fill in the CHO tenant configuration described in the appendix.

The [core data and interface specification](../../integration/CORE-DATA-CONTRACT.md)
defines the domain data requirements, code integration points, X12/FHIR references
and mapping deliverables for architects and SI teams across all core vendors.

> **Status:** CHO's own adapters and backends (`cho`) are the reference implementation for every domain. The QNXT, Facets and HealthEdge adapters ship as interface stubs, and each one is implemented per engagement from this questionnaire. An unimplemented legacy-core adapter is intentionally scoped engagement work, not a generic CHO product gap: the same workflow runs natively in CHO in Replace mode. Augment (shadow) comparison also depends on the per-engagement adapter, which supplies the core's results for CHO to compare against.

## From questionnaire to implementation

After discovery, complete the [connector onboarding and acceptance checklist](../../implementation/CORE-CONNECTOR-ONBOARDING.md). For the first QNXT engagement, use the [connectivity, security and partnership design](../../implementation/QNXT-CONNECTIVITY-ENGAGEMENT.md); licensed specifications and test access remain pending.
Engineers building the connector should start with the [core adapter implementer guide](../../integration/CORE-ADAPTER-IMPLEMENTER-GUIDE.md), which covers each interface, the CMS-0057-F data path and the conformance tests.
It records the agreed environment/access matrix, domain scope, mapping and
failure contracts, reconciliation controls, traceable acceptance evidence and
cutover/support ownership. QRE answers scope the work; they are not themselves
proof that a connector is implemented or deployed.

## Contact Information and Milestones
- Provide plan contacts for executive sponsor, core platform owner, configuration lead, EDI/integration lead, utilization management lead, interoperability (CMS-0057-F) lead, information security, and claims operations (name, phone, email for each).
- Identify any vendor or BPaaS contacts (core vendor account team, hosting provider, UM vendor, clearinghouse, system integrator) and whether their involvement requires a separate statement of work or license.
- Supply target dates for: questionnaire complete, connectivity in test, read adapters complete, Augment (shadow) start, first Replace cutover, and each later line-of-business cutover.
- Identify any regulatory dates the implementation must meet (for example, CMS-0057-F API compliance dates or a state Medicaid contract date).

## Plan Identification and Implementation Footprint
- List the legal entity and plan names in scope.
- List each line of business (commercial, Medicare Advantage, Medicaid, CHIP, exchange) and the states served.
- Provide approximate membership by line of business and annual claim volume by claim type (professional, institutional, dental, pharmacy if applicable).
- Identify lines of business or programs that are explicitly out of scope (for example, delegated or carved-out benefits).

## Core Platform and Version
Complete one row per environment (production, UAT, test, development).

| Environment | Core platform | Version, release and patch | Hosting (on-premises, cloud, BPaaS) | Database engine and version | Vendor APIs enabled (QXI, FXI, HealthEdge) and version | Database access (views, replica, warehouse) | Network path | Authentication |
|---|---|---|---|---|---|---|---|---|
| Production | | | | | | | | |
| UAT | | | | | | | | |
| Test | | | | | | | | |

- Name the core administration platform and the exact version, release and patch level running in production and in each lower environment.
- Describe the hosting model: plan-managed on-premises, plan-managed cloud, or vendor-hosted / BPaaS.
- List known customizations, extensions or custom database objects, and who maintains them.
- Provide the vendor upgrade schedule and any upgrades planned during the implementation window.
- Identify other platforms that own part of the core data (for example, a separate enrollment system, provider data management system, pricer, claims editing engine or UM system).

## Core Data Access
- State which access methods are licensed and enabled in each environment:
  - **Vendor web APIs:** QXI (QNXT), FXI (Facets), or the HealthEdge APIs. List the modules or services licensed and the version of each.
  - **Vendor open-access APIs:** any other published APIs the core vendor offers, with product names and versions.
  - **Plan-built APIs:** REST or FHIR APIs the plan or a partner already exposes over the core, including existing CMS Patient Access or Provider Directory APIs.
  - **Read-only database access:** views, a reporting replica or a data warehouse, with refresh latency.
  - **Batch extracts:** scheduled files and their layouts.
  - **X12 EDI:** 834, 837, 835, 277, 278 feeds and the gateway that handles them.
  - **Integration platform:** an existing ESB, iPaaS or event stream (for example, Kafka) that publishes core changes.
- Describe the initial historical load: how many years of members, claims and authorizations must be loaded, and in what format.
- Describe how ongoing changes will be detected: change data capture on the database (if the contract allows it), vendor events or change APIs, polling, or batch deltas.
- For each method, state whether it supports reads only or also writes (for example, creating authorizations, loading claims or posting adjustments).
- Confirm whether vendor API or database access requires vendor approval, additional licensing, or affects support terms.
- Provide API documentation (WSDL/OpenAPI), extract file layouts or companion guides for each method.
- Describe network connectivity (site-to-site VPN, private link, SFTP) and any region- or environment-specific endpoints.
- State system availability windows, batch cycle times, blackout periods, and maximum request rates or concurrent sessions for each API.

## Domain Operating Mode Matrix
Operating modes apply differently to processing engines and to data domains, so the matrix is split in two.

**Processing engines** compute a result, so they can run in shadow. *Legacy only* means the core computes and CHO does not. *Augment* means CHO computes in shadow and its results are compared with the core's, while the core stays authoritative. *Replace* means CHO is authoritative.

| Processing engine | Engine today (core, vendor or plan tool) | Starting mode | Target mode | Cutover order and dependencies |
|---|---|---|---|---|
| Claims adjudication (one row per claim type and line of business) | | | | |
| Benefit and cost-sharing calculation | | | | |
| Pricing and fee schedule resolution | | | | |
| Claims edits (NCCI/MUE and commercial edits) | | | | |
| Claims scrubbing (pre-payment validation) | | | | |
| Coordination of benefits | | | | |
| Prior authorization rules | | | | |
| Risk adjustment (HCC) | | | | |

**Data domains** don't run in shadow. The question is which system is the source of truth and which holds a synchronized copy, and whether that changes during the implementation.

| Data domain | Source of truth today | Source of truth at target | Copy direction (core to CHO, CHO to core, or both) | Access method | Sync frequency and latency |
|---|---|---|---|---|---|
| Members and eligibility | | | | | |
| Benefit plans and products | | | | | |
| Providers, organizations and networks | | | | | |
| Provider contracts and fee schedules | | | | | |
| Claims and claim history | | | | | |
| Authorizations | | | | | |
| Appeals and grievances | | | | | |
| Premium billing | | | | | |
| Capitation | | | | | |
| ID cards | | | | | |

- For each engine moving to Replace, state the cutover sequence and dependencies (for example, providers and pricing before adjudication).
- Where CHO's role is read-only (FHIR, CMS-0057-F APIs, analytics), confirm that no write-back to the core is expected.
- Where the source of truth changes, state the date or milestone, and whether updates must flow both ways for a period.

## Transition Ownership (Split Operations)
While some claim types run in CHO and others in the core, some functions span both systems. For each item below, state which system owns it during the transition and how the other system stays in sync.
- **Accumulators:** deductibles, out-of-pocket maximums and benefit limits must reflect claims from both systems. Name the owner, how often each system receives the other's updates, and how timing gaps are handled (for example, two claims for the same member adjudicated in different systems on the same day).
- **Claim history for edits:** duplicate checking, frequency and benefit limits, and NCCI or other edits that look across claims must see history from both systems.
- **Authorization-to-claim matching:** state how claims in either system find authorizations held in the other.
- **Coordination of benefits:** state which system holds other-insurance data and how both systems use it.
- **Payments and finance:** state who owns the payment cycle, the check or EFT run, a single 835 per provider per cycle, general ledger feeds, provider 1099s and overpayment recovery.
- **Adjustments and reversals:** state where an adjustment to a claim originally paid in the other system is processed.
- **Appeals:** state where an appeal of a claim decided in the other system is worked and how an overturn is reprocessed.
- **Member and provider correspondence:** state which system produces EOBs, denial letters and provider notices for each claim type.

## Downstream Consumers
- List every system and report that reads from the core today: data warehouse, state and regulatory reporting, encounter submissions, HEDIS and quality feeds, risk adjustment submissions, finance, member and provider portals, call center tools, letters and correspondence vendors.
- For each, state whether it will read from the core, CHO, or both during the transition, and who updates the feed.
- Identify feeds with contractual or regulatory deadlines (for example, state encounter submission timelines).

## Operations Readiness
- State which system claims examiners, configuration analysts and UM staff will work in during Augment and after each Replace cutover (pend queues, manual review, configuration changes).
- Describe training, desk procedures and support model changes required for each cutover.
- Identify who handles configuration changes in each system while both are live, and how changes are kept consistent (for example, a new fee schedule or benefit change applied to both).

## Contracts and Regulatory
- Confirm what the core vendor contract allows: direct database access, third-party integration, API use, and any restrictions on using vendor data structures. Identify whether vendor approval or a contract amendment is needed.
- For BPaaS or vendor-hosted cores, confirm the hosting provider's process and lead time for connectivity, access and change requests.
- For Medicaid and CHIP lines of business, identify state notification or readiness review requirements for changing claims or core systems, and their lead times.
- For Medicare Advantage, identify any CMS notification or audit considerations for system changes.
- Identify other contractual obligations affected by a core change (delegated vendor contracts, clearinghouse agreements, provider contract terms on payment timing).

## Prior Authorization and Da Vinci Responsibilities (CRD, DTR, PAS)
- Identify the system of record for prior authorizations today (core UM module or a separate UM platform) and the vendor and version.
- For each Da Vinci capability, state who will provide it in production: CHO, the UM vendor, a clearinghouse or intermediary (for example, Availity), the core vendor, or the plan:
  - **CRD (Coverage Requirements Discovery):** CDS Hooks service that tells the provider's EHR whether authorization or documentation is required.
  - **DTR (Documentation Templates and Rules):** questionnaires and rules that collect required documentation, including the source of clinical criteria (for example, MCG, InterQual or plan-authored).
  - **PAS (Prior Authorization Support):** FHIR `$submit` endpoint and its conversion to and from X12 278 where required.
- Identify where authorization rules live (core, UM system, rules engine, policy documents) and how they are maintained.
- If CHO provides CRD, DTR or PAS while the UM system stays authoritative, describe how authorization decisions and status updates flow back to the UM system and to the provider.
- Identify which EHRs and provider organizations will connect first, and whether Epic Payer Platform or other EHR-vendor integration is required.
- Describe current X12 278 volume and channels (portal, EDI, fax, phone) and expected adoption of the FHIR path.
- Identify who owns the other CMS-0057-F APIs (Patient Access, Provider Access, Payer-to-Payer, Provider Directory) and prior authorization metrics reporting.

## Data Domains
For each domain in the matrix, identify the source objects or tables (or API operations), primary keys, required history depth, how often data must be synced, and acceptable latency.
- **Members and eligibility:** subscribers, dependents, coverage spans, PCP assignment, COB and other-insurance records, enrollment transaction source (834 or direct entry).
- **Benefit plans:** plan/product definitions, benefit packages, cost-sharing (deductibles, copays, coinsurance, out-of-pocket maximums), accumulators and their reset rules.
- **Providers and organizations:** practitioners, facilities, groups, networks, affiliations, contracts and the provider identifiers used (NPI, TIN, plan-assigned IDs).
- **Claims:** headers, lines, diagnoses, adjudication results, pend and reason codes, adjustments, reversals and the version history of each claim.
- **Authorizations:** requests, decisions, service lines, units, date spans, status history and decision reasons.
- **ID cards:** card templates by product, fulfillment vendor and trigger events for reissue.

## Configuration Capture
- Describe how fee schedules and pricing are configured (in the core, an external pricer, or both), and how state Medicaid fee schedule updates are applied.
- Identify the claims editing engine and edit sources (for example, NCCI/MUE and any commercial edit vendor), and whether edits run inside or outside the core.
- Provide the list of pend, denial and remark codes in use and their mapping to CARC/RARC codes.
- Describe how adjustments, reversals and appeals overturns are processed and represented in the core.
- Describe the coordination of benefits rules and how other-insurance data is maintained.
- Identify prior authorization rules that affect claims payment and how authorizations are matched to claims.

## Identifier and Code Mapping
- Describe member ID formats, including subscriber/dependent suffixes and any ID changes over time.
- Describe the claim number format and how adjusted or reversed claims are linked to the original.
- Provide the line-of-business, product and plan code values and their meanings.
- Identify any custom code sets that need a crosswalk to standard terminologies.

## Augment (Shadow) Validation
- Confirm which results will be compared between CHO and the core (overall outcome, denial reasons, allowed amount, plan paid, deductible, copay, coinsurance, member responsibility, per-line results).
- Agree on the acceptable discrepancy rate and observation period required before a domain or claim type moves to Replace (CHO's default guideline is under 1% sustained over 30 days).
- Name who reviews discrepancies and signs off on each cutover.

## Security, Privacy and Compliance
- Confirm that the BAA and data use terms covering CHO and its hosting environment are in place.
- Describe required authentication methods for each integration (OAuth 2.0 client credentials, mutual TLS, service accounts) and credential rotation requirements.
- State data residency, encryption and audit logging requirements.
- Identify policies restricting PHI in lower environments, and whether masked or synthetic data must be used for testing.
- Confirm whether any AI-assisted features (such as CHO's advisory claims examiner) are in scope, and the plan's governance requirements for them.

## Testing and Cutover
- Describe the test environment, its refresh schedule, and whether it mirrors production configuration.
- Provide test scenarios or a sample claim set with known outcomes for regression testing.
- Describe production approval and change control steps for each cutover.
- Describe the rollback plan if a domain or claim type must return from Replace to Augment or Legacy only.
- For a full replacement, describe the decommission plan: historical data retention, final data conversion, and the 835 and encounter submission handoff.

## Implementation Outline (Initial View)
This is a starting view of what an implementation involves. Scope and duration are sized from the answers above.

| Phase | What happens | Plan delivers | CHO delivers | Exit criteria |
|---|---|---|---|---|
| 0. Discovery | Complete this QRE; confirm access methods, vendor contract terms, licensing, BAA, state notice requirements and the domain mode matrix | Questionnaire answers, documentation, test access | Gap analysis, adapter design, plan for each domain | Signed scope and domain mode matrix |
| 1. Connect and read | Build read adapters for the domains in scope (members, benefit plans, providers, claims, authorizations) over the chosen access method; load and sync tenant data | Connectivity, credentials, test data | Adapters (QXI, FXI, database or extract based), identifier mapping, sync jobs | CHO data reconciles to the core; FHIR and CMS-0057-F APIs served from synced data |
| 2. Interoperability layer | Turn on CMS-0057-F APIs and the agreed CRD, DTR and PAS responsibilities over the legacy core | UM rules and criteria sources, EHR partners, payer-specific questionnaires | API configuration, rule loading, 278 and PAS mapping, decision write-back where required | Conformance and interoperability tests pass; first providers live |
| 3. Augment | Run CHO in shadow for selected engines and claim types; compare results with the core; agree transition ownership for accumulators, claim history, payments and correspondence | Discrepancy review and sign-off | Legacy-result feed for comparison, discrepancy reporting, configuration fixes | Discrepancy rate below the agreed threshold for the agreed period |
| 4. Replace | Cut over one domain or claim type at a time | Change approval, operations readiness | Cutover runbook, cross-system accumulator and history sync, monitoring, rollback | CHO authoritative with accumulators, payments and downstream feeds reconciled |
| 5. Decommission (optional) | Retire the legacy core for lines of business fully on CHO | Retention and conversion decisions | Final conversion, 835 and encounter handoff | Core retired for those lines of business |

**What drives size and effort:**
- The access method. Vendor APIs (QXI or FXI) are usually the cleanest path. Database views and extracts are faster to start but need more reconciliation.
- How much of the core is customized, and how many lines of business and states are in scope.
- How many domains move beyond Legacy only. Phases 0–2 (Layer 1, Foothold, in `docs/POSITIONING.md`) deliver CMS-0057-F interoperability over the existing core without replacing anything.
- Whether write-back to the core is required (for example, authorization decisions or claim loads).
- How long both systems run side by side. Split operations (shared accumulators, claim history, one payment cycle) are the hardest part of a phased cutover.
- How many downstream feeds have to move.
- Vendor contract terms, licensing, hosting-provider lead times and state readiness reviews, which are often the critical path ahead of any engineering.

## Appendix: How Answers Map to CHO Configuration

| Questionnaire section | CHO configuration |
|---|---|
| Core platform (claims) | Tenant `configuration.claimsPlatform`: `platform` (`cho`, `qnxt`, `facets`, `healthedge`), `apiEndpoint`, `keyVaultSecretName`, `timeoutMs`, `retryCount`, `platformSettings` |
| Core platform (providers) | Tenant `configuration.providerPlatform` (same fields) |
| Core platform (benefit plans) | Tenant `configuration.benefitPlanPlatform` (same fields) |
| Core platform (ID cards) | Tenant `configuration.idCardPlatform` (same fields) |
| Eligibility source | Tenant `configuration.eligibilityPlatform` (same fields) |
| Prior authorization | `Cms0057:Authorization:OperatingMode` (`Replace` or `Augment`) and `AugmentBackend` (for example `qnxt`) |
| Operating mode (processing engines) | Tenant `operatingMode.engines`, keyed by `{claimType}-{lineOfBusiness}` (for example `professional-medicaid`: `augment`) |
| Access method, connectivity, identifiers | Adapter `platformSettings` and the per-engagement adapter build |
| Augment validation | Discrepancy comparison and cutover criteria (see `docs/architecture/OPERATING-MODE.md`) |
| CRD, DTR, PAS | fhir-service CRD, DTR and PAS configuration (see `docs/interop/davinci.md` and `docs/compliance/CMS-0057-F-READINESS-MATRIX.md`) |

Related documents: [Implementation Blueprint](../../implementation/IMPLEMENTATION-BLUEPRINT.md), [worked example](examples/qre-core-admin-adapter-example.md), [claim adapter pattern](../../architecture/claim-adapter-pattern.md), [provider adapter pattern](../../architecture/provider-adapter-pattern.md), [benefit plan adapter pattern](../../architecture/benefit-plan-adapter-pattern.md), [operating modes](../../architecture/OPERATING-MODE.md).
