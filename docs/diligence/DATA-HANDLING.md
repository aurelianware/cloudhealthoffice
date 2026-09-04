# Data Handling Rules — Founding-Partner Pilot

**Default:** synthetic only.
**PHI:** only after a signed BAA and a named environment on the Order Form.

## Classification

| Class | Allowed in CHO? | Typical week |
| --- | --- | --- |
| Synthetic / fixture | Yes, always | Week 1–8 demo |
| De-identified (HIPAA expert or safe harbor) | Yes, after written confirmation of method | Optional week 5+ |
| Limited data set | Yes, after BAA + data-use terms | Optional |
| Production PHI | Yes, after BAA + commercial license + adapter-status Live/Hybrid for those resources | Not the default accelerator |

## Hard rules

1. Do not put real member, patient, provider-identified, or claim files in GitHub issues, pull requests, screenshots, chat, or the public repo. Ever.
2. The demo tenant id is `demo-tenant`. Do not reuse it for PHI.
3. Screenshots for case studies use synthetic data or customer-approved redaction.
4. If a file might be PHI, treat it as PHI.
5. CHO staff do not take PHI copies onto laptops. Work happens in the named environment.
6. Adapter headers must remain enabled. Do not strip `X-CHO-Adapter-Mode` in a buyer demo.
7. Production use of Cloud Health Office requires a commercial license under BSL 1.1. Evaluation and synthetic sandbox use do not.

## What the accelerator generates

- FHIR request/response samples (synthetic)
- Adapter-status JSON
- Compliance-status JSON (config posture, **not** an attestation)
- Prior-auth metrics **template** populated with synthetic counts
- Architecture and data-map diagrams

Those artifacts are stored under:

```text
cms-0057-pilot-evidence/
  YYYY-MM-DD-demo-mode/
  YYYY-MM-DD-hybrid/
  YYYY-MM-DD-live-payer-backed/
```

See [CMS-0057-F-DEMO-MODE-LIVE-ADAPTERS.md](../compliance/CMS-0057-F-DEMO-MODE-LIVE-ADAPTERS.md).
