# Claim Deny-status backfill runbook

One-off, per environment and per tenant, after the claims-service and payment-service builds
with the Pay/Deny lifecycle (`ClaimStatus.Deny = 10`) are deployed. Opt-in, dry-run by default,
idempotent.

## Why

Before `Deny` existed, the adjudication pipeline set `ClaimStatus.Denied` the moment it denied a
claim, and `Denied` is terminal (`VersionState=Denied`): a wrong denial could not be reprocessed
before the provider was told. Now the pipeline writes `Deny` (non-final, `VersionState=Adjudicated`)
and the payment run's $0 remittance finalizes it to `Denied` once the 835 goes out (see
[`claim-versioning.md`](../architecture/claim-versioning.md#claim-lifecycle-pay--deny-before-paid--denied)).

Existing `Denied` rows that no payment run has remitted yet should be `Deny`, so examiners can
still reprocess or resolve them and the next payment run remits and finalizes them normally.

## What "never remitted" means here — and why it is not provable from claims-service

The backfill moves a claim only when, on claims-service's side, it is `Denied` with **no**
payment-cycle evidence (`ClaimRepository.IsLegacyUnremittedDenied`): no check / 835 trace
(`adjudicationResult.checkNumber`), no `adjudicationResult.paymentDate`, no `edi835ControlNumber`,
`VersionState` `Denied` (or legacy/unset), not superseded.

That is **not** proof it was never in an 835: before this change payment-service remitted denials
(PR #1337) without writing anything back to the claim. The only proof is payment-service's
`EraEnvelopes` (non-reversal envelopes, `ClaimIds`). So:

1. Export the remitted claim ids from payment-service and pass them as `remittedClaimIds`;
   those claims stay `Denied` (their provider has the denial).
2. If a remitted claim is moved anyway, the damage is bounded: the payment run never remits a
   `Deny` claim that is already in an 835 — it only finalizes it back to `Denied` (trace
   `<run>-DF`). Until that run, though, the claim is editable even though its provider was told.

## Steps

1. Enable it for the run (claims-service config): `Claims__DenyStatusBackfill__Enabled=true`.
   Without it the endpoint answers 503.
2. Export the remitted claim ids for the tenant from payment-service's database, e.g. (Mongo):

   ```js
   // payment-service database, collection EraEnvelopes (non-reversal envelopes only)
   db.EraEnvelopes.distinct("ClaimIds", { TenantId: "<tenant>", ReversalRunId: { $in: [null, ""] } })
   ```

   Field names are as payment-service's Mongo serializer writes `EraEnvelopeRecord`
   (`TenantId`, `ClaimIds`, `ReversalRunId`); check one document first. This is the same set
   `IEraEnvelopeRepository.GetClaimIdsWithEnvelopeAsync(ids, reversal: false)` answers from.
   Keep the file in restricted ticket storage: it holds claim ids.
3. Dry run (default) as a tenant admin (`settings:manage`), for that tenant:

   ```http
   POST /api/claims/admin/lifecycle/deny-backfill
   X-Tenant-ID: <tenant>
   { "dryRun": true, "limit": 500, "remittedClaimIds": [ ...ids from step 2... ] }
   ```

   The response lists `reopened` (would move), `skippedRemitted`, and the candidate count.
   Review it.
4. Apply with the same body and `"dryRun": false`. Repeat until `reopened` is empty (each run
   handles at most `limit` candidates, max 5000; remitted ids are listed again each time and
   count toward the limit — raise `limit` if they crowd the page). Every move is logged as
   `AUDIT deny-backfill: claim <id> (tenant <t>) Denied -> Deny by <actor>`.
5. A rerun is a no-op: moved claims are `Deny` and no longer match; the write itself re-checks
   the predicate (ETag on Cosmos, filter on Mongo), so a remittance that lands mid-run wins.
6. Once every tenant is done, set payment-service `PaymentRuns__RemitLegacyDeniedClaims=false`.
   Until then the payment run also remits legacy `Denied` rows that are in no 835 yet (once,
   without a finalize call — they are already final), so no denial goes unremitted whether or
   not the backfill has run.
7. Disable the endpoint again (`Claims__DenyStatusBackfill__Enabled=false`).

## Rollback

There is no automatic reverse. A claim moved to `Deny` by mistake is finalized back to `Denied`
by the next payment run (it is in an 835, so it is only finalized, never re-remitted), or can be
denied again by an examiner and remitted normally.
