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
| Full numbers | no API returns them | premium-billing-service's service token only |
| Encrypted at rest | not yet (queued; same mechanism) | yes |

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
  Sponsor events are 4811 to 4817; provider events are 4701 to 4705.

Sponsor endpoints (`api/v1/sponsors/{group}/...`):

| Endpoint | Permission |
|---|---|
| `GET bank-account` (active and pending, masked) | billing:read, payments:read or enrollment:read |
| `GET bank-account-changes`, `GET bank-account-changes/pending` (masked) | the same, or payments:approve |
| `POST bank-account-changes` (propose, 202) | billing:run or enrollment:process |
| `POST bank-account-changes/{id}/approve`, `/reject` | payments:approve, user token, not the proposer |
| `POST bank-account-changes/{id}/cancel` | billing:run or enrollment:process |
| `GET /api/v1/internal/sponsors/{group}/bank-account` (full numbers) | `[RequireServiceClient("premium-billing-service")]` |

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
- **Provider accounts:** no API returns full numbers. Every provider response
  is masked.

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
  and `unwrapKey` on the key.
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
- **Legacy plaintext:** a value without the `enc:v1:` prefix is read as is.
  For a sponsor's billing account number, each read logs event 4817 (sponsor
  and tenant, never the number). It is stored encrypted on the next create or
  full update of that sponsor. The status-only write leaves it unchanged.
  Bank-account records are re-encrypted on their next write.
- provider-service will reuse the same library for `ProviderBankAccounts`.
  That item is queued separately.
