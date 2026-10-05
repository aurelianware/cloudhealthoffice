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
| Full numbers | capitation-service's service token only; NACHA files go to the bank directly | premium-billing-service's service token only; NACHA files go to the bank directly |
| Encrypted at rest | yes (records and the provider-row copy) | yes |

No person ever holds a file of full account numbers. premium-billing-service
and capitation-service send NACHA files to the tenant's bank themselves (see
[NACHA files go to the bank directly](#nacha-files-go-to-the-bank-directly));
the approver who releases the payment gets a masked summary and a receipt.

## Dual control

Each record holds the **active** (approved) account, at most one **pending**
change, and the history. Every write is conditional on the record's revision
(Mongo filtered replace, Cosmos If-Match), so approving a change and switching
the active account is one atomic step.

- A change is only proposed. It does nothing until a different user holding
  `payments:approve` approves it. The first account is pending too.
- **Provider accounts set before dual control are not active; they must be
  proposed and approved.** There were no live payments before dual control,
  so every account goes through approval once. The `Provider.BankAccount`
  copy on the provider row is never the active account: with no approved
  record, the masked read is 404, the full read is 404 `NoApprovedAccount`,
  `GET` provider (by id or NPI) shows no bank account, and capitation marks
  the disbursement as needing attention. Records an earlier build seeded from
  the row (`activeChangeId` `legacy-provider-row`) do not count either: an
  account is active only when `activeChangeId` names an Approved change in the
  record's history. To restore such an account, propose it again (its full
  numbers; a masked echo of the row copy is not a change) and have a second
  user approve it. List, search and version reads still show the row copy,
  masked; it is not what payments use.
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
  active account, the one approved through dual control (never the provider
  row's copy from before dual control, and never a pending change): none is
  404 `NoApprovedAccount` (unknown NPI: `ProviderNotFound`).
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

## NACHA files go to the bank directly

`CloudHealthOffice.NachaTransmission` (`src/services/shared/`, kept out of
Infrastructure so SSH.NET does not reach every service) sends each NACHA file
from the service to the tenant's bank by SFTP. premium-billing-service (debits:
`POST eft/nacha/generate`, the NACHA part of `POST eft/drafts/batch`) and
capitation-service (credits: `POST disbursements/nacha-file`, the NACHA part of
`POST disbursements/batch`) use it after the existing checks (payments:approve,
user token, maker-checker).

- **What the approver gets:** counts, debit and credit totals, one line per
  entry (sponsor group or provider NPI, name, last 4, amount, trace number),
  the transmission status (`Transmitted`, `AwaitingRetrieval`, `NotSent`) and,
  when delivered, a receipt: tenant, remote file name, destination (host and
  directory), byte size, SHA-256 of the file, entry count, total debits and
  credits (read from the entry records of the bytes sent), transmitted at and
  by, run id and batch id (the file reference). No response has a
  `fileContent`, a full routing number or a full account number;
  `eft/nacha/generate-and-download` is removed and the portal has no download.
- **State:** drafts and disbursements become `Submitted` only after the bank's
  server accepted the file. When it cannot be sent they become
  `AwaitingRetrieval`. When it could not even be held (below), they stay
  `Pending` with the reason, for the next release.
- **Per-tenant settings:** tenant-service
  `configuration.paymentControls.nachaTransmission`:

  ```json
  {
    "enabled": true,
    "host": "sftp.bank.example",
    "port": 22,
    "username": "cho-plan",
    "privateKeySecretRef": "nacha--{tenantId}--sftp-key",
    "passwordSecretRef": "nacha--{tenantId}--sftp-passphrase",
    "hostKeyFingerprint": "SHA256:<base64>",
    "remoteDirectory": "/inbound/ach"
  }
  ```

  Only Key Vault secret names are stored, and they must start with
  `nacha--{tenantId}--` (like trading-partner's `tp--{tenant}--` rule). A
  literal credential in the body (`password`, `privateKey`, anything unknown),
  another tenant's prefix, or `enabled` without host, username, remote
  directory, a credential name and a SHA-256 `hostKeyFingerprint` is 400.
  Setting or changing this block needs platform:tenants (it decides where
  payment files and the bank credentials go), not settings:manage alone: a
  settings manager may resend it unchanged or leave it out (the stored block
  is kept), and any other change is 403 and audited as refused. Changes are
  audited with host, port, host-key pin and directory. The sending services read the names
  with their own service token; any other reader without settings:manage sees
  `privateKeyConfigured` / `passwordConfigured` instead. `passwordSecretRef`
  is the private key's passphrase when a key is set, otherwise the SFTP password.
- **Credentials:** read from Key Vault with the service's managed identity
  (`DefaultAzureCredential`, `SecretClient`) at send time:
  `NachaTransmission:KeyVaultUri` (default `SecretProvider:AzureKeyVaultUri`),
  optionally `NachaTransmission:ManagedIdentityClientId`. The identity needs
  secret `get` on the `nacha--*` secrets. Secrets are never logged and never
  put in an error message.
- **Host key pinning:** strict. Without a valid `SHA256:` fingerprint nothing
  connects (and no secret is read); a server presenting another key is refused.
  Get the pin from the bank, or `ssh-keyscan -p 22 host | ssh-keygen -lf - -E sha256`,
  verified out of band.
- **Atomic upload:** the file is written as `.{name}.{guid}.part` in the
  remote directory, then renamed to its final name; a partial upload is
  deleted; an existing file of the same name is never overwritten.
- **Development:** `NachaTransmission:Mode=LocalFolder` writes to a local
  folder (`NachaTransmission:LocalFolder`, default
  `{temp}/cho-nacha-outbox/{service}/{tenant}`) the same way. Startup fails
  with that mode outside Development and Testing.

### When a file cannot be sent

Transmission not configured, disabled, or failing: the file is encrypted with
`IFieldProtector` (each service's own `FieldProtection:KeyRing`, the same
settings sponsor-service and provider-service use, with purpose
`premium-billing-service` / `capitation-service`) and held in Mongo
(`NachaHeldFiles`, TTL index on `expiresAt`, 7 days; reads refuse it after
expiry). Without a key ring or a store (Cosmos native SDK deployments: TODO),
nothing is held and the payments stay `Pending`.

| Endpoint (premium-billing `api/v1/eft/...`, capitation `api/v1/capitation/disbursements/...`) | Who |
|---|---|
| `GET nacha/held` (what waits, why, expiry; never the file) | premium: billing:read, payments:read or payments:approve; capitation: payments:read or payments:approve |
| `POST nacha/held/{fileReference}/retry` (sends the held file again) | payments:approve, user token, not the user who released it (403 "Separation of duties") |
| `POST nacha/held/{fileReference}/retrieve` body `{ "reason": "..." }` (returns the file as `text/plain`, `Cache-Control: no-store`) | platform:admin, user token, not the user who released it; reason required (400) |

- Every retrieval returns the file once, is recorded on the held file
  (who, when, reason) and logged (event 4905, with the reason). The first
  retrieval marks the payments `Submitted` (the platform admin delivers the
  file by hand); after it, retry is refused (409) so nothing is sent twice.
- FinanceApprover, TenantAdmin and every service are refused (platform:admin
  is never granted by a wildcard or a service token).
- A successful retry marks the payments `Submitted` and drops the held copy.
  Two retries at once cannot both send (the held file is claimed first).
- A held file that expired (410) or is gone (404) puts its payments back to
  `Pending` on the retry attempt, for the next release.
- The held file's SHA-256 is checked before it is used; a mismatch is refused.
- Audit events: 4901 delivered, 4902 held, 4903 not sent, 4904 retried, 4905
  retrieved, 4906 refused. None carries a number or the file.

Also fixed: the premium debit file wrote the debit total in the credit field
of the batch and file control records; it is now in the debit field.

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
  key rotation handled by Data Protection). The purpose includes the service
  name, so one service's values cannot be decrypted by another.
- **Bound to the record:** a bank number on a record is stored as
  `enc:v2:<ciphertext>`, whose purpose also names the tenant, the record
  (provider id; sponsor id for `BillingAccountNumber`; tenant + group number
  for `SponsorBankAccounts`) and the field. A ciphertext copied to another
  record, tenant or field does not decrypt (the read fails, 503 where the
  numbers are needed). Values without a record (held NACHA files) are
  `enc:v1:<ciphertext>`, and record values written before binding existed
  (`enc:v1:`) still decrypt; they are stored as `enc:v2:` by the record's
  next write or by the migration below.
- **`FieldProtection:RejectPlaintext`** (default `false`): when `true`,
  reading a protected field that still holds plaintext fails
  (`FieldProtectionException`, 503) instead of returning it. Turn it on per
  service once nothing is left in plaintext (the service's
  `--encrypt-bank-accounts` exits 0 for every tenant). It stays off by
  default because turning it on before the migration would break reads of
  existing rows. Outside Development, a service with it off logs a warning
  at startup (event 4818, category `CloudHealthOffice.FieldProtection`); this
  applies to every service that calls `AddChoFieldProtection` (sponsor,
  provider, premium-billing, capitation).
- **`FieldProtection:RejectUnbound`** (default `false`): when `true`, reading
  a record field that is still `enc:v1:` (encrypted before record binding)
  fails (`FieldProtectionException`, 503) instead of decrypting it without
  the binding, so a ciphertext copied between records is never accepted.
  Values that are `enc:v1:` by design (held NACHA files, read without a
  record) are unaffected, and so is the migration. Turn it on together with
  `RejectPlaintext`, once the migration exits 0 for every tenant.
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
  request value in a stored `enc:` form is refused (400). A
  `ProviderBankAccounts` value that does not decrypt fails that read (503).
  A provider-row copy that does not decrypt does not fail provider reads
  (claims, rosters, FHIR never show the numbers); it stays encrypted on the
  object, is logged (event 4708) and the full read refuses it (503).
- **Legacy plaintext:** a value without an `enc:` prefix is read as is
  (unless `FieldProtection:RejectPlaintext`).
  For a sponsor's billing account number, each read logs event 4817 (sponsor
  and tenant, never the number). It is stored encrypted on the next create or
  full update of that sponsor. The status-only write leaves it unchanged.
  Bank-account records are re-encrypted on their next write. For provider
  bank-account records each read of plaintext logs event 4707 (provider and
  tenant). A provider row is re-encrypted on its next whole-row write (update,
  draft, activate, supersede, suspend, terminate); rows never written again
  (superseded versions) need the migration below.

### Re-encrypting existing provider data

`provider-service --encrypt-bank-accounts` stores every routing, account and
tax number in `Providers` (all version rows) and `ProviderBankAccounts`
(active, pending and history) as `enc:v2:` (bound to its record): plaintext
and `enc:v1:` values alike, tenant by tenant. Run it after deploying, with
provider-service's own configuration (the same `MongoDb` or `CosmosDb` and
`FieldProtection` settings and identity as the pods, so it writes with the
shared key ring):

```
dotnet provider-service.dll --encrypt-bank-accounts [--tenant <id>]... [--dry-run]
# from a checkout:
dotnet run --project src/services/provider-service -- --encrypt-bank-accounts --dry-run
```

- It only encrypts. It never makes an account active: a provider-row account
  from before dual control still has to be proposed and approved.
- Without `--tenant` every tenant found in the two collections is processed,
  plus rows without a tenant. With `MongoDb:UseTenantScoping`, name each
  tenant (one database per tenant).
- It prints, per tenant, rows and records scanned, encrypted, already
  encrypted and conflicts. It never prints a number.
- Idempotent and resumable: bound values are left alone, and each document
  is updated on its own with a compare-and-set (Mongo: on the values it read
  and, for records, the revision, which it does not bump; Cosmos: the row's
  ETag, and for records the service's own revision-checked save). A document the
  service changed meanwhile is a conflict, left for the next run. Stop it at
  any time and run it again.
- Exit code 0: nothing left in plaintext. 2: conflicts or failures, run again.
  1: it did not start (no key ring outside Development/Testing, half a key-ring
  configuration, or neither Mongo nor Cosmos configured). With
  `MongoDb:ConnectionString` it uses Mongo; otherwise `CosmosDb:Endpoint` /
  `CosmosDb:Key` (containers `CosmosDb:ContainerName`, default `Providers`,
  and `CosmosDb:ProviderBankAccountsContainer`, default `ProviderBankAccounts`).
- When it exits 0 for every tenant, set `FieldProtection__RejectPlaintext=true`
  on provider-service.
- sponsor-service has no such command yet: its values are encrypted and bound
  on the record's next write, so keep `RejectPlaintext` off there until every
  sponsor and sponsor bank-account record has been written once.
