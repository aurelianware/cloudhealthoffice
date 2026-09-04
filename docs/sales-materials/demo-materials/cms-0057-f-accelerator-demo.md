# 20-Minute CMS-0057-F Accelerator Demo

**Audience:** CIO + compliance owner at a regional Medicaid / MA / QHP plan
**Data class:** synthetic (`demo-tenant`)
**SKU:** [CMS-0057-F-ACCELERATOR-OFFER.md](../CMS-0057-F-ACCELERATOR-OFFER.md)
**Do not:** claim certification, show unlabeled mock data as live, or open with Layer 3 CAPS replacement

If the live stack is up, run [`scripts/demo/cms-0057-f-demo.sh`](../../../scripts/demo/cms-0057-f-demo.sh) with `FHIR_URL=http://localhost:5023`. If it is not, walk the same beats on screenshots and still read the adapter labels out loud.

---

## Minute 0–2 — Frame

> You keep QNXT / Facets / HealthEdge. We stand up the FHIR, SMART, prior-auth, and audit surface CMS is requiring, in your cluster, in 6–8 weeks. We do not change how you adjudicate claims this quarter.

Say we are **pre-pilot**. Founding-partner terms exist because there is no production reference yet. That sentence is a trust move, not an apology.

Show the one-page offer. Price is on the page. Do not dance.

## Minute 2–4 — Labels first

Open `GET /fhir/r4/adapter-status`.

Read three lines out loud:

1. Effective mode (Demo or Hybrid on the synthetic tenant).
2. Data class: `synthetic`.
3. Payer-to-payer: **Out of scope**.

> If a resource is mock, the header says Demo. We will not call it live.

Point at `X-CHO-Adapter-Mode` on `/fhir/r4/metadata`.

## Minute 4–8 — Patient Access + Provider Directory

- `GET /fhir/r4/Patient/pat-001` — **Demo / synthetic**.
- `GET /fhir/r4/Coverage?patient=pat-001` — **Demo / synthetic**.
- `GET /fhir/r4/ExplanationOfBenefit?patient=pat-001` — **Hybrid** (claims-service proxy; still synthetic fixtures unless they brought a feed).
- `GET /fhir/r4/Practitioner/{npi}` — **Hybrid** proxy.

One sentence on SMART: patient-scoped tokens cannot read another patient. Do not spend the demo on OAuth ceremony unless they are the IAM person.

## Minute 8–13 — Prior auth (the CMS clock)

Walk PAS `$submit` three ways on synthetic fixtures:

1. Approve
2. Pend (manual review / SLA window)
3. Deny with a structured reason

Say:

> PAS technical surface is implemented. Your UM rules, denial taxonomy, and letter content are yours. We do not ship correspondence in this accelerator.

CRD / DTR: show that the endpoints exist; do not pretend payer questionnaires are loaded.

## Minute 13–15 — Bulk export and honesty

Show a Bulk Export job start. Label it **Demo scaffold**.

> Payer-to-payer is not turnkey. Bulk FHIR and consent are building blocks. We will not sell it as done.

Show `/fhir/r4/compliance-status` and immediately say it is **config posture, not an attestation**.

## Minute 15–17 — What we do not touch

One slide: claims adjudication stays on their core. Million Claim Challenge is optional credibility for the CTO (“local Kubernetes evidence, not a production-cloud claim”). Layer 1 does not depend on it.

## Minute 17–19 — Layer 2 appendix (only if they lean in)

Appeals is a complete CHO domain: FHIR profiles, state machine, X12 275 ingest. Operating Mode is Augment then Replace, per tenant, per domain. That is an **order-form amendment**, not week 1.

If they do not lean in, skip this. Do not turn a compliance meeting into a core-replacement meeting.

## Minute 19–20 — Ask

> $90,000 founding-partner accelerator, 6–8 weeks, one LOB, 90 days runtime included, BAA before PHI, case study in the order form. Diligence binder is already written. Can we put week 1 on the calendar?

Send: offer + [docs/diligence/README.md](../../diligence/README.md) + this script’s recording if they want it.

## Forbidden lines

- “100% compliant in five minutes”
- “Trusted by health plans”
- “We just onboarded a similar payer seeing 80% reduction”
- “Payer-to-payer is production ready”
- “This compliance-status percentage is your CMS score”
