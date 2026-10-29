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
  test (`interop-cho` profile), every service in it runs
  `ASPNETCORE_ENVIRONMENT=Development` and therefore loads the development
  issuers and the public development symmetric key, which `ChoAuthOptions.Validate`
  refuses on any host that is not Development or Testing. The tenant is synthetic
  and the issuer is a test issuer. That is a test *configuration*, not a bypass:
  fhir-service still authenticates its calls to member-service and the rest, and
  SMART middleware still validates every inbound token. If a future exchange needs
  an authentication profile an upstream RI cannot satisfy, document that fact and
  configure a narrowly scoped, standards-appropriate test path; never a global
  bypass.
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

What the runner can now do that it could not before:

* `InfernoRunner.StartGroupRunAsync` — run one **group** rather than the whole
  suite. This is how CHO is judged honestly rather than generously: US Core
  3.1.1 covers roughly thirty clinical resource types — allergies, care plans,
  immunizations, vital signs — and CHO is a payer that publishes almost none of
  them. A whole-suite run would bury the Patient and Encounter verdicts under
  failures for resources CHO never claimed. Naming the groups says which claim
  is tested; it does not soften the verdict on those groups, and `RollUp` still
  fails on any failure within them.
* `InfernoInput.AccessToken` — the `auth_info` input a server suite declares.
  `auth_type: public` with an `access_token` and no token endpoint tells Inferno
  the authorization already happened and this is the result, which is true: the
  token is minted against CHO's own development issuer rather than fetched
  through a launch the suite would have to drive. Two details are not
  negotiable, and both are pinned by tests: the value is a JSON **string**, not
  a nested object, and the input must carry `type: auth_info`, because Inferno's
  session-data repository switches on `type` and *raises* on a non-text input
  that arrives without one rather than ignoring it.

* `InfernoRunner.DescribeSuiteAsync` / `ResolveGroupId` — turn the group id the
  kit's source declares into the id Inferno addresses it by. These are not the
  same string: `us_core_v311_patient` is what the kit writes down, and
  `us_core_v311-us_core_v311_fhir_api-us_core_v311_patient` is what the API
  accepts, because Inferno prefixes a nested group with its ancestors. The
  prefix is read from the running kit rather than composed from a naming rule —
  a wrong rule does not fail loudly, it names a group that does not exist, and a
  conformance run that silently tested nothing is the one result this harness
  must never produce. An ambiguous short id is an error rather than a first
  match.

### The conformance scenarios

All four are in [`interop/scenarios.json`](../../interop/scenarios.json) and
all four run:

| Scenario | Suite | Groups | What it judges |
| --- | --- | --- | --- |
| `INFERNO-USCORE-SERVER-001` | `us_core_v311` | `capability_statement`, `patient`, `encounter` | The US Core floor CMS-0057-F sets |
| `INFERNO-C4BB-SERVER-001` | `c4bb_v200` (financial) | `capability_statement_group`, `patient`, `coverage`, `eob`, `organization`, `practitioner` | Patient, Coverage, Organization, Practitioner and the adjudicated EOB |
| `INFERNO-PLANNET-SERVER-001` | `davinci_plan_net_server_v110` | all | The public Provider Directory (G-36), with no token |
| `INFERNO-BULK-SERVER-001` | `bulk_data_v200` | `bulk_data_group_export_v200`, `bulk_data_patient_export_v200` | Group and Patient `$export` (G-60), the files included |

`INFERNO-PLANNET-SERVER-001` is the one scenario pointed somewhere other than
the authenticated FHIR base: its `choSurface` is `publicDirectory`, so the kit's
`url` is the directory the profile publishes (`/fhir/r4/directory/interop`), and
it is given no token, because the directory is public by regulation and a run
with credentials would not prove that. The kit finds what it tests with
parameterless searches, so the seeder writes one network, one practitioner, one
hospital and one plan with a document, and then reads every Plan-Net type back
from the directory without a token before the kit starts. The pinned kit is
generated from Plan-Net **1.1.0** while CHO serves **1.2.0**; the canonicals are
the same, so it matches CHO's resources, but a finding that turns on a rule
1.2.0 changed is the kit's version, not CHO (see `knownMismatches` on
`inferno-plan-net`).

`INFERNO-BULK-SERVER-001` is the one scenario where the kit authenticates
itself (`infernoAuth: backendServices`). Before the run the harness reads the
public keys the kit publishes (`/custom/bulk_data_v200/.well-known/jwks.json`)
and registers them through smart-auth-service's admin API as the backend
client in `backendClient` of `interop/cho-seed.json`, bound to the seeded
billing provider. The kit then runs SMART Backend Services (`private_key_jwt`,
ES384) against `cho-smart-auth-service`, kicks off `Group/1234567893/$export`
and `Patient/$export`, polls, downloads every NDJSON file with the token the
manifest requires, and validates the resources in them. The profile turns
bulk export on (`BulkExport__Enabled`, files held in memory) and lets the
bundled issuer serve `interop-tenant` (`SmartAuth__Tenants__0`). The issuer is
plain HTTP on the interop network, so smart-auth-service drops OpenIddict's
HTTPS requirement for an http issuer, which `SmartIssuer.Resolve` only accepts
in Development.

Which groups run is **inventory data, not code**. It is a claim about what CHO
serves, so it belongs where a reader can check it against the scenario's
description rather than buried in a test method, where narrowing a run would
look like a refactor instead of a change of claim.

What is deliberately not run, and why:

* `c4bb_v200devnonfinancial` — it checks the superseded `-NonFinancial`
  canonicals while CHO declares CARIN BB 2.2.0 `-Basis`; see the mismatch
  section above. It would match nothing in either direction.
* `c4bb_v200_related_person` — CHO publishes no `RelatedPerson`.
* The SMART launch groups — the harness supplies an already-issued token rather
  than driving a launch.
* `c4bb_server_v200_visual_inspection_and_attestation` — manual by construction.
* The Bulk Data server TLS group and SMART Backend Services group — their TLS
  checks cannot pass on the plain-HTTP interop network (TLS ends at the ingress
  in a deployment). Both passed against a TLS-serving local CHO in the first
  manual run. To run them with hosted Inferno against the GKE demo, see
  [GCP-CONFORMANCE-TESTING.md](../deploy/GCP-CONFORMANCE-TESTING.md).
* The Bulk Data system export group — CHO does not serve system-level
  `$export` (501): a payer exports per provider Group or per patient.

Run them with:

```bash
./scripts/interop/fetch-inferno.sh us-core c4bb plan-net bulk-data   # explicit setup, once
./scripts/interop/run.sh inferno us-core
./scripts/interop/run.sh inferno c4bb
./scripts/interop/run.sh inferno plan-net
./scripts/interop/run.sh inferno bulk-data
./scripts/interop/run.sh inferno all
```

In CI they are the `inferno` job in
[`davinci-interop.yml`](../../.github/workflows/davinci-interop.yml): nightly
and `workflow_dispatch` only, and `continue-on-error`. Both choices are
deliberate. Each kit builds a Ruby image from an upstream checkout, starts a
queue, a worker and an HL7 validator of its own plus seven CHO services beside
them, and validates every resource it reads against an IG package it
downloads — tens of minutes a pull request that did not touch the FHIR surface
learns nothing from paying for. And an external validator's verdict is a finding
to triage rather than a merge gate: a kit release can tighten what it checks
overnight, and a red required check on an unrelated pull request trains people
to ignore it.

#### What the harness supplies, and what it refuses to decide

* **The endpoint.** `http://cho-fhir-service:8080/fhir/r4` — the Compose service
  name, because the kit runs *inside* the Compose network. A `127.0.0.1` URL
  would point the kit at its own container, nothing would answer, and Inferno
  would report that identically to CHO failing every test.
  `ChoUnderTestAddressTests` fails the build if that address stops naming a
  service the Compose file defines, or if a host port drifts from its Compose
  default.
* **A token.** A development-signed user token, minted from the `auth` block of
  [`interop/cho-seed.json`](../../interop/cho-seed.json) — the same block
  `seed-cho.mjs` reads, so the two cannot drift into an opaque 401.
  `ChoAuthOptions.Validate` refuses symmetric signing keys on any host that is
  not Development or Testing, so nothing minted here authenticates against
  anything deployed.
* **Seeded data.** The scenario shells out to `scripts/interop/seed-cho.mjs`
  rather than reimplementing the seed in C#: the script is what a developer runs
  by hand, and a conformance run that seeded differently would be judging a
  system nobody else can reproduce. A failed seed stops the run before the kit
  starts — a suite pointed at an empty server reports a wall of failures that
  say nothing about CHO's conformance.
* **A preflight read.** CHO's own `metadata` is fetched from the host before the
  kit is handed the container address. This is not a duplicate of the kit's
  capability test; it is what separates "CHO answered and the kit judged it"
  from "the kit could not reach CHO" — two outcomes Inferno reports identically,
  and only one of which is a conformance result.
* **Nothing else.** The verdict is the kit's. The harness records every
  non-passing result as a finding, and never reinterprets a `fail` or turns a
  run that skipped everything into a pass.

#### What a kit actually needs to run

A kit is not one container, and until this was wired the `interop-c4bb` and
`interop-us-core` profiles started only one. Both kit containers died on boot
with `SQLite3::CantOpenException` before serving a single request, so the
nightly `inferno` job has never produced a conformance verdict — it is
`continue-on-error`, so the workflow still reported green and only the job's own
conclusion said otherwise.

Upstream's `docker-compose.yml` at each pinned tag runs five services. Four of
them are load-bearing, and each is fatal on its own:

| Service | Why the run needs it |
| --- | --- |
| `inferno` | The web app and the JSON API `InfernoRunner` drives. |
| `worker` | `bundle exec sidekiq -r ./worker.rb`. Inferno queues every test run; with no worker a run is accepted and then never executes. |
| `redis` | The queue itself. |
| `hl7_validator_service` | The FHIR validator. The suite's verdict *is* its output. |
| `nginx` | Browser-side proxy only — `location /` forwards to `inferno:4567` and `/hl7validatorapi/` to the validator. The harness talks to the app directly, so this is the one upstream service deliberately not reproduced. |

Each app also needs a writable data directory. `config/database.yml` at every
pinned tag puts the SQLite database at `data/inferno_production.db`, relative to
the image's `WORKDIR` of `/opt/inferno`, and the app and its worker must see the
same file — hence one shared named volume per kit.

The app and the worker find redis and the validator through the two variables in
each kit's own `.env.production`: `REDIS_URL` and
`FHIR_RESOURCE_VALIDATOR_URL`. Inferno loads that file with `Dotenv.load`, which
does **not** overwrite a variable already set in the environment, so the
Compose `environment:` blocks redirect both to CHO's shared sidecars rather than
to upstream's `redis` and `hl7_validator_service` hostnames. None of that is
inferred: the variable names, the validator port and the database path were read
out of the four kits at their pinned tags.

One redis and one validator carry all four kit profiles. Exactly one kit profile
is ever started at a time — a scenario names a single external target — and
teardown is `down --volumes`, so no state crosses between kits. Both are pinned
by digest, with the tag they were resolved from in a comment beside them;
`InteropVersionsTests.Every_image_the_compose_file_starts_is_pinned_by_digest`
fails the build if either floats, because the validator's version is part of any
result read from these kits.

A kit that starts before the validator answers reports errors against CHO that
are not CHO's, so neither sidecar is merely started: both carry a healthcheck
and the kit waits on `condition: service_healthy`. Upstream ships no healthcheck
for the validator image, but the image carries curl and the service answers
`GET /validator/version` with 200 once it is serving, which is what the check
asks. What that does **not** prove is that the IG packages a suite needs are
cached: those are fetched per validator session at first use. The fetch is
synchronous, so it makes the first validation of a run slow rather than wrong.

Every running container's logs are captured into the run artifacts, not only the
kit app's — when an Inferno run goes wrong the sentence explaining why is
usually in the worker or the validator, neither of which the scenario names.

Suite ids at the current pins:

| Kit | Suite ids |
| --- | --- |
| PDex `v0.13.2` | `pdex_payer_server`, `pdex_payer_client`, `pdex_provider_client` |
| DTR `v0.17.1` | `dtr_payer_server`, `dtr_full_ehr`, `dtr_light_ehr`, `dtr_smart_app` |
| CARIN BB `v0.16.3` | `c4bb_v110`, `c4bb_v200`, `c4bb_v200devnonfinancial`, `c4bb_v200_client` |
| US Core `v1.1.6` | `us_core_v311`, `us_core_v400`, `us_core_v501`, `us_core_v610`, `us_core_v700`, `us_core_v800` (plus a `us_core_client_*` suite per version) |
| Plan-Net `v0.13.4` | `davinci_plan_net_server_v110` |
| Bulk Data `v0.13.2` | `bulk_data_v101`, `bulk_data_v200`, `bulk_data_v200_client` |

Every id above is read from the `Inferno::TestSuite` ids in each kit's own `lib/`
at the pinned tag, not from documentation.

CARIN BB and US Core are what Provider Access actually serves, so those two kits
are the ones G-14 and G-60 would be judged against; PDex and DTR do not cover
them. `us_core_v311` is CHO's suite — `Cms0057ComplianceChecker` and the
CapabilityStatement target US Core 3.1.1+, the floor CMS-0057-F sets — with
`us_core_v610` the natural second run.

Fetch them with `./scripts/interop/fetch-inferno.sh us-core c4bb plan-net bulk-data`;
all four were fetched at these pins and the script's pin check passed (Plan-Net
and Bulk Data on 2026-10-28).

### What the `interop-cho` profile runs

The profile is the system under test, so what it binds decides what a passing
suite means. It serves CHO's **real** path:

| fhir-service binding | Served by |
| --- | --- |
| `FhirAdapters:Sources:Patient` = `ChoNative` | `cho-member-service` |
| `FhirAdapters:Sources:Coverage` = `ChoNative` | `cho-coverage-service` |
| `FhirAdapters:Sources:Encounter` = `ChoNative` | `cho-encounter-service` |
| `FhirAdapters:Sources:Claim` = `ChoNative` | `cho-claims-service` |
| `FhirAdapters:Resources:Practitioner` (Hybrid, proxied) | `cho-provider-service` |
| `PublicProviderDirectory:Directories:interop` (public Plan-Net directory) | `cho-provider-service` (networks, providers) and `cho-benefit-plan-service` (InsurancePlan, Endpoint; with its own `interop-redis`) |

`FhirAdapters:Mode` is `Hybrid`, not `Demo`, because that is what the bindings
make true — `Demo` is the honest label only while `MockFhirDataAdapter` serves
these resources. `DataClassification` stays `synthetic`: the data is seeded into
the services' own stores, not invented by the adapter.

An Inferno suite run against a mock-backed profile certifies the mock. That is
worse than no result, because it reads as conformance, so
`InteropChoProfileTests` fails the build if the profile drifts back onto `Mock`,
if a `ChoNative` binding names no service, or if the SMART issuer names a host
the profile does not start.

`cho-smart-auth-service` is part of the profile. Before, `SmartAuth__Issuer`
defaulted to `http://localhost:18024`, where nothing listened: discovery and JWKS
retrieval could not succeed, so no SMART suite could have obtained a token. The
issuer is the in-network name (`http://cho-smart-auth-service:8080`) because that
is the address both fhir-service and an Inferno container reach it on, and it
must be byte-identical on the two services — tokens, discovery and JWKS all name
it whatever `Host` a request arrives with.

#### Running the same path without Docker

Useful when diagnosing a suite failure, and the way the recipe above was
verified. Run member-service and fhir-service from the SDK against a local
mongod, and set on fhir-service:

```
FhirAdapters__Mode=Hybrid
FhirAdapters__Sources__Patient=ChoNative
Services__MemberServiceUrl=http://127.0.0.1:<member-port>/
ChoAuth__Outbound__DevelopmentHosts__0=127.0.0.1
```

That last line is the trap. `ChoOutboundHosts` auto-allowlists every
`Services:*` host so a service token is attached to it, but it deliberately
**skips loopback** (`!IsLoopbackOrIp(uri.Host)`), and `ChoAuth:Outbound:DevelopmentHosts`
is the documented escape hatch. Without it the call reaches member-service with
no token and `GET /fhir/r4/Patient/{id}` answers 502 "Patient upstream is
unavailable", which reads like a missing service token rather than a refused
one. Inside Compose the hosts are service names rather than loopback, so the
setting is neither needed nor set there.

#### Seeding it

`./scripts/interop/seed-cho.mjs` writes the synthetic record set in
[`interop/cho-seed.json`](../../interop/cho-seed.json) — one member, one
coverage, one professional claim and one inpatient institutional claim — and
then reads it back as FHIR. No dependencies: Node's own `crypto` signs the
development token.

Writes go through each service's **own HTTP API**, never into Mongo behind it,
so a seed passes exactly the validation a real write does. That is the point: an
Inferno suite reading seeded data must be reading data CHO itself accepted. A
fixture the services would reject fails the seed rather than producing a
resource no caller could have created.

The verify pass reads two things, because neither implies the other:

* `GET /fhir/r4/Patient/{id}` through fhir-service — proves the whole ChoNative
  path, not just that member-service took a write.
* `GET /fhir/ProviderAccess/ExplanationOfBenefit?patient={id}` — the Provider
  Access payload itself, with each claim's `expectProfile` asserted. A claim
  landing on the wrong CARIN profile is a silently *weaker* answer rather than
  an error: `CarinNonFinancialEobProjector` falls to the outpatient profile for
  any institutional claim it cannot read as inpatient.

It is safe to re-run. None of the three services treats a repeat write as a
no-op — member-service answers 409, but coverage-service and claims-service both
answer 201 and create a second record, because a resubmitted claim is a real
thing — so the script asks before each write. Running it twice leaves two EOBs,
not four.

`InteropSeedFixtureTests` (in the claims-service suite, where the types live)
guards the two things in the fixture that would drift quietly: the **numeric**
`claimType` and `lineOfBusiness` values, because claims-service serializes enums
as numbers and the fixture speaks its API; and the UB-04 `facilityTypeCode` that
decides the institutional profile. It deserializes the fixture with the
service's own types and runs the real projector over it.

#### What the profile still needs

* **A first run.** No suite has been run against it.

### CARIN BB non-financial: `-Basis` against `-NonFinancial`

This one blocks a result rather than merely colouring it, so it is worth stating
plainly.

CHO's non-financial ExplanationOfBenefit declares the CARIN BB 2.2.0 profiles:

```
http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-ExplanationOfBenefit-Inpatient-Institutional-Basis
…-Outpatient-Institutional-Basis, …-Professional-NonClinician-Basis, …-Oral-Basis
```

The `c4bb_v200devnonfinancial` suite at kit `v0.16.3` checks the earlier
canonicals from the non-financial *branch* snapshot it bundles, which end in
`-NonFinancial` rather than `-Basis`. That snapshot's `package.json`
self-identifies as `hl7.fhir.us.carin-bb` 2.0.0 dated 2023-04-24; the names were
changed to `-Basis` before 2.2.0.

Different canonical URLs mean the suite cannot match CHO's EOBs at all, so a run
of it today would report nothing useful in either direction. The resolution is a
later kit release regenerated against CARIN BB 2.2.0 — **not** renaming CHO's
profiles to match a superseded draft, which would make the suite pass by making
CHO wrong.

`c4bb_v200` (the financial suite) is unaffected by this and is a legitimate run
against CHO's Patient, Coverage, Organization and Practitioner projections, which
the non-financial branch did not rename.

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

* Seven scenarios execute today: `BR-PAS-SUBMIT-001` (PAS), `BR-CRD-001` (CRD),
  `BR-DTR-001` (DTR), `INFERNO-USCORE-SERVER-001` (US Core),
  `INFERNO-C4BB-SERVER-001` (CARIN BB), `INFERNO-PLANNET-SERVER-001`
  (Plan-Net) and `INFERNO-BULK-SERVER-001` (Bulk Data). Everything else in the inventory is
  `NotRun` — deliberately, because a placeholder must never look like a result.
* CHO participates in the three br-payer scenarios as the **client**, through its
  real production code (`MetadataController` for PAS, `CrdController` for CRD);
  its FHIR service does not run as a container in any of them. In the four
  conformance scenarios CHO is the **server**, and the `interop-cho` profile runs
  its real services. Its contents are described under "What the `interop-cho`
  profile runs".
* `BR-CRD-001` proves protocol interoperability and that the external payer's
  rules ran. It does **not** compare coverage decisions between CHO and the
  payer — see "Protocol compatibility vs payer rule parity" below.
* `br-provider` is pinned and defined, but nothing drives it. Upstream pairs it
  with the FAST UDAP security server in its own Compose stack; that topology is
  not reproduced here.
* The conformance runs cover one seeded member, one coverage and two claims. A
  suite that passes on that record has shown CHO's projections are conformant
  for it; it has not shown they stay conformant across a population. Widening
  the fixture is the obvious next move, and the kits will say so loudly when it
  happens.
* `INFERNO-PDEX-SERVER-001`, `INFERNO-PDEX-CLIENT-001` and
  `INFERNO-DTR-PAYER-001` remain unimplemented. Their kits are pinned; nothing
  drives them.
* The CARIN BB kit's non-financial suite cannot judge CHO's EOBs at the current
  pin — see "CARIN BB non-financial: `-Basis` against `-NonFinancial`" below. US
  Core has no such gap.
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
expect it to need work. It now runs CHO's native stack rather than the mock, and
its SMART issuer names a server that exists, but it still has no seeded data and
no suite has been pointed at it.


### Bounded payer startup recovery

The pinned br-payer image downloads IG packages from packages2.fhir.org during
startup and can exit when a package retrieval fails. Its Compose policy permits
up to two error-exit restarts (`on-failure:2`) inside the existing harness readiness
window. The pinned image, IG versions, 480-second readiness limit and protocol
assertions are unchanged. A persistent startup failure still fails the scenario;
container logs remain in evidence artifacts. Each scenario still tears its stack
down after execution.
