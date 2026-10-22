# Contract dual-read window and cutover (ADR 016 phases 4 and 5)

Prices every claim with both the legacy engine contract and the canonical
contract the backfill wrote, pays on one of them, and reports per line what the
other would have allowed. During the window (phase 4) it pays on **legacy**;
after the cutover (phase 5) it pays on **canonical** and keeps comparing against
legacy for another cycle.

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
FeeScheduleEngine__ContractDualRead__Mode              Off | Shadow | Canonical  (default Off)
FeeScheduleEngine__ContractDualRead__Tenants           comma-separated; empty means every tenant
FeeScheduleEngine__ContractDualRead__CanonicalTenants  comma-separated; these are paid on canonical
FeeScheduleEngine__CanonicalProviderContractCollection default ProviderContractsCanonical
```

A tenant's path is `Off` → `Shadow` → one full monthly claim cycle of nothing but
matches → `Canonical`, one tenant at a time.

`Mode=Off` is a no-op: the engine is registered exactly as it was before phase 4,
one contract read and one pricing pass. `Mode` applies to the tenants named in
`Tenants` (empty means all of them); every other tenant is read and priced
exactly as before.

`CanonicalTenants` is the per-tenant cutover flag, and it wins over `Mode`, so one
deployment can hold one tenant in the window while another is already paid on
canonical. `Mode=Canonical` with an empty `Tenants` cuts over everyone, which is
what the end of the rollout looks like.

`Enabled=true` is still read as a synonym for `Mode=Shadow`, for a deployment
configured before phase 5.

Inside the window a claim costs **two pricing passes**. The second one reads no
database: it prices out of the contracts the first pass already fetched. The
canonical contract is read once per claim, not once per line.

Open the window for one tenant at a time, and only after that tenant's backfill
plan has an empty conflict report and no `Review` flags standing.

## Reading the result

Every line inside the window emits `cho.pricing.contract_parity.total`, tagged
`cho.tenant`, `cho.mode` (`shadow` or `canonical`) and `cho.outcome`. The two
amounts on the record are named by the store they came from, and the delta is
always canonical minus legacy, so a dashboard built during the window still reads
correctly after the cutover:

| `cho.outcome` | What it means | What to do |
| --- | --- | --- |
| `match` | same allowed amount, same schedule, same route | nothing — this is the one that has to be all of them |
| `same_amount_different_route` | same money, reached differently: another schedule, rate source, network status, per-stay or lesser-of decision | explain it before cutover; the money agrees today, and the next rate load is where it stops agreeing |
| `amount_delta` | different allowed amount. The size is on `cho.pricing.contract_parity.amount_delta` | a contract disagreement; fix the store that is wrong and re-plan the backfill |
| `canonical_pends` | the canonical contract pends a line legacy priced | usually a stored-not-evaluated provision (stop-loss, cost outlier) or a term naming a missing schedule. The log line names the reason |
| `legacy_pends` | legacy pends a line the canonical contract priced | the canonical contract is the better one; confirm that is intended |
| `no_canonical_contract` | the backfill has not written this provider and plan, **or** the canonical store could not be read. After the cutover it also means the line fell back to legacy | run the backfill for the provider, or check the store |
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

Only then does the cutover below move payment onto the canonical result.

## The cutover, and rolling it back

Add the tenant to `CanonicalTenants`. From the next claim its lines are paid on
the canonical contract, and the legacy contract is priced in the shadow instead,
so the comparison keeps running in the other direction. Leave it that way for one
more cycle: a regression then shows up as a parity delta rather than as a
provider's phone call.

**Rollback is the same setting, moved back.** Remove the tenant from
`CanonicalTenants` and it is paid on legacy again from the next claim. Nothing is
migrated in either direction and no claim is rewritten, so there is no window
during which a rollback is unsafe. Claims already adjudicated keep the amounts
they were adjudicated at; reversing one of those is an ordinary claim adjustment,
not part of this switch.

One case to watch after the cutover: a provider the backfill never reached is
still **paid on the legacy contract**, by a deliberate fallback, rather than
dropped to the plan default. The cutover is gated on an empty conflict report, so
this is a provider that appeared since. It pays correctly and is reported as
`no_canonical_contract` — run the backfill for it.

## Closing the window entirely

Set `Mode=Off` and empty both tenant lists. The window is a read and a
comparison, so there is nothing to undo.

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

**The PricingApi schedule fallback.** ADR 016 phase 4 also calls for PricingApi
reading the canonical schedule store first and falling back to
`fee_schedule_entries`. That is a job over schedules rather than contracts, it
depends on the fee-schedule re-ingest that phase 3 left open, and it is not part
of this window.

**Retiring the legacy stores.** The rest of phase 5 — making the legacy engine
contract collection read-only and archiving it, retiring `fee_schedule_entries`,
and removing the dual-read code itself — can only happen once every tenant has
been cut over and has stayed cut over. Until then the legacy contract is what a
rollback pays on, so it stays where it is.
