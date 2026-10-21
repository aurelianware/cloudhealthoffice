# Provider Access security challenge v1

This .NET 8 campaign owns a real FHIR service, member service, consent service,
ephemeral MongoDB 7, and two loopback RSA/JWKS issuers. It accepts built assembly
paths and a new evidence directory, never a remote target. All records are
synthetic. Attribution uses the application's configured panel catalog; no live
provider roster is claimed. Member and consent hosts use their existing
Development NoOp encryption mode. Production startup guards remain intact.

## Reproduce

From the repository root with .NET 8, Python 3.12+, and MongoDB download access:

```sh
dotnet build src/services/fhir-service --configuration Release
dotnet build src/services/member-service --configuration Release
dotnet build src/services/consent-service --configuration Release
dotnet test tools/SecurityChallenge.Tests --configuration Release

dotnet tools/SecurityChallenge/bin/Release/net8.0/SecurityChallenge.dll provider-access \
  src/services/fhir-service/bin/Release/net8.0/fhir-service.dll \
  src/services/member-service/bin/Release/net8.0/member-service.dll \
  src/services/consent-service/bin/Release/net8.0/consent-service.dll \
  /tmp/cho-provider-access-new

# Requires a clean committed checkout and independently passes a baseline first.
python3 tools/SecurityChallenge/run-fhir-sensitivity.py provider-access \
  src/services/member-service/bin/Release/net8.0/member-service.dll \
  src/services/consent-service/bin/Release/net8.0/consent-service.dll \
  /tmp/cho-provider-access-sensitivity-new
```

## Exact catalog: 30 scenarios

| Group | Assertions |
| --- | --- |
| `provider.control`, `provider.system-control`, `provider.mixed-control` | Real native Patient reads succeed for attributed user, system and mixed patient/user scopes. |
| `tenant.b.control` | A second trusted issuer reads the other tenant's record with the same member ID. |
| `attribution.foreign-member`, `attribution.foreign-tenant`, `attribution.lowercase`, `attribution.query-spoof` | Unattributed member reads refuse, including lowercase routing and a misleading provider/patient query. |
| `identity.tenant-header-conflict` | A header cannot replace the validated token's tenant. |
| `memberless.search`, `provider.search-control` | Provider search needs member context; a valid context and `_id` search succeeds. |
| `consent.draft-control`, `consent.activated-denial` | HTTP creation of a draft deny leaves access allowed; HTTP activation blocks the next request on the same FHIR host. |
| `consent.system-denial`, `consent.lowercase-denial`, `consent.search-denial` | The active opt-out covers system scopes, lowercase routes and member search. |
| `consent.patient-self-control`, `consent.other-member-control`, `consent.other-tenant-control` | An opt-out does not block the member's own patient access or attributed provider reads for a different member/tenant. |
| `consent.revoked-optout-control` | HTTP revocation of the deny restores access on the next request. |
| `consent.p2p-isolation-control` | An active P2P deny does not become a Provider Access opt-out. |
| `consent.permit-control`, `consent.withdrawn-permit-denial`, `consent.repermit-control` | An active permit allows, member withdrawal refuses, and a newly activated permit restores access. |
| `consent.deny-overrides-permit` | An active deny overrides an active permit. |
| `registry.pre-outage-control`, `registry.outage-denial`, `registry.outage-after-denial`, `registry.patient-self-control` | After a known successful provider read, stop only the owned consent process. The same FHIR host refuses provider reads for previously allowed and denied members; patient self-access still succeeds. |
| `upstream.audit` | Successful member-service reads carry the tenant-bound `fhir-service` actor in persisted access audit rows. |

Denied FHIR reads require 403, no synthetic canary in the response, and unchanged
non-audit database state. HTTP observations retain status and body hashes, not
raw tokens or response bodies. Consent records are created, activated and revoked
through the real HTTP API; member fixtures use existing-state Mongo seeding.
The sequential lifecycle cases exercise subsequent-request freshness, not
concurrent revocation linearizability or a distributed cache invalidation claim.

The campaign exposed a functional search mismatch: the provider filter did not
recognize Patient `_id` as member context, while the native adapter rejects
`patient` as an unsupported Patient search parameter. The filter now treats one
Patient `_id` as the member, matching direct Patient reads. Repeated `_id` values
refuse. HTTP regression tests cover attributed, unattributed and repeated IDs;
the real campaign verifies a one-entry native search and its opt-out denial.

## Sensitivity and evidence

Disposable `git archive` source copies introduce two separately built defects:
bypass attribution, and bypass the Provider Access consent decision. Exact
mutation anchors must match once. Each defect must fail specified negative
scenarios with healthy positive controls and a complete 30-case inventory.
Build failures, setup errors, blocked tests or missing scenarios cannot count
as detection. Mutants never change the checkout or add production bypass flags.
The consent defect must also expose a previously allowed member during a registry
outage, proving the fail-closed scenario is sensitive.

Schema 1 manifests record source identity, dirty status, service/dependency
fingerprints, runtime and fixture limitations. Checksummed observations, state
hashes, audit assertions, scenario results and review-only knowledge-base drafts
stay private pending review. Exit 0 requires all 30 unique expected cases to
PASS and cleanup to complete; setup/test errors return 1, invalid invocation 2.
CI performs baseline plus both mutation experiments without artifact publication.

This profile does not establish production security or compliance. It does not
cover live roster integration, other resource adapters, registry restart/recovery,
malformed registry responses, consent expiry timing, production encryption,
Kafka delivery, deployed ingress, authorization-server token revocation, or
concurrent/distributed revocation. Follow the repository's SECURITY.md for
private triage of real findings before public disclosure.
