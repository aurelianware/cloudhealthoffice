# Appeals encryption key on GCP

This standalone module provisions only appeals encryption resources in an existing
GCP project. It does not create or change a cluster, database, node pool, registry,
or any project-wide reader role. Keep its Terraform state in a durable, access-controlled
backend appropriate to the deployment; it contains resource metadata, not key payloads.
Do not apply this module and also manage the same resources from another state.

Resources:

- Google service account `cho-appeals`.
- Secret `appeal-body-encryption-key-v1`, protected against Terraform destruction.
- Secret Accessor and Secret Manager Viewer on **that secret only**, for `cho-appeals`.
  Accessor reads the key; Viewer supports the provider's version-metadata interface.
- Workload Identity User on that service account for the existing GKE pool's
  `cloudhealthoffice/appeals-service` Kubernetes identity only.

The project must already have Secret Manager API and GKE Workload Identity enabled.
No service-account credential file is generated. The application uses ADC through
Workload Identity. The adapter verifies CRC32C, allows only explicitly configured
secret names, and does not list all project secrets.

## Review and provision

From the repository root:

```bash
terraform -chdir=infrastructure/gcp/appeals-secrets init
terraform -chdir=infrastructure/gcp/appeals-secrets plan \
  -var='project_id=cloud-health-office-demo' -out=appeals-secrets.tfplan
# Review the five additions and obtain approval for the scoped access change.
terraform -chdir=infrastructure/gcp/appeals-secrets apply appeals-secrets.tfplan
python3 scripts/demo/gcp/initialize_appeals_key.py --project cloud-health-office-demo
```

The initializer creates a random 256-bit key only if the named secret has **no
versions**. It sends the base64 key through stdin to Secret Manager; it does not
print it, save it to a file, or place it in Terraform state. Any existing version,
including a disabled or destroyed version, prevents automatic replacement. Keep
this key for as long as any encrypted record needs it. Never replace v1's material
as a rotation shortcut; a new logical version needs a new named key, access grant,
accepted-version configuration, and an explicit retention/migration plan.

Do not run initialization against a differently named secret to overwrite or
recover a key. If resources already exist outside this Terraform state, import
and review them first. A missing, disabled, inaccessible, or corrupt key fails
closed; there is no plaintext fallback.

## Enable the deployed application

Merge the application/manifest support before enabling these repository variables:

- `APPEALS_SECRETS_PROJECT`: output `appeals_secrets_project`.
- `APPEALS_SECRETS_SERVICE_ACCOUNT`: output `appeals_secrets_service_account`.

The deployment workflow forwards both to the renderer. They must be supplied
together. Only appeals-service changes provider: it uses its own annotated
Kubernetes service account, permits `appeal-body-encryption-key-v1`, and uses
`/health/ready` so a missing key prevents readiness. Existing secret-provider and
security behavior for other services is preserved. No secret payload belongs in
GitHub variables, workflow inputs, manifests, Terraform variables, or source.

After rollout, check readiness, then reconcile the existing saved intake through
contact-service. Do not amend an Unknown outcome or POST around the contact
workflow. Callback routing from PR #1560 is also required. Verify the same contact
receipt and exactly one owner case. This module and its tests alone do not establish
live GCP encryption or case-acceptance evidence.

References: [Google authentication](https://docs.cloud.google.com/secret-manager/docs/authentication),
[secret access API](https://docs.cloud.google.com/secret-manager/docs/reference/rest/v1/projects.secrets.versions/access),
[version metadata API](https://docs.cloud.google.com/secret-manager/docs/reference/rest/v1/projects.secrets.versions/list).
