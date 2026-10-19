# Decision: benefit-plan-service `POST /api/v1/adjudication/adjudicate`

Status: **decided: Option B (remove it and its callers). Done.** See
[Outcome](#outcome) at the end. The sections up to the recommendation are the
analysis as written before the decision, in the present tense of that time.

## What it does today

`AdjudicationController.Adjudicate` (benefit-plan-service) runs its own
adjudication in one request, about 700 lines:

1. routing (tenant operating mode, `ClaimTypeRouter`; Legacy Only returns at
   once);
2. claims scrub, NCCI/MUE, provider integrity (OIG/LEIE/SAM.gov), member
   eligibility for the service date, prior-auth rules (`PriorAuthRuleEngine`)
   and the terminology crosswalk;
3. pricing (`RateResolutionService`), pending a line that fell back to billed
   charges (`PRICING_REVIEW`);
4. the benefit engine in **Prospective** mode, so no accumulator is written;
5. COB, and a merged `AdjudicationResponse`.

Since #1335 it is documented as a deprecated dry run: nothing is persisted and
its answer is never written back.

The real adjudicator is the claims-service pipeline
(`ClaimAdjudicationOrchestrator`, run on submission). Its stages are scrub,
duplicate check, provider integrity, network credentialing, pricing, benefit
calculation, NCCI, COB, AI examination and persistence. It calls
benefit-plan-service's `resolve-rates`, `calculate-benefits`,
`provider-integrity`, `commit-accumulators` and `reverse-claim`. **It never
calls `/adjudicate`.**

So the two disagree by construction:

- Both check eligibility (CARC 27) and prior authorization (CARC 197), but
  differently. `/adjudicate` evaluates `PriorAuthRuleEngine` rules in
  process; the pipeline's `BenefitCalculationStage` asks authorization-service
  (`IAuthorizationValidationClient`) and pends `PAUNAVAIL` when it cannot.
- Only `/adjudicate` applies operating-mode routing (`ClaimTypeRouter`) and the
  plan terminology crosswalk.
- Only the pipeline runs the duplicate check, network credentialing, examiner
  edit dispositions, AI examination and the accumulator commit.

A `/adjudicate` answer is not a prediction of what the pipeline will decide.

## Who calls it

| Caller | How |
|---|---|
| `src/tools/mcc-platform-validator/Program.cs` | `AdjudicateClaimAsync`, only with `--synchronous-diagnostic`. The default (`--servicebus-only`) scores the persisted pipeline outcome. |
| `scripts/run-mcc-local-k8s.sh`, `scripts/sweep-mcc-local-k8s.sh` | pass `--synchronous-diagnostic` when `SERVICEBUS_ONLY=false` (default `true`) |
| `tests/CloudHealthOffice.AdjudicationController.Tests/AdjudicationControllerTests.cs` | 24 requests to `/adjudicate` |
| `tests/CloudHealthOffice.AdjudicationController.Tests/AuthPipelineTests.cs` | one permission check |
| `tests/CloudHealthOffice.E2E/GoldenPathAdjudicationTests.cs` | one golden-path step (E2E suite, not in the solution) |
| Argo "step 6" | **gone.** `docs/architecture/ADJUDICATION-PIPELINE.md` still describes a 7-step Argo DAG whose step 6 posts to `/adjudicate`. That workflow (`claims-adjudication-workflow.yaml`) was removed in #1217; `infrastructure/argo-workflows/x12-837-ingest.yaml` and `src/engines/cho-enrollment-wiring/argo/validate-provider-step.yaml` say so. No workflow, manifest or deploy script under `infrastructure/`, `k8s/` or `deploy/` calls it. |

Also tied to it:

- `ChoMetrics.ClaimProcessingLatency` (`cho_claims_processing_duration`) is
  recorded **only** by `/adjudicate`. The Grafana `cho-adjudication`
  dashboard's per-step latency panel therefore shows synchronous-diagnostic
  runs, not production adjudication.
- Docs that describe it as live: `ADJUDICATION-PIPELINE.md`, ADR 016's
  diagram, `prospective-adjudication.md`,
  `docs/security/service-auth-rollout-playbook.md`, the validator README and
  `src/site/graphics/provider-integrity-pipeline.svg`.

## Option A: delegate to the real pipeline

Make the dry run run the pipeline itself, so its answer is the pipeline's.

benefit-plan-service cannot call the pipeline without a cycle (claims-service
already calls benefit-plan-service for pricing and benefits). In practice this
means a dry-run entry point in claims-service, for example
`POST /api/claims/adjudication/dry-run`, and `/adjudicate` either proxying to
it or being replaced by it.

Work:

- a dry-run flag on `ClaimAdjudicationContext`, and an audit of every stage
  for side effects: persistence, the accumulator outbox and commit, the
  `claims.pended.v1` emit from AI examination, the claim-event outbox,
  examiner dispositions;
- mapping the validator's `AdjudicationRequest` shape to a `Claim` (or
  changing the validator to send a claim);
- deciding what happens to what only `/adjudicate` does (operating-mode
  routing, the crosswalk, rule-engine prior auth): add it to the pipeline, or
  drop it;
- re-pointing the 24 controller tests, the E2E step and the validator;
- a test that a dry run leaves no trace (no claim, no outbox row, no
  accumulator write, no Kafka message).

Consequences: a synchronous diagnostic that agrees with production; a new
side-effect switch through the pipeline that every future stage must honour,
which is a new way to write by accident. The MCC already scores the persisted
pipeline outcome, so the benchmark itself gains little.

Effort: roughly 3–5 days, most of it the stage audit and the no-trace tests.
It touches claims-service adjudication code that other remediation work is
changing.

## Option B: remove it and its callers

Work:

- delete `Adjudicate` and the private helpers only it uses (`RecordLatency`,
  `IsMemberEligibleForServiceDate`, `MapToPaLineOfBusiness`, `MapToPaProgram`,
  `MapToScrubClaim`, `NormalizeClaimType`, `ParseScrubClaimType`; the other
  endpoints call none of them), and the request/response DTOs if nothing else
  uses them;
- remove `--synchronous-diagnostic` and `AdjudicateClaimAsync` from the
  validator, and the `SERVICEBUS_ONLY=false` branch from the two MCC scripts;
- go through the 24 controller tests: delete those that only exercise the
  endpoint, and move any behaviour still worth guarding to `/estimate`,
  `resolve-rates`, `calculate-benefits` or the pipeline's stage tests
  (DRG-stay pricing once per claim and the `PRICING_REVIEW` pend are the two
  to check first); drop the AuthPipelineTests line and the E2E step;
- either record `ClaimProcessingLatency` from the pipeline or remove the
  Grafana panel;
- rewrite `ADJUDICATION-PIPELINE.md` around the claims-service pipeline, and
  update ADR 016's diagram, `prospective-adjudication.md`, the auth playbook,
  the validator README and the site graphic. The 2026-07-07 MCC diagnostics
  note is historical and stays as it is.

Consequences: one adjudicator, and no endpoint that looks like one. Anyone who
used `SERVICEBUS_ONLY=false` loses the synchronous mode. `/estimate` remains
for what-if pricing. What only this endpoint did (operating-mode routing, the
crosswalk, rule-engine prior auth) stops running anywhere. Whether the
pipeline should do it is a separate question, and the same one Option A has
to answer.

Effort: roughly 1–2 days, most of it sorting the tests.

## Recommendation

**Option B.** The endpoint is already a deprecated dry run with no production
caller, its only consumer is an opt-in diagnostic, and its answers diverge
from the pipeline's. Option A costs more and adds a side-effect switch to the
adjudication path, to get back a diagnostic that the default MCC mode no
longer needs. If a synchronous what-if is wanted later, it should be built on
the pipeline as a separate decision.

Whichever option is chosen, `ADJUDICATION-PIPELINE.md` is wrong today about
Argo step 6 and should be corrected.

## Outcome

The owner chose **Option B**. Done on branch
`remediation/remove-adjudicate-endpoint`:

- **Endpoint.** `AdjudicationController.Adjudicate`, its private helpers
  (`RecordLatency`, `IsMemberEligibleForServiceDate`, `MapToPaLineOfBusiness`,
  `MapToPaProgram`, `MapToScrubClaim`, `NormalizeClaimType`,
  `ParseScrubClaimType`), its DTOs (`AdjudicationRequest`,
  `AdjudicationLineRequest`, `AdjudicationCobInfo`, `AdjudicationResponse`,
  `AdjudicationTotals`, `AdjudicationLineResponse`) and the controller
  dependencies only it used (`IPriorAuthRuleEngine`, `IOperatingModeProvider`,
  `IClaimTypeRouter`, `ITerminologyCrosswalkClient`) are gone.
  `CobRequestValidation` stays (calculate-benefits uses it).
- **Terminology crosswalk client.** `ITerminologyCrosswalkClient` /
  `HttpTerminologyCrosswalkClient`, their DI registration, their tests and
  benefit-plan-service's outbound allowance for `terminology-service` are
  removed: `/adjudicate` was the only consumer.
- **Kept, still used elsewhere.** `ClaimTypeRouter`, `IOperatingModeProvider`
  and `IPriorAuthRuleEngine` (the payment estimate uses all three);
  `IBenefitCalculationEngine.CalculateWithModeAsync` and `AugmentResult` (part
  of the shared engine interface; nothing in production calls the method now);
  every engine and the other adjudication endpoints.
- **Validator and scripts.** `--synchronous-diagnostic`, `AdjudicateClaimAsync`,
  the expected-pend observation / false-pend sweep that only ran in that mode
  and the scripts' `SERVICEBUS_ONLY` / `PEND_OBSERVATION_ENABLED` are removed.
  `--synchronous-diagnostic` now fails with an error; `--servicebus-only` and
  `--no-pend-observation` are accepted as no-ops. The summary's
  `AdjudicationStepTimings` fields stay in the contract and are now empty.
- **Tests.** The `/adjudicate` controller tests are deleted. What was still
  worth guarding moved to the payment estimate: DRG-stay case rate paid and
  cost-shared once, the `PRICING_REVIEW` case (case rate above billed), the
  TOB 111 / POS 21 → Inpatient Hospital categorization and the facility-setting
  pricing rows. Provider-integrity exclusion vs. manual review, CARC 27
  eligibility, NCCI and pricing pends are covered by the pipeline's stage
  tests. The AuthPipelineTests `/adjudicate` assertion is dropped; the E2E
  golden path now submits a claim and checks the 835 with fixed amounts.
- **Metrics.** `ChoMetrics.ClaimProcessingLatency`
  (`cho.claims.processing.duration`) is removed. There is no equivalent
  pipeline latency metric, so the Grafana `cho-adjudication` latency panels
  were removed, not repointed. `ChoMetrics.AdjudicationOutcome` was also
  recorded only by `/adjudicate`; the counter is kept (documented as not
  recorded) and its "Adjudication Outcomes" panel removed. Emitting it from the
  pipeline is a follow-up.
- **Docs.** `ADJUDICATION-PIPELINE.md` is rewritten around the claims-service
  pipeline (the Argo step 6 description is gone); ADR 016's diagram,
  `prospective-adjudication.md`, `claim-adjudication-pipeline.md`,
  `observability.md`, the auth playbook, the validator README and the site's
  observability page are updated. `src/site/graphics/provider-integrity-pipeline.svg`
  is left as is: it illustrates the Part 11 article, which describes that
  period. The 2026-07-07 MCC diagnostics note and the podcast material are
  historical and unchanged.

**No longer runs anywhere on the claim path:** Legacy-Only / Augment routing
of an individual claim (the estimate still uses routing only to label its
authority), the plan terminology crosswalk before pricing, and in-process
`PriorAuthRuleEngine` evaluation for a claim (the pipeline asks
authorization-service; the estimate and fhir-service PAS still use the rule
engine). Whether the pipeline should do any of these is a separate decision.
