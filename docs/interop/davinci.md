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
| [HL7-DaVinci/br-payer](https://github.com/HL7-DaVinci/br-payer) | External server | CRD, DTR, PAS | Apache-2.0 | **Executed** — the smoke scenario runs against it |
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

## 6. Running the smoke test

One command starts the dependency, runs the scenario, collects evidence and cleans
up:

```bash
./scripts/interop/run.sh br-payer smoke
```

Harness unit tests only (no Docker, no third-party code, a couple of seconds):

```bash
./scripts/interop/run.sh unit
# or
dotnet test tests/DaVinciInterop.Tests --filter Category=DaVinciInteropUnit
```

Running the external scenario directly, if you would rather drive it yourself:

```bash
CHO_INTEROP_ENABLED=1 dotnet test tests/DaVinciInterop.Tests --filter Category=DaVinciInterop
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
   `[Trait("Category", "DaVinciInterop")]` and `[InteropFact]`, using
   `InteropEnvironment.For(...)`, `InteropHttpClient` and `InteropScenarioRun`.
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

## 13. Limitations

* One scenario executes today: `BR-PAS-SUBMIT-001`. Everything else in the
  inventory is `NotRun` — deliberately, because a placeholder must never look like
  a result.
* CHO participates in the smoke scenario as the **client** and through its real
  `MetadataController`. CHO's FHIR service does not run as a container in this
  scenario; the `interop-cho` profile exists for the scenarios where CHO is the
  server, and is exercised by no scenario yet.
* `br-provider` is pinned and defined, but nothing drives it. Upstream pairs it
  with the FAST UDAP security server in its own Compose stack; that topology is
  not reproduced here.
* No Inferno suite executes. Only the runner seam exists.
* CDS-Library is pinned but unused: the pinned br-payer image already bundles the
  clinical content its own rules need.
* The smoke scenario submits a service code that matches no PlanDefinition on the
  RI, so the payer answers with review action `A3` (Not Required). That is a
  deterministic, content-independent path on purpose — it is a proof of protocol
  interoperability, not of prior-authorization decision logic.
* The scenario asserts PAS structural conformance (profiles, ClaimResponse shape,
  X12 review action coding) using the Firely parser CHO itself runs. It does not
  run a full IG profile validator against the response; adding one is a reasonable
  next step and would turn structural findings into richer ones.

## 14. Recommended next step

`BR-CRD-001` — CRD CDS Hooks against br-payer. The pinned image already advertises
its CRD services at `/cds-services` with `davinci-crd.version` extensions, it needs
no additional content or prior state, and it exercises a second protocol through
the same harness with no orchestration changes. `BR-PAS-INQUIRE-001` is the natural
follow-on, since `BR-PAS-SUBMIT-001` already establishes the prior authorization
an inquiry needs.
