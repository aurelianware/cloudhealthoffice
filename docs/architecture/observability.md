# CHO Observability

This doc describes the OpenTelemetry conventions every Cloud Health Office
service shares: how tracing + metrics get wired, what PHI must never appear in
spans, how to add new business spans, and how `/metrics` is scraped.

## Wiring

Every service Program.cs calls:

```csharp
using CloudHealthOffice.Infrastructure.Observability;

builder.Services.AddChoObservability(builder.Configuration);
// ...
var app = builder.Build();
app.UseChoObservability();
```

These come from `CloudHealthOffice.Infrastructure`. All OTel packages are
declared there transitively; service csprojs do not PackageReference them
directly.

`AddChoObservability` wires:

- Resource attributes (service.name, service.version, deployment.environment, service.namespace=CloudHealthOffice)
- Tracing: AspNetCore, HttpClient, StackExchange.Redis, MongoDB, and the
  CHO business-span source `ChoActivitySource`
- Metrics: AspNetCore, HttpClient, and the CHO meter `ChoMetrics`
- A Prometheus scraping endpoint at `/metrics`
- An OTLP exporter pointed at `Observability:OtlpEndpoint` (when set)
- A console exporter (when `Observability:EnableConsole` is true)
- The mandatory `PhiScrubbingSpanProcessor` (see below)

## Configuration

Per-service `appsettings.json`:

```json
"Observability": {
  "ServiceName": "<service-directory-name>",
  "OtlpEndpoint": "http://otel-collector:4317",
  "EnableConsole": false
}
```

Per-service `appsettings.Development.json`:

```json
"Observability": {
  "OtlpEndpoint": null,
  "EnableConsole": true
}
```

- `OtlpEndpoint: null` means "don't export OTLP". Dev reads telemetry via the
  console exporter. Prod points at the OTLP collector.
- Explicit `null` beats key omission for readability — a reader sees the
  exporter is intentionally off, not forgotten.

## PHI scrubbing — non-negotiable

`PhiScrubbingSpanProcessor` runs on every exported Activity via `OnEnd` and
drops any attribute whose name matches the prohibited list (SSN, MBI, DOB,
raw member/subscriber/patient IDs, contact info, auth tokens, etc.). Drops are
counted via `cho.telemetry.scrub.total` with `attribute_name` + `service_name`
labels — if that counter is non-zero in production, the code writing those
attributes has a bug.

The processor is wired unconditionally. There is no config flag to disable it.
Teams that need different scrubbing rules must fork the extension rather than
opt out.

### What's prohibited (case-insensitive, exact or suffix-after-dot match)

| Category | Attribute names |
|---|---|
| SSN | `ssn`, `social_security_number`, `socialSecurityNumber` |
| MBI | `mbi`, `medicareBeneficiaryIdentifier` |
| DOB | `dob`, `dateOfBirth`, `date_of_birth`, `birthDate` |
| Member/subscriber/patient | `member_id`, `memberId`, `subscriber_id`, `subscriberId`, `patient_id`, `patientId` |
| Contact | `email`, `emailAddress`, `email_address`, `phone`, `phoneNumber`, `phone_number`, `address`, `streetAddress`, `street` |
| Name | `first_name`, `firstName`, `last_name`, `lastName`, `full_name`, `fullName` |
| Auth | `password`, `api_key`, `apiKey`, `token`, `secret`, `authorization` |

### What's allowed

Everything that doesn't match the prohibited list — either by exact name or
by the segment after the final `.`. Standard OTel namespaces (`http.*`,
`db.*`, `net.*`, `rpc.*`, `messaging.*`) are NOT blanket-allowed: a prohibited
suffix always wins. Concretely:

- `http.method` / `http.status_code` / `db.statement` → pass through.
- `http.request.header.authorization` → stripped, suffix `authorization` is prohibited.
- `db.user.password` → stripped, suffix `password` is prohibited.
- `cho.tenant_id` → pass through.
- `cho.member_id` → stripped; `cho.member_id_hash` → pass through (its
  suffix is `member_id_hash`, not in the list).

Comparisons are case-insensitive.

### Hashed identifiers

`ChoActivitySource.StartActivity(..., memberId: "M-123", claimId: "C-456")` sets:

- `cho.member_id_hash` — SHA-256(memberId), first 16 hex chars, lowercase
- `cho.claim_id_hash` — same hashing applied to claim IDs (consistent policy)

Both go through `ChoActivitySource.HashIdentifier`. Never write raw
`cho.member_id` or `cho.claim_id` — the scrubber will strip them and increment
`cho.telemetry.scrub.total{attribute_name="member_id"}`.

## Adding a new business span

```csharp
using CloudHealthOffice.Infrastructure.Observability;

using var activity = ChoActivitySource.StartActivity(
    "adjudicate-claim",
    ActivityKind.Internal,
    tenantId: ctx.TenantId,
    claimId: claim.Id,                    // hashed automatically
    claimType: claim.Type,
    memberId: claim.MemberId);            // hashed automatically

activity?.SetTag("cho.adjudication_step", "ncci-bundling");
// do the work
activity?.SetTag("cho.outcome", "approved");
```

Guidelines:

- Keep span names verbs — `adjudicate-claim`, not `claim-adjudication`.
- Use `cho.*` tags for CHO-specific dimensions. Stick to snake_case after the prefix.
- Any dimension that could identify a member goes through `HashIdentifier` or doesn't get attached.
- Don't catch-and-re-throw just to set tags; put the tag after the operation completes normally.

## Emitting metrics

The shared `ChoMetrics` class exposes histograms and counters. To record from
a service, reference the type directly:

```csharp
using CloudHealthOffice.Infrastructure.Observability;

ChoMetrics.RequestDuration.Record(
    elapsed.TotalSeconds,
    new KeyValuePair<string, object?>("http.method", "POST"),
    new KeyValuePair<string, object?>("http.route", "/api/v1/claims"),
    new KeyValuePair<string, object?>("http.status_code", 200));
```

Available instruments (all under the `CloudHealthOffice` meter):

| Instrument | Unit | Purpose |
|---|---|---|
| `cho.http.request.duration` | s (histogram) | End-to-end HTTP request latency |
| `cho.claims.processing.duration` | s (histogram) | Claim adjudication latency |
| `cho.edi.transactions.total` | count | EDI transactions processed (837, 835, 270, 271, …) |
| `cho.claims.adjudication.outcome.total` | count | Adjudication outcomes (approved, denied, pended) |
| `cho.pas.submit.duration` | s (histogram) | Da Vinci PAS $submit time (target < 15s) |
| `cho.pas.submit.decisions.total` | count | PAS decisions by type and rule |
| `cho.telemetry.scrub.total` | count | PHI SpanProcessor drops — should stay at 0 in prod |

## Reading `/metrics`

Every service exposes Prometheus-format metrics at `/metrics`. The dot-
separated OTel names convert to underscores; a histogram ends up as three
series (`_bucket`, `_count`, `_sum`). Example:

```
# HELP cho_http_request_duration_seconds HTTP request duration in seconds
# TYPE cho_http_request_duration_seconds histogram
cho_http_request_duration_seconds_bucket{http_method="GET",http_route="/health/live",http_status_code="200",le="0.005"} 12
cho_http_request_duration_seconds_count{http_method="GET",http_route="/health/live",http_status_code="200"} 12
cho_http_request_duration_seconds_sum{http_method="GET",http_route="/health/live",http_status_code="200"} 0.0483
```

Health endpoints (`/health`, `/health/live`, `/health/ready`, `/metrics`)
are intentionally excluded from tracing so they don't flood spans, but
they DO get recorded in metrics via AddAspNetCoreInstrumentation — that's
how you spot readiness-probe regressions.

## Dev vs prod exporter behavior

| | Dev | Prod |
|---|---|---|
| OTLP exporter | disabled (`OtlpEndpoint: null`) | enabled, points at `http://otel-collector:4317` |
| Console exporter | enabled | disabled |
| Prometheus `/metrics` | always on | always on |
| PHI scrubber | always on | always on |

## Scraping in Kubernetes

`infrastructure/k8s/otel-collector/otel-collector-daemonset.yaml` is a scaffold
for the future OTel collector rollout. It is NOT currently deployed; each
service's `/metrics` is scrapable by any Prometheus running in-cluster without
the collector in the path. The OTLP exporter is dormant until an
`Observability:OtlpEndpoint` override is set via env var or config override.

## Test contract

`CloudHealthOffice.Infrastructure.Tests.ObservabilityTestHelper.AssertStandardContract`
is the single smoke test every integration-capable service test project runs:

1. `GET /metrics` → 200, body contains the CHO histogram name — proves
   `AddChoObservability` + `UseChoObservability` are wired.
2. A span from `ChoActivitySource.StartActivity` with a raw memberId never
   carries the raw value — only `cho.member_id_hash`.

Per-service test files are a one-line call into this helper, so the contract
lives in exactly one place.
