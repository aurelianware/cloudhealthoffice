# Runbook: NACHA bank transmission for payment runs (payment-service)

**Scope:** sending the NACHA CCD+ credit file of a completed fee-for-service ACH
payment run (`POST /api/paymentruns/{id}/eft-file`) to the tenant's bank by SFTP.
**Default:** off. Nothing is sent until both switches below are on.

> This moves money. Every step that changes configuration needs two people:
> one to make the change, one to check it against what the bank sent in writing.

Related: [bank-account-data.md](../security/bank-account-data.md) (the shared
`CloudHealthOffice.NachaTransmission` library, tenant settings validation, Key
Vault naming, and what premium-billing-service and capitation-service do).

## 1. How it works

```
generate EFT file (pins SHA-256 on the run)          POST /api/paymentruns/{id}/eft-file
        |
approve + transmit (user != run creator, != run executor)   POST /api/paymentruns/{id}/eft-file/transmission
        |
  approval still describes the money? (effective date ahead, payments unreversed / not reissued), else 409
        |
  regenerate file -> must hash to the pinned and the approved SHA-256, else 409 and nothing sent
        |
  record Pending -> Transmitting (conditional write: one attempt at a time)
        |
  SFTP: pinned host key -> key auth -> upload .{name}.{guid}.part -> rename to ACH-FFS-{run}.ach
        |
  Transmitted (200)  |  Failed (502, nothing reached the drop, may retry)  |  NeedsReview (409, outcome unknown)
```

- **One record per file** (`PaymentFileTransmissions`, Mongo collection or Cosmos
  container, id `nacha:{tenant}:{fileReference}`), pinned to the file's SHA-256
  at approval. The record holds no file, no routing/account/TIN number and no PHI.
- **Never twice.** A `Transmitted` file is returned as is. A `NeedsReview` file
  (the rename's reply was lost, the attempt crashed, any unexpected error) is
  never re-sent by the service: it must be reconciled or resolved first (section 7).
  The SFTP transmitter also never overwrites a file already in the drop.
- **Late outcomes.** If an attempt outlives its lease (it is parked as NeedsReview)
  and then finishes delivered or unknown, the record is forced back to NeedsReview
  with a `LateOutcome` audit entry ("late delivery evidence"), even if a resolver
  had meanwhile recorded "not received". Only a record already `Transmitted` stays.
- **Never changed bytes.** Each attempt regenerates the file from the run and the
  approved accounts. If a payee's account changed since approval the bytes differ
  and the attempt is refused (409 "File differs from the approved file").
- **Never stale money.** Before every attempt, first or retry (409 "Approval no
  longer valid", audited, nothing sent):
  - the effective entry date (the run's payment date, pinned in the file) must be a
    Federal Reserve banking day **after** today in `BankTransmission:BankTimeZone`
    (default America/New_York); `BankTransmission:AllowSameDayEffectiveDate=true`
    accepts today (same-day ACH; mind the bank's cut-offs);
  - the file must pay exactly the payments approved, each still this run's ACH
    payment, not in Exception, amounts unchanged, none of its claims reserved for a
    reversal run, none of its claims now paid by another run (reissued).
  A stale file is never "fixed" by the service: a new file needs a new approval
  (today: cancel/reverse and pay in a new run; a re-issue flow is a follow-up).
- **Duplicate files.** Each run's file gets its own file ID modifier (A-Z, 0-9) for
  its creation day, claimed once per tenant, destination and origin
  (`NachaFileIdModifiers`) and kept on the run, so two runs completed in the same
  minute never produce headers the bank would reject as duplicates, and every
  rebuild stays byte-identical. At most 36 files per day. Files pinned before this
  rebuild with `Nacha:FileIdModifier` (default A).
- **Dual control.** Extends payment-run maker-checker: neither the run's creator nor
  its executor may approve, retry or reconcile its transmission; a run without a
  recorded creator is refused; service tokens never can. A `NeedsReview` file is
  resolved by a user who neither approved, attempted nor reconciled it.
- **Atomic rename assumption.** The upload writes `.{name}.{guid}.part`, then
  renames it. Only "final name present, temporary gone" counts as delivered after a
  failed rename reply; "temporary still there" is **unknown** (NeedsReview, the
  temporary file is left in place), because servers that rename by copy-then-delete
  may be mid-copy. Confirm with the bank that rename in their drop is atomic and that
  dot-files / `*.part` are never collected. Closing the session after a successful
  rename never turns a delivery into a failure.
- **Audit.** Every attempt, refusal, lease expiry, reconciliation and resolution is
  appended to the record's `attempts` (operator, time, SHA-256, resulting status,
  detail) in the same conditional write as the state change, and logged as
  `AUDIT ...` (event ids 4931-4939). Credentials and file content are never logged.

## 2. Prerequisites with the bank

Get these from the bank **in writing** (ACH origination agreement / onboarding pack):

| Item | Used as |
|---|---|
| SFTP host, port, username, inbound directory | tenant `nachaTransmission.host/port/username/remoteDirectory` |
| The server's SSH host key fingerprint (SHA-256) | `hostKeyFingerprint` |
| Where to register CHO's public key | you generate the key pair (section 3) |
| Immediate destination / origin, ODFI, company id, company name | payment-service `Nacha:*` (already needed to generate files) |
| File naming rules, cut-off times, whether files are moved out of the drop after pickup | runbook notes; see section 5 (an absent file is not proof of non-delivery) |
| Confirmation that dot-files / `*.part` in the drop are ignored, and that rename in the drop is atomic | the upload writes `.{name}.{guid}.part` before renaming (section 1) |
| Their duplicate-file rule (usually destination + origin + creation date + file ID modifier) | file ID modifiers are allocated per day (section 1) |
| Their test (certification) SFTP endpoint, if they have one | section 6 |

## 3. Credentials (Key Vault)

1. Generate a dedicated key pair per tenant and bank (never reuse):
   `ssh-keygen -t ed25519 -f nacha-{tenant} -C "cho-{tenant}-ach"` (or RSA 4096 if the bank requires it).
2. Store the **private key** in the service Key Vault as
   `nacha--{tenantId}--sftp-key`, and its passphrase (if any) as
   `nacha--{tenantId}--sftp-passphrase`. Names must start with `nacha--{tenantId}--`;
   any other name is refused before Key Vault is read.
3. Send the **public key** to the bank through their registration channel. Delete
   local copies of the private key once stored.
4. Give payment-service's managed identity secret `get` on `nacha--*`. Configure
   `NachaTransmission:KeyVaultUri` (default `SecretProvider:AzureKeyVaultUri`) and,
   for a user-assigned identity, `NachaTransmission:ManagedIdentityClientId`.

## 4. Host key pin (no trust on first use)

- Preferred: the fingerprint the bank gave you, `SHA256:<base64>`.
- To cross-check (never as the only source): from a trusted network,
  `ssh-keyscan -p <port> <host> | ssh-keygen -lf - -E sha256`, and compare with the
  bank's value by phone/email with a second person. Pin only on a match.
- Without a valid pin nothing connects and no secret is read. A server that
  presents any other key is refused before authentication; the attempt is
  `Failed` with "host key refused" and the presented fingerprint. **Never** re-pin
  in response to that error alone: call the bank (key rotation) and treat it as a
  security incident until explained.

## 5. Enabling

1. **Tenant settings** (tenant-service, needs `platform:tenants`):
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

2. **payment-service configuration:**

   | Key | Value |
   |---|---|
   | `BankTransmission:Enabled` | `true` (default `false`: endpoints answer 409 "Bank transmission disabled", nothing is regenerated, recorded or sent, and no SFTP, Key Vault or tenant-service client is registered) |
   | `BankTransmission:TransmittingLease` | default `00:15:00`; an attempt older than this is presumed of unknown outcome |
   | `BankTransmission:BankTimeZone` | default `America/New_York`; "today" for the effective-date check |
   | `BankTransmission:AllowSameDayEffectiveDate` | default `false`; `true` accepts an effective date of today (same-day ACH) |
   | `TenantService:BaseUrl` | e.g. `http://tenant-service` |
   | `ChoAuth:Outbound:Hosts` | add `tenant-service` (settings are read with payment-service's own service token) |
   | `ChoAuth:ServiceToken:*` | payment-service's service identity (already required for run execution) |
   | `NachaTransmission:KeyVaultUri` | the vault from section 3 |

   Restart payment-service; the flag is read at startup.

3. **Development only:** `NachaTransmission:Mode=LocalFolder` writes files to
   `{temp}/cho-nacha-outbox/payment-service/{tenant}` instead of SFTP; startup fails
   with that mode outside Development/Testing.

## 6. First file: test / prenote

payment-service does **not** generate prenotes (zero-dollar `23`/`33` entries) or
test files; it only sends a completed run's live credits. Therefore:

1. If the bank has a certification endpoint, point a **non-production tenant**
   at it (section 5 with the test host, key and pin) and send a small run there.
   Have the bank confirm they parsed it (batch header, company id, entry hash, totals).
2. Payee accounts are validated by provider-service's dual-controlled approval;
   if your ODFI requires prenotes before live credits, run them through the bank's
   portal until prenote generation exists (follow-up).
3. For the first production file: agree a date with the bank, pick a small run,
   approve it (`POST .../eft-file/transmission`), and have the bank confirm
   receipt and totals against `GET .../eft-file/transmission` (`approvedSha256`,
   `entryCount`, `totalCreditAmount`) the same day.

## 7. Operating it

| Call | Who | Result |
|---|---|---|
| `GET /api/paymentruns/{id}/eft-file/transmission` | payments:read | the record (404 if never approved) |
| `POST /api/paymentruns/{id}/eft-file/transmission` | payments:approve, user, not the run creator or executor | approve + send, or retry a `Failed` one. 200 `Transmitted`, 502 `Failed`, 409 `NeedsReview` / hash mismatch / approval no longer valid / in progress / disabled |
| `POST .../eft-file/transmission/reconcile` | payments:approve, user, not the run creator or executor | `NeedsReview` only: lists the drop (read-only). File there with the expected size: `Transmitted` (`confirmedBy: RemoteListing`); otherwise stays `NeedsReview` |
| `POST .../eft-file/transmission/resolve` body `{"bankReceived": true, "reason": "..."}` (or `false`) | payments:approve, user, not the run creator or executor, not the approver, not anyone who attempted or reconciled it; works while disabled | `NeedsReview` only. `true`: `Transmitted` (`BankConfirmation`). `false`: `Failed` (may then be retried) |

### Failed

Nothing reached the bank's drop (not configured, host key refused, connection or
upload failed before the rename). Read `reason`, fix the cause, retry. Retries are
audited as new attempts; the approval and its SHA-256 stay.

### NeedsReview (do this carefully)

The file may be at the bank. **Do not** recreate the run, regenerate under another
name, or upload by hand.

1. `POST .../reconcile`. If `Transmitted`, done.
2. If still `NeedsReview` with "not in the drop": many banks move files out of the
   drop when they collect them, so absence proves nothing. Call the bank's ACH
   operations with the file name, `approvedSha256`, entry count and total.
3. A second user records the bank's written answer with `POST .../resolve`
   (reason: who at the bank, when, their reference).
4. Only after `bankReceived: false` may the file be retried. The transmitter still
   refuses to overwrite a file of the same name that turns up in the drop.

### Hash mismatch (409 "File differs from the approved file")

A payee's approved account or EFT enrollment changed after the file was pinned.
Nothing was sent. The pinned file stands; this needs a person (the run's payments
were already issued as ACH). Do not work around it.

### Approval no longer valid (409)

The effective entry date is today or past (or not a banking day), or a payment in
the file was reversed, reissued by another run, moved to check, put in Exception
or changed. Nothing was sent and the refusal is on the record. Do not change the
clock, the date or the record: the money in the file is not what was approved.
Settle the payments by a new run (and a new approval).

## 8. Disabling / emergency stop

Set `BankTransmission:Enabled=false` and restart (all transmit and reconcile calls
then answer 409 and do nothing), or set the tenant's `nachaTransmission.enabled`
to `false` (attempts then fail as not configured, nothing connects). An attempt
already in flight completes or becomes `NeedsReview`; it is never retried by itself.

`resolve` is deliberately **not** gated by the flag: after an emergency stop,
operators must still be able to record what the bank said about a `NeedsReview`
file (it sends and contacts nothing). `GET .../transmission` works too, and on
Cosmos it never creates the `PaymentFileTransmissions` container (only the first
approval, which needs the flag on, does).

## 9. Not built yet (follow-ups)

- **GL posting.** The seam: on `Transmitted`, a `PaymentFileTransmitted` event is
  written to the record's `outbox` in the same conditional write (payload: tenant,
  run id/number, file reference, SHA-256, entry count, total credits/debits,
  effective entry date, transmitted at, approver, evidence; deterministic
  `eventId` for de-duplication). No dispatcher publishes it yet; the GL PR adds
  the dispatcher (as in appeals-service `AppealOutboxDispatcher`) and the posting.
- **Bank acknowledgements and returns.** The record models `acknowledgement`
  (`Awaiting` after transmission, `Accepted`, `Rejected`) but nothing ingests the
  bank's ack/return files yet (an inbound SFTP pull plus parsing of the bank's
  format and of NACHA return entries, R01-R85). A settlement event belongs there.
- **PGP encryption of the file.** No OpenPGP implementation exists in the repo.
  SFTP already encrypts in transit; if the bank requires PGP on top, implement the
  `INachaFileEncryptor` seam (shared library, documented TODO) before the upload (bank's public key from Key Vault)
  and hash both the clear and the encrypted bytes.
- **Prenote / test file generation** (section 6).
- **Re-issuing a stale file** with a new effective date (today: a new run).
- **Cosmos repository tests.** The Mongo stores are tested against a real mongod
  (EphemeralMongo); the Cosmos implementations get emulator tests from the separate
  Cosmos CI work.
- **Automatic retries.** Retries are operator-triggered by design.
