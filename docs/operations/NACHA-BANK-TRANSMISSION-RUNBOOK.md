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
- **Effective entry dates.** All dates are in `BankTransmission:BankTimeZone`
  (default America/New_York) on the Federal Reserve calendar (weekends and Fed
  holidays are not banking days).
  - **At run create:** `paymentDate` in the request is optional. A requested date
    may not be in the past or more than a year ahead (400); a weekend or holiday is
    rolled forward to the next banking day. Without one, the date is provisionally
    the next banking day.
  - **At execution**, before any payment or 835 is built, the run's payment date is
    fixed: the requested date if still acceptable, otherwise the earliest acceptable
    banking day (the next banking day after today, or today with
    `AllowSameDayEffectiveDate`). The 835s carry it in **BPR16**.
  - **When the file is first pinned** (`POST .../eft-file`), its effective entry
    date is that same date, so **BPR16 = the NACHA effective entry date**. The date
    is part of the pinned bytes.
  - **At send time** only a date that is past (or today, without same-day) is
    refused. That happens when the file was generated, approved or retried too
    late. Such a file is re-dated (below), never edited.
- **Never stale money.** Before every attempt, first or retry (409 "Approval no
  longer valid", audited, nothing sent):
  - the pinned effective entry date must still be acceptable (above);
  - the file must pay exactly the payments approved, each still this run's ACH
    payment, not in Exception, amounts unchanged, none of its claims reserved for a
    reversal run, none of its claims now paid by another run (reissued).
- **Re-dating** (`POST .../eft-file/redate`, reason required). Only for a file whose
  date can no longer be sent, and only while its transmission record is absent,
  `Pending` or `Failed` (which includes a `NeedsReview` resolved "not received").
  Never `Transmitting`, `Transmitted` or an unresolved `NeedsReview`.
  - **Failed files:** before a `Failed` file is superseded, the bank's drop is
    listed (read-only) for its name. The result is recorded on the record. If the
    file is there, or the drop cannot be checked, the re-date is refused: confirm
    with the bank first.
  - **Concurrent re-dates:** a re-pin applies only to the exact file (reference and
    SHA-256) the request checked. If another re-date already replaced it, the
    request gets 409 and nothing changes.
  - **The re-dater may not approve or send the new file.**
  - **835 dates (BPR16):** the run's 835s are **not** rewritten. payment-service
    does not track whether a trading partner has already fetched an 835, so they
    are treated as delivered. Each one is listed in the new file's
    `remittanceDateNotices` (envelope, partner, ISA13, BPR16, new effective date),
    and the same list appears on the new file's transmission record. A run warning
    is added too. Tell those providers the funds settle on the new date. It works in
  two steps, so the old file is unsendable before the new one exists:
  1. The old record becomes `Superseded`, linked to the new file reference. If the
     file was never approved, a `Superseded` record is created for it.
  2. A new file is pinned: created now, a new effective date chosen as above, a new
     file ID modifier, reference and name ending `-R{n}`. The old file moves to the
     run's `eftFileHistory`.

  The new file needs a fresh approval (`POST .../transmission`). A `Superseded`
  record is final: it is never sent, retried, reconciled or resolved. If the
  re-date is interrupted between the two steps, calling it again completes it.
- **Duplicate files.** Each run's file gets its own file ID modifier (A-Z, 0-9) for
  its creation day, claimed once per tenant, destination and origin
  (`NachaFileIdModifiers`) and kept on the run, so two runs completed in the same
  minute never produce headers the bank would reject as duplicates, and every
  rebuild stays byte-identical. At most 36 files per day. Files pinned before this
  rebuild with `Nacha:FileIdModifier` (default A).
- **Dual control.** Extends payment-run maker-checker. Neither the run's creator nor
  its executor may approve, retry, reconcile or re-date its transmission; service
  tokens never can. A `NeedsReview` file is resolved by a user who neither approved,
  attempted nor reconciled it. **Rollout:** paying a run by ACH therefore needs
  **three different users**: the creator (maker), the executor (checker) and the
  transmitter (approver). Resolving an unknown outcome needs a **fourth**. Runs with
  no recorded creator (created before creators came from the token) **cannot be
  transmitted** at all: pay them another way, or recreate them.
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
| `POST .../eft-file/redate` body `{"reason": "..."}` | payments:approve, user, not the run creator or executor; works while disabled (sends nothing) | only when the pinned date can no longer be sent and the record is absent / `Pending` / `Failed`: old record `Superseded`, new `-R{n}` file pinned (needs a fresh approval). 409 otherwise |
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

Nothing was sent, and the refusal is on the record. There are two causes:

- **The effective entry date passed** (or is today, without same-day). This happens
  when approval or a retry came too late. Re-date the file
  (`POST .../eft-file/redate` with a reason), then approve the new `-R{n}` file.
- **A payment in the file changed**: reversed, reissued by another run, moved to
  check, put in Exception, or a different amount. The money in the file is not what
  was approved. Do not re-date and do not change the record. Settle the payments
  another way (a new run and a new approval).

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
- **File ID modifiers in capitation-service and premium-billing-service.** Those
  services still use a fixed `FileIdModifier` (`Nacha:FileIdModifier`, default A;
  `capitation-service/Services/NachaCreditFileService.cs`,
  `premium-billing-service/Services/NachaFileService.cs`). Two of their files on one
  day, or one of theirs and a payment-service file with the same immediate
  destination and origin, can be rejected by the bank as duplicates. Until they use
  an allocator like payment-service's: give each sending service its own **immediate
  origin** (or its own bank drop) with the bank, and keep to one file per service
  per day.
- **835 delivery tracking.** With it, a re-date could regenerate the run's 835s with
  the new BPR16 whenever no trading partner has fetched them yet. Without it, the
  service only issues the notices described in section 1.
- **GenerationCount** on a pinned file can miss an increment when two
  reproductions race (it is informational only).
- **Cosmos repository tests.** The Mongo stores are tested against a real mongod
  (EphemeralMongo); the Cosmos implementations get emulator tests from the separate
  Cosmos CI work.
- **Automatic retries.** Retries are operator-triggered by design.
