# Payer Operations — Consulting-to-Product Backlog

**Status:** planning document. Everything below is a **candidate under
evaluation**, not a shipped feature and not a commitment. Nothing here may be
described on the public site as available.

**Why this exists:** the strategic goal of the Payer Operations & Core
Administration practice is explicitly *not* a services organization built on
billable hours. Each engagement is also a search for the repeatable pattern
underneath a one-off problem:

> Solve the immediate payer problem. Identify the repeatable pattern. Automate
> the repeatable pattern in Cloud Health Office.

**Related:** [ADR 012](../adr/012-product-led-professional-services.md) ·
[`/services#payer-operations`](../../src/site/services.html) ·
[POSITIONING.md](../POSITIONING.md)

---

## How a candidate graduates

1. **Observed twice, on different plans.** One engagement is an anecdote. The
   same question from a second plan, about a different fee schedule or a
   different core, is a pattern.
2. **The manual work is mechanical.** If the value is in the judgment rather
   than the execution, it stays a service.
3. **The output is evidence, not an opinion.** Candidates that produce a
   reproducible artifact (a comparison, a variance report, a test result) fit
   the platform's existing evidence model; see
   [ADR 011](../adr/011-rules-and-evidence-model.md).
4. **It can be demonstrated on synthetic data.** A capability we cannot show
   without a customer's confidential reimbursement data cannot be published as
   evidence, which limits how it can be sold.

## Candidates

Ordered by how often the underlying question shows up in payer operations work,
not by build cost.

| # | Candidate | The manual work it replaces | Existing platform surface to build on |
| --- | --- | --- | --- |
| 1 | **Fee schedule version comparison** | Diffing an old and new schedule by hand to find material payment differences before a load | FeeScheduleEngine, reference data services |
| 2 | **Expected-versus-actual payment comparison** | Building a spreadsheet of what claims should have paid against what they did | Claims repricing engine, benchmark scoring |
| 3 | **Bulk claim replay** | Re-running a representative claim population after a configuration change | Million Claim Challenge harness, synthetic claim generator |
| 4 | **Reimbursement simulation** | Hand-modelling the effect of a proposed rate change before committing to it | FeeScheduleEngine, pricing API |
| 5 | **Configurable pricing test scenarios** | Rebuilding the same test cases from scratch for every engagement | Benchmark fixtures, acceptance suites |
| 6 | **Regression test harness for configuration changes** | Ad-hoc before/after comparisons with no durable record | Existing acceptance and validator tooling |
| 7 | **Claim explainability report** | Explaining, line by line, why a claim priced the way it did | Adjudication pipeline, event evidence |
| 8 | **Pricing variance dashboard** | Manually aggregating variance findings into something an executive can read | Operator console |
| 9 | **Provider contract test cases** | Reconstructing contract-specific expectations per investigation | Provider and contract configuration model |
| 10 | **Configuration impact analysis** | Tracing which providers, benefits, or claims a change will touch | Reference data, provider services |
| 11 | **Reference-data validation** | Checking code-set and terminology updates for gaps before they reach production | TerminologyService, ReferenceDataService |
| 12 | **Audit evidence generation** | Assembling the paper trail after the fact | Event evidence and audit surface |

## Sequencing view

Candidates 1–3 share the most infrastructure and answer the most frequent
question ("will this change pay correctly, and did it?"). They are the natural
first cluster. Candidate 7 (explainability) is the highest-leverage but depends
on adjudication instrumentation that is worth scoping separately. Candidates
8–12 are best treated as outputs of the first cluster rather than independent
efforts.

No dates are attached here deliberately. A candidate moves into the roadmap when
it clears the four graduation criteria above, not when it looks appealing.

## Constraints that apply to all of them

- No proprietary fee schedules, licensed code sets, or contracted reimbursement
  data may be redistributed by Cloud Health Office. The plan brings what it is
  licensed to use.
- Payer-confidential reimbursement information is never published, exposed, or
  used as public evidence.
- None of these replaces contracted repricing networks, provider contracts, or
  third-party pricing services, and none may be described as doing so.
- Public evidence for any of these follows the existing rule: synthetic data,
  named source revision, and an explicit statement of what has not been proven.
