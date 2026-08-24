# The Clearinghouse Is Not the Payer Platform

**Audience:** partners, healthcare investors, and technical evaluators.  
**Date:** 2026-08-24  
**Related PRs:** #1111, #1112, #1114, #1115, #1116, #1117, #1118, #1119 (plus the earlier Stedi 270/271 eligibility work)

This is an engineering field note in the same series as the Million Claim Challenge write-ups. It describes shipped code. It is not a production-volume result, and it is not a live ERA enrollment certificate.

## The one-sentence version

Cloud Health Office is the claims operating system a health plan actually runs. Stedi is one of the networks it can speak.

## The ninety-second version

A healthcare payer platform has to answer a short list of questions, every day, for every claim:

- Is this member covered?
- Can we accept this claim?
- Did the clearinghouse take it?
- Where is it now?
- Does the payer need more documentation?
- What was allowed, paid, and left to the patient?
- What should a provider, examiner, or dental application do next?

Those questions are not a clearinghouse. They are a **platform**. Clearinghouses move HIPAA transactions. Cloud Health Office interprets them, stores them as distinct lifecycle facts, adjudicates against benefit plans, and turns the stack into a tenant-safe workflow view.

That distinction is the product.

Incumbent core-admin systems buried these facts inside a vendor. Modern “EDI API” companies often stop at the wire. CHO sits in the gap investors actually diligence: **source-available healthcare business logic, Kubernetes-native, with a replaceable network adapter.**

## What we just shipped

Over a focused sequence of pull requests, CHO activated a vendor-neutral **Healthcare Transaction Gateway** through Stedi’s documented JSON APIs:

| Transaction | What it answers | PR |
| --- | --- | --- |
| 270 / 271 | Eligibility | earlier Stedi eligibility work |
| 837P / 837I / 837D | Claim submission | #1111 |
| 277CA | Accepted or rejected into processing | #1112, #1114 |
| 275 outbound | Supporting documents | #1115 |
| 275 inbound | Payer-side attachment receive | #1116 |
| 276 / 277 | Current claim status | #1117 |
| 835 | Electronic remittance | #1118 |
| Claim intelligence | Unified lifecycle read model | #1119 |

Stedi is the first real adapter. The Mock gateway remains for offline tests. Availity, Change Healthcare, or a direct payer link would implement the same interfaces. A neutrality test fails the build if a vendor name leaks into the canonical models.

## Rules that make this a platform, not a pipe

These constraints were written into the code, not the pitch:

1. **277CA Accepted is not Paid.** Acknowledgment is entry into processing.
2. **276/277 Paid does not invent an 835.** Status is not remittance.
3. **835 is stored, not posted.** `AvailableForPosting` means matched and durable. Accounting is a later PR.
4. **Matching is deterministic.** Payer claim control number, then patient control number, then an explicit transmission id. No name, DOB, or amount matching.
5. **Tenant identity never comes from an inbound payload.** It comes from the matched original claim.
6. **Logs do not carry PHI.** Metrics use gateway, status, and error category.

## Claim intelligence

`GET /api/claims/{claimId}/intelligence` rebuilds a view from those stores. Applications — CloudDentalOffice, a future provider portal, operations, later AI — consume one API instead of re-implementing HIPAA.

The view includes:

- business lifecycle (`Processing`, `Paid`, `PendingInformation`, …)
- source transaction states (`837`, `277CA`, `276277`, `275`, `835`)
- financial summary from a matched 835
- attachment summary without bytes or storage URLs
- a timeline whose event ids are the source record ids, so a later status change is not a duplicate event

## What this is not

- Not a provider portal UI.
- Not CloudDentalOffice itself (CDO consumes the API later).
- Not payment posting or bank reconciliation.
- Not a live Stedi sandbox ERA certificate. 835 retrieve is contract-tested against the documented API; live ERA validation needs payer enrollment and a real transaction id.
- Not a production-cloud capacity claim. Scale evidence lives in the Million Claim Challenge.

## Why it is valuable

Healthcare investors diligence payer software against a brutal checklist: eligibility, claims, remittance, attachments, tenant isolation, PHI handling, FHIR/CMS, and “are you stuck on one clearinghouse?”

CHO can point at public pull requests for each of those, plus a million-claim adjudication benchmark that is independent of Stedi.

The economic shape is the same as the rest of the platform: **compliance and interchange as the front door, claims operations as the expansion path, source-available so a plan can inspect what it is buying.** Stedi makes the network real. CHO makes the plan runnable.
