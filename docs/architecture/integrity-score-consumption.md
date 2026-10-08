# Integrity Score Consumption (Capability 5.10)

Status: 5.10 — verification integrity score surface (Phase 1 closer)
Services: `src/services/provider-service`, `src/services/benefit-plan-service`, `src/portal`
Depends on: 5.4.5 (verification write-back), 5.4 (network roster), 5.7 (FHIR Practitioner)

> **Addendum, July 2026.** Until this month, "Adjudication critical
> path" below was aspirational for real traffic: `HttpProviderIntegrityGate`
> was only ever reached through the standalone `AdjudicationController.Adjudicate`
> endpoint (used by the MCC benchmark validator), never through
> claims-service's actual orchestrated pipeline — `BenefitCalculationStage`
> called `calculate-benefits`, which by design (D14 in
> `claim-adjudication-pipeline.md`) never touches provider integrity. Real
> claims were never checked against federal exclusion lists. Fixed by adding
> claims-service's `ProviderIntegrityStage` (Order=150), reached through a
> new side-effect-free `GET /api/v1/adjudication/provider-integrity/{npi}`
> endpoint rather than folding the check into `calculate-benefits` itself.
> The gate was also hardened at the same time: total verification
> unavailability and a live `Failed`/`ManualReviewRequired` status now both
> resolve to `Passed=false` with a new `RequiresManualReview` flag, rather
> than the old fail-open `Passthrough()` default. See "Provider integrity
> stage (added July 2026)" in `claim-adjudication-pipeline.md`.

> **Addendum, October 2026 — exclusion screening not performed.** The
> OIG LEIE / SAM.gov adapters are still placeholders. Previously the
> placeholder returned an empty "not excluded" result that the scorer
> awarded 100 ("Clear — screened at ..."), so unscreened providers read as
> screened-clean everywhere. `ExclusionScreeningResult.WasScreened` (default
> `false`) now records whether a real source was queried. When it is not
> set, the scorer leaves the Exclusion Screening dimension unevaluated
> (excluded from the composite), adds a Warning flag
> `EXCLUSION_NOT_SCREENED`, rates the provider `Unknown` (unless otherwise
> `Blocked`), and the orchestrator returns `ManualReviewRequired`. The gate
> treats an `Unknown` cached rating like a never-refreshed projection (live
> fallback) and treats a live `Unknown` rating or `EXCLUSION_NOT_SCREENED`
> flag as `RequiresManualReview`, so claims pend `MEDREVIEW` rather than
> pass. Until real adapters ship, every claim reaching `ProviderIntegrityStage`
> will pend for review; disable the stage via `EnabledStages` only as an
> explicit, audited operator decision.

> **Addendum, October 2026 — real LEIE / SAM.gov screening.** Real
> adapters now exist behind configuration (see "Exclusion screening
> sources" below). They stay **off** by default, so until an operator
> enables them the placeholder behaviour above still applies. Once enabled,
> `WasScreened` is `true` only when every enabled source screened with
> current data; any source that could not (stale dataset, HTTP failure,
> rejected API key) leaves the provider NOT screened.

## Exclusion screening sources

`provider-verification-service` screens against two federal lists through
`CompositeExclusionScreeningAdapter` (engine:
`DataSources/Exclusions/`). Each source reports its own outcome in
`ExclusionScreeningResult.SourceResults` (source, `WasScreened`, mode,
`DataAsOf`, note), and the Exclusion Screening dimension `Detail` names
which sources screened and why any did not, e.g.
`Clear — screened at 2026-10-08 (screened: OIG LEIE (data as of 2026-10-07), SAM.gov (data as of 2026-10-08))`.

| Source | Mode | How |
|---|---|---|
| OIG LEIE | Local dataset | `LeieDatasetSync` downloads the full-database `UPDATED.csv`, parses it (RFC 4180, columns mapped by header name) and loads it into the local store. |
| SAM.gov | `Extract` (default) | `SamExtractDatasetSync` downloads the public exclusions extract (zipped CSV) from the SAM.gov Extracts API — one request per sync, independent of network size — and loads it into the same store. |
| SAM.gov | `Api` (optional) | `SamExclusionsApiScreener` calls the Exclusions API (`{ApiBaseUrl}{ApiVersion}/exclusions`, default v4) per provider by `npi` and `exclusionName`. Use only for small volumes: SAM keys are rate-limited per account (personal keys can be ~10 requests/day, system accounts ~1,000/day). |

**Matching policy** (`ExclusionMatcher`). False negatives are the compliance
risk; false positives go to manual review, never denial.

- Exact NPI match on an exclusion in effect → `IsExcluded = true`,
  confidence 1.0 (hard stop, `Blocked`).
- Exclusion ended (LEIE `REINDATE` / SAM termination date in the past, or
  SAM record status Inactive) → not a match.
- NPI match with a waiver (`WAIVERDATE`/`WVRSTATE`) or a future-dated
  exclusion → possible match (0.95), manual review.
- Individual name (normalized: diacritics, punctuation and Jr/Sr/III
  suffixes stripped) + DOB equal → 0.95 (0.85 with first initial only);
  DOB conflicting → not a match; DOB unavailable (NPPES has none) with exact
  first + last name → 0.7, review. A list row that carries a *different*
  NPI and no DOB confirmation is treated as a namesake (not reported).
- Organization legal name (normalized: punctuation, `&`, leading THE and
  trailing LLC/INC/CORP/CO/PC/PLLC/... removed) → 0.85 possible match;
  an excluded individual listed with that business name → 0.7.
- Anything ≥ 0.7 raises `POSSIBLE_EXCLUSION_MATCH` (score 40) for review.
- The orchestrator now screens after NPPES returns so names (individual
  first/last, or organization legal name) accompany the NPI.

**Staleness rule.** A local dataset reports `WasScreened = true` only when
its last *successful* sync is within `StalenessWindow` (default 35 days).
Against older data the source still searches, but reports NOT screened and
downgrades any NPI hit to a 0.9 possible match (it may since have ended).
A never-synced dataset reports NOT screened.

**Sync safety.** Files are downloaded to a temp file, header-validated
(an HTML/JSON error page is rejected), and loaded under a new sync id. The
load is rejected — previous dataset and its timestamp kept — when it has
fewer than `MinimumRecordCount` rows or fewer than `MinimumRetainedFraction`
of the previous count. A per-source lease in the store stops replicas
downloading concurrently. Failures back off `SyncRetryDelay` (4 h) so a
rate-limited SAM key is not exhausted.

**Storage.** With `MongoDb:ConnectionString` set, records go to the
tenant-agnostic collections `exclusion_list_records` (indexes: source+NPI,
source+normalized last name+DOB, source+normalized business name) and
`exclusion_list_sync_status` in `MongoDb:DatabaseName` (or
`ExclusionScreening:MongoDatabaseName`). Without it each replica holds an
in-memory copy (development only: LEIE + SAM extract need several hundred
MB per replica).

**Re-screening after a sync.** `GET /api/v1/exclusions/status` returns
each dataset's `lastSuccessfulSyncAt`, record count and `isStale`. A
provider whose stored `ExclusionScreening.SourceResults[].DataAsOf` predates
the latest sync is due for re-screening; the sync worker logs that
boundary on every successful sync. Today re-screening rides the existing
cadence: `IntegrityProjectionWorker` refreshes providers when
`NextVerificationDue` passes (verification-service sets it from
`ReverificationInterval`, default 30 days), i.e. roughly once per LEIE
publication cycle — not immediately after a sync. **Follow-up:** have `IntegrityProjectionWorker`
read `/exclusions/status` and refresh projections whose screen predates
the latest sync immediately rather than on cadence.

**Secrets.** The SAM.gov key is read from
`ProviderVerification:ExclusionScreening:Sam:ApiKey`, falling back to the
existing `ProviderVerification:SamGovApiKey` secret. It travels only as the
`api_key` query parameter; the SAM/extract HTTP clients have their request
loggers removed, the code never logs URLs, and OpenTelemetry HTTP
instrumentation (1.15) redacts query strings. A 401/403 from SAM logs a
warning that the key was rejected (keys expire every 90 days) without the
key, and the source reports NOT screened until it is rotated.

### Configuration

```jsonc
// appsettings.json (provider-verification-service)
"ProviderVerification": {
  "SamGovApiKey": "",                       // secret (Key Vault / k8s Secret); fallback SAM key
  "ExclusionScreening": {
    "Leie": {
      "Enabled": false,                     // true to screen against OIG LEIE
      "DownloadUrl": "https://oig.hhs.gov/exclusions/downloadables/UPDATED.csv",
      "MinimumRecordCount": 10000
    },
    "Sam": {
      "Enabled": false,                     // true to screen against SAM.gov
      "Mode": "Extract",                    // Extract (default) | Api
      "ApiKey": "",                         // secret; falls back to SamGovApiKey
      "ExtractDownloadUrl": "https://api.sam.gov/data-services/v1/extracts?fileType=EXCLUSION",
      "MinimumRecordCount": 10000,
      "ApiBaseUrl": "https://api.sam.gov/entity-information/",   // Api mode
      "ApiVersion": "v4",                                        // Api mode
      "RequestTimeout": "00:00:20",
      "MaxRetries": 3,                      // 429 / 5xx / timeout retries
      "RetryBaseDelay": "00:00:02",         // exponential; Retry-After wins
      "MaxRetryDelay": "00:01:00",
      "PageSize": 10,
      "MaxPages": 5                         // more results → NOT screened
    },
    "StalenessWindow": "35.00:00:00",
    "SyncInterval": "1.00:00:00",
    "SyncCheckInterval": "01:00:00",
    "SyncRetryDelay": "04:00:00",
    "SyncLeaseDuration": "00:30:00",
    "DownloadTimeout": "00:10:00",
    "MinimumRetainedFraction": 0.5,
    "MongoDatabaseName": null,
    "RecordsCollectionName": "exclusion_list_records",
    "SyncStatusCollectionName": "exclusion_list_sync_status"
  }
},
"MongoDb": { "ConnectionString": "", "DatabaseName": "CloudHealthOffice" }
```

Deployment checklist: set `Leie:Enabled` / `Sam:Enabled` (k8s ConfigMap
`provider-verification-service-config`), the SAM key and
`MongoDb__ConnectionString` (Secret `provider-verification-service-secrets`),
allow egress to `oig.hhs.gov` and `api.sam.gov`, then confirm
`GET /api/v1/exclusions/status` shows both datasets non-stale before
relying on screening. Until then claims keep pending for review.

## Why a canonical decision tree

Integrity-score data has three consumers patterns that look similar
from a distance but differ on staleness tolerance and the cost of a
miss:

- **Display-only consumers** can tolerate a score that's a few hours
  stale; the worst case is showing operators slightly old metadata.
- **Adjudication-path consumers** can tolerate stale data within a
  bounded window but must escalate to a fresh score outside that
  window — the worst case is paying a claim against a provider who
  was excluded yesterday.
- **Operator-driven investigations** must always read fresh data —
  the worst case is making a judgement on a stale score.

Before 5.10, each consumer rediscovered this trade-off and made its
own choice. The roster (5.4) used the cached projection; FHIR
projections (5.7–5.9) used the cached projection; the
adjudication gate (`HttpProviderIntegrityGate` in
`benefit-plan-service`) called `provider-verification-service` per
adjudication. The pattern was consistent in spirit but not in code.

5.10 documents the canonical decision tree so future consumers
reference this doc instead of re-deriving the policy.

## Decision tree

```
Need an integrity score?
│
├── Display, sort, or non-critical-path read?
│   └── Use the cached projection (provider-service: GET /providers/...).
│       The IntegrityProjectionWorker keeps it fresh on a schedule;
│       per-tenant cadence configurable via IntegrityProjection:Windows.
│
├── Adjudication / claims pricing / prior-auth gating?
│   └── Use the cached projection by default; fall back to live HTTP
│       (provider-verification-service) when the cached score is null
│       or older than ProviderIntegrityGate:StalenessFallbackThreshold.
│       Default threshold: 7 days.
│
└── Admin investigation / scheduled re-verification / on-demand refresh?
    └── Call provider-verification-service directly (live-only path).
        On-demand from the portal: POST /providers/{id}/verification/refresh.
        Scheduled: IntegrityProjectionWorker on the worker's cadence.
```

## Consumer table

| Consumer | Code | Path |
|---|---|---|
| Network roster | `NetworkRosterService` (provider-service) | Cached projection |
| FHIR Practitioner projection | `FhirPractitionerProjector` (provider-service) | Cached projection |
| FHIR PractitionerRole projection | `FhirPractitionerRoleProjector` (provider-service) | Cached projection (panel-gating extension only — score is on Practitioner) |
| FHIR Organization projection | `FhirOrganizationProjector` (provider-service) | Cached projection (only when score is on the Provider with `ProviderType.Organization`) |
| Provider list grid | `Pages/Providers.razor` (portal) | Cached projection (via portal `ProviderService` HTTP client) |
| Provider detail card | `Pages/ProviderDetailsDialog.razor` (portal) | Cached projection; "Refresh now" button calls live |
| Adjudication critical path (standalone endpoint) | `HttpProviderIntegrityGate` via `AdjudicationController.Adjudicate` (benefit-plan-service) | Cached-or-live (default) |
| Adjudication critical path (real pipeline) | `ProviderIntegrityStage` (claims-service) → `GET /api/v1/adjudication/provider-integrity/{npi}` → `HttpProviderIntegrityGate` (benefit-plan-service) | Cached-or-live (default); added July 2026 — see addendum above |
| Admin tenant backfill | `IntegrityProjectionAdminController` (provider-service) | Live (per-provider in a loop) |
| Scheduled re-verification | `IntegrityProjectionWorker` (provider-service) | Live (worker → verification-service) |
| Per-provider on-demand refresh | `ProvidersController.Refresh` (provider-service) | Live |

## `HttpProviderIntegrityGate` — cached-or-live in detail

5.10 migrates the adjudication gate from "live every adjudication"
to "cached-by-default, live-on-staleness". The migration preserves
the existing 1-hour `IMemoryCache` (per-pod request-coalescing
layer) and adds a tiered read:

```
CheckAsync(npi, forceRefresh=false)
  │
  ├── 1. IMemoryCache hit?  → return (cached_hit)
  │
  ├── 2. forceRefresh=true? → live verification-service (live_only)
  │
  ├── 3. GET /providers/npi/{npi} from provider-service
  │     ├── 404 / transport error
  │     │   → live verification-service (null_fallback)
  │     ├── projection row exists, score is null or rating is Unknown
  │     │   → live verification-service (null_fallback)
  │     ├── projection row exists, LastVerifiedAt < now - threshold
  │     │   → live verification-service (stale_fallback)
  │     └── projection row exists, score is fresh
  │         → return projection (cached_hit)
  │
  └── 4. Cache the result in IMemoryCache (1 hour TTL)
```

### Configuration

```jsonc
// appsettings.json (benefit-plan-service)
"ProviderIntegrityGate": {
  "StalenessFallbackThreshold": "7.00:00:00"  // 7 days (default)
}
```

The threshold defaults to 7 days — roughly one missed
`IntegrityProjectionWorker` sweep cycle (NPPES window is 24h) plus
margin. Test environments tighten to 1h for fast iteration; production
defaults to 7 days; high-trust environments can extend to 30 days.

### Telemetry

```
Meter:      CloudHealthOffice
Instrument: cho.provider.integrity_gate.decisions.total (Counter)
Tags:
  cho.path     ∈ { cached_hit, stale_fallback, null_fallback, live_only }
  cho.rating   ∈ { Clear, Advisory, Caution, Alert, Blocked, unknown }
```

The metric drives operational tuning of the threshold:

- High `stale_fallback` rate ⇒ threshold is tighter than the worker's
  refresh cadence — extend the threshold or increase worker frequency.
- High `null_fallback` rate ⇒ projection backfill hasn't run on that
  tenant — invoke `POST /api/v1/admin/providers/backfill-integrity-projection`.
- High `cached_hit` rate is the steady state; per-pod request-coalescing
  through `IMemoryCache` further reduces upstream calls within the TTL.

## Staleness alerting

`IntegrityProjectionStalenessReporter` (provider-service) piggybacks
on the existing `IntegrityProjectionWorker` sweep — no new hosted
service is introduced (Decision 3). For each tenant, after the
sweep's refresh pass, the reporter counts head-Active providers
whose `LastVerifiedAt` is older than
`IntegrityProjection:StalenessAlertThreshold` and updates a
per-tenant snapshot read by an `ObservableGauge`.

```jsonc
// appsettings.json (provider-service)
"IntegrityProjection": {
  "StalenessAlertThreshold": "7.00:00:00"  // 7 days (default)
}
```

```
Instrument: cho.provider.integrity_score.stale_count (ObservableGauge)
Tag:        cho.tenant_id = <string>
```

The default threshold matches `ProviderIntegrityGate:StalenessFallbackThreshold`
in `benefit-plan-service` so operators get one knob to tune by default.
Decoupled options keys preserve the freedom to alert sooner than
fall-back kicks in (e.g., warn at 5d, fall back at 7d) without code
changes.

## Portal display

The portal renders the cached projection in two places:

1. **Provider list grid** (`Pages/Providers.razor`): an `Integrity`
   column rendering `<IntegrityBadge Compact="true" />`. The badge
   reads `IntegrityScore` and `IntegrityRating` from the
   `ProviderListItem` DTO returned by provider-service.

2. **Provider detail dialog** (`Pages/ProviderDetailsDialog.razor`):
   a "Verification Integrity" card with `<IntegrityBadge />`,
   last-verified / next-due timestamps, a "Refresh now" button
   (visible-disabled when `providers:verification.refresh` permission
   is missing), and a stub link "View detailed verification report"
   reserved for a Phase 2 capability that surfaces the per-source
   verification dimension breakdown.

### Rating colour map (Decision 9)

The `IntegrityRating` enum carries six values that operators
distinguish meaningfully (Clear / Advisory / Caution / Alert / Blocked
/ Unknown). MudBlazor's four-color palette is insufficient to render
them losslessly, so two custom CSS classes augment the palette:

| Rating | MudBlazor color | CSS override |
|---|---|---|
| Clear | `Color.Success` | — |
| Advisory | `Color.Warning` | — |
| Caution | `Color.Default` | `--cho-rating-caution: #ff7a1a` |
| Alert | `Color.Error` | — |
| Blocked | `Color.Default` | `--cho-rating-blocked: #8b0028` |
| Unknown / null | `Color.Default` | — |

Custom CSS is co-located with `IntegrityBadge` (component-local) and
the CSS variables are declared in `wwwroot/css/site.css`'s `:root`
block alongside the rest of the Sentinel palette.

## Out of scope (Phase 2 territory)

- **Per-source dimension breakdown.** `provider-verification-service`'s
  `/integrity-score` endpoint returns composite score + rating + flags;
  per-dimension scores (NPPES contribution, LEIE contribution, PECOS
  contribution, FSMB contribution, Open Payments contribution) live on
  the full `/{npi}/verify` record. Surfacing those on the portal is a
  Phase 2 capability — the detail card includes a stub link reserved
  for that work.
- **Credentialing-aware re-verification cadence.** Adjusting
  `NextVerificationDue` based on integrity-score trend (e.g.,
  shortening the window when the score drops) is a Phase 2 capability
  that crosses the credentialing service boundary (5.6) and is not
  handled here.
- **Adjudication-time re-adjudicate-with-fresh.** The
  `IProviderIntegrityGate.CheckAsync` interface gained a
  `forceRefresh` parameter in 5.10; the only current direct caller
  (`AdjudicationController`, including the new July 2026
  `provider-integrity/{npi}` endpoint claims-service's
  `ProviderIntegrityStage` calls) passes the default. A future capability
  could add an admin-triggered "re-adjudicate against this claim with a
  fresh score" affordance.

## Cross-references

- `verification-writeback.md` — capability 5.4.5 (the projection write
  path that 5.10 consumes from).
- `network-roster-api.md` — capability 5.4 (one of the pre-5.10
  consumers; "Known gap" section is now resolved).
- `provider-versioning.md` — projection-metadata exemption section
  references this doc for the consumer-side decision tree.
- `fhir-practitioner-projection.md` — capability 5.7
  (`ProviderIntegrityScoreExt` extension consumer).
- `fhir-practitionerrole-projection.md` — capability 5.8.
- `fhir-organization-projection.md` — capability 5.9.
- `network-tier-organization-reference.md` — benefit-plan capability
  5.5. Adds `IOrganizationLookupClient`, the second consumer of the
  `HttpClient("ProviderService")` registration introduced here for
  `HttpProviderIntegrityGate`.
