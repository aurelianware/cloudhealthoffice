# Stedi 275 claim attachment validation

Date: 2026-08-23 UTC

Branch: `feat/stedi-275`

Commit: `2ca248b5`

## Stedi 275 API

| Step | Mechanism | API |
| --- | --- | --- |
| Create file | JSON | `POST https://claims.us.stedi.com/2025-03-07/claim-attachments/file` |
| Upload | Pre-signed PUT | `uploadUrl` from create response (`Content-Type` must match) |
| Version | **2025-03-07** | Host `claims.us.stedi.com` |

Documented JSON body: `{ "contentType": "application/pdf" }`.
Documented success: `{ "attachmentId": "<uuid>", "uploadUrl": "<url>" }`.

Unsolicited only. Professional, institutional, and dental claim types.
Stedi recommended max size: 64MB. MIME allow-list: PDF, TIFF, JPEG, JPG, PNG.

## Live Stedi 275

**Not executed.** Stedi documents that sandbox accounts cannot submit test
claims; test attachments require a production-account test API key
([Claim attachments](https://www.stedi.com/docs/healthcare/submit-claim-attachments),
[Test claim workflows](https://www.stedi.com/docs/healthcare/test-claims-workflow)).

```
Contract-tested against Stedi's documented 275 API;
live 275 pending production/test account capability.
```

## Synthetic fixture

| Field | Value |
| --- | --- |
| Gateway | Stedi (stubbed HTTP) / Mock |
| Synthetic claim | CLM-P-1001 |
| Attachment type | ClinicalNote |
| Association | claim-level (and service-line 1 in a separate case) |
| Synthetic size | 18 bytes (`%PDF-1.4 synthetic`) |
| Checksum (SHA-256) | `105156e58646c5274f4f7420d8cd21020fc3cca3eeffb8453fbd3b5859675a7c` |
| Content type | application/pdf |
| Gateway result | GatewayAccepted |
| Retry count | 0 on success; 1 on stubbed 5xx-then-success |
| Idempotency | second identical call is replay, HTTP call count unchanged |
| Storage | metadata + content reference; no raw bytes on the transmission record |
| 837 / 277CA | unchanged |

No raw file bytes, base64, auth headers, or real PHI are recorded here.
