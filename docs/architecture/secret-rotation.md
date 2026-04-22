# Secret Rotation

Covers rotation of symmetric key material fetched through
`ISecretProvider` — HMAC signing keys, AES-GCM data encryption keys,
fingerprint HMAC keys. Asymmetric-key rotation (RS256 / JWKS, TLS certs)
uses different patterns and is out of scope.

## The rotation model

Keys are identified by an **operator-controlled logical version string**
(`v1`, `v2`, …) rather than the opaque version ID that Key Vault assigns
to each `SecretClient.SetSecret` call. The logical version appears in:

1. **The Key Vault secret name.** A secret named `{prefix}-{version}`,
   e.g. `member-identifier-encryption-key-v2`. Rotating the key means
   publishing a new secret, NOT creating a new KV version of the same
   secret. (KV versions are still assigned, but the app never reads them.)
2. **The app's configuration.** Two keys per consumer: `CurrentKeyVersion`
   (used for new writes) and `AcceptedKeyVersions` (rolling window for
   reads / decrypts).
3. **Where the data lives** — either embedded in the artifact (0x02
   ciphertext envelope, QR payload's `KeyVersion` field) or covered by
   dual-read (fingerprinter candidates).

The primitive is `RotatingKeyProvider`
(`src/services/shared/CloudHealthOffice.Infrastructure/Configuration/`).
Consumers ask it for `GetKeyAsync(prefix, version, devConfigFallback)`
and get cached key bytes; the cache is invalidated by
`SecretRefreshService` on every `IConfiguration` reload, so new secret
values propagate within one reload interval (default 5 minutes) with no
pod restart.

## End-to-end rotation sequence

For any consumer (identifier encryptor, fingerprinter, QR signer):

1. **Provision the new secret.** Run
   `scripts/azure/rotate-secret.sh` with `SECRET_PREFIX`, `NEW_VERSION`,
   `KEY_VAULT_NAME`. The script is idempotent and only handles Key
   Vault state — it does not touch app config.
2. **Widen the accepted window.** Add `NEW_VERSION` to
   `…AcceptedKeyVersions`. Do NOT flip `CurrentKeyVersion` yet.
   Deploy / update config.
3. **Wait one reload interval.** Every service pod refreshes its
   `RotatingKeyProvider` cache off the next `SecretProviderConfigurationProvider`
   tick. Verify via the `RotatingKeyProviderHealthCheck` that every
   accepted version resolves — the health endpoint reports Degraded
   when a listed version is unresolvable.
4. **Flip `CurrentKeyVersion`.** New writes from this point emit under
   the new version. Old records continue to decrypt against the old
   version because it's still in `AcceptedKeyVersions`.
5. **(Eventually) drop the prior version** from `AcceptedKeyVersions`.
   Only safe after a backfill job has re-encrypted / re-fingerprinted
   every record under the current version. See the concrete sequence
   below.

## Envelope migration — 0x01 → 0x02 for `KeyVaultIdentifierEncryptor`

Pre-A.7.3 ciphertexts use a 0x01 envelope:
`[0x01][12 IV][16 tag][ciphertext]` — no key version is carried.

Post-A.7.3 ciphertexts use a 0x02 envelope:
`[0x02][keyVerLen][keyVer UTF-8][12 IV][16 tag][ciphertext]` — the key
version is embedded so the decryptor knows which key to use.

`DecryptAsync` supports both forever (read-only path). 0x01 envelopes are
decrypted using `MemberEncryption:LegacyKeySecretName` — a single
fixed secret name, no fallback chain. A service booting without an
explicit `MemberEncryption` block but with the legacy
`Member:IdentifierEncryption:KeySecretName` config continues to *emit*
0x01 envelopes against that same key, so zero new config is required for
backward-compatible operation. When an operator adds the
`MemberEncryption` section, new writes start emitting 0x02.

## Fingerprinter dual-read

HMAC fingerprints are lookup keys — embedding a version would change the
lookup value and invalidate every existing row. So instead of versioned
output, `IIdentifierFingerprinter` has two methods:

- `FingerprintAsync` — one hash under the current version, used on write.
- `FingerprintCandidatesAsync` — one hash per accepted version (newest
  first), used on read. Callers match `candidates.Contains(storedFingerprint)`
  (or SQL `WHERE fingerprint IN (...)`).

`IdentifiersController` uses candidates in both the Add dedupe check and
the Remove lookup — Add is subtle because it's a read against existing
rows inside a write endpoint, and without candidates a rotation would
silently allow duplicate identifiers past the 409.

## Health check

`RotatingKeyProviderHealthCheck` (`Infrastructure/HealthChecks/`) accepts
a list of `(secretPrefix, versions)` probes and reports Degraded (not
Unhealthy) if any accepted version is unresolvable. Degraded rather than
Unhealthy because a missing legacy version is an operational concern
that gets surfaced before it hits a request, not a per-request failure.
If a request does hit a missing version, the read paths fail closed:
the decryptor throws `StaleEncryptionKeyException` and the fingerprinter
throws `StaleFingerprintKeyException` on its candidates call. Fail-open
behaviour (e.g. returning a partial candidate set) would silently admit
duplicate PII identifiers past the uniqueness check, so the read paths
require every accepted version to resolve.

Each host service registers its own probes — e.g. member-service registers
two (encryption prefix + fingerprint prefix), idcard-service registers one.

## Backfill is out of scope for A.7.3

This PR lets old records keep being read, and lets new records be written
under the new key. It does NOT re-encrypt or re-fingerprint existing
records under the new version. That backfill belongs to a separate
migration job per consumer and is an operational decision on timing.

**Concrete sequence to retire `v1` entirely** (e.g. after a compromised
key rotation):

1. Deploy a backfill job that reads every record whose identifier is
   either a 0x01 envelope or fingerprinted under v1, decrypts/re-
   fingerprints with the current plaintext, and re-writes under the
   current version. This is read-write and per-consumer; the identifier
   encryptor + fingerprinter in member-service are the two consumers
   today.
2. Verify zero remaining v1 records (query Cosmos / Mongo for records
   whose first base64url byte decodes to 0x01 for the encryptor, and
   compute `FingerprintCandidatesAsync[v1]` equality against a sample
   for the fingerprinter).
3. Remove `v1` from `MemberEncryption:AcceptedKeyVersions` and
   `MemberFingerprinting:AcceptedKeyVersions`. Deploy.
4. Until step 3 is complete, do NOT remove the `v1` secret (or the
   legacy secret) from Key Vault — doing so would cause
   `StaleEncryptionKeyException` on read of any remaining v1 record
   and send the service into a persistent degraded state for affected
   tenants.

## Future consumers

Any new cryptographic consumer that needs key rotation should use
`RotatingKeyProvider` directly. Register a `RotatingKeyVersionProbe`
with the health check so operators see unresolvable versions in the
readiness endpoint. Candidates include:

- Webhook signing secrets (if added).
- CSRF tokens backed by a shared secret at scale.
- Session cookie signing keys.

Do NOT route asymmetric keys (RS256 signers, TLS private keys) through
`RotatingKeyProvider` — they have their own JWKS / cert-rotation
patterns and the abstractions don't line up.

## Explicit non-goals

- Backfill re-encryption / re-fingerprinting (see above — separate job
  per consumer).
- HashiCorp Vault rotation. The enum value exists in
  `SecretProviderType` but the implementation is planned for a later
  release; today only Azure Key Vault is supported.
- Asymmetric key rotation. See the future-consumers section.
- Automatic drop of 0x01 envelope decode support after backfill. The
  decode path stays in the encryptor indefinitely — dropping it is a
  future call requiring a separate PR and a deployment gate.
