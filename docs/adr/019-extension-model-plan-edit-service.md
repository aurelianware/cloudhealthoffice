# ADR 019: Extend Without Forking — The Extension Model and the Plan Edit Service

## Status

Accepted. The first extension point, the plan edit service, is implemented in
claims-service (`PlanEditsStage`) with its contract in
`CloudHealthOffice.PlanEdits.Contracts`.

## Context

Every health plan changes its core administration system. It adds edits for
its own benefit and payment policies, rules for each state it serves, and
integrations with its own systems. On incumbent platforms much of that work is
built by a system integrator during implementation. The plan often cannot take
it along: it may belong to the integrator, or it only runs inside the vendor's
environment. A plan that wants to move hosting or change vendors keeps its data
and loses its logic.

Source availability does not solve this on its own. If the only way to change
Cloud Health Office (CHO) is to edit its source, every plan carries a fork,
upgrades become merges, and the plan is locked to an old version. That is the
same lock-in in another form.

CHO already lets a plan change a great deal through data rather than code:

- state appeals and grievance rule packs: versioned, effective-dated, cited,
  and validated against the federal floor at startup (a pack can tighten a
  federal clock, never loosen it);
- prior-authorization rules keyed by state, line of business, program and
  tenant, with tenant overrides of platform rules;
- per-tenant state compliance settings (prompt pay, PA turnaround);
- per-tenant adjudication stage enablement and enforcement modes;
- the edit override policy, per tenant and category;
- adapter interfaces for benefit plans, claims and external gateways.

What was missing was a supported way for a plan to add its **own logic**
without changing CHO's code.

## Decision

### 1. Three ways to change CHO, in order of preference

| Tier | What the plan changes | Where it lives | Survives a CHO upgrade |
|---|---|---|---|
| **Configure** | Data CHO interprets: rule packs, compliance settings, override policy, stage modes | The plan's configuration and data | Yes |
| **Extend** | The plan's own code, called by CHO through a versioned contract | The plan's repository, deployed by the plan, outside CHO's processes | Yes |
| **Change** | CHO's source | A fork | Only by merging |

Changing the source stays allowed under the license. It is the last resort, and
a plan that needs it is a sign an extension point is missing.

### 2. Rules every extension point follows

- **Out of process.** CHO never loads plan code into its own processes. An
  extension is a service CHO calls over a network contract. That keeps CHO's
  security boundary, memory and release cycle independent of plan code.
- **Versioned contract.** Each extension point has a numbered contract. Within
  a version, fields are only added and both sides ignore fields they do not
  know. A breaking change is a new version, and CHO keeps accepting the
  previous one until an ADR retires it.
- **Inside CHO's guardrails.** An extension can add scrutiny; it cannot remove
  CHO's. It cannot pay a claim CHO would deny, loosen a federal floor, bypass
  the examiner workflow or skip audit.
- **Fails safe.** No usable answer is never read as "nothing found". The plan
  chooses, explicitly, whether that pends the claim or lets it continue.
- **Owned by the plan.** The extension lives in the plan's repository and is
  named in the plan's configuration. It moves with the plan.

### 3. First extension point: the plan edit service

CHO sends each claim to the plan's edit service and pends the claim with the
edits that fired, for an examiner to uphold or override like any CHO edit.
Each response is bounded in time as well as size: the whole exchange, body
included, must finish within the tenant's timeout.

**Placement.** `PlanEditsStage`, Order 450: after CHO's NCCI/MUE edits (400), and
so after pricing, coordination of benefits (275) and benefit calculation (300);
before the AI examination (600). Optional (`IsRequired = false`). A
tenant with no configured service passes straight through and nothing is sent.

**Configuration** (`Adjudication:PlanEdits:Tenants:{tenantId}`):

| Setting | Default | Meaning |
|---|---|---|
| `Endpoint` | — | Absolute https URL (http only in Development/Testing), with no credentials, query string or fragment (a key in a query string would be logged). Must not be a CHO service host: those receive CHO service tokens. |
| `SigningSecret` | — | At least 32 characters, from a secret store. |
| `Mode` | `PendForReview` | `PendForReview` pends on fired edits (pend code `PLANEDIT`); `SoftValidation` records them on the stage result and in telemetry only, for proving a new service on live claims. |
| `WhenUnavailable` | `Pend` | `Pend` pends `SVCUNAVAIL` (transient: never overridden by an approval, retried on the approval re-run); `Pass` continues without the plan's edits and says so in the stage notes. |
| `TimeoutMilliseconds` | 3000 | 200–15000 per attempt; one retry inside twice that budget. |

A misconfiguration stops claims-service at startup; it is never discovered on a
claim. Each tenant gets its own HTTP client, so one plan's outage cannot open
another plan's circuit breaker. Redirects are not followed.

**Contract, version 1** (`docs/contracts/plan-edits/v1/`):

- *Request*: a signed JSON POST carrying the claim facts edits are written
  against: identifiers, claim type, frequency, line of business, plan, member
  id with birth date and gender, provider NPIs, place of service, dates,
  diagnoses, and lines (codes, modifiers, units, charges, dates, pointers, NDC,
  authorization numbers). No names, addresses, pay-to details or free-text
  notes are sent.
- *Signature*: HMAC-SHA256 with the shared secret over `{timestamp}.{body}`,
  sent as `X-CHO-Signature: v1=<hex>` with `X-CHO-Timestamp`. The plan refuses
  a timestamp more than five minutes from its clock.
  `Idempotency-Key` equals the request id. Evaluating edits has no side effect,
  so CHO may resend after a transient failure.
- *Response*: HTTP 200, `application/json`, at most 256 KiB, at most 100 edits.
  Each edit has a `ruleId`, a `message`, optional `lineNumbers` (which must be
  lines on the claim) and an optional `suggestedCarc`/`suggestedRarc`. Any other
  status, a body that is not contract JSON, or an edit that breaks a rule is
  "no usable answer", never "no edits".

**Two kinds of edit.**

- *Review edit* (no CARC): upheld, it records that a person reviewed it and
  changes no payment.
- *Denial edit* (a CARC and named lines): upheld, it denies those lines in full,
  group CO, with the plan's CARC and RARC. That reaches the 835 and the EOB
  through the same line adjustments as an upheld NCCI edit.

**Decisions.** A plan edit is a first-class decidable edit:

- its identity is `PLAN|{ruleId}|{lines}`, never its message;
- it is listed by the claim edits endpoint;
- it is governed by the `PLAN_EDIT` override-policy category (default: an
  examiner may override it, with a reason; tenants can change that like any
  category);
- it is part of the pend fingerprint, and it survives a later stage's pend.

On the approval re-run the plan's service is asked again, with purpose
`approval-readjudication`. Edits the examiner decided are settled; an edit
nobody decided — a new rule, or the same rule on different lines — pends and
the approval is refused. Only a decision per edit settles a plan edit: unlike
CHO's own review pends, a plan-edit pend is never passed because its pend reason
was reviewed. A whole-claim approval (no edit-level decisions) upholds every
plan edit by default, so a denial edit still denies its lines; overriding one
takes an edit-level approval under the `PLAN_EDIT` policy.

**Guardrails in this version.** A plan edit never approves, pays, removes a CHO
edit, or denies a whole claim on its own. Automatic denial without an examiner
is deliberately left out of version 1.

**Work queue.** New tenants get a "Plan Edits" queue for `PLANEDIT`. Existing
tenants route those claims to their fallback queue until an administrator adds
one; seeding never rewrites a tenant's queues.

## Consequences

Positive:

- A plan's edit logic lives in the plan's repository, in any language, and
  survives CHO upgrades and a change of host or vendor.
- Plan edits reach examiners through the same workbench, policy, audit and
  remittance path as CHO's own edits.
- The contract is small and testable from both sides: the response rules ship
  as code (`PlanEditResponseValidator`) a plan can run in its own tests.

Tradeoffs:

- Each claim of a configured tenant makes one more network call. Throughput
  benchmarks without a plan edit service are unaffected; with one, the plan's
  latency adds to every claim.
- With `WhenUnavailable = Pend`, a plan edit service outage pends that plan's
  claims. That is the safe default; `Pass` exists for plans that accept the
  risk.
- The plan operates another service and keeps its secret in step with CHO.

## Scope boundaries

Not in this version, each a candidate for its own ADR:

- automatic denial by a plan edit without examiner review;
- per-rule override levels (one `PLAN_EDIT` category today);
- group codes other than CO for denial edits;
- secret rotation with two active secrets;
- a separate FHIR EOB rendering of review edits (upheld denial edits already
  reach the EOB and 835 as line adjustments).

## Next extension points

In the order we expect to need them:

1. **Jurisdiction packs** loadable per tenant, generalising the appeals rule
   pack to prompt pay, prior authorization and encounters, so adding a state is
   loading a validated pack rather than a CHO release.
2. **Published event contracts and webhooks**: versioned schemas for the claim,
   remittance and authorization events CHO already emits, with signed delivery
   to a plan's subscribers.
3. **Work routing rules** a plan defines over pend codes, edits and amounts.

## References

- `src/services/claims-service/Services/Adjudication/Stages/PlanEditsStage.cs`
- `src/services/claims-service/Services/Adjudication/PlanEdits/`
- `src/services/shared/CloudHealthOffice.PlanEdits.Contracts/`
- [Plan edit service contract, version 1](../contracts/plan-edits/v1/README.md)
- `src/samples/CloudHealthOffice.PlanEditService.Sample/` — a worked example a plan would keep in its own repository
- [ADR 011: rules and evidence](011-rules-and-evidence-model.md)
