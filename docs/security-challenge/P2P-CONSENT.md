# Native payer-to-payer consent binding challenge

This .NET **8**, 55-scenario profile follows the P2P-01 export / P2P-04 match
remediations (ADR 020 and ADR 022) through real FHIR, member, coverage and claims
service processes. A green result establishes the catalog below, not a complete
platform penetration test or production certification.

## Real boundaries and declared limits

Two FHIR hosts serve separate synthetic source tenants. They share real native
upstreams and an ephemeral MongoDB 7 database, including the durable
`p2p_consent_attestations` collection. Both source tenants contain `shared-member`
with different canaries and different Coverage/EOB IDs; payer A has additional
members, and payer B has a foreign-only member. All members have real projected
coverage and paid-claim fixtures. The runner requires exactly the expected
Patient, Coverage and EOB, including their member references, on successful
exports; an empty or Patient-only package cannot satisfy its positive control.

Two local RSA/JWKS issuers authenticate callers through normal SMART validation.
Only one is configured as the registration authority for `payer_id`; the other
is cryptographically trusted but cannot supply a registered requesting payer.
The registration-authority fixture mints two requesting-payer identities. This
exercises resource-server claim trust, not smart-auth-service registration or
consent collection/provenance. All keys/tokens are ephemeral and loopback only.

Fixtures are inserted directly into Mongo as existing state. Enrollment, claim
intake, adjudication, payment correctness and other cross-service link writes
are not tested. Member-service uses its existing Development-only NoOp identifier
encryptor from an empty owned content root. FHIR, coverage and claims run in
Testing. No production guard or authentication handler is overridden. Unused
upstreams point to closed loopback endpoints; the separate consent-service
registry is not a source for inbound HRex attestations under ADR 022 and is not
exercised. Provider Access opt-outs use another policy and remain unsupported.

The expiry/future scenarios deliberately alter and restore test attestation dates
in Mongo. They establish enforcement of stored time bounds, not live revocation
propagation. A new FHIR process reads a match-created attestation from Mongo while
the original host remains running: this demonstrates reuse by a separate process,
not crash recovery or infrastructure failover.

## Reproduce

Build the runner and actual services from a source checkout using .NET 8:

```sh
dotnet build src/services/fhir-service --configuration Release
dotnet build src/services/member-service --configuration Release
dotnet build src/services/coverage-service --configuration Release
dotnet build src/services/claims-service --configuration Release
dotnet test tools/SecurityChallenge.Tests --configuration Release
dotnet tools/SecurityChallenge/bin/Release/net8.0/SecurityChallenge.dll p2p-consent \
  src/services/fhir-service/bin/Release/net8.0/fhir-service.dll \
  src/services/member-service/bin/Release/net8.0/member-service.dll \
  src/services/coverage-service/bin/Release/net8.0/coverage-service.dll \
  src/services/claims-service/bin/Release/net8.0/claims-service.dll \
  /tmp/cho-p2p-consent-new-evidence
```

Git and initial NuGet/Mongo download access are required; Docker is not. No remote
target URL is accepted. The evidence directory must be new. Exit 0 requires the
exact 55-case inventory, all PASS, and completed execution/cleanup. Setup or case
errors, failures and inconclusive controls return 1; invalid invocation returns 2.
A failed positive control blocks dependent cases as INCONCLUSIVE. Raw responses,
request bodies, tokens and private keys are not retained in evidence.

## Exact scenario catalog (55)

| IDs | Assertions |
| --- | --- |
| `export.inline-control`, `export.cache-control` | A valid inline consent authorizes the native three-resource package; a later request reuses the stored attestation. |
| `match.control`, `match.export-control` | Member match succeeds for an identified, covered member and stores consent that authorizes a subsequent export without inline consent. |
| `cache.foreign-payer`, `.foreign-member`, `.foreign-tenant` | Stored consent authorizes only the requesting payer, member and source tenant it was recorded for (403). The tenant case runs before payer B records any consent. |
| `tenant.b.inline-control`, `.cache-control` | The other source tenant can export its own same-ID member through its own host and stored consent. |
| `tenant.foreign-member`, `member.other-control` | A foreign-only member is hidden (404), while a second local member's own consent works. |
| `binding.export-body-payer`, `binding.match-body-payer`, `binding.header-conflict` | Request bodies and headers cannot substitute the registered requesting payer or source tenant (403). |
| `binding.no-payer`, `.non-registration-issuer` | A backend with no registered payer, or a payer claim from the other trusted issuer, is refused (403). |
| `binding.patient`, `.user`, `.cho`, `.scope`, `identity.anonymous` | Patient/user contexts, CHO staff and unrelated SMART scopes cannot invoke the payer export (403); anonymous requests get 401. |
| `match.missing-consent`, `.foreign-member`, `.demographic-conflict` | A match requires consent and cannot accept cross-tenant or contradictory identity criteria (422). |
| `cache.expired`, `.future`, `.fresh-process` | Stored period bounds are enforced (403); a separate FHIR process can use an in-force, match-created Mongo attestation. |
| `resource.member-confinement`, `.payer-confinement` | Both captured export packages contain exactly the expected Patient, Coverage and EOB and the correct member references; no foreign-payer canary. |
| `resource.cost-redaction` | A real upstream EOB contains payment/total fields, while the exported EOB omits payment, total and header adjudication. This is bounded field coverage, not exhaustive redaction testing. |
| `attestation.attribution` | The recorded attestation has the authenticated actor, expected policy, current recording time and exact fingerprint of the originally supplied consent. |
| `audit.member`, `.coverage`, `.claims` | Each real upstream records successful reads for the authenticated source tenant and actor `fhir-service`. These are upstream access records, not a durable FHIR disclosure-decision audit. |
| `state.domain-unchanged` | All Members, Coverage and Claims documents remain unchanged across the campaign. |
| `export.consent.{inactive,wrong-recipient,wrong-patient,expired,future,regular,nested-deny,wrong-scope,malformed-period,unknown-party}` | Each supplied unusable consent is refused (403), even when an earlier valid attestation exists. |
| `match.consent.{inactive,wrong-recipient,wrong-patient,expired,future,regular,nested-deny,wrong-scope,malformed-period,unknown-party}` | The same ten unusable consents cannot authorize a match or write an attestation (422). |

Every expected denial must return its specified status, disclose no canary, and
leave all non-audit, nonempty Mongo collections unchanged, including attestations,
outboxes and domain events. The final domain assertion independently covers
successful reads. Valid consent acceptance is expected to write an attestation.

## Controlled-defect experiments

From a clean committed checkout with the baseline services/runner built, use
Python 3.12+:

```sh
python3 tools/SecurityChallenge/run-fhir-sensitivity.py p2p-consent \
  src/services/member-service/bin/Release/net8.0/member-service.dll \
  src/services/coverage-service/bin/Release/net8.0/coverage-service.dll \
  src/services/claims-service/bin/Release/net8.0/claims-service.dll \
  /tmp/cho-p2p-consent-new-sensitivity
```

The script requires a complete passing baseline, archives Git HEAD into owned
temporary source trees, and builds two deliberate FHIR defects:

1. **Consent member bypass:** `NamesMember` accepts any nonempty matched-member
   identifier set. Both wrong-patient consent cases must FAIL.
2. **Cached-consent key bypass:** lookup retains only memberId, dropping source
   tenant and requesting payer. Both foreign-payer and foreign-tenant cache cases
   must FAIL.

Each experiment must complete the same exact inventory, exit 1, produce the
specified FAILs, retain the inline/cached export, match/export and payer B positive
controls, and contain no ERROR or INCONCLUSIVE results. Build/startup failure does
not count as detection. Additional FAILs are permitted. Two specified mutations
are not comprehensive mutation coverage; no weakened code is installed in the
working checkout or production services. The original FHIR/SMART audience/scope
mode of the sensitivity runner remains available and is independently checked.

## Evidence and knowledge-base feed

The existing packet schema records the source commit/dirty flag, fingerprints of
all four service assemblies/dependencies, fixture profile, actual Mongo version,
runtime/OS, scenario results, hashed HTTP observations, state/audit assertions,
unsupported areas, reproduction instructions, checksums and review-only KB draft.
The mutation summary binds original/mutated source hashes and nested packet
manifests to the archived source commit. Review binary provenance alongside those
records: a clean runner checkout alone does not prove arbitrary supplied binaries
were built from its source.

CI runs the baseline and both defect experiments without uploading packets or
publishing articles. Review baseline and sensitivity evidence together before
feeding the knowledge base. Expected mutant FAILs are detection evidence, not
platform findings. Real baseline failures require private triage under SECURITY.md.
Optional `CHO_SECURITY_LAB_DEBUG=1` process diagnostics must remain private.

Unsupported: production identity/TLS/encryption; cryptographic consent provenance;
provider-access opt-outs; revocation propagation; outbound payer exchange;
claims/appeals/authorization link writes; financial accounting; per-tenant
databases; live messaging; durable FHIR decision audit; exhaustive EOB redaction;
production ingress and resilience. No compliance or certification claim.
