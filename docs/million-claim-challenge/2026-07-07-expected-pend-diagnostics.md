# Expected-Pend Diagnostics — Code-Path Trace

**Status: internal engineering finding. Not a published benchmark result, not an
ADR.** This document is the static-analysis trace that motivated adding
`--pend-diagnostics` to the MCC platform validator. It answers *why* the 1K
validation run in PR #841 showed expected-pend scenarios terminating as
`Paid` or `Denied` instead of `Pended`, using file/line citations against the
codebase as of this writing. It has not yet been confirmed by an empirical
diagnostics run — see "How to produce the empirical companion report" below.

> **Defects A/B remediated (this PR — pend-persistence defect fix).** The
> two structural defects this trace identified inside claims-service's own
> async orchestrator are now fixed:
>
> - **Defect A** — `UpdateAdjudicationProjectionAsync` never patched
>   `/status`, so an orchestrator-computed `Pend` (NCCI/MUE, COB) never
>   reached `ClaimStatus`. Fixed: the repository now patches
>   `ClaimStatus.Pended` when the orchestrator resolved `Pend`, subject to
>   a precedence rule that never downgrades a claim already at a
>   later-stage disposition. See
>   `docs/architecture/claim-adjudication-pipeline.md` D9a.
> - **Defect B** — `CoordinationOfBenefitsStage` never wrote `PendDetails`
>   for COB pends. Fixed: it now writes `PendCode="COB"` + a reason string,
>   mirroring NCCI's existing precedent. See
>   `docs/architecture/claim-cob-pipeline.md` D4.
>
> Orchestrator-computed NCCI/MUE and COB pends are now visible in the
> examiner work queue (`ClaimsController.GetWorkQueueSummary` /
> `GetWorkQueueItems`), which they never were before.
>
> **This does not change the headline finding below.** The Argo Workflow's
> `update-claim-step` is still the only caller of
> `PUT /api/claims/{id}/pend`, and it still only redirects NCCI/MUE to
> pend — COB, subrogation, retro-eligibility, and dual-eligible/spend-down
> have no equivalent branch there. The validator's own write-back path
> (`UpdateAdjudicationSummaryAsync`) still cannot produce `Pended` — that
> is unchanged. And the coverage gap (no code anywhere detects
> subrogation, retro-eligibility-coverage-change, or Medicaid
> dual-eligible/spend-down) is exactly as described below and remains the
> future edit-model ADR's problem.
>
> **New finding surfaced while fixing Defect A, not fixed here (out of
> scope):** the validator's `POST /api/v1/claims` call triggers the async
> orchestrator in the background (via `ClaimVersionSubmittedMessage`),
> which can now legitimately pend a claim. But the validator's own
> synchronous write-back (`UpdateAdjudicationSummaryAsync`, called
> moments later in the validator's own request chain) has no precedence
> guard at all — it unconditionally sets `/status` to
> `Approved`/`Denied`/`InAdjudication` based on the synchronous
> adjudication response, with no `IsFinalDisposition`-style check against
> whatever the async orchestrator already wrote. If the async pend lands
> first and the synchronous write-back lands second, the write-back will
> silently overwrite `Pended` back to a terminal status. This fix's
> precedence rule only protects the orchestrator's own projection write
> (Defect A's scope); it does not add a guard to
> `UpdateAdjudicationSummaryAsync`, which is a different method serving a
> different caller (the validator's direct HTTP path) and was out of
> scope for this surgical fix.
>
> The findings below are preserved unedited as the original record.

## Headline finding

**`ClaimStatus.Pended` is reachable from exactly one code path in the entire
codebase, and neither the validator nor claims-service's own async
adjudication pipeline uses it.**

The only place that ever sets `Claim.Status = ClaimStatus.Pended` is
`ClaimsController.PendClaim` (`PUT /api/claims/{id}/pend`),
`src/services/claims-service/Controllers/ClaimsController.cs:354-389`. Its own
doc comment says it plainly:

> "This is the primary entry point used by the Argo adjudication workflow
> when a deterministic edit (NCCI/MUE today, others later) requires human
> review." (`ClaimsController.cs:344-345`)

That "Argo adjudication workflow" is the literal Argo Workflow template
`infrastructure/argo-workflows/claims-adjudication-template.yaml`, not
claims-service's own message-driven orchestrator, and not the validator. Its
`update-claim-step` (`claims-adjudication-template.yaml:406-497`) is the only
caller of `PUT /pend` in the codebase, and it only calls it when the
synchronous adjudication response carries `editFailures` **and**
`success == false` (`claims-adjudication-template.yaml:428-457`) — i.e. NCCI/MUE
only. Every other outcome falls through to:

```python
elif not adjudication.get('success', True):
    status = "Denied"
```

(`claims-adjudication-template.yaml:469-470`) — so even the one workflow that
*can* reach `Pended` only routes NCCI/MUE there; COB, subrogation,
retro-eligibility, and dual-eligible/spend-down have no equivalent branch in
that workflow and would deny if they ever produced `success == false` there.

**The validator does not exercise this workflow at all.** It calls the same
synchronous endpoint the Argo workflow calls
(`POST /api/v1/adjudication/adjudicate`,
`src/tools/mcc-platform-validator/Program.cs:1007`), then writes the result
back itself via `PUT /api/claims/{id}/adjudication-summary`
(`Program.cs:1105`, handled by
`ClaimsController.UpdateAdjudicationSummary`,
`ClaimsController.cs:451-472`). That handler's status resolver:

```csharp
private static ClaimStatus ResolveAdjudicationStatus(AdjudicationResult adjudication)
{
    if (adjudication.PayerPayment == 0 && !string.IsNullOrEmpty(adjudication.DenialReasonCode))
        return ClaimStatus.Denied;
    return adjudication.PayerPayment > 0 ? ClaimStatus.Approved : ClaimStatus.InAdjudication;
}
```

(`ClaimsController.cs:474-484`) has exactly three possible outputs —
`Denied`, `Approved`, `InAdjudication` — and **cannot structurally produce
`Pended`**, regardless of what the synchronous adjudication response
contained. This is why the validator's own writeback path can never observe
a pend, independent of anything the engine does.

## Is there a second, hidden path? (checking claims-service's own pipeline)

The validator's `POST /api/v1/claims` call
(`Program.cs:951`, hitting `ClaimsV1Controller` →
`ClaimSubmissionService.SubmitAsync`,
`src/services/claims-service/Services/ClaimSubmissionService.cs:145-274`)
*does* publish a `ClaimVersionSubmittedMessage`
(`ClaimSubmissionService.cs:240-259`), which *does* trigger claims-service's
own message-driven `ClaimAdjudicationOrchestrator`
(`src/services/claims-service/Services/Adjudication/ClaimAdjudicationOrchestrator.cs:56-136`)
in the background, racing with the validator's synchronous call chain. This
pipeline has real `Pend` support:

- `NcciEditsStage` (`Stages/NcciEditsStage.cs:201-222`) — NCCI/MUE failures
  produce `ClaimAdjudicationStageResult.Pend(...)` by default
  (`NcciEnforcementMode.PendForReview`), and populate
  `context.PendDetails` with `PendCode="NCCI"` or `"MUE"`
  (`NcciEditsStage.cs:154-179`).
- `CoordinationOfBenefitsStage` (`Stages/CoordinationOfBenefitsStage.cs:382-403`)
  — Cloud Health Office secondary/tertiary detection produces `Pend` by default
  (`CobEnforcementMode.PendForSecondary`), reason
  `cob-secondary-not-supported-phase-1`. Coverage-service unavailability also
  pends (`CoordinationOfBenefitsStage.cs:405-429`), reason
  `cob-coverage-service-unavailable`.

But this pipeline **still never sets `ClaimStatus.Pended`.**
`PersistenceStage` (`Stages/PersistenceStage.cs:49-77`) writes the outcome via
`IClaimRepository.UpdateAdjudicationProjectionAsync`
(`src/services/claims-service/Repositories/ClaimRepository.cs:805-916`), whose
patch list is:

```csharp
var ops = new List<PatchOperation>
{
    PatchOperation.Set("/adjudicationResult", adjudicationResult),
    PatchOperation.Set("/claimLines", head.ClaimLines),
    PatchOperation.Set("/lastUpdatedDate", DateTime.UtcNow),
};
if (pendDetails is not null) ops.Add(PatchOperation.Set("/pendDetails", pendDetails));
```

(`ClaimRepository.cs:883-900`) — **`/status` is never in that list.** So even
when NCCI/MUE correctly populates `PendDetails`, the claim's `ClaimStatus`
field is left untouched by this write path. And the orchestrator's
`ResolveFinalOutcome` precedence (`ClaimAdjudicationOrchestrator.cs:290-307`,
Reject > Deny > Pend > Pass) is used **only** to label the
`ClaimVersionAdjudicatedMessage` Service Bus event
(`ClaimAdjudicationOrchestrator.cs:232-241`) — it is never used to call
`PendClaim`/`PUT /pend`, and the orchestrator never calls that endpoint at
all.

Worse, `CoordinationOfBenefitsStage`'s own doc comment says the quiet part
explicitly: *"PendDetails is NOT touched — that channel is reserved for
NCCI's deterministic edit-failure snapshots"*
(`CoordinationOfBenefitsStage.cs:76-83`). So COB pends don't even get the
partial credit NCCI gets — they leave zero trace on the persisted claim.

**Confirming this is truly invisible, not just unscored:** the human work
queue (`ClaimsController.GetWorkQueueSummary` /
`GetWorkQueueItems`, `ClaimsController.cs:979-1029`) filters
`_claimRepository.SearchAsync(..., status: ClaimStatus.Pended, ...)`
(`ClaimsController.cs:983-987`). A claim that NCCI-pended through this
pipeline has `PendDetails` populated but `Status` untouched — so it **never
appears in the examiner work queue either.** The work queue's own
`CobRequired` bucket (`PendCode is "COB"`, `ClaimsController.cs:999`) is
structurally unreachable today, because `CoordinationOfBenefitsStage` never
writes `PendCode="COB"` anywhere.

## Net effect

Three independent code paths can touch a claim submitted by the validator,
and none of them can leave it in `ClaimStatus.Pended`:

| Path | Can detect pendable situations? | Can persist `ClaimStatus.Pended`? |
|---|---|---|
| Validator's own writeback (`UpdateAdjudicationSummary`) | No (only sees `AdjudicationResult`, not edit failures) | No — 3 fixed outputs, none is `Pended` |
| claims-service async orchestrator (triggered by the validator's own `POST /api/v1/claims`) | Yes (NCCI/MUE, COB) | No — `UpdateAdjudicationProjectionAsync` never patches `/status` |
| Argo Workflow `update-claim-step` | Only NCCI/MUE (`editFailures` check) | Yes, via `PUT /pend` — but the validator never runs this workflow |

This is **not just a routing gap** (validator doesn't take the Argo Workflow
path). It's also a **coverage gap**: even the one path that can reach
`Pended` only wires up NCCI/MUE, and the async orchestrator that *does* have
COB pend-detection logic throws the result away before it reaches the claim
record. Fixing the routing alone (pointing the validator at the Argo
Workflow) would only fix NCCI/MUE; COB, subrogation, retro-eligibility, and
dual-eligible/spend-down would still terminate wrong.

## Per scenario-family trace (Deliverable 3.1)

| Family | Where detected | Where it becomes `Success=false` | Could it pend instead? |
|---|---|---|---|
| NCCI/MUE | `AdjudicationController.Adjudicate` step 0b, `src/services/benefit-plan-service/Controllers/AdjudicationController.cs:251-305`; independently re-detected by `NcciEditsStage.cs:119-152` | `AdjudicationController.cs:296-304` (`UnprocessableEntity`, `error="NCCI_MUE_EDIT_FAILURE"`) | Yes — `NcciEditsStage` already computes `Pend`; only the Argo Workflow's `update-claim-step` acts on it (NCCI/MUE branch only) |
| COB (secondary/tertiary/birthday/gender rule) | Only in claims-service's async orchestrator, `CoordinationOfBenefitsStage.cs:186-201`, via a live call to coverage-service's `/member/{id}/cob` | Nowhere in the synchronous path — the validator never sends `AdjudicationRequest.Cob` (see `Program.cs:970-1005`, no `cob` field in the payload), so `AdjudicationController.Adjudicate` has no COB signal at all and adjudicates the claim as an ordinary claim (likely `Success=true`) | Yes — `CoordinationOfBenefitsStage` already computes `Pend`, but `PendDetails` is explicitly not populated for COB (`CoordinationOfBenefitsStage.cs:76-83`), so even a correct COB pend leaves no trace on the claim |
| Subrogation (accident/workers-comp/third-party) | **Nowhere.** No stage, engine, or synchronous-adjudication field encodes subrogation. `EdgeCaseClaimGenerator` sets no distinguishing claim/request field for these scenarios beyond place-of-service `"23"` (`src/CloudHealthOffice.BenchmarkClaimGenerator/Generators/EdgeCaseClaimGenerator.cs:171`) | N/A — nothing detects it, so the claim adjudicates normally and most likely pays | No pend logic exists to redirect; needs new detection before an edit-model can even classify it |
| Retro-eligibility coverage change | **Nowhere** for this specific scenario. (`RetroEligibilityTermination` is a different scenario and does correctly deny via eligibility checks — not the one the answer key expects to pend.) | N/A — no signal reaches adjudication; the claim adjudicates as ordinary and most likely pays | Same as subrogation — no detection to redirect |
| Medicaid dual-eligible / spend-down | **Nowhere.** `EdgeCaseClaimGenerator` only adjusts the member's date of birth for these scenarios (`EdgeCaseClaimGenerator.cs:121-124`); no spend-down/dual-eligible signal is transmitted to adjudication | N/A — no signal reaches adjudication; likely pays | Same — no detection to redirect |

**Correction to the original hypothesis:** the task description hypothesized
that the synchronous path "collapses pendable situations ... into
`Success=false` business denials." That is precisely true for NCCI/MUE. For
COB, subrogation, retro-eligibility, and dual-eligible/spend-down, the static
trace suggests the more likely failure mode is **`Success=true` (Paid)**, not
a denial — because no code path transmits or detects the distinguishing
signal for those scenarios in the request the validator actually sends. This
is a prediction from static analysis; confirming it (Paid vs. Denied vs.
Pended, and which denial code if any) is exactly what the empirical
`--pend-diagnostics` per-claim capture is for.

## Denial-code → pendable mapping (Deliverable 3.3, candidate)

| Denial/error code | Emitted by | Pendable? | Scenario families |
|---|---|---|---|
| `NCCI_MUE_EDIT_FAILURE` | `AdjudicationController.cs:299` | **Yes** — already pend-capable in `NcciEditsStage`; Argo Workflow already redirects it | NCCI/MUE |
| `PROVIDER_EXCLUDED` | `AdjudicationController.cs:340` | No — federal exclusion (OIG/LEIE/SAM.gov) is a correct hard denial | Provider integrity |
| `PRIOR_AUTH_REQUIRED` | `AdjudicationController.cs:403` | Policy-dependent — some payers pend for retro-auth review instead of denying outright; not in scope of this investigation's five families but structurally similar (detected, converted to denial, no pend option wired) | Prior authorization |
| `SCRUB_VALIDATION_FAILURE` | `AdjudicationController.cs:240` | No — structural/data-quality rejection, needs resubmission, not a clinical/coverage judgment call | Pre-adjudication scrub |
| `LEGACY_ROUTED` | `AdjudicationController.cs:188-198` | No — routing decision, not a denial | Operating-mode routing |
| CoB pend reasons (`cob-secondary-not-supported-phase-1`, `cob-coverage-service-unavailable`) | `CoordinationOfBenefitsStage.cs:92,96` | **Yes** — already produces `Pend`, but never persisted (see above) | COB |
| *(none — no code emits anything for these)* | — | **Yes**, per the Argo/edit-model design intent, but nothing detects them today | Subrogation, retro-eligibility coverage change, Medicaid dual-eligible/spend-down |

## How to produce the empirical companion report

This document is static analysis only — it was written without a live
Kubernetes/.NET environment available in this session. To confirm or refute
the "Correction to the original hypothesis" table above with real
adjudication responses and persisted claim state, run:

```bash
PEND_DIAGNOSTICS_PATH=/tmp/mcc-pend-diagnostics.json \
PEND_DIAGNOSTICS_NCCI_SAMPLE=200 \
CLAIMS=1000 \
./scripts/run-mcc-local-k8s.sh
```

or directly:

```bash
dotnet run --project src/tools/mcc-platform-validator -- \
  --claims 1000 --parallelism 10 --tenant demo \
  --pend-diagnostics /tmp/mcc-pend-diagnostics.json \
  --pend-diagnostics-ncci-sample 200
```

The run prints an aggregate scenario table to stdout (capture the job logs
via `kubectl logs job/mcc-validator -n cloudhealthoffice` if run through the
k8s script) — paste that table into an ADR or episode packet as the primary
artifact. The full per-claim JSON at `PEND_DIAGNOSTICS_PATH` is the
supporting evidence; it is not meant to be committed if large (recommended:
attach it to the episode packet or store it out-of-repo, and commit only the
aggregate table, dated, as a follow-up to this document).

A diagnostics-on run is **not** a valid throughput benchmark — it performs
one additional `GET /api/claims/{id}` read per diagnosed claim after the
timed benchmark window closes (same posture as `--pend-observation`).
