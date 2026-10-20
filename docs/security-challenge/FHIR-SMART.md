# FHIR / SMART security challenge

This .NET **8** profile exercises actual FHIR and member-service Kestrel processes,
MongoDB 7, and two ephemeral RSA issuers serving public JWKS on loopback. It does
not override authentication handlers or substitute a fake Patient adapter. The
FHIR service retrieves keys over HTTP, validates tokens through its normal
SMART scheme, and calls member-service using its CHO service identity. Issuer A
and B have separate keys, audiences and allowed payer identities; both deliberately
publish the same key ID to exercise issuer-specific key lookup.

## Scope and fixtures

Two synthetic payers share `shared-member`, with different `CANARY-a` / `CANARY-b`
patient names. Payer A has a second member to exercise patient confinement within
a payer. Existing-state member documents are inserted directly into Mongo;
this does not test enrollment/intake or production identifier encryption.

FHIR runs in Testing with explicit ExternalIssuer configuration and the Patient
adapter set to ChoNative. Member-service runs in Development from an owned empty
content root, selecting its existing NoOp identifier encryptor. Neither service's
production guards are modified. Other downstream URLs are closed loopback
endpoints, not live services. The optional coverage/PCP lookup is unavailable;
this profile establishes no PCP/coverage assurance. Shared-database tenant row
filtering is exercised, not per-tenant databases.

Bulk export is currently an in-process job/manifest implementation. The campaign
tests creation, status and cancellation authorization; it cannot demonstrate
protection of exported files that this implementation does not serve. Job polling
and cancellation use the current `$export-status.read` / `.write` scopes in
addition to `$export.read`; no claim of a complete Bulk Data implementation is made.

## Reproduce

From a source checkout, with Git, Python 3.12+ and the pinned .NET 8 SDK:

```sh
dotnet build src/services/fhir-service --configuration Release
dotnet build src/services/member-service --configuration Release
dotnet test tools/SecurityChallenge.Tests --configuration Release
dotnet tools/SecurityChallenge/bin/Release/net8.0/SecurityChallenge.dll fhir-smart \
  src/services/fhir-service/bin/Release/net8.0/fhir-service.dll \
  src/services/member-service/bin/Release/net8.0/member-service.dll \
  /tmp/cho-fhir-smart-new-evidence
```

The output directory must not exist. The runner accepts local assemblies, never a
remote URL. Every service, issuer and database is owned and disposed by the run.
NuGet and the first Mongo binary download require network access. Raw tokens,
private keys and response bodies are not included in evidence. Optional
`CHO_SECURITY_LAB_DEBUG=1` process logs stay in private local diagnostics, never CI
uploads. Keep all unreviewed evidence private.

Exit 0 requires the exact 42-case inventory, all PASS, and completed setup,
execution and cleanup. Invalid invocation returns 2. A failed assertion,
prerequisite, execution error, missing case or inconclusive control returns 1.
Positive-control failures block dependent cases as INCONCLUSIVE. A setup failure
is ERROR, not a successful denial. Denied HTTP requests must return the specified
status, expose no patient canary, and leave non-audit Mongo collections unchanged.
The bulk owner status is independently compared before and after foreign actions
because those jobs are in process memory rather than Mongo.

## Exact scenario catalog (42)

| IDs | Assertions |
| --- | --- |
| `identity.valid-control`, `identity.second-issuer-control` | Real Patient data is readable using each issuer's own key, audience and payer. |
| `identity.anonymous`, `.malformed`, `.wrong-issuer`, `.wrong-audience`, `.expired`, `.future`, `.bad-signature`, `.unsigned` | Invalid credentials return 401 without canary disclosure or domain writes. |
| `identity.issuer-key-confusion`, `.issuer-audience-confusion` | An issuer cannot borrow another trusted issuer's key or audience (401). |
| `identity.issuer-tenant-confusion`, `.header-conflict` | A token cannot exceed its issuer's allowed payer or replace its payer using a request header (403). |
| `scope.unrelated`, `.write-only`, `.lowercase-route` | Patient reads require the applicable read scope, including differently cased routes (403). |
| `scope.scp-control`, `.missing-patient` | `scp` works as a scope claim; a patient scope without patient context is refused (403). |
| `patient.foreign-id`, `.foreign-query`, `.foreign-subject`, `.foreign-beneficiary` | Direct IDs and every supported member parameter retain the patient binding (403). |
| `patient.search-control`, `.foreign-id-search`, `.pagination-confinement` | Searches return exactly the bound member; foreign `_id` is empty; a bounded page retains confinement. |
| `tenant.a.shared-id`, `tenant.b.shared-id`, `tenant.b.foreign-id` | Identical member IDs resolve within the authenticated payer; a foreign-only member is hidden (404). |
| `tenant.a.search`, `tenant.b.search` | Staff searches have the correct payer totals and canaries; payer A follows the actual next-page link and payer B has no next page. |
| `cho.reader-control`, `.permission-denial`, `.bulk-denial` | CHO staff need members:read; TenantAdmin alone cannot invoke SMART-only bulk export (403). |
| `bulk.patient-denial`, `.scope-denial` | A patient scope or unrelated backend scope cannot start a bulk job (403). |
| `bulk.create-control`, `.own-status`, `.foreign-status`, `.foreign-cancel`, `.owner-state-unchanged` | An authorized backend creates/polls its job; another payer cannot read/cancel it (404), and the owner's manifest remains unchanged. |
| `upstream.audit` | Member-service records successful reads with payer A and actor `fhir-service`, providing evidence of the upstream service-identity boundary. |

## Prove that controls detect defects

A green baseline alone is insufficient. From a **clean committed checkout**, run:

```sh
python3 tools/SecurityChallenge/run-fhir-sensitivity.py \
  src/services/member-service/bin/Release/net8.0/member-service.dll \
  /tmp/cho-fhir-smart-new-sensitivity
```

The script first requires a complete passing baseline, then archives the exact
Git HEAD into two temporary source trees. It applies exactly one uniquely matched
source edit per tree, builds FHIR, and reruns the same 42-case campaign:

1. **Scope bypass:** disables the SMART middleware scope refusal. The unrelated,
   write-only and lowercase-route scope cases must FAIL.
2. **Audience bypass:** preserves issuer lookup but admits any presented audience.
   Wrong-audience and cross-issuer-audience cases must FAIL.

A mutant only counts as detected when the campaign exits 1, completes its exact
inventory, contains the specified FAILs, retains valid controls for both issuers,
CHO reads and bulk creation, and has no ERROR or INCONCLUSIVE results. Build or
startup failure never counts as detection. Additional FAILs are allowed because
a deliberately weakened check can affect multiple scenarios. These are two
specified mutations, not comprehensive mutation coverage.

No weakened binary, production bypass flag or source edit is installed into the
working checkout. Temporary source/build trees are removed after each experiment.
The retained `sensitivity-results.json` binds the source commit, original/mutated
source hashes, targeted failures, controls and nested packet manifests. Nested
manifests identify the runner's clean checkout and binary fingerprints; the
sensitivity record explicitly states that the FHIR binary came from mutated
archived source. Build logs and checksummed packets remain private.

## Evidence and knowledge-base feed

Each normal run emits the existing packet shape: manifest, JSONL results,
coverage with unsupported areas, hashed HTTP observations, Mongo state assertions,
upstream audit assertions, findings summary, reproduction instructions, review-only
knowledge-base draft and SHA256 checksums. The manifest includes both service
binary/dependency fingerprints, source identity/dirty flag, actual Mongo version,
runtime/OS and all material lab limitations. A clean runner checkout does not by
itself prove an arbitrary supplied binary was built from that commit; builds and
sensitivity source records must be reviewed alongside the fingerprints.

Combine reviewed baseline and sensitivity packets in the knowledge base. Explain
which deliberate defects were detected, the exact tested source and profile, and
unsupported areas. Mutant FAILs are expected test-sensitivity evidence, not
confirmed platform vulnerabilities. Real baseline failures require private triage
under SECURITY.md before public disclosure. CI automatically runs the profile and
the two mutation experiments but does not upload packets or publish articles.

Not covered: production IdP/TLS/ingress, authorization-server grants/PKCE/refresh
and revocation, JWKS rotation/outages, production encryption, provider attribution
and consent, resource data beyond Patient, bulk file downloads, financial
accounting, per-tenant databases, or comprehensive PHI detection in operational
logs. No production readiness, compliance or independent certification claim.
