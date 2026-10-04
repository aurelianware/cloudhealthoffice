# Bank-account data

CHO holds two kinds of bank accounts. Both redirect money, so both follow the
same rules: masked reads, dual control on every change, and as few readers of
the full numbers as possible.

| | Provider accounts (payments out) | Sponsor accounts (premium auto-debit in) |
|---|---|---|
| Service | provider-service | sponsor-service |
| Storage | `ProviderBankAccounts`, one record per tenant and provider | `SponsorBankAccounts`, one record per tenant and sponsor group |
| Propose a change | providers:write | billing:run or enrollment:process |
| Approve or reject | payments:approve, user token, not the proposer | payments:approve, user token, not the proposer |
| Masked reads | providers:read, payments:read (pending: payments:approve) | billing:read, payments:read, enrollment:read (review reads: also payments:approve) |
| Full numbers | capitation-service's service token only | premium-billing-service's service token only |
| Encrypted at rest | yes (records and the provider-row copy) | yes |

## Dual control

Each record holds the **active** (approved) account, at most one **pending**
change, and the history. Every write is conditional on the record's revision
(Mongo filtered replace, Cosmos If-Match), so approving a change and switching
the active account is one atomic step.

- A change is only proposed. It does nothing until a different user holding
  `payments:approve` approves it. The first account is pending too.
- The proposer cannot approve (403 "Separation of duties"). A service token
  cannot approve or reject (403), although service tokens satisfy every tenant
  permission. There is no per-tenant override.
- Approval is refused with 409 when the change is no longer pending, when the
  active account changed after the proposal, or when another write wins the
  revision race.
- A newer proposal supersedes (cancels) the pending one. The proposer side can
  cancel; the approver side can reject.
- Once a change is decided, its full numbers are dropped and only the last 4
  are kept. The approved account's numbers live only on the active account.
- Audit log entries record each step: actor, sponsor or provider, tenant,
  change id and status. They never include routing, account or tax numbers.
  Sponsor events are 4811 to 4817; provider events are 4701 to 4708 (4706:
  full read for a disbursement; 4707: a record with legacy plaintext numbers;
  4708: a provider-row copy that could not be decrypted).

Sponsor endpoints (`api/v1/sponsors/{group}/...`):

| Endpoint | Permission |
|---|---|
| `GET bank-account` (active and pending, masked) | billing:read, payments:read or enrollment:read |
| `GET bank-account-changes`, `GET bank-account-changes/pending` (masked) | the same, or payments:approve |
| `POST bank-account-changes` (propose, 202) | billing:run or enrollment:process |
| `POST bank-account-changes/{id}/approve`, `/reject` | payments:approve, user token, not the proposer |
| `POST bank-account-changes/{id}/cancel` | billing:run or enrollment:process |
| `GET /api/v1/internal/sponsors/{group}/bank-account` (full numbers) | `[RequireServiceClient("premium-billing-service")]` |

Provider endpoints (`api/v1/providers/npi/{npi}/...`; also under the legacy
`api/providers` prefix):

| Endpoint | Permission |
|---|---|
| `GET bank-account` (active account, masked; capitation chooses the disbursement method with it) | providers:read or payments:read |
| `GET bank-account-changes`, `GET bank-account-changes/pending` (masked) | payments:approve or providers:read |
| `PUT bank-account`, `POST bank-account-changes` (propose, 202) | providers:write |
| `POST bank-account-changes/{id}/approve`, `/reject` | payments:approve, user token, not the proposer |
| `POST bank-account-changes/{id}/cancel` | providers:write |
| `GET /api/v1/internal/providers/npi/{npi}/bank-account` (full numbers) | `[RequireServiceClient("capitation-service")]` |

A proposal holds the whole account and the auto-debit enrollment
(`eftEnabled`, `preferredMethod`, Stripe ids). If the routing and account
numbers are left out, the active account's numbers are kept, so turning
auto-debit on or off does not require typing them again. It still needs
approval.

## Who can read the full numbers

- **Sponsor accounts:** only premium-billing-service's service token, through
  the internal endpoint. That endpoint returns the active approved account,
  never a pending one. A sponsor with `eftEnabled` false is answered without
  numbers. Every user token is refused, TenantAdmin and PlatformAdmin
  included, and so is every other service. The tenant comes from the token.
  Each read is audit-logged (event 4816).
- premium-billing-service makes that call only after it has checked the user
  who releases the debit: `payments:approve`, a user token, and not the user
  who prepared the invoice (maker-checker). The user releases the debit; the
  service token only fetches the numbers for it. A missing approved account or
  a refusal is an item needing attention. "Not enrolled" is a normal skip.
- **Provider accounts:** only capitation-service's service token, through
  `GET /api/v1/internal/providers/npi/{npi}/bank-account`. It returns the
  active account (the approved one; for a provider whose account was set
  before dual control and never changed since, the account on the provider
  row, exactly as the masked read capitation already used), never a pending
  change: none is 404 `NoApprovedAccount` (unknown NPI: `ProviderNotFound`).
  It returns routing and account numbers, type, holder, method, Stripe id and
  last 4; never the bank tax id. An account without EFT enabled is answered
  without numbers. Numbers that cannot be decrypted are 503, never
  ciphertext. Every user token is refused, TenantAdmin and PlatformAdmin
  included, and so is every other service. The tenant comes from the token.
  Each read is audit-logged (event 4706: provider, NPI, tenant, calling
  service; never a number). Every other provider response is masked.
- capitation-service makes that call only when it builds a NACHA credit
  (`POST disbursements/nacha-file` and the NACHA part of
  `POST disbursements/batch`), after it has checked the user who releases the
  payment: `payments:approve` and separation of duties (not the user who
  prepared the statement or ran the run; a disbursement whose statement is
  missing is not paid). It calls with its own service token from a dedicated
  client (`ProviderBankAccounts`, `HttpProviderBankAccountSource`), never the
  user's. No approved account, an account without EFT numbers, a refusal or
  no answer is an item needing attention (`needsAttention` in the response;
  the disbursement stays Pending with the reason in `errorMessage`), never a
  silent skip. "EFT not enabled" on the masked read stays a normal skip. The
  masked read still decides the method (Stripe, check, NACHA); Stripe and
  check payments never read the numbers.

## Masking

Masked reads keep the last 4 digits only (`routingNumberLast4`,
`accountNumberLast4`). The sponsor document's `BillingInfo.billingAccountNumber`
is masked in every sponsor response (get, list, create, update) as
`billingAccountNumberLast4`. A `PUT` that leaves the number out keeps the
stored one, so a client that writes back what it read loses nothing; an empty
string clears it.

## Encryption at rest

`CloudHealthOffice.FieldProtection` (`src/services/shared/`) is the shared
mechanism. A service opts in with a project reference and
`AddChoFieldProtection(configuration, environment, "<service>")`.

- It is built on ASP.NET Core Data Protection (authenticated encryption, with
  key rotation handled by Data Protection). A protected value is stored as
  `enc:v1:<ciphertext>`. The purpose includes the service name, so one
  service's values cannot be decrypted by another.
- **Key ring:** an Azure Blob, wrapped by a Key Vault key
  (`PersistKeysToAzureBlobStorage` + `ProtectKeysWithAzureKeyVault`), so all
  pods share the same keys. The configuration keys are:
  - `FieldProtection:KeyRing:BlobUri`, e.g.
    `https://<account>.blob.core.windows.net/dataprotection/sponsor-service/keys.xml`;
  - `FieldProtection:KeyRing:KeyVaultKeyId`, e.g.
    `https://<vault>.vault.azure.net/keys/sponsor-service-dataprotection`;
  - optionally `FieldProtection:KeyRing:ManagedIdentityClientId`, for a
    user-assigned identity. Otherwise `DefaultAzureCredential` is used.

  The identity needs blob read/write on the container, and `get`, `wrapKey`
  and `unwrapKey` on the key. Each service has its own key ring: provider-service
  uses, for example, `.../dataprotection/provider-service/keys.xml` and a
  `provider-service-dataprotection` key, set through the same three settings
  (`FieldProtection__KeyRing__BlobUri`, `FieldProtection__KeyRing__KeyVaultKeyId`,
  optionally `FieldProtection__KeyRing__ManagedIdentityClientId`).
- In Development and Testing, without those settings, the key ring is a local
  directory (`FieldProtection:KeyRing:LocalDirectory`, defaulting to the
  user's local application data).
- Anywhere else, without a key ring, nothing is encrypted with pod-local
  keys. Writes of protected fields fail (bank-account endpoints answer 503).
  Already-stored plaintext can still be read. Setting only one of `BlobUri` and
  `KeyVaultKeyId` fails at startup.
- **sponsor-service** encrypts the routing and account numbers in
  `SponsorBankAccounts` (active account and pending change) and
  `BillingInfo.BillingAccountNumber` on sponsor documents.
- **provider-service** (purpose and application name `provider-service`)
  encrypts routing, account and bank tax id numbers in `ProviderBankAccounts`
  (active account, pending change, history) and in the legacy
  `Provider.BankAccount` copy on provider documents and version rows. The
  last 4 are filled in at write time, so masked reads need no decryption.
  Writes of a bank number without a key ring answer 503 before anything is
  written (a provider create or update with a bank account included). A
  request value in the stored `enc:v1:` form is refused (400). A
  `ProviderBankAccounts` value that does not decrypt fails that read (503).
  A provider-row copy that does not decrypt does not fail provider reads
  (claims, rosters, FHIR never show the numbers); it stays encrypted on the
  object, is logged (event 4708) and the full read refuses it (503).
- **Legacy plaintext:** a value without the `enc:v1:` prefix is read as is.
  For a sponsor's billing account number, each read logs event 4817 (sponsor
  and tenant, never the number). It is stored encrypted on the next create or
  full update of that sponsor. The status-only write leaves it unchanged.
  Bank-account records are re-encrypted on their next write. For provider
  bank-account records each read of plaintext logs event 4707 (provider and
  tenant). A provider row is re-encrypted on its next whole-row write (update,
  draft, activate, supersede, suspend, terminate); rows never written again
  (superseded versions) need the migration below.

### Re-encrypting existing provider data

`provider-service --encrypt-bank-accounts` encrypts every plaintext routing,
account and tax number in `Providers` (all version rows) and
`ProviderBankAccounts` (active, pending and history), tenant by tenant. Run
it once after deploying, with provider-service's own configuration (the same
`MongoDb` and `FieldProtection` settings and identity as the pods, so it
writes with the shared key ring):

```
dotnet provider-service.dll --encrypt-bank-accounts [--tenant <id>]... [--dry-run]
# from a checkout:
dotnet run --project src/services/provider-service -- --encrypt-bank-accounts --dry-run
```

- Without `--tenant` every tenant found in the two collections is processed,
  plus rows without a tenant. With `MongoDb:UseTenantScoping`, name each
  tenant (one database per tenant).
- It prints, per tenant, rows and records scanned, encrypted, already
  encrypted and conflicts. It never prints a number.
- Idempotent and resumable: encrypted values are left alone, and each
  document is updated on its own with a compare-and-set on the values it read
  (and, for records, the revision, which it does not bump). A document the
  service changed meanwhile is a conflict, left for the next run. Stop it at
  any time and run it again.
- Exit code 0: nothing left in plaintext. 2: conflicts or failures, run again.
  1: it did not start (no key ring outside Development/Testing, half a key-ring
  configuration, or no Mongo connection). It covers Mongo deployments; a
  Cosmos deployment re-encrypts each value on its next write.
