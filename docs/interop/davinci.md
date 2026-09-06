# Da Vinci external interoperability harness

## 1. What these tests are for

Cloud Health Office has a mature internal acceptance suite
(`tests/Cms0057Acceptance.Tests`) that proves CHO implements the behavior its own
CMS-0057-F acceptance specification requires. Everything that suite exercises is
CHO code against CHO fixtures.

This harness answers a different question:

> Can Cloud Health Office exchange standards-conformant requests and responses
> with an independent Da Vinci implementation it does not own?

That question can only be answered by talking to somebody else's code. So the
harness starts real HL7 Da Vinci reference implementations in containers, pinned
by digest, and performs real FHIR exchanges across a process boundary. Nothing is
mocked, replayed, or reproduced inside CHO and called external validation.

## 2. How this differs from CMS-0057-F acceptance evidence

| | Internal acceptance | External interoperability |
| --- | --- | --- |
| Project | `tests/Cms0057Acceptance.Tests` | `tests/DaVinciInterop.Tests` |
| Question | Does CHO implement the required behavior? | Can CHO interoperate with an independent implementation? |
| Vocabulary | `PASSABLE` / `PARTIAL` / `GAP` / `N/A` | `Passed` / `Failed` / `Skipped` / `NotRun` |
| Evidence | `artifacts/cms0057-evidence/cms0057-evidence.json` | `artifacts/interop/run.json` |
| Workflow | `.github/workflows/cms0057-acceptance-evidence.yml` | `.github/workflows/davinci-interop.yml` |

**The two are never merged into one score.** An interoperability result never
changes a CMS-0057-F scenario status, and a CMS acceptance status never implies an
interoperability result. The vocabularies are deliberately different so the two
cannot be added together by accident, and `run.json` states the separation inside
the artifact itself (`relationshipToCmsAcceptance`).

Interoperability success is not CMS certification. It is evidence that an
independent implementation accepted what CHO sent and that CHO accepted what it
sent back.

## 3. Supported external targets

All pins live in [`interop/versions.json`](../../interop/versions.json), which is
the machine-readable source of truth.

| Target | Role | Protocols | License | Status in this repository |
| --- | --- | --- | --- | --- |
| [HL7-DaVinci/br-payer](https://github.com/HL7-DaVinci/br-payer) | External server | CRD, DTR, PAS | Apache-2.0 | **Executed** — PAS `$submit`, CRD CDS Hooks and DTR `$questionnaire-package` scenarios run against it |
| [HL7-DaVinci/br-provider](https://github.com/HL7-DaVinci/br-provider) | External client | CRD, DTR, PAS | MIT | Pinned and defined; no scenario yet |
| [inferno-framework/davinci-pdex-test-kit](https://github.com/inferno-framework/davinci-pdex-test-kit) | Conformance runner | PDex | Apache-2.0 | Runner seam only — see §11 |
| [inferno-framework/davinci-dtr-test-kit](https://github.com/inferno-framework/davinci-dtr-test-kit) | Conformance runner | DTR | Apache-2.0 | Runner seam only — see §11 |
| [HL7-DaVinci/CDS-Library](https://github.com/HL7-DaVinci/CDS-Library) | Content source | CRD/DTR/PAS rule content | See note below | Pinned for future use; not consumed yet |

No upstream source is vendored into this repository. Upstream code is either
pulled as a pinned image or fetched at a pinned tag into the git-ignored
`interop/.external/`.

**CDS-Library licensing:** the repository carries no `LICENSE` file at the pinned
commit. It is therefore treated as reference-only — fetched at its pin during
explicit setup, never copied into CHO. If a future scenario needs to embed any of
its fixtures, resolve licensing with HL7 first and retain attribution.

## 4. Pinned versions

Recorded in `interop/versions.json`, enforced by `InteropVersionsTests` (which
fails the build on a floating tag, and asserts the Compose stack starts exactly
the digests recorded in the manifest).

| Target | Pin | Upstream commit |
| --- | --- | --- |
| br-payer | `hlseven/davinci-br-payer@sha256:6074aebc39929a00cf93c1efa28c227eb46aab2418afa208eb293133cb150d8c` | `09d794e202717b4f6c86823626d05eb8667f4010` |
| br-provider | `hlseven/davinci-br-provider@sha256:6ddcea188bfc38cb8a2bf3e5bbda7f290970a659d2be3b85182d305565e9b74a` | `6a28d5c78ce9d566167d1d65e39d6f5f0e215a67` |
| davinci-pdex-test-kit | tag `v0.13.2` | `bc3a769f5ae9e4b1d6bdcd67d3b1b0eeab348da8` |
| davinci-dtr-test-kit | tag `v0.17.1` | `ed28c3a6a9742f4f81365f8747656e5c6add543c` |
| CDS-Library | branch `master` | `560403a97a4c50248713fad90314faaeeff7977d` |

Both burden-reduction images are multi-arch manifest lists, so the same digest
resolves on `linux/amd64` and `linux/arm64`. The upstream commit for each image
comes from its `org.opencontainers.image.revision` label — it is what the image
was actually built from, not an assumption about what the repository's default
branch happened to contain.

## 5. Local prerequisites

* .NET 8 SDK
* Docker, with the daemon running
* Outbound HTTPS to Docker Hub and to `packages2.fhir.org`

The last one is not optional: the burden-reduction images download the Da Vinci
CRD, DTR, PAS and CDex IG packages at startup and **fail to boot** without that
egress. No VSAC key or other licensed terminology credential is needed for
anything in this harness. (Upstream br-payer can use a `VSAC_API_KEY` to expand
some CQL value sets; the scenario here does not exercise a rule that needs one, and
the baseline harness must never require a licensed credential.)

On a host whose egress is proxied, copy
`interop/docker-compose.proxy.example.yml` and point the harness at your copy:

```bash
export CHO_INTEROP_COMPOSE_OVERRIDE=$PWD/interop/docker-compose.proxy.yml
```

An overlay may add host plumbing only. It must never override an `image:` — the
pins are enforced by tests.

## 6. Running the scenarios

One command starts the dependency, runs the scenario, collects evidence and cleans
up:

```bash
./scripts/interop/run.sh br-payer smoke   # PAS $submit               (BR-PAS-SUBMIT-001)
./scripts/interop/run.sh br-payer crd     # CRD CDS Hooks             (BR-CRD-001)
./scripts/interop/run.sh br-payer dtr     # DTR $questionnaire-package (BR-DTR-001)
./scripts/interop/run.sh br-payer all     # all three, merged into one evidence document
```

Scenarios run one at a time even under `all`. They share a Compose project name
and host ports, so the suite serializes them deliberately
(`InteropCollection`) — running them concurrently would have them contend for the
same container and the same evidence file, and the failure would look like a
flaky external implementation rather than the harness fighting itself.

Harness unit tests only (no Docker, no third-party code, a couple of seconds):

```bash
./scripts/interop/run.sh unit
# or
dotnet test tests/DaVinciInterop.Tests --filter Category=DaVinciInteropUnit
```

Running the external scenarios directly, if you would rather drive them yourself:

```bash
CHO_INTEROP_ENABLED=1 dotnet test tests/DaVinciInterop.Tests --filter Category=DaVinciInterop
CHO_INTEROP_ENABLED=1 dotnet test tests/DaVinciInterop.Tests --filter Scenario=BR-CRD-001
CHO_INTEROP_ENABLED=1 dotnet test tests/DaVinciInterop.Tests --filter Scenario=BR-DTR-001
```

Without `CHO_INTEROP_ENABLED=1` the external scenarios **skip**. An ordinary
`dotnet test` over the solution never downloads or starts third-party code.

Useful switches:

| Variable | Effect |
| --- | --- |
| `CHO_INTEROP_ENABLED=1` | Opt in to scenarios that start external containers |
| `CHO_INTEROP_KEEP_STACK=1` | Leave containers running after the run, for debugging |
| `CHO_INTEROP_ARTIFACTS=<dir>` | Write evidence somewhere other than `artifacts/interop` |
| `CHO_INTEROP_COMPOSE_OVERRIDE=<file>` | Layer a host-specific Compose overlay |
| `CHO_INTEROP_READY_TIMEOUT_SECONDS` | Readiness bound (default 480) |
| `CHO_INTEROP_SCENARIO_TIMEOUT_SECONDS` | Scenario bound (default 300) |

## 7. Updating a pin

Pins never move on their own. There is no auto-update, and a nightly failure after
an upstream change is a finding to triage — not a signal to bump.

To upgrade deliberately:

1. Resolve the new immutable reference. For an image:
   ```bash
   docker buildx imagetools inspect hlseven/davinci-br-payer:latest
   docker inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' <image>@<digest>
   ```
   For an Inferno kit, pick a release tag and record the commit it resolves to.
2. Update the entry in `interop/versions.json`: `digest`, `reference`,
   `imageCreatedUtc`, `sourceCommit`, and `implementationGuides` if the new build
   installs different IG packages.
3. Update the `image:` line in `interop/docker-compose.interop.yml` to the same
   digest. `InteropVersionsTests` fails if the two disagree.
4. Update the pin table in §4 of this document and the assertion in
   `InteropVersionsTests.The_burden_reduction_payer_is_pinned_to_the_digest_the_smoke_scenario_was_proven_against`.
5. Run `./scripts/interop/run.sh br-payer smoke` and review the diff in
   `artifacts/interop/run.json`.

A dependency upgrade is therefore always a reviewable diff naming the exact old
and new upstream artifact.

**When a scenario fails after a pin upgrade**, work it in this order:

1. Did CHO change? Compare against the previous CHO commit at the old pin.
2. Did upstream change? Read the upstream diff between the two recorded commits.
3. Is there an IG or version difference? Compare the `implementationGuides` block
   and the `compatibility` block in `run.json`.
4. Record the finding in the run evidence and in §12 below.
5. Fix CHO **only if CHO is wrong.** A discrepancy may equally be an upstream bug
   or an IG ambiguity, and the harness does not assign blame automatically.

## 8. Artifacts

A run writes:

```
artifacts/interop/
  run.json          machine-readable evidence (schemaVersion 1)
  junit.xml         the same outcomes for CI consumption
  requests/         redacted request bodies the harness sent
  responses/        redacted response bodies the external system returned
  service-logs/     tail of each external container's log (failures only)
```

Container logs are captured only when a scenario fails: on a passing run they add
megabytes of upstream startup noise without telling a reviewer anything
`run.json` does not already say.

`run.json` is the artifact intended for later publication (after whatever public
summarization the site needs). Raw container logs are never published as-is.

Nothing in these artifacts carries a bearer token, a client secret, a private key
or PHI. Authorization-class headers are replaced at capture time; bodies and logs
pass through a redaction pass on capture and again on write; URLs are stripped of
userinfo and credential-shaped query parameters. `RedactionTests` holds that
guardrail in place.

## 9. Synthetic data policy

Every value that reaches an external implementation comes from
`SyntheticInteropData`:

| | Value |
| --- | --- |
| Member | `interop-member-001` (identifier type `MB`) |
| Provider | NPI `1234567893` — the conventional test NPI; correctly formed, issued to nobody |
| Payer | `interop-payer-a` |
| Coverage | `interop-coverage-001` |
| Prior authorization | `interop-pa-001` |

Identifiers are valid in *format* so that an implementation which validates format
accepts them, and they name nobody. There is no code path in the harness that
sends production data to a third-party system.

## 10. Security model

* **Opt-in.** External scenarios skip unless `CHO_INTEROP_ENABLED=1`. No ordinary
  unit-test run downloads or starts third-party code.
* **Pinned.** Every external artifact is an image digest or a tag plus commit;
  `docker compose up` runs with `--pull never` so an unpinned image can never be
  fetched mid-scenario.
* **No secrets.** The interop Compose stack references no repository secret, no
  cloud credential and no GitHub token, and the CI job runs with
  `permissions: contents: read`. No external container gets the Docker socket.
* **Network isolation.** External containers sit on their own `cho-interop` bridge
  network, separate from the development stack, with host ports bound to
  `127.0.0.1`. The only egress they need is `packages2.fhir.org` for IG packages —
  documented per target in `versions.json` under `requiredEgress`.
* **CHO's trust model is untouched.** There is no `DisableAuthentication` switch,
  and nothing here weakens SMART middleware. When CHO runs as the system under
  test (`interop-cho` profile), it runs in the existing Demo adapter mode with a
  synthetic tenant and a test issuer — a test *configuration*, not a bypass. If a
  future exchange needs an authentication profile an upstream RI cannot satisfy,
  document that fact and configure a narrowly scoped, standards-appropriate test
  path; never a global bypass.
* **Cleanup.** The harness tears its stack down even when a scenario throws;
  `scripts/interop/run.sh` traps `EXIT`/`INT`/`TERM` as a backstop; and the CI job
  audits that no `cho-interop` container survived.

## 11. Adding a new interoperability scenario

Future scenarios are added without touching orchestration:

1. Add a row to [`interop/scenarios.json`](../../interop/scenarios.json) with its
   id, protocol, `choRole`, external target and `requiredServices`. Leave
   `implemented` false until a test actually executes it.
2. If it needs a target that is not yet pinned, add it to `interop/versions.json`
   (with license, pin, endpoints and IG versions) and a service to
   `interop/docker-compose.interop.yml` under its own profile.
3. Write the test in `tests/DaVinciInterop.Tests/Scenarios/`, marked
   `[Collection(InteropCollection.Name)]`, `[Trait("Category", "DaVinciInterop")]`,
   `[Trait("Scenario", "<id>")]` and `[InteropFact]`, using
   `InteropEnvironment.For(...)`, `InteropHttpClient` and `InteropScenarioRun`.
   The collection attribute is not optional: scenarios share a Compose project and
   host ports, so they must not run concurrently.
   Write evidence with `writer.MergeWithPrevious([result])` so running alongside
   another scenario produces one run document rather than erasing the other's
   result.
4. Flip `implemented` to true and update the expectation in
   `ScenarioInventoryTests.Only_the_scenario_this_harness_actually_executes_is_marked_implemented`.

### Inferno integration seam

Inferno Core exposes a JSON API alongside its browser UI, and `InfernoRunner` is
built on that API. The harness never scrapes rendered HTML: a conformance result
that depends on page markup is not worth publishing.

What already exists:

* `InfernoRunner.CreateSessionAsync` — create a test session for a suite and set
  its inputs (CHO's FHIR base URL, SMART configuration, and so on).
* `InfernoRunner.StartSuiteRunAsync` — `POST /api/test_runs`.
* `InfernoRunner.WaitForCompletionAsync` — bounded polling of
  `GET /api/test_runs/{id}?include_results=true`.
* `InfernoRunner.MapStatus` / `RollUp` — Inferno's vocabulary
  (`pass`/`fail`/`error`/`skip`/`omit`/`wait`/`cancel`) mapped into
  `InteropStatus`, with `omit`/`wait` treated as Skipped rather than counted
  against the run, and a suite that skipped everything reported Skipped rather
  than Passed. `InfernoRunnerTests` pins that mapping.

What the next PR has to do:

* Run `./scripts/interop/fetch-inferno.sh pdex` (or `dtr`) to check the kit out at
  its pinned tag into `interop/.external/`, then bring up the `interop-pdex` (or
  `interop-dtr-inferno`) profile, which builds the kit's image from that checkout.
  Upstream publishes no image for either kit, which is why the checkout is an
  explicit setup step.
* Start CHO with the `interop-cho` profile, since these suites drive CHO as the
  system under test.
* Confirm the exact input names each suite declares (they are versioned with the
  kit) and supply CHO's endpoint accordingly.

Suite ids at the current pins:

| Kit | Suite ids |
| --- | --- |
| PDex `v0.13.2` | `pdex_payer_server`, `pdex_payer_client`, `pdex_provider_client` |
| DTR `v0.17.1` | `dtr_payer_server`, `dtr_full_ehr`, `dtr_light_ehr`, `dtr_smart_app` |

## 12. Version awareness and known mismatches

Da Vinci IGs evolve, Inferno kits may be draft, and an external RI may track a
newer STU than CHO. Each result records a `compatibility` block naming the IG
version each side was operating under.

Known at the current pins:

* **PAS IG version.** CHO targets the Da Vinci PAS **STU 2.2.x** family
  (`docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md`); the pinned br-payer image
  installs **PAS 2.2.1**, **CRD 2.2.1**, **DTR 2.2.0** and **CDex 2.1.0** at
  startup. Same family, but CHO's declaration is a family and the RI's is a point
  release, so `run.json` reports `mismatch: true` for PAS rather than claiming
  agreement it cannot demonstrate. Pinning CHO's own declaration to a point
  release is a reasonable follow-up.
* **`Claim/$inquire` OperationDefinition canonical.** CHO's CapabilityStatement
  advertises
  `http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-inquire`; the
  pinned br-payer advertises `.../OperationDefinition/Claim-inquiry`. The harness
  records this as a **Warning** finding
  (`pas.operation.inquire.canonicalMismatch`) and does not adjudicate it: the
  follow-up work is to check the published PAS IG and correct whichever side is
  wrong. It is not asserted here because the smoke scenario does not invoke
  `$inquire`; only `$submit`, which both sides name identically, is asserted.
* **`Claim/$submit` canonical agrees** between CHO and the RI, and the smoke
  scenario asserts that agreement.
* **CRD version.** CHO targets the Da Vinci CRD **STU 2.2.x** family; the pinned
  payer advertises **2.2** through its `davinci-crd.version` discovery extension.
  Same family, but as with PAS one side names a family and the other a release, so
  `run.json` reports `mismatch: true` rather than claiming an agreement it cannot
  demonstrate.
* **CHO advertises no `davinci-crd.version` extension** in its own CDS Hooks
  discovery, while the payer does. Recorded by `BR-CRD-001` as the Warning finding
  `crd.surface.noVersionExtension`. A CRD client cannot tell from CHO's discovery
  which CRD version CHO implements. Not adjudicated here; a reasonable follow-up.
* **DTR version.** CHO targets the Da Vinci DTR **STU 2.2.x** family; the pinned
  payer reports **2.2.0**, read at runtime from the version on the
  `dtr-std-questionnaire` StructureDefinition it has installed rather than assumed
  from the pin. Same family; `run.json` reports `mismatch: true` for the same
  reason as CRD and PAS — one side names a family, the other a release.

## 13. Limitations

* Three scenarios execute today: `BR-PAS-SUBMIT-001` (PAS), `BR-CRD-001` (CRD)
  and `BR-DTR-001` (DTR). Everything else in the inventory is `NotRun` —
  deliberately, because a placeholder must never look like a result.
* CHO participates in all three scenarios as the **client**, and through its real
  production code on the CHO side (`MetadataController` for PAS,
  `CrdController` for CRD). CHO's FHIR service does not run as a container in
  any of them; the `interop-cho` profile exists for the scenarios where CHO is
  the server, and is exercised by no scenario yet.
* `BR-CRD-001` proves protocol interoperability and that the external payer's
  rules ran. It does **not** compare coverage decisions between CHO and the
  payer — see "Protocol compatibility vs payer rule parity" below.
* `br-provider` is pinned and defined, but nothing drives it. Upstream pairs it
  with the FAST UDAP security server in its own Compose stack; that topology is
  not reproduced here.
* No Inferno suite executes. Only the runner seam exists.
* CDS-Library is pinned but unused: the pinned br-payer image already bundles the
  clinical content its own rules need, including the CRD rule fixtures
  `BR-CRD-001` exercises.
* Same-content rule parity is **deferred**. CHO and br-payer are two different
  payers with two different rule sets, so comparing their coverage decisions
  today would compare rule content, not implementations. A later PR can load the
  same pinned CDS-Library content into both and make that comparison meaningful.
* The smoke scenario submits a service code that matches no PlanDefinition on the
  RI, so the payer answers with review action `A3` (Not Required). That is a
  deterministic, content-independent path on purpose — it is a proof of protocol
  interoperability, not of prior-authorization decision logic.
* The scenario asserts PAS structural conformance (profiles, ClaimResponse shape,
  X12 review action coding) using the Firely parser CHO itself runs. It does not
  run a full IG profile validator against the response; adding one is a reasonable
  next step and would turn structural findings into richer ones.

## 14. CRD interoperability (`BR-CRD-001`)

### The exchange

```
CHO interop runner  ──  GET  /cds-services                    ──▶  br-payer
                    ◀──  discovery: 6 CRD services, davinci-crd.version 2.2

CHO interop runner  ──  POST /cds-services/order-sign-crd     ──▶  br-payer
                    ◀──  CDS Hooks response + coverage-information determination
```

CHO is the **provider-side CRD client**; br-payer is the **payer CDS service**.
The payer is never mocked, and its rules are never reimplemented in CHO.

### Why order-sign

Selected from what the pinned image actually advertises, not from assumption. It
exposes six CRD services (`appointment-book`, `encounter-discharge`,
`encounter-start`, `order-dispatch`, `order-select`, `order-sign`), all advertising
CRD 2.2. `order-sign` is the one its own rule fixtures — `PriorAuthRequired`,
`ExcludedServices`, `DocumentationRequired` — declare as their named trigger
event, so it is the hook that exercises real coverage logic rather than returning
a default. It also needs no licensed terminology, no credentials, and no prior
server state.

The service id is **resolved from discovery by hook**, never hard-coded and never
taken by list position: a server may reorder or rename services between releases,
and a scenario that indexed into the array would quietly start testing something
else instead of failing honestly.

### Behavioural cases

Three synthetic draft orders, three genuinely different determinations from the
payer's own rule fixtures:

| Billing code (HCPCS) | Upstream fixture | Payer's determination |
| --- | --- | --- |
| `L8000` | `PriorAuthRequired` | `covered=covered`, `pa-needed=auth-needed`, `doc-needed=no-doc`, plus a DTR questionnaire canonical |
| `J3490` | `ExcludedServices` | `covered=not-covered` |
| `E0100` | *(matches no fixture)* | `covered=conditional`, `info-needed=detail-code` |

The third code is what makes the first two meaningful. Two differing answers could
be two hard-coded branches; three distinct answers, one of which is the no-rule
default, show the payer actually resolving rules per billing code. The scenario
asserts all three are distinct.

CHO does not compute these answers. It supplies inputs the payer's rules key off
and validates the shape and distinctness of what comes back — the external
implementation remains the system producing the decision.

### Upstream fixture dependency

The payer scopes its CRD rules to a payer identifier on the member's Coverage. A
request carrying CHO's own payer id would match no rule, and the exchange would
prove only that the endpoint answers. The synthetic Coverage therefore names the
payer identifier the reference implementation's own scenario library uses:

```
system  urn:oid:2.16.840.1.113883.6.300
value   00001
```

This is **upstream test fixture data, not CHO production configuration**. It is
synthetic on both sides and names no real payer. It lives in
`SyntheticInteropData.UpstreamRulePayerIdentifier*` with that caveat attached.
Everything else in the request — member, coverage, practitioner, order — comes
from CHO's own synthetic interoperability identity set.

### Prefetch and the FHIR server callback

`order-sign-crd` advertises nine prefetch templates. The scenario supplies the two
the service needs (`patient`, `coverage`) and asserts the rest were not required.

A CDS Hooks request must name a `fhirServer` the service may dereference for
anything prefetch did not supply. Rather than pointing that at a placeholder and
hoping it is never called, the harness points it at `FhirCallbackWatch` — a
listener it actually runs — and **fails if any callback arrives**. The scenario is
correct by construction rather than accidentally successful, and if a future
scenario does need the payer to fetch data, that listener is the seam to replace
with the `interop-cho` profile.

### Response validation

* HTTP status, and a body that parses as a CDS Hooks response.
* `cards` present — CDS Hooks requires the member even when empty, so its absence
  is a protocol violation rather than "no recommendations".
* Any card that *is* present validated for `summary` (≤140 chars), `indicator`
  (`info`/`warning`/`critical`) and `source.label`.
* The Da Vinci `ext-coverage-information` extension on the system action parsed
  for `covered`, `pa-needed`, `doc-needed`, `info-needed`, `questionnaire`,
  `billingCode` and `coverage-assertion-id`.
* The determination tied back to the coverage and billing code CHO submitted.

An absent field is parsed as absent, never defaulted. A missing `pa-needed` means
the payer said nothing about prior authorization — materially different from
saying none is required.

Note that a CRD server's decision arrives in the **system action**, not in cards:
the pinned payer answers `order-sign` with zero cards and one system action
carrying the whole determination. A scenario that inspected only `cards` would
conclude nothing happened.

### Protocol compatibility vs payer rule parity

These are kept apart deliberately:

* **Protocol interoperability** — can CHO conduct a standards-conformant CRD
  exchange with an independent implementation? This is what `BR-CRD-001` asserts,
  and what a `Passed` means.
* **Payer rule parity** — would CHO and br-payer reach the *same* coverage
  decision? This is **not** asserted, and a difference is not a defect. They are
  two different payers with two different rule sets; comparing their decisions on
  non-identical content would compare rule content, not implementations.

The scenario therefore compares advertised *surfaces* rather than decisions, and
records what it finds as findings. Parity on identical rule content is deferred
until both sides can be loaded from the same pinned CDS-Library content.

### Interpreting the findings

| Finding | Severity | Meaning |
| --- | --- | --- |
| `crd.discovery.services` | Info | What the payer advertised and which service was selected, with the reason |
| `crd.discovery.prefetch` | Info | Prefetch keys the selected service advertises |
| `crd.determination.priorAuthRequired` | Info | The PA determination, as a PHI-free coded summary |
| `crd.determination.questionnaireOffered` | Info | The payer named a DTR questionnaire — the CRD→DTR hand-off |
| `crd.determination.contrast` | Info | All three determinations, for reviewing decision behaviour at a glance |
| `crd.surface.hooks` | Info | Hooks each side advertises |
| `crd.surface.serviceIdDiffers` | Info | Service ids differ. Expected — ids are server-chosen and clients resolve them from discovery. Recorded because a client that hard-coded one would break |
| `crd.surface.noVersionExtension` | **Warning** | CHO's discovery advertises no `davinci-crd.version` extension while the payer's does, so a CRD client cannot tell which CRD version CHO implements. Recorded, not adjudicated |

A Warning does not fail the scenario. Only assertions do, and they are limited to
things that are unambiguously protocol requirements.

### Running just CRD

```bash
./scripts/interop/run.sh br-payer crd
```

## 15. DTR interoperability (`BR-DTR-001`)

### The chain

```
CHO ──CRD order-sign──────────────────────────▶ br-payer
    ◀── coverage-information:
          pa-needed = auth-needed
          questionnaire = <canonical>

CHO ──$questionnaire-package(<canonical>)─────▶ br-payer
    ◀── Parameters: packagebundle containing that Questionnaire
```

CHO is the **provider-side DTR client**; br-payer is the **payer DTR server**.

### Why it chains rather than picks

The questionnaire is never chosen by CHO. The payer decides which one applies
when it evaluates coverage, and this scenario follows that decision into the
payer's DTR surface — which is exactly what a provider system must do in
production.

Selecting a questionnaire from a CHO fixture would have tested a different, much
weaker thing: that the payer answers for a canonical CHO already knew. Chaining
means CHO does not reimplement, mirror or second-guess the payer's
questionnaire-selection rule. It consumes it. That is what makes this independent
evidence.

The evidence records the linkage, so a run reads as a workflow rather than as
isolated green rows:

```json
{
  "scenarioId": "BR-DTR-001",
  "protocol": "DTR",
  "choRole": "Client",
  "externalRole": "payer-server",
  "linkedFromScenario": "BR-CRD-001",
  "linkedArtifact": "http://example.org/fhir/Questionnaire/PriorAuthRequired",
  "status": "Passed"
}
```

`linkedFromScenario` / `linkedArtifact` are deliberately protocol-neutral: a
future PAS submit → inquire chain would use the same two fields for an
authorization number.

### The operation

`POST {fhirBase}/Questionnaire/$questionnaire-package`, advertised by the payer's
CapabilityStatement under the DTR canonical
`http://hl7.org/fhir/us/davinci-dtr/OperationDefinition/questionnaire-package`.
The scenario asserts it is advertised before invoking it.

Request `Parameters`, carrying only what the operation requires:

| Part | Cardinality | What the scenario sends |
| --- | --- | --- |
| `coverage` | 1..1 (required) | Synthetic Coverage with `subscriberId` and a beneficiary identifier |
| `questionnaire` | 0..* | The canonical CRD returned, verbatim |

The pinned implementation also accepts `order`, `context` and `changedsince`, and
requires at least one of `questionnaire` / `order` / `context`. The scenario sends
the questionnaire canonical and nothing more — padding the request with resources
the operation does not use would make it look richer while proving less.

The Coverage carries `subscriberId` and a beneficiary **identifier** because the
payer looks a member up by identifier and explicitly refuses to trust a
sender-supplied reference as a lookup key. It still will not match — the member is
synthetic — and the payer says so in an OperationOutcome, which the scenario
records as an Info finding rather than suppressing. That refusal is a sensible
privacy property, not a defect.

### What comes back

`Parameters` conforming to `dtr-qpackage-output-parameters`, containing:

| Part | Contents |
| --- | --- |
| `packagebundle` | `Bundle` (collection, profile `DTR-QPackageBundle`) |
| `outcome` | `OperationOutcome` with any warnings |

For the fixtures this scenario exercises, the bundle carries a `Questionnaire`
(profile `dtr-std-questionnaire`) and a draft `QuestionnaireResponse` (profile
`dtr-questionnaireresponse`).

### Package completeness — as declared, not as assumed

A DTR package carries exactly the dependencies its questionnaire names.
`PackageResourceIndex` walks the Questionnaire — the `cqf-library` extension,
`item.answerValueSet` bindings, and SDC sub-questionnaire extensions, through
nested items — and asserts every canonical it finds resolves **inside the
package**.

The questionnaires these two CRD paths lead to declare no Library, ValueSet or
sub-questionnaire dependencies, so a package containing just the Questionnaire is
complete. The scenario does **not** require a Library or ValueSet to be present:
demanding resource types the implementation had no reason to send would fail a
conformant server. It records `dtr.package.noDeclaredDependencies.*` so that an
upstream change which *adds* a dependency shows up as a change rather than
passing silently.

Canonical versions are never normalised away. A dependency present at a different
version than requested is reported as a **version mismatch**, not as missing —
different consequence, different fix, and calling it "missing" would send a
reader looking for something that is sitting in the package.

### CQL, terminology and profile validation — what is *not* claimed

* **No CQL is executed.** If a future package includes a Library, its structure is
  validated; its CQL is not run. This scenario proves package exchange, not rule
  evaluation.
* **No licensed terminology is required.** Nothing here needs VSAC or any other
  credentialed terminology service.
* **FHIR structure is validated; DTR profile conformance is not independently
  validated.** Every returned resource is parsed with the Firely parser CHO's own
  FHIR service uses, and the scenario asserts DTR-specific structure it checks by
  hand (package parameter present, questionnaire present at the requested
  canonical, dependencies resolvable, bundle type). It does **not** run a profile
  validator against the DTR 2.2.0 StructureDefinitions. The resources declare
  `meta.profile`, but a declared profile is a claim by the sender, not validation —
  and this document does not treat it as one.

### Behavioural cases

Two CRD determinations, two different questionnaires:

| Billing code | CRD determination | Questionnaire the payer named |
| --- | --- | --- |
| `L8000` | `covered`, `pa-needed=auth-needed` | `.../Questionnaire/PriorAuthRequired` |
| `E0466` | `covered`, `pa-needed=auth-needed`, `doc-needed=clinical` | `.../Questionnaire/DocumentationRequired` |

The scenario asserts the two canonicals differ, which is what shows the chain
follows the payer's decision rather than returning a constant.

### `$next-question` — deliberately out of scope

The payer also advertises
`.../OperationDefinition/DTR-Questionnaire-next-question`. The questionnaires
these paths return declare the DTR **standard** profile, not
`dtr-questionnaire-adapt`, so they are usable as delivered and adaptive
progression is not needed to prove the package exchange. If a package ever
returns an adaptive questionnaire, the scenario records
`dtr.package.adaptiveQuestionnaire` — the seam for a later adaptive scenario.

### Interpreting the findings

| Finding | Severity | Meaning |
| --- | --- | --- |
| `dtr.discovery.operationAdvertised` | Info | The payer advertises the operation under the DTR canonical |
| `dtr.chain.<code>` | Info | Which canonical CRD named and what the package contained |
| `dtr.chain.contrast` | Info | Two codes led to two different questionnaires |
| `dtr.package.noDeclaredDependencies.<code>` | Info | The questionnaire names no dependencies, so the package is complete without them |
| `dtr.package.outcomeIssue` | Info | Something the payer reported alongside the package — expected for a synthetic member |
| `dtr.package.dependencyVersionMismatch` | **Warning** | A dependency resolved only by disregarding the version it asked for |
| `dtr.package.adaptiveQuestionnaire` | Info | Completing this questionnaire would need `$next-question` |

### Running just DTR

```bash
./scripts/interop/run.sh br-payer dtr
```

The scenario performs its own CRD call rather than reading a saved artifact from
an earlier run, so it is reproducible on its own and cannot go stale against a
CRD result recorded under a different pin.

## 16. Recommended next step

`BR-PAS-INQUIRE-001` — PAS `$inquire` against br-payer, chained from
`BR-PAS-SUBMIT-001`. The chaining machinery `BR-DTR-001` introduced
(`linkedFromScenario` / `linkedArtifact`) is protocol-neutral and applies
directly: `$submit` establishes a prior authorization and returns an
authorization number, and `$inquire` should find it. That closes the third Da
Vinci protocol as a workflow rather than as isolated calls, and needs no new
infrastructure.

After that, the Inferno suites (`INFERNO-DTR-PAYER-001`,
`INFERNO-PDEX-SERVER-001`) are the natural next frontier — but they are a larger
step, because they reverse the direction. CHO becomes the system under test
rather than the client, which means the `interop-cho` profile has to run for the
first time. That profile has existed since #1159 and has never been exercised;
expect it to need work.
