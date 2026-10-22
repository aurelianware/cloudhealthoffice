# Contract dual-read window (ADR 016 phase 4)

Prices every claim with both the legacy engine contract and the canonical
contract the backfill wrote, **pays on legacy**, and reports per line what the
canonical contract would have allowed.

Decision record: [ADR 016](../adr/016-unified-contract-pricing.md). The backfill
that fills the canonical store is
[its own runbook](PROVIDER-CONTRACT-BACKFILL.md).

## What it is for

The window is how the cutover is earned rather than hoped for. Nothing about the
canonical contract is trusted until a tenant's own claims, for a full monthly
cycle, price the same either way. Until then the claim is paid on the contract
that paid it yesterday.

Two things follow from that, and they are the whole design:

- **Nothing the window does can change an allowed amount.** The result the
  caller gets back is always the paid pass. The shadow pass is compared and
  discarded.
- **Nothing the window does can stop a claim being paid.** A canonical store
  that is down, a document this build cannot read, a shadow pass that throws —
  each is recorded as an outcome and the claim is paid on legacy.

## Turning it on

Per tenant, through configuration on benefit-plan-service. Off for everyone
until a tenant is named:

```
FeeScheduleEngine__ContractDualRead__Enabled   false by default
FeeScheduleEngine__ContractDualRead__Tenants   comma-separated; empty means every tenant
FeeScheduleEngine__CanonicalProviderContractCollection   default ProviderContractsCanonical
```

`Enabled=false` is a no-op: the engine is registered exactly as it was before
phase 4, one contract read and one pricing pass. With `Enabled=true` and
`Tenants` naming tenant `t1`, every other tenant is also still read and priced
exactly as before — the second read is not made and nothing is compared.

Inside the window a claim costs **two pricing passes**. The second one reads no
database: it prices out of the contracts the first pass already fetched. The
canonical contract is read once per claim, not once per line.

Open the window for one tenant at a time, and only after that tenant's backfill
plan has an empty conflict report and no `Review` flags standing.

## Reading the result

Every line inside the window emits `cho.pricing.contract_parity.total`, tagged
`cho.tenant` and `cho.outcome`:

| `cho.outcome` | What it means | What to do |
| --- | --- | --- |
| `match` | same allowed amount, same schedule, same route | nothing — this is the one that has to be all of them |
| `same_amount_different_route` | same money, reached differently: another schedule, rate source, network status, per-stay or lesser-of decision | explain it before cutover; the money agrees today, and the next rate load is where it stops agreeing |
| `amount_delta` | different allowed amount. The size is on `cho.pricing.contract_parity.amount_delta` | a contract disagreement; fix the store that is wrong and re-plan the backfill |
| `canonical_pends` | the canonical contract pends a line legacy priced | usually a stored-not-evaluated provision (stop-loss, cost outlier) or a term naming a missing schedule. The log line names the reason |
| `legacy_pends` | legacy pends a line the canonical contract priced | the canonical contract is the better one; confirm that is intended |
| `no_canonical_contract` | the backfill has not written this provider and plan, **or** the canonical store could not be read | run the backfill for the provider, or check the store |
| `shadow_failed` | the shadow pass itself failed | a bug, not a contract finding. Nothing is known about those lines' parity |

Each non-`match` line is also logged at warning level with the provider NPI, the
plan, the procedure code, both amounts and the reason, so a delta can be chased
to the contract that caused it. No member identity is on the metric or the log.

Matches are counted too, deliberately: "no deltas" means nothing beside how many
lines were compared.

## The exit criterion

A tenant leaves the window when **one full monthly claim cycle** produced no
outcome other than `match` that nobody can explain. In practice:

1. `amount_delta`, `canonical_pends` and `no_canonical_contract` must be zero.
2. `same_amount_different_route` must be zero, or every instance written down
   with why it is harmless.
3. `shadow_failed` must be zero — a failing shadow pass means the window
   measured nothing, not that it found nothing.
4. `match` must be a plausible count for the tenant's monthly line volume. A low
   total means the window was not actually open for the claims that matter.

Only then does phase 5's per-tenant flag move payment onto the canonical result.

## Closing it again

Set `Enabled=false`, or drop the tenant from `Tenants`. The window is a read and
a comparison, so there is nothing to undo: no claim was priced on the canonical
contract and nothing was written.

## Writes during the window

Contract writes through the engine's repository go to the **canonical** store
only. That is the direction ADR 016 settles on — provider-contracts-service
becomes the one contract writer, and the legacy collection is archived at phase
5 — and it is why the two cannot drift while the window is open.

Nothing writes the legacy engine collection in the live path: the ADR's own
finding is that it has no admin API. A seeder that writes it goes through its own
client and does not pass the dual read, so during the window the legacy store
should be left alone.

## Not covered here

ADR 016 phase 4 also calls for PricingApi reading the canonical schedule store
first and falling back to `fee_schedule_entries`. That is a job over schedules
rather than contracts, it depends on the fee-schedule re-ingest that phase 3 left
open, and it is not part of this window.
