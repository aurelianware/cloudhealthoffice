# QNXT connectivity engagement design

Status: discovery design, October 22, 2026. First target: QNXT; licensed
specifications and payer test access are still to be arranged. No live QNXT
connection, certified operation set or production acceptance is asserted here.
Use the [connector onboarding template](CORE-CONNECTOR-ONBOARDING.md) and
[adapter QRE](../features/questionnaires/qre-core-admin-adapter-questions.md).

The [core data and interface specification](../integration/CORE-DATA-CONTRACT.md)
defines vendor-neutral requirements and field-mapping/acceptance deliverables.
The SI must bind these to the payer's actual licensed QNXT interfaces.

## Architecture and interface selection

CHO's native services are the reference backend. A QNXT connector implements the
same domain contracts against the payer's licensed interfaces. Specify ownership
per domain: coexistence does not make both systems authoritative for the same
write. Never use native synthetic data when an incumbent operation fails.

TriZetto Open Access, QXI and EDI/extract refreshes are candidate capabilities to
resolve during discovery, not interchangeable transports. Public Cognizant
material calls QXI **QNXT Extended Integration**; confirm the installed product,
module names and supported integration services with the payer/vendor. Do not
infer REST, SOAP, OAuth, write rights or an operation name from a marketing name.

| Need | Proposed selection rule | Evidence required before implementation |
| --- | --- | --- |
| Current member eligibility and benefit inquiries | Licensed synchronous service where available; otherwise explicitly dated read model | Operation contract, member identity, effective-date semantics, response freshness and outage behavior |
| Provider/network and contract reference data | Initial load plus supported change publishing or scheduled extracts; live reads when needed | Full/delta layouts, deletions, network/contract dates, reconciliation |
| Claims and authorization status/history | Service query plus supported updates/events or extract reconciliation | Version/chain identity, corrections, final/denied/partial status mapping and history |
| Accumulators and time-sensitive decisions | Authoritative supported read for the required date; pend/unavailable if freshness cannot satisfy the decision | Service semantics, plan year versus point-in-time history, concurrent update behavior |
| Submit/update transactions | Only explicitly licensed operations or agreed EDI route | Acknowledgment, idempotency, ownership, rejection and compensation contract |
| Bulk historical hydration and nightly refresh | Agreed EDI transactions or purpose-built extracts per domain | Completeness, watermark, control totals, backdated corrections and deletion semantics |

Nightly EDI is not a universal replica of QNXT. Identify which business data each
transaction actually supplies, and identify gaps requiring another source.
Define acceptable lag separately for each API and workflow. Return source timestamp
and stale/unavailable status when applicable; never present yesterday's snapshot
as a current coverage determination. Separate batch arrival, validation, accepted
load and reconciled completeness checkpoints. An acknowledgment is not proof the
business update has been applied.

## Network and security worksheet

Complete separately for integration test, UAT and production. This is an
engagement requirement checklist, not a claim that every payer needs a VPN.

| Area | Decision and owner to record |
| --- | --- |
| Route | Approved public TLS, private endpoint, site-to-site IPsec VPN or hosting-provider interconnect; payer network and CHO platform owners |
| Private connectivity | Endpoint ownership, non-overlapping CIDRs, approved routes, private DNS, NAT, proxies, egress IP allowlists and exact destination ports |
| Availability | Redundant tunnels where required, failover tests, MTU/MSS, health checks, maintenance windows, recovery time and escalation |
| Boundary | Inbound versus outbound initiation, firewall rules per environment and interface; no blanket core database access |
| TLS | Supported versions, server trust chain, hostname verification, client certificates if required, expiration monitoring and rotation |
| OAuth if supported | Issuer, token endpoint, audience, scopes, grant, client authentication method, token lifetime, rotation and revocation owner |
| Other authentication | Vendor-supported SOAP/WS-Security, mTLS, managed gateway or service identity as specified by the licensed interface; no assumed OAuth support |
| Batch transfer | Managed SFTP/object transfer endpoint, host-key validation, encryption requirements, signing, atomic completion signal and retention |
| Operations | Secret manager references, least-privilege identities, per-tenant separation, access audit, sanitized logs, clock synchronization and incident runbook |

Use separate identities and secrets per environment. Never commit credentials or
payer endpoint details to the public repository. External SMART user tokens do
not become core service credentials. A trusted gateway may translate an approved
authentication boundary only with explicit ownership and audit attribution.
Refresh expiring tokens safely; bound retries and do not loop on invalid
credentials. Test expired tokens, wrong audience/scope, revoked certificates,
unreachable tunnels, throttling and malformed upstream responses.

## Canonical connector implementation milestones

1. **Discovery:** obtain licensed specifications, version/config baseline and a
   payer-approved domain/operation matrix. Record all access blockers.
2. **Contract harness:** build synthetic fixtures and transport-independent
   mapping tests for the agreed operations. This proves contracts, not QNXT access.
3. **First vertical slice:** current eligibility query, identity crosswalk,
   effective date and explicit unavailable behavior against the payer sandbox.
   Confirm feasibility before committing to a particular wire adapter.
4. **Hydration/replay:** implement the selected reference-data and history feeds.
   Prove counts, financial totals where applicable, duplicate/out-of-order handling,
   retroactive corrections, restart checkpoints and reconciliation exceptions.
5. **Expand by agreed domain:** claims/status, provider, benefits and authorization
   operations with separate evidence and acceptance. Writes require an explicit
   source-of-truth and retry contract before activation.
6. **Operational acceptance:** failure drills, rotation, upgrades, dual run,
   cutover/rollback and support ownership. Record deployed evidence separately.

Avoid a speculative vendor wire client until access resolves the contract. Reuse
canonical domain models, resilience policies and test harnesses; keep payer
mappings, endpoints, licensed artifacts and deployment configuration separate.

## Ownership and anchor-payer partnership

| Party | Proposed accountability to agree in the SOW |
| --- | --- |
| Anchor payer | Authorized interface access and test data, domain decisions, core/configuration subject-matter experts, business acceptance and cutover approval |
| CHO maintainers | Canonical contracts, reusable connector implementation and regression harness, repository reviews, supported version matrix and release policy |
| Implementation partner | Discovery/mapping, environment wiring and deployment, payer-specific extensions, integration evidence and handoff within agreed scope |
| Core vendor / hosting operator | Licensed interface specifications, supported configuration and access approvals, vendor defect escalation and upgrade notices as contracted |
| Named runtime operator | Monitoring, incident response, reconciliation exceptions, secrets/certificates, replay and restoration; assign explicitly rather than assume CHO operates it |

Seek a payer running QNXT with a funded, bounded first use case and permission to
contribute reusable integration code. Agree delivery funding, maintenance funding,
acceptance milestones, support boundaries and upgrade regression obligations.
Separate reusable connector code from customer configuration and proprietary
specifications. Confirm contribution rights and applicable CHO licensing before
publication; a payer engagement does not automatically grant redistribution rights
to vendor SDKs, schemas, documentation or customer mappings. Preserve a synthetic
regression corpus that can run without payer access. Future Facets and HealthEdge
partners follow the same process with their own licensed connectors.

## Managed Google FHIR store boundary

The optional managed FHIR store is a downstream projection/analytics component;
see [ADR 023](../adr/023-managed-fhir-store-projection.md). The current repository
projects native finalized-claim EOBs. It does not automatically ingest QNXT or
replace native eligibility, adjudication, benefits, authorization or financial
ledgers. A future incumbent feed must first establish its mapping and ownership.
Patient/Coverage/provider reference hydration, projection reconciliation and
rejection/replay operations need their own acceptance evidence.

CHO's authorization, consent, attribution and field-disclosure policies remain
required at the serving boundary. Store-wide export does not establish a
member-scoped CMS bulk export implementation. No live Google deployment health
or production acceptance is inferred from the presence of Terraform or code.

## Public discovery references

- [Cognizant QNXT Open Access](https://www.cognizant.com/us/en/industries/healthcare-technology-solutions/trizetto/core-administration/qnxt/open-access)
- [Cognizant QNXT brochure](https://www.cognizant.com/en_us/trizetto/documents/trizetto-healthcare-products-qnxt-brochure.pdf)

These establish product context only. The payer's licensed version-specific
contracts decide the actual operations, security mechanisms and rights.
