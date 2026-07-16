# Healthcare Payer Domain

This guide explains common payer-domain concepts for software engineers working
in CloudHealthOffice. It is intentionally practical: each concept points toward
where it appears in the platform.

## Claims

A claim is a request for payment for healthcare services. Claims are not simply
"paid" or "bad." A correct claim system can pay, deny, pend, reverse, adjust, or
route a claim for review depending on contracts, benefits, eligibility, edits,
and workflow rules.

Relevant docs:

- [Claims processing pipeline](../architecture/claim-adjudication-pipeline.md)
- [Claim submission API](../architecture/claim-submission-api.md)
- [Claim adjustment workflow](../architecture/claim-adjustment-workflow.md)
- [Million Claim Challenge](../benchmarks/README.md)

## Benefits

Benefits define what a member's plan covers, how cost sharing works, and which
services require edits or review. A benefit model usually interacts with service
categories, accumulators, coverage windows, prior authorization rules, and
network rules.

Relevant docs:

- [Declarative benefit model](../architecture/declarative-benefit-model.md)
- [Benefit plan adapter pattern](../architecture/benefit-plan-adapter-pattern.md)
- [Plan versioning](../architecture/plan-versioning.md)

## Provider Networks

Provider networks describe which providers participate in which products,
tiers, contracts, or organizations. Network status can affect pricing, payment,
denials, pends, and member responsibility.

Relevant docs:

- [Network as organization](../architecture/network-as-organization.md)
- [Network roster API](../architecture/network-roster-api.md)
- [Credentialing workflow](../architecture/credentialing-workflow.md)

## Pricing

Pricing decides the allowed amount and payment amounts after benefit and edit
rules are applied. It may involve fee schedules, contract rates, NCCI/MUE edits,
COB, accumulators, copays, coinsurance, and deductibles.

Relevant code:

- `src/engines/CloudHealthOffice.FeeScheduleEngine`
- `src/engines/CloudHealthOffice.NcciEngine`
- `src/engines/CloudHealthOffice.CobEngine`
- `src/services/payment-service`

## Authorizations

Prior authorization determines whether a service requires approval before it is
rendered or paid. A missing, expired, wrong-provider, or wrong-procedure
authorization can produce a denial or pended workflow depending on policy.

Relevant docs:

- [Prior Authorization API](../features/PRIOR-AUTHORIZATION-API.md)
- [Authorization request](../features/AUTHORIZATION-REQUEST.md)
- [Authorization inquiry](../features/AUTHORIZATION-INQUIRY.md)

## Eligibility

Eligibility answers whether a member had coverage for a service date. It is
temporal: service date, enrollment start, termination date, retroactive changes,
and line of business can all matter.

Relevant docs:

- [Temporal eligibility](../architecture/temporal-eligibility.md)
- [Member foundation](../architecture/member-foundation.md)

## Members And Employers

Members are covered people. Subscribers, dependents, employers, sponsors, and
coverage relationships can affect eligibility, coordination of benefits,
family accumulators, newborn coverage, and ID cards.

Relevant code:

- `src/services/member-service`
- `src/services/sponsor-service`
- `src/services/idcard-service`

## Appeals

Appeals are formal challenges to denials or coverage decisions. They require
workflow state, documents, deadlines, audit history, and role-based handling.

Relevant docs:

- [Appeals backend interface](../features/APPEALS-BACKEND-INTERFACE.md)
- [Appeals integration](../features/APPEALS-INTEGRATION.md)

## Accumulators

Accumulators track deductible, out-of-pocket, visit, dollar, or service limits
over a benefit period. They must update consistently during adjudication and
support family-level and individual-level rules.

Relevant docs:

- [Accumulator service](../architecture/accumulator-service.md)
- [Family accumulator models](../architecture/family-accumulator-models.md)

## Unsupported Is A Domain Signal

In the Million Claim Challenge, unsupported scenarios are not counted as wins.
They mark domain behavior that the current validation path cannot honestly score
yet. This is especially important for subrogation, behavioral-health carve-out,
Medicaid spend-down, prior-auth variants, and retroactive coverage change.
