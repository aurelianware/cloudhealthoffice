# Provider contract backfill (ADR 016 phase 3)

Builds the canonical provider contract out of the four places a provider's
rates live today, and writes it to a canonical contract collection of its own,
beside the engine's existing one.

Tool: `tools/ProviderContractBackfill`. Decision record:
[ADR 016](../adr/016-unified-contract-pricing.md).

## What it reads, and what it writes

| Store | Collection | Read for |
| --- | --- | --- |
| Legal contract (provider-contracts-service) | `provider_contracts` | identity, scope, dates, network participation, and the rate sheet when the contract already carries one |
| Fee-for-service rate configs | `ffs_rate_configs` | a `FeeScheduleRef` term: schedule, `FeeSchedulePercentage` as the multiplier, the two modifier toggles, the effective dates |
| Capitation rate configs | `capitation_rate_configs` | a `Capitation` term |
| Engine contract | `ProviderContracts` | `ContractLines` as scoped terms, `FeeScheduleId` as the last-resort term, `LesserOfBilledCharges` — **read only, never written** |
| Engine fee schedules | `FeeSchedules` | checking that every schedule a term names exists |

It writes **only** `ProviderContractsCanonical`, one document per
provider-and-plan, under the engine's own key `{tenant}:{npi}:{plan}`. Ids are
deterministic, so a second run replaces rather than duplicates.

The canonical contracts go in their own collection rather than over the
engine's. ADR 016 phase 4 prices every claim with both contracts and pays on the
legacy one, which it can only do while both exist — and it means this backfill
is undone by dropping a collection instead of restoring a dump. **Nothing the
tool does changes what the engine prices today.**

The rate configs are read as raw documents rather than through their services'
models. Nothing has ever written `ffs_rate_configs` — ffs-service has a model
and a `Program.cs` and no repository — so whatever is in it was put there by
hand or by a seeder, and a reader that insisted on a shape would fail on the
first surprise instead of reporting it.

## Running it

Settings come from `appsettings.json` or the environment, never the command
line:

```
MongoDb__ConnectionString            required
MongoDb__DatabaseName                default CloudHealthOffice
Collections__LegalContracts          default provider_contracts
Collections__FfsRateConfigs          default ffs_rate_configs
Collections__CapitationRateConfigs   default capitation_rate_configs
Collections__EngineContracts         default ProviderContracts (read, never written)
Collections__CanonicalContracts      default ProviderContractsCanonical (the only thing written)
Collections__FeeSchedules            default FeeSchedules
```

### 1. Plan

```
dotnet run --project tools/ProviderContractBackfill -- \
  plan --out plan.json --csv plan.csv \
  --confirm-database CloudHealthOffice [--tenant t1,t2]
```

Reads only. It prints the conflict report, writes the plan as JSON and, with
`--csv`, a one-row-per-provider summary for the people who have to agree to it.
Without `--tenant` it plans every tenant that has a contract in either store.

The JSON holds the **actual documents** that would be written, so what a
reviewer reads is what lands. The CSV is for reading, not for applying.

### 2. Review

Every row carries a decision:

| Decision | Meaning |
| --- | --- |
| `Ready` | complete; nothing needs a person |
| `Review` | written, but flagged — see below |
| `Blocked` | **not** written; the stores disagree or the contract names something that does not exist |
| `Skipped` | nothing to do: no rates anywhere, so there is nothing to make canonical |

`Review` means one of: the contract came from an engine contract with no legal
agreement behind it (`engine-only`), the legal contract is not Active, its
network participation is `TieredException` (which the engine's network status
has no value for, so it becomes in-network and the tiering has to be carried by
the terms' scopes), or the tenant has no fee schedules at all so the schedules
the terms name could not be checked.

A `Review` row is still written, because a flagged contract that prices is
better than a provider with no contract — but **the cutover must not happen
while the flag stands**.

`Blocked` is the conflict report. ADR 016 requires every run to produce one, and
the phase 5 cutover is gated on it being empty for the tenant. The conflicts
are:

- **More than one rate prices every claim line for the same dates.** A
  fee-for-service rate config and the engine contract naming different
  schedules, or two rate configs at different percentages. A canonical contract
  holds one default rate, and picking one would change what the provider is paid
  without anyone deciding to. Resolve it in whichever store is wrong, then
  re-plan.
- **A hybrid contract.** Some services capitated, the rest fee-for-service, and
  nothing records which are which. Enter the capitated scope on the contract's
  terms (`PUT /api/v1/contracts/{id}/reimbursement-terms`) and re-plan.
- **The terms name a fee schedule the tenant does not have.** The engine pends a
  line whose term names a missing schedule, so writing it would stop the
  provider's claims from paying. Load the schedule, or correct the rate config.
- **The legal contract names no plan ids**, so it covers every plan in its line
  of business. The engine looks a contract up by one plan, so name the plans on
  the contract and re-plan.
- **More than one legal contract covers the same provider and plan.** Both would
  project onto one engine id. Narrow the contracts, or terminate one.
- **A fee-for-service rate config names no fee schedule**, so there is no rate to
  make canonical.
- **The rate sheet built from these stores is not valid** — a term the engine
  cannot price, a duplicate term id, or a term an earlier one always answers
  before. The message names the rule.

Note that two rates in succession — one that ends in May and its replacement
from June — are a rate history, not a conflict, and become two terms. And the
same rate recorded in two stores is one term: a duplicate is only collapsed when
the one kept is in force for at least as long, so collapsing can never leave a
gap where no rate prices.

### 3. Dry run

```
dotnet run --project tools/ProviderContractBackfill -- \
  apply --plan plan.json --confirm-database CloudHealthOffice --log backfill.log
```

Writes nothing. Prints `WOULD create` / `WOULD replace` per contract, then the
conflict report.

### 4. Apply

```
dotnet run --project tools/ProviderContractBackfill -- \
  hash --plan plan.json

dotnet run --project tools/ProviderContractBackfill -- \
  apply --plan plan.json --confirm-database CloudHealthOffice \
  --execute --sha256 <hash> --operator <your name> --log backfill.log
```

`--execute` needs the plan's current hash and an operator name, so a plan that
was edited — or regenerated against a database that has moved on — cannot be
applied by accident. `apply` also refuses a plan built against a different
database than the one it is connected to.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | done, nothing blocked or failed |
| 1 | some provider is blocked by a conflict, or a write failed |
| 2 | invalid arguments or plan file — nothing changed |
| 3 | plan hash mismatch — nothing changed |
| 4 | wrong database — nothing changed |

Exit 1 after `--execute` means the writable contracts **were** written and
something else was blocked. Re-plan after resolving the conflicts; applying the
new plan replaces the same documents.

## Rolling back

The backfill only ever writes `ProviderContractsCanonical`, and nothing reads
that collection until ADR 016 phase 4 ships, so rolling back is dropping what
was written. No dump is needed beforehand.

```
mongosh --eval 'db.ProviderContractsCanonical.deleteMany({"TenantId": {"$in": ["t1"]}})' CloudHealthOffice
```

The engine's own `ProviderContracts` was never touched, so the pricing in force
before the run is still the pricing in force after it, and after the rollback.

## After the backfill

The backfill is phase 3, and on its own it changes no payment. Phase 4 is the
dual-read window: the engine prices with both the canonical and the legacy
contract, pays on legacy, and emits a parity metric per line, for at least one
full monthly claim cycle per tenant. Phase 5 is the per-tenant flag that moves
payment onto the canonical result.

So a canonical contract starts pricing only when phase 4 begins reading it —
which is why a `Blocked` row must stay unwritten, and a `Review` row must be
looked at, before that window opens. A tenant with anything in its conflict
report is not ready for phase 4.

## Not covered here

ADR 016 phase 3 also calls for re-ingesting the Pricing API's
`fee_schedule_entries` into global `FeeSchedule` documents through the CMS
loaders, with the cross-store parity test as the invariant. That is a separate
job over schedules rather than contracts, and this tool does not do it.
