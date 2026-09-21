# Secret Management

Cloud Health Office resolves secrets through `ISecretProvider`. There are three
backends, selected by the `SecretProvider:Provider` configuration key
(`SecretProvider__Provider` as an environment variable):

| Value | Backend | Needs |
| --- | --- | --- |
| `None` *(default)* | `NullSecretProvider` — resolves nothing | nothing |
| `AzureKeyVault` | Azure Key Vault | a vault + workload identity |
| `HashiCorpVault` | HashiCorp Vault KV v2 | a reachable Vault |

**Vault is optional.** The default is `None`, and every path an outside engineer
follows — local development, the Kubernetes quickstart, and the Million Claim
Challenge — runs on that default. Nothing in this repository requires you to
stand up Vault to reproduce a benchmark.

## Running without a secret store (the default)

This is the supported path for local development and for reproducing MCC.

Configuration reaches services as **environment variables**, supplied by
Kubernetes Secrets and ConfigMaps in a cluster, or by `docker-compose` locally.
This is ordinary ASP.NET configuration and does not involve `ISecretProvider`
at all — which is why the platform is fully functional without one.

`scripts/deploy-local.sh` sets this up for you:

```bash
kubectl set env deployment --all -n cloudhealthoffice SecretProvider__Provider=None
```

### What does not work with `None`

Three services resolve a rotating encryption key through `ISecretProvider` and
register a health check for it:

- `appeals-service`
- `consent-service`
- `personal-representative-service`

With `None`, that key cannot be resolved, so their **readiness** probe fails
even though the process is running. `deploy-local.sh` points those three at
`/health/live` instead, so they report healthy locally:

```bash
kubectl patch deployment/appeals-service -n cloudhealthoffice --type json \
  -p='[{"op":"replace",
        "path":"/spec/template/spec/containers/0/readinessProbe/httpGet/path",
        "value":"/health/live"}]'
```

**None of these three services participates in claims adjudication**, so this
does not affect the Million Claim Challenge. Do not carry this probe change into
an environment that handles real appeals, consent or personal-representative
data — there, configure a real secret store so the encryption key resolves.

## Supplying credentials as Kubernetes Secrets

Credentials are referenced, never committed. Manifests use `secretKeyRef`, and
the Secret itself is created out of band:

```bash
kubectl create secret generic sftp-credentials -n cho-workflows \
  --from-literal=password='<password>'

kubectl create secret generic backend-api-credentials -n cho-workflows \
  --from-literal=token='<token>' \
  --from-literal=url='http://backend-api.cloudhealthoffice.svc.cluster.local/api/v1'
```

Consumers that can degrade gracefully mark the reference `optional: true`, so a
missing Secret produces a documented fallback rather than a crash loop. The X12
jobs do this: without `backend-api-credentials` they use their built-in sample
payloads, which is what local development uses.

Templates carry `REPLACE_WITH_*` placeholders — see
`infrastructure/k8s/secrets/` and `scripts/secrets-manifest.example.env`.

## Running with HashiCorp Vault

Set the provider and point it at a Vault:

```bash
SecretProvider__Provider=HashiCorpVault
SecretProvider__HashiCorpVaultAddress=https://vault.internal:8200
SecretProvider__HashiCorpVaultKubernetesRole=cloudhealthoffice
```

Kubernetes auth is preferred: the pod's projected service account token is
exchanged for a Vault token, so no long-lived credential is stored anywhere.
`SecretProvider__HashiCorpVaultToken` accepts a static token, and is for **local
development and tests only** — a static token in a cluster is the thing
Kubernetes auth exists to avoid.

Secrets are read from KV v2 (mount `secret` by default). The provider takes the
conventional `value` key, and falls back to the sole key when a secret was
written under a different name. When several keys exist and none is `value`, it
returns null rather than guessing which one is the credential.

Construction fails fast when the address is missing, or when neither a role nor
a token is configured.

## Running with Azure Key Vault

```bash
SecretProvider__Provider=AzureKeyVault
SecretProvider__AzureKeyVaultUri=https://my-vault.vault.azure.net/
```

Authentication uses workload identity. See
[AZURE-KEYVAULT-INSTALLATION.md](../AZURE-KEYVAULT-INSTALLATION.md).

## What is not routed through `ISecretProvider`

`ISecretProvider` serves .NET services. Some credentials are consumed by
**containers running shell scripts** — the X12 SFTP jobs invoke `sshpass` from
an Alpine image — and by **provisioning scripts** that run before any service
starts. Those take their credentials from Kubernetes Secrets via `secretKeyRef`,
which is the appropriate mechanism for them; routing them through a .NET
interface would require Vault Agent injection or the Vault CSI driver, which
this repository does not currently deploy.

The rule is the same either way: **the credential is referenced, never
committed.**

## Related

- [CREDENTIAL-ROTATION.md](CREDENTIAL-ROTATION.md) — credentials that were once
  committed and must be rotated.
