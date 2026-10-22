# Demo persona seeder

Seeds a demo tenant with a small cast of named, **synthetic** personas, and writes a
demo script with the IDs and numbers to type.

Record search always needs criteria, in every tenant (`docs/architecture/portal-search.md`
§3.5). Demos therefore don't browse lists: they look records up the way a real user would,
using the IDs in the script.

## The cast

| Persona | Shows |
|---|---|
| **Delgado family** (Desert Sun Builders, employer PPO) | Maria: family, PCP, an approved PT authorization and three PT visits toward her visit limit. Luis: other insurance on file, a claim for coordination of benefits. Sofia: a brain MRI billed without prior authorization, and the provider's appeal. |
| **Gloria Pham** (Medicaid) | State Medicaid ID on her card; an inpatient stay at Mesa Vista Medical Center, 3 of 5 days approved, under concurrent review. |
| **Harold Jenkins** (Medicare Advantage) | MBI on his card; an expedited knee MRI request submitted today, decision open. |
| **Canyon Ridge Orthopedics** (group, three clinicians) | Approved claims ready for a payment run; a provider-services "where's my money" call. |
| **Copper State Logistics** (employer group) | A premium invoice for three employees, which goes past due and then delinquent. |
| **John Smith / Jon Smyth** | Exact vs "sounds like" name search. |

Everything is defined in `PersonaCatalog.cs`. Dates are relative to the run date
(`--as-of`), so the stories stay current. IDs are fixed, so a re-run finds what an
earlier run created rather than duplicating it.

## Running it

```bash
export CHO_BEARER_TOKEN=...   # a CHO token for the demo tenant (services take the tenant from it)
dotnet run --project tools/DemoPersonaSeeder -- \
  --tenant demo --confirm-synthetic demo --out demo-script.md
```

- **`--confirm-synthetic`** must repeat the tenant ID. The seeder writes invented people
  into whatever tenant it targets; never point it at a tenant holding real members.
- **`--dry-run`** writes the script without calling any service.
- **Service URLs** default to local docker compose (the portal's `appsettings.Development.json`).
  Override them with `--claims-url` etc. or `CHO_*_URL` variables (`--help` lists them).

**Permissions.** The token needs the permissions of each create:
- `settings:manage` for plans;
- `providers:write` and `networks:write`;
- `contracts:write`;
- `enrollment:process` for groups;
- `members:write` and `coverage:write`;
- `authorizations:write` and `authorizations:decide`;
- `claims:work`;
- `appeals:write`;
- `billing:run`.

A TenantAdmin token has all of them.

The run prints one line per step:
- `+` created
- `=` already there
- `~` updated
- `!` failed

A failed step doesn't stop the run. The script lists failures at the end, and the exit code is 1.

## What it doesn't do

- **Payment runs.** Executing a payment run needs a trading partner for each payee, and approval by a different user than the one who created the run (`payments:approve`). The seeder stops at approved claims; run the payment run from the portal to give Canyon Ridge a check number.
- **Past-due premium.** When the billing day has already passed, billing sets the due date 30 days out. Copper State's invoice becomes past due on that date, and delinquent after the grace period, once delinquency processing runs.
- **Patient account numbers.** `POST /api/v1/claims` has no field for the provider's patient control number, so the provider's account numbers live in the demo script only.
- **Claim outcomes** come from the tenant's adjudication rules. The script shows the intended story next to the status each claim actually reached (the seeder waits up to `--claim-wait-seconds` for each).
