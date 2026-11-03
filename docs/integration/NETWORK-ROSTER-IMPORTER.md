# Network roster importer

Loads a plan's network roster from its core admin or credentialing system:
which providers are in which network, for which plan and dates. Claims-service
decides in or out of network by asking whether the billing provider is an
active member of a network the plan's in-network tier names
(`networkTiers[].networkId`). Without the roster every provider is out of
network, which is the `NETWORK` gap in [shadow adjudication](SHADOW-ADJUDICATION.md).

## Endpoint

`POST /api/v1/providers/import/network-roster` (provider-service), multipart:

| Field | Meaning |
|---|---|
| `file` | The roster, CSV (comma, pipe or tab), header row first |
| `lineOfBusiness` | For rows that name none: Commercial (default), Medicare, Medicaid, Exchange, TRICARE or VA |
| `dryRun` | `true` to check and report only |

Permission `providers:write`. Two more are used when the caller holds them:

- `networks:write`: a network the file names that does not exist is created
  (as a PPO unless the row's `Network Type` says otherwise). Without it those
  rows are refused.
- `providers:credential`: a row marked credentialed records the core's
  approval (see [Credentialing](#credentialing)). Without it the report warns
  and nothing is recorded.

`./scripts/cpcds/trial.sh check|load <folder>` sends any file whose name
contains `roster`, after members and before claims (`CHO_PROVIDER_URL`,
default `http://localhost:5004`).

## File layout

One row per provider, network and participation. Header names are matched
ignoring case, spaces and punctuation.

| Column | Required | Values |
|---|---|---|
| `NPI` | yes | 10 digits |
| `Network` | yes | The network's id, or its name (case ignored) |
| `Effective Date` | yes | `yyyy-MM-dd`, `yyyyMMdd` or `MM/dd/yyyy` |
| `Term Date` | no | Same formats; after the effective date |
| `Plan` | no | The benefit plan's `planId` (e.g. `PPO-GOLD`), recorded on the participation; blank: not tied to one plan. In or out of network is decided by the network |
| `Line of Business` | no | As `lineOfBusiness` above |
| `Tier` | no | `Tier1` (default), `Tier2`, ... or a number |
| `Credentialed` | no | `yes` / `no` (also `Y`, `approved`, `pending`, ...) |
| `Credentialed Date` | no | When the core approved the provider |
| `Network Type` | no | For a network the import creates: `PPO` (default), `HMO`, `EPO`, ... |

```csv
NPI,Network,Plan,Effective Date,Term Date,Credentialed
1234567893,Gold PPO network,PPO-GOLD,2026-01-01,,yes
1588667638,Gold PPO network,PPO-GOLD,2026-01-01,2026-12-31,yes
```

## What it does

- **NPIs CHO does not have** are created from NPPES (name, type, primary
  taxonomy, practice address) as network providers. An NPI NPPES does not know
  is refused: create it in CHO first.
- **Providers CHO knows only from a claim** (reference-only, created by the
  CPCDS claim load) become network providers (`referenceOnly: false`).
- **Participations** are added once. A row whose network, plan, line of
  business and effective date the provider already has is `unchanged`, so a
  roster can be reloaded. The import never removes or ends a participation:
  send a `Term Date` to end one.
- Each provider changed gets one new version (amend, add, activate), the same
  path as `POST /api/v1/providers/{id}/network-participations`.

The report counts rows, participations added, unchanged rows, providers
created, providers that became network providers, networks created, providers
credentialed and refused rows, with the first 100 errors as `line N: reason`.

## Credentialing

Claims-service also checks that an in-network provider is credentialed on the
date of service. When the core (or the plan's delegated credentialing
verification organization) has credentialed the provider, mark the row
`Credentialed: yes`. The import then records a `DelegatedAuthority` approval,
the same record `PUT /api/v1/providers/{id}/credentialing` writes, dated the
`Credentialed Date` or else the provider's earliest effective date in the file,
so claims for earlier dates of service see it. A provider already approved is
left alone. Denials, suspensions and re-credentialing stay in the credentialing
workflow.

## Directory

A network provider can be published in the public Provider Directory
(Plan-Net) of a plan whose `PublicProviderDirectory` is configured. A trial
tenant with no directory configured publishes nothing.
